using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.RaceToFinish.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.Tracking;

/// <summary>Result of an import run, shown on the admin page.</summary>
public sealed class ImportResult
{
    /// <summary>Gets or sets when it ran, UTC.</summary>
    public DateTime RanUtc { get; set; }

    /// <summary>Gets or sets rows from Jellyfin's per-user watch data.</summary>
    public int UserData { get; set; }

    /// <summary>Gets or sets rows from Jellyfin's activity log.</summary>
    public int ActivityLog { get; set; }

    /// <summary>Gets or sets rows from the Playback Reporting plugin.</summary>
    public int PlaybackReporting { get; set; }

    /// <summary>Gets or sets a value indicating whether a Playback Reporting database was found.</summary>
    public bool PlaybackReportingFound { get; set; }

    /// <summary>Gets or sets problems worth telling the admin about.</summary>
    public List<string> Notes { get; set; } = [];
}

/// <summary>
/// Brings in viewing history the server already has, for stats and the yearly recap. Every imported
/// row is marked with an "imported" source and a unique key, so it never counts toward a race and
/// running the import again updates rows instead of duplicating them. Hidden accounts are skipped.
/// </summary>
public class HistoryImporter
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IActivityManager _activityManager;
    private readonly IApplicationPaths _paths;
    private readonly HistoryDatabase _database;
    private readonly ItemSnapshotBuilder _snapshots;
    private readonly UserAccess _access;
    private readonly ILogger<HistoryImporter> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="HistoryImporter"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="activityManager">Activity log.</param>
    /// <param name="paths">Server paths.</param>
    /// <param name="database">History database.</param>
    /// <param name="snapshots">Snapshot builder.</param>
    /// <param name="access">User access.</param>
    /// <param name="logger">Logger.</param>
    public HistoryImporter(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IActivityManager activityManager,
        IApplicationPaths paths,
        HistoryDatabase database,
        ItemSnapshotBuilder snapshots,
        UserAccess access,
        ILogger<HistoryImporter> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _activityManager = activityManager;
        _paths = paths;
        _database = database;
        _snapshots = snapshots;
        _access = access;
        _logger = logger;
    }

    /// <summary>Runs every import source. Safe to run again at any time.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Counts per source.</returns>
    public async Task<ImportResult> RunAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new ImportResult { RanUtc = DateTime.UtcNow };
            // People who deleted their own data are never imported again.
            List<User> participants = _access.GetUsers()
                .Where(UserAccess.IsParticipant)
                .Where(u => !_database.IsImportBlocked(ItemSnapshotBuilder.Id(u.Id)))
                .ToList();
            var participantIds = participants.Select(u => u.Id).ToHashSet();

            // Import only what happened before this plugin started tracking. Anything later is already
            // recorded live, with real watch time, so importing it too would count it twice.
            DateTime cutoff = _database.TrackingStartedUtc;

            result.UserData = Safe(result, "Jellyfin watch data", () => ImportUserData(participants, cutoff, cancellationToken));
            result.ActivityLog = await SafeAsync(result, "Jellyfin activity log", () => ImportActivityLogAsync(participantIds, cutoff, cancellationToken)).ConfigureAwait(false);
            result.PlaybackReporting = Safe(result, "Playback Reporting", () => ImportPlaybackReporting(participantIds, cutoff, result));

            _database.SetMeta("last_import_utc", HistoryDatabase.Iso(result.RanUtc));
            _database.SetMeta(
                "last_import_counts",
                string.Create(CultureInfo.InvariantCulture, $"{result.UserData}|{result.ActivityLog}|{result.PlaybackReporting}"));
            _logger.LogInformation(
                "[RaceToFinish] History import: {UserData} from watch data, {Activity} from activity log, {Pbr} from Playback Reporting",
                result.UserData,
                result.ActivityLog,
                result.PlaybackReporting);
            return result;
        }
        finally
        {
            _running.Release();
        }
    }

    // ---------- Source 1: Jellyfin per-user watch data ----------
    // One row per played movie or episode: last played date, play count. Jellyfin keeps only the last play.
    private int ImportUserData(List<User> users, DateTime cutoff, CancellationToken cancellationToken)
    {
        int written = 0;
        foreach (User user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                IsPlayed = true,
                Recursive = true,
            };

            var rows = new List<(SessionRecord, ItemSnapshot?)>();
            foreach (BaseItem item in _libraryManager.GetItemList(query))
            {
                if (!ItemSnapshotBuilder.IsTrackable(item))
                {
                    continue;
                }

                var data = _userDataManager.GetUserData(user, item);
                if (data is null || !data.Played)
                {
                    continue;
                }

                int? runtime = item.RunTimeTicks is long ticks ? (int)TimeSpan.FromTicks(ticks).TotalSeconds : null;
                DateTime? last = data.LastPlayedDate is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

                // Last played after tracking began: the tracker has it. (Jellyfin keeps only the last play,
                // so an earlier viewing of something rewatched since then can't be recovered here.)
                if (last is DateTime lastPlayed && lastPlayed >= cutoff)
                {
                    continue;
                }
                string userId = ItemSnapshotBuilder.Id(user.Id);
                string itemId = ItemSnapshotBuilder.Id(item.Id);
                rows.Add((
                    new SessionRecord
                    {
                        UserId = userId,
                        ItemId = itemId,
                        Source = SessionSource.ImportedUserData,
                        StartedUtc = last is null || runtime is null ? last : last.Value.AddSeconds(-runtime.Value),
                        EndedUtc = last,
                        WatchedSeconds = runtime ?? 0,
                        RuntimeSeconds = runtime,
                        Percent = 1,
                        Finished = true,
                        PlayCount = data.PlayCount,
                        ImportKey = $"userdata:{userId}:{itemId}",
                    },
                    _snapshots.Build(item)));
            }

            written += _database.AddImported(rows);
        }

        return written;
    }

    // ---------- Source 2: Jellyfin activity log ----------
    // "VideoPlayback" (started) and "VideoPlaybackStopped" events, paired per user and item. Jellyfin keeps
    // these for 30 days by default (Dashboard > Logs > Activity log retention).
    private async Task<int> ImportActivityLogAsync(HashSet<Guid> participants, DateTime cutoff, CancellationToken cancellationToken)
    {
        // Jellyfin's Type filter is a partial match, so "VideoPlayback" returns both the start and the stop
        // events. One query, de-duplicated by entry ID, with the exact types checked here.
        var byId = new Dictionary<long, ActivityLogEntry>();
        int skip = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _activityManager.GetPagedResultAsync(new ActivityLogQuery { Type = "VideoPlayback", HasUserId = true, Skip = skip, Limit = 500 }).ConfigureAwait(false);
            foreach (var entry in page.Items)
            {
                byId[entry.Id] = entry;
            }

            skip += page.Items.Count;
            if (page.Items.Count == 0 || skip >= page.TotalRecordCount)
            {
                break;
            }
        }

        var rows = new List<(SessionRecord, ItemSnapshot?)>();
        foreach (var group in byId.Values
            .Where(e => e.Type is "VideoPlayback" or "VideoPlaybackStopped")
            .Where(e => participants.Contains(e.UserId) && Guid.TryParse(e.ItemId, out _))
            .GroupBy(e => (e.UserId, ItemId: Guid.Parse(e.ItemId!))))
        {
            var ordered = group.OrderBy(e => e.Date).ToList();
            BaseItem? item = _libraryManager.GetItemById(group.Key.ItemId);
            if (item is not null && !ItemSnapshotBuilder.IsTrackable(item))
            {
                continue;
            }

            ItemSnapshot? snapshot = item is null ? null : _snapshots.Build(item);
            long? runtimeTicks = item?.RunTimeTicks;
            DateTime? openStart = null;
            foreach (var entry in ordered)
            {
                DateTime when = DateTime.SpecifyKind(entry.Date, DateTimeKind.Utc);
                if (entry.Type == "VideoPlayback")
                {
                    openStart = when;
                    continue;
                }

                if (when >= cutoff)
                {
                    // Watched since tracking began: the tracker already has this one, more accurately.
                    openStart = null;
                    continue;
                }

                // A stop: pair it with the start before it. The log has no pause or seek information, so the
                // time between start and stop is an upper bound on time watched, capped at the runtime.
                TimeSpan span = openStart is DateTime start ? when - start : TimeSpan.Zero;
                if (runtimeTicks is long rt && span > TimeSpan.FromTicks(rt))
                {
                    span = TimeSpan.FromTicks(rt);
                }

                double share = runtimeTicks is long r && r > 0 ? Math.Min(1, span.Ticks / (double)r) : 0;
                rows.Add((
                    new SessionRecord
                    {
                        UserId = ItemSnapshotBuilder.Id(group.Key.UserId),
                        ItemId = ItemSnapshotBuilder.Id(group.Key.ItemId),
                        Source = SessionSource.ImportedActivityLog,
                        StartedUtc = openStart ?? when,
                        EndedUtc = when,
                        WatchedSeconds = (int)span.TotalSeconds,
                        RuntimeSeconds = runtimeTicks is long t ? (int)TimeSpan.FromTicks(t).TotalSeconds : null,
                        Percent = Math.Round(share, 4),
                        Finished = share >= WatchTracker.FinishedShare,
                        ImportKey = string.Create(CultureInfo.InvariantCulture, $"activity:{entry.Id}"),
                    },
                    snapshot));
                openStart = null;
            }
        }

        return _database.AddImported(rows);
    }

    // ---------- Source 3: Playback Reporting plugin ----------
    // Full session history with durations, if that plugin is installed. Read-only; its file is never changed.
    private int ImportPlaybackReporting(HashSet<Guid> participants, DateTime cutoff, ImportResult result)
    {
        string path = Path.Combine(_paths.DataPath, "playback_reporting.db");
        if (!File.Exists(path))
        {
            return 0;
        }

        result.PlaybackReportingFound = true;
        string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT rowid, DateCreated, UserId, ItemId, ItemType, ClientName, DeviceName, PlayDuration FROM PlaybackActivity " +
            "WHERE ItemType IN ('Movie', 'Episode')";
        using var reader = command.ExecuteReader();

        var rows = new List<(SessionRecord, ItemSnapshot?)>();
        var snapshotCache = new Dictionary<Guid, ItemSnapshot?>();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(2), out Guid userId) || !participants.Contains(userId) ||
                !Guid.TryParse(reader.GetString(3), out Guid itemId))
            {
                continue;
            }

            // Playback Reporting stores the server's local time.
            if (!DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime started))
            {
                continue;
            }

            if (!snapshotCache.TryGetValue(itemId, out ItemSnapshot? snapshot))
            {
                BaseItem? item = _libraryManager.GetItemById(itemId);
                snapshot = item is not null && ItemSnapshotBuilder.IsTrackable(item) ? _snapshots.Build(item) : null;
                snapshotCache[itemId] = snapshot;
            }

            int seconds = reader.IsDBNull(7) ? 0 : (int)Math.Max(0, reader.GetInt64(7));
            int? runtime = snapshot?.RuntimeSeconds;
            double share = runtime is int r && r > 0 ? Math.Min(1, seconds / (double)r) : 0;
            DateTime startedUtc = started.ToUniversalTime();
            if (startedUtc >= cutoff)
            {
                continue;
            }

            rows.Add((
                new SessionRecord
                {
                    UserId = ItemSnapshotBuilder.Id(userId),
                    ItemId = ItemSnapshotBuilder.Id(itemId),
                    Source = SessionSource.ImportedPlaybackReporting,
                    StartedUtc = startedUtc,
                    EndedUtc = startedUtc.AddSeconds(seconds),
                    WatchedSeconds = seconds,
                    RuntimeSeconds = runtime,
                    Percent = Math.Round(share, 4),
                    Finished = share >= WatchTracker.FinishedShare,
                    ClientName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    DeviceName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ImportKey = string.Create(CultureInfo.InvariantCulture, $"pbr:{reader.GetInt64(0)}"),
                },
                snapshot));
        }

        return _database.AddImported(rows);
    }

    private int Safe(ImportResult result, string source, Func<int> run)
    {
        try
        {
            return run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[RaceToFinish] Import from {Source} failed", source);
            result.Notes.Add($"{source}: import failed ({ex.GetBaseException().Message}).");
            return 0;
        }
    }

    private async Task<int> SafeAsync(ImportResult result, string source, Func<Task<int>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[RaceToFinish] Import from {Source} failed", source);
            result.Notes.Add($"{source}: import failed ({ex.GetBaseException().Message}).");
            return 0;
        }
    }
}
