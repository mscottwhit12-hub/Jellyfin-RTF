using System.Collections.Concurrent;
using Jellyfin.Plugin.RaceToFinish.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.Tracking;

/// <summary>
/// Records viewing sessions from Jellyfin's playback events.
///
/// Only time actually watched is counted: each progress report adds the distance the playhead moved,
/// but only if that distance is plausible for the real time that passed (allowing up to 2.5x playback
/// speed). Jumps forward (seeking), jumps back and "mark as played" add nothing. A session is
/// "finished" when at least 85% of the runtime was actually watched.
/// </summary>
public sealed class WatchTracker : IHostedService, IDisposable
{
    /// <summary>Share of the runtime that must actually be watched for a session to count as finished.</summary>
    public const double FinishedShare = 0.85;

    /// <summary>Sessions shorter than this are not saved (a peek, not a watch).</summary>
    public static readonly TimeSpan MinimumWatched = TimeSpan.FromSeconds(60);

    /// <summary>A session with no reports for this long is treated as ended (app closed, TV turned off).</summary>
    public static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(30);

    private const double MaxSpeed = 2.5;
    private static readonly TimeSpan JitterAllowance = TimeSpan.FromSeconds(3);

    private readonly ISessionManager _sessionManager;
    private readonly HistoryDatabase _database;
    private readonly ItemSnapshotBuilder _snapshots;
    private readonly ILogger<WatchTracker> _logger;
    private readonly ConcurrentDictionary<string, WatchState> _active = new(StringComparer.Ordinal);
    private Timer? _sweeper;

    /// <summary>Initializes a new instance of the <see cref="WatchTracker"/> class.</summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="database">History database.</param>
    /// <param name="snapshots">Snapshot builder.</param>
    /// <param name="logger">Logger.</param>
    public WatchTracker(ISessionManager sessionManager, HistoryDatabase database, ItemSnapshotBuilder snapshots, ILogger<WatchTracker> logger)
    {
        _sessionManager = sessionManager;
        _database = database;
        _snapshots = snapshots;
        _logger = logger;
    }

    /// <summary>Gets the number of sessions currently being followed (for diagnostics).</summary>
    public int ActiveCount => _active.Count;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sweeper = new Timer(_ => SweepAbandoned(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        _logger.LogInformation("[RaceToFinish] Watch tracker started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sweeper?.Change(Timeout.Infinite, Timeout.Infinite);

        // Server shutting down: save what has been watched so far.
        foreach (string key in _active.Keys.ToList())
        {
            if (_active.TryRemove(key, out WatchState? state))
            {
                Save(state, state.LastReportUtc);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sweeper?.Dispose();
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => Guard(() => Report(e, stopped: false));

    // Jellyfin 12 also raises "automated" progress events between the app's real reports, with a position
    // it estimates by assuming normal-speed playback. Only the app's own reports are trusted: estimates keep
    // ticking even when an app has frozen or crashed.
    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (!e.IsAutomated)
        {
            Guard(() => Report(e, stopped: false));
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) => Guard(() => Report(e, stopped: true));

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Never let tracking break playback.
            _logger.LogError(ex, "[RaceToFinish] Watch tracking error");
        }
    }

    private void Report(PlaybackProgressEventArgs e, bool stopped)
    {
        BaseItem? item = e.Item;
        if (!ItemSnapshotBuilder.IsTrackable(item) || e.Users is null || e.Users.Count == 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        long? position = e.PlaybackPositionTicks;
        string sessionKey = !string.IsNullOrEmpty(e.PlaySessionId) ? e.PlaySessionId : (e.Session?.Id ?? e.DeviceId ?? "unknown");

        foreach (var user in e.Users)
        {
            if (!UserAccess.IsParticipant(user))
            {
                continue;
            }

            string key = $"{sessionKey}|{user.Id:N}|{item!.Id:N}";
            WatchState state = _active.GetOrAdd(key, _ => new WatchState
            {
                UserId = ItemSnapshotBuilder.Id(user.Id),
                Item = item,
                StartedUtc = now,
                LastReportUtc = now,
                LastPositionTicks = position ?? 0,
                MaxPositionTicks = position ?? 0,
                DeviceName = e.DeviceName ?? e.Session?.DeviceName,
                ClientName = e.ClientName ?? e.Session?.Client,
            });

            lock (state)
            {
                Advance(state, position, now);
            }

            if (stopped && _active.TryRemove(key, out WatchState? done))
            {
                Save(done, now);
            }
        }
    }

    private static void Advance(WatchState state, long? positionTicks, DateTime now)
    {
        if (positionTicks is long position)
        {
            TimeSpan wall = now - state.LastReportUtc;
            TimeSpan moved = TimeSpan.FromTicks(position - state.LastPositionTicks);

            // Count only forward movement that real time can explain. Anything bigger is a seek.
            if (moved > TimeSpan.Zero && moved <= (wall * MaxSpeed) + JitterAllowance)
            {
                state.WatchedTicks += moved.Ticks;
            }

            state.LastPositionTicks = position;
            state.MaxPositionTicks = Math.Max(state.MaxPositionTicks, position);
        }

        state.LastReportUtc = now;
    }

    private void SweepAbandoned()
    {
        DateTime cutoff = DateTime.UtcNow - Abandoned;
        foreach (var pair in _active)
        {
            if (pair.Value.LastReportUtc < cutoff && _active.TryRemove(pair.Key, out WatchState? state))
            {
                Guard(() => Save(state, state.LastReportUtc));
            }
        }
    }

    private void Save(WatchState state, DateTime endedUtc)
    {
        TimeSpan watched = TimeSpan.FromTicks(state.WatchedTicks);
        if (watched < MinimumWatched)
        {
            return;
        }

        long runtimeTicks = state.Item.RunTimeTicks ?? 0;
        double share = runtimeTicks > 0 ? Math.Min(1.0, state.WatchedTicks / (double)runtimeTicks) : 0;

        var session = new SessionRecord
        {
            UserId = state.UserId,
            ItemId = ItemSnapshotBuilder.Id(state.Item.Id),
            Source = SessionSource.Live,
            StartedUtc = state.StartedUtc,
            EndedUtc = endedUtc,
            WatchedSeconds = (int)watched.TotalSeconds,
            PositionSeconds = (int)TimeSpan.FromTicks(state.MaxPositionTicks).TotalSeconds,
            RuntimeSeconds = runtimeTicks > 0 ? (int)TimeSpan.FromTicks(runtimeTicks).TotalSeconds : null,
            Percent = Math.Round(share, 4),
            Finished = share >= FinishedShare,
            DeviceName = state.DeviceName,
            ClientName = state.ClientName,
        };

        _database.AddSession(session, _snapshots.Build(state.Item));
        _logger.LogInformation(
            "[RaceToFinish] Recorded {Minutes:0.0} min of {Title} ({Percent:P0}{Finished})",
            watched.TotalMinutes,
            state.Item.Name,
            share,
            session.Finished ? ", finished" : string.Empty);
    }

    private sealed class WatchState
    {
        public string UserId { get; set; } = string.Empty;

        public BaseItem Item { get; set; } = null!;

        public DateTime StartedUtc { get; set; }

        public DateTime LastReportUtc { get; set; }

        public long LastPositionTicks { get; set; }

        public long MaxPositionTicks { get; set; }

        public long WatchedTicks { get; set; }

        public string? DeviceName { get; set; }

        public string? ClientName { get; set; }
    }
}
