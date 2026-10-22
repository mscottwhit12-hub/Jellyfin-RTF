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
/// Only real watching counts. Each report from the app adds the stretch the playhead moved, but only if real
/// time can explain it (up to 2.5x playback speed); jumps forward (seeking), jumps back and "mark as played"
/// add nothing. The stretches seen are kept as ranges, so rewatching a part never counts twice, and a viewing
/// is "finished" once 85% of the item has been seen.
///
/// A viewing that is interrupted (a long pause, switching from the TV to the phone, the app restarting the
/// stream after an audio or subtitle change) continues as the same viewing when the person picks it up again
/// near where they stopped, within 12 hours.
/// </summary>
public sealed class WatchTracker : IHostedService, IDisposable
{
    /// <summary>Share of the item that must be seen for a viewing to count as finished.</summary>
    public const double FinishedShare = 0.85;

    /// <summary>A brand-new viewing shorter than this is not saved (a peek, not a watch).</summary>
    public static readonly TimeSpan MinimumWatched = TimeSpan.FromSeconds(60);

    /// <summary>A session with no reports for this long is saved as it stands (app closed, TV off, long pause).</summary>
    public static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(30);

    /// <summary>How long after stopping someone can pick a viewing up again and have it continue.</summary>
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromHours(12);

    /// <summary>How far from the stop position a continuation may start (resume points are approximate).</summary>
    public const int ResumeToleranceSeconds = 180;

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
        if (_database.Error is not null)
        {
            _logger.LogError("[RaceToFinish] Watch tracker not started: {Error}", _database.Error);
            return Task.CompletedTask;
        }

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

        // Server shutting down: save what has been watched so far. It continues after the restart.
        foreach (string key in _active.Keys.ToList())
        {
            if (_active.TryRemove(key, out WatchState? state))
            {
                Guard(() => Save(state, state.LastReportUtc));
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

            string personItem = $"|{user.Id:N}|{item!.Id:N}";
            string key = sessionKey + personItem;
            bool created = false;
            WatchState state = _active.GetOrAdd(key, _ =>
            {
                created = true;
                return new WatchState
                {
                    UserId = ItemSnapshotBuilder.Id(user.Id),
                    Item = item,
                    StartedUtc = now,
                    LastReportUtc = now,
                    StartPositionTicks = position ?? 0,
                    LastPositionTicks = position ?? 0,
                    DeviceName = e.DeviceName ?? e.Session?.DeviceName,
                    ClientName = e.ClientName ?? e.Session?.Client,
                };
            });

            if (created)
            {
                // The same person started the same item on another stream (another device, or the app restarted
                // the stream). Save the old one now so this one can continue it.
                foreach (string other in _active.Keys.Where(k => k != key && k.EndsWith(personItem, StringComparison.Ordinal)).ToList())
                {
                    if (_active.TryRemove(other, out WatchState? previous))
                    {
                        Save(previous, previous.LastReportUtc);
                    }
                }
            }

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
                state.Segments.Add([Seconds(state.LastPositionTicks), Seconds(position)]);
            }

            state.LastPositionTicks = position;
        }

        state.LastReportUtc = now;
    }

    private static int Seconds(long ticks) => (int)Math.Round(TimeSpan.FromTicks(ticks).TotalSeconds);

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
        SessionRecord session;
        lock (state)
        {
            session = Snapshot(state, endedUtc);
        }

        // Skip the database entirely for a brief peek that nothing could continue.
        if (session.WatchedSeconds == 0)
        {
            return;
        }

        SaveOutcome outcome = _database.SaveLiveSession(
            session,
            _snapshots.Build(state.Item),
            ResumeWindow,
            ResumeToleranceSeconds,
            (int)MinimumWatched.TotalSeconds,
            FinishedShare);

        if (outcome != SaveOutcome.Skipped)
        {
            _logger.LogInformation(
                "[RaceToFinish] {Outcome} viewing of {Title}: {Minutes:0.0} min watched, {Percent:P0} seen{Finished}",
                outcome == SaveOutcome.Continued ? "Continued" : "Recorded",
                state.Item.Name,
                session.WatchedSeconds / 60.0,
                session.Percent ?? 0,
                session.Finished ? ", finished" : string.Empty);
        }
    }

    private static SessionRecord Snapshot(WatchState state, DateTime endedUtc)
    {
        long runtimeTicks = state.Item.RunTimeTicks ?? 0;
        return new SessionRecord
        {
            UserId = state.UserId,
            ItemId = ItemSnapshotBuilder.Id(state.Item.Id),
            Source = SessionSource.Live,
            StartedUtc = state.StartedUtc,
            EndedUtc = endedUtc,
            WatchedSeconds = (int)TimeSpan.FromTicks(state.WatchedTicks).TotalSeconds,
            StartPositionSeconds = Seconds(state.StartPositionTicks),
            PositionSeconds = Seconds(state.LastPositionTicks),
            RuntimeSeconds = runtimeTicks > 0 ? Seconds(runtimeTicks) : null,
            Segments = state.Segments.ToList(),
            DeviceName = state.DeviceName,
            ClientName = state.ClientName,
        };
    }

    private sealed class WatchState
    {
        public string UserId { get; set; } = string.Empty;

        public BaseItem Item { get; set; } = null!;

        public DateTime StartedUtc { get; set; }

        public DateTime LastReportUtc { get; set; }

        public long StartPositionTicks { get; set; }

        public long LastPositionTicks { get; set; }

        public long WatchedTicks { get; set; }

        public List<int[]> Segments { get; } = [];

        public string? DeviceName { get; set; }

        public string? ClientName { get; set; }
    }
}
