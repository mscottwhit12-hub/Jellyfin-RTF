using System.Globalization;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.Data;

/// <summary>Row returned for the recent-activity list and the export.</summary>
public sealed class ActivityRow
{
    /// <summary>Gets or sets the session.</summary>
    public SessionRecord Session { get; set; } = new();

    /// <summary>Gets or sets what was watched, if known.</summary>
    public ItemSnapshot? Item { get; set; }
}

/// <summary>Per-user totals.</summary>
public sealed class HistorySummary
{
    /// <summary>Gets or sets the sessions recorded live by the tracker.</summary>
    public int LiveSessions { get; set; }

    /// <summary>Gets or sets the seconds watched in live sessions.</summary>
    public long LiveSeconds { get; set; }

    /// <summary>Gets or sets the sessions brought in by imports.</summary>
    public int ImportedSessions { get; set; }

    /// <summary>Gets or sets the earliest session date known, UTC.</summary>
    public DateTime? FirstUtc { get; set; }
}

/// <summary>
/// The viewing-history database: one SQLite file in the server's data folder.
/// Uses the SQLite library Jellyfin itself ships, so the plugin adds no native files.
/// All times are stored as UTC ISO-8601 text.
/// </summary>
public sealed class HistoryDatabase
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _connectionString = string.Empty;
    private readonly object _lock = new();

    /// <summary>Initializes a new instance of the <see cref="HistoryDatabase"/> class.</summary>
    /// <param name="paths">Server paths.</param>
    /// <param name="logger">Logger.</param>
    /// <remarks>
    /// Never throws: a problem with this database must not stop Jellyfin from starting. A damaged file is set
    /// aside and replaced; any other failure leaves the plugin running without history (see <see cref="Error"/>).
    /// </remarks>
    public HistoryDatabase(IApplicationPaths paths, ILogger<HistoryDatabase> logger)
    {
        DatabasePath = Path.Combine(paths.DataPath, "race-to-finish", "history.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Cache = SqliteCacheMode.Private, Pooling = false }.ToString();
            try
            {
                EnsureSchema();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
            {
                // 11 = SQLITE_CORRUPT, 26 = SQLITE_NOTADB. Keep the damaged file for recovery and start fresh.
                string aside = DatabasePath + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                File.Move(DatabasePath, aside);
                logger.LogError(ex, "[RaceToFinish] History database was damaged; moved it to {Aside} and started a new one", aside);
                EnsureSchema();
            }

            logger.LogInformation("[RaceToFinish] Viewing history database at {Path}", DatabasePath);
        }
        catch (Exception ex)
        {
            Error = "The viewing history database could not be opened (" + ex.GetBaseException().Message + "). Watch tracking is off until this is fixed.";
            logger.LogError(ex, "[RaceToFinish] {Error}", Error);
        }
    }

    /// <summary>Gets the database file path.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets why the database is unavailable, or null when it works.</summary>
    public string? Error { get; }

    /// <summary>
    /// Gets when live tracking began (when this database was first created). Imports only bring in history
    /// from before this moment, so nothing is counted twice.
    /// </summary>
    public DateTime TrackingStartedUtc => GetMeta("tracking_started_utc") is string text ? ParseIso(text) : DateTime.UtcNow;

    // ---------- Writes ----------

    /// <summary>Saves (or refreshes) what an item is.</summary>
    /// <param name="item">Snapshot.</param>
    public void UpsertItem(ItemSnapshot item)
    {
        lock (_lock)
        {
            using var connection = Open();
            UpsertItem(connection, null, item);
        }
    }

    /// <summary>
    /// Saves a live session. If it continues an unfinished viewing of the same item by the same person (it starts
    /// near where that one stopped, within <paramref name="resumeWindow"/>), the two become one viewing: the parts
    /// seen are combined, so pausing, switching devices or a restarted stream never split a watch.
    /// </summary>
    /// <param name="session">The session; Segments, Covered, Percent and Finished are recomputed here.</param>
    /// <param name="item">What was watched.</param>
    /// <param name="resumeWindow">How long after the earlier session ended a continuation may start.</param>
    /// <param name="resumeToleranceSeconds">How far from the earlier stop position the continuation may start.</param>
    /// <param name="minimumSeconds">A brand-new session shorter than this is not kept.</param>
    /// <param name="finishedShare">Share of the runtime that must be seen to count as finished.</param>
    /// <returns>What happened.</returns>
    public SaveOutcome SaveLiveSession(SessionRecord session, ItemSnapshot? item, TimeSpan resumeWindow, int resumeToleranceSeconds, int minimumSeconds, double finishedShare)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            SessionRecord? previous = null;
            if (session.StartedUtc is DateTime started && session.StartPositionSeconds is int startPosition)
            {
                using var find = Command(
                    connection,
                    tx,
                    "SELECT id, watched_seconds, segments, started_utc, device_name, client_name FROM sessions " +
                    "WHERE user_id = $u AND item_id = $i AND source = 'live' AND finished = 0 AND ended_utc >= $since " +
                    "AND ABS(COALESCE(position_seconds, 0) - $pos) <= $tol ORDER BY ended_utc DESC LIMIT 1",
                    ("$u", session.UserId),
                    ("$i", session.ItemId),
                    ("$since", Iso(started - resumeWindow)),
                    ("$pos", startPosition),
                    ("$tol", resumeToleranceSeconds));
                using var reader = find.ExecuteReader();
                if (reader.Read())
                {
                    previous = new SessionRecord
                    {
                        Id = reader.GetInt64(0),
                        WatchedSeconds = reader.GetInt32(1),
                        Segments = reader.IsDBNull(2) ? [] : JsonSerializer.Deserialize<List<int[]>>(reader.GetString(2), Json) ?? [],
                        StartedUtc = reader.IsDBNull(3) ? null : ParseIso(reader.GetString(3)),
                        DeviceName = NullableString(reader, 4),
                        ClientName = NullableString(reader, 5),
                    };
                }
            }

            if (previous is null && session.WatchedSeconds < minimumSeconds)
            {
                return SaveOutcome.Skipped;
            }

            if (previous is not null)
            {
                session.Id = previous.Id;
                session.WatchedSeconds += previous.WatchedSeconds;
                session.Segments = previous.Segments.Concat(session.Segments).ToList();
                session.StartedUtc = previous.StartedUtc ?? session.StartedUtc;
                session.StartPositionSeconds = null; // keep the original start position
            }

            session.Segments = Coverage.Merge(session.Segments);
            session.CoveredSeconds = Coverage.Length(session.Segments);
            double share = session.RuntimeSeconds is int runtime && runtime > 0 ? Math.Min(1.0, session.CoveredSeconds.Value / (double)runtime) : 0;
            session.Percent = Math.Round(share, 4);
            session.Finished = share >= finishedShare;

            if (item is not null)
            {
                UpsertItem(connection, tx, item);
            }

            if (previous is null)
            {
                InsertOrUpdateSession(connection, tx, session);
            }
            else
            {
                Execute(
                    connection,
                    tx,
                    "UPDATE sessions SET ended_utc = $end, watched_seconds = $watched, position_seconds = $pos, covered_seconds = $covered, " +
                    "segments = $segments, percent = $pct, finished = $fin, device_name = $dev, client_name = $client WHERE id = $id",
                    ("$end", session.EndedUtc is null ? null : Iso(session.EndedUtc.Value)),
                    ("$watched", session.WatchedSeconds),
                    ("$pos", session.PositionSeconds),
                    ("$covered", session.CoveredSeconds),
                    ("$segments", JsonSerializer.Serialize(session.Segments, Json)),
                    ("$pct", session.Percent),
                    ("$fin", session.Finished ? 1 : 0),
                    ("$dev", session.DeviceName ?? previous.DeviceName),
                    ("$client", session.ClientName ?? previous.ClientName),
                    ("$id", previous.Id));
            }

            tx.Commit();
            return previous is null ? SaveOutcome.Added : SaveOutcome.Continued;
        }
    }

    /// <summary>Adds many imported sessions in one transaction. Rows with an existing import key are updated, not duplicated.</summary>
    /// <param name="rows">Sessions and items.</param>
    /// <returns>How many rows were written.</returns>
    public int AddImported(IReadOnlyList<(SessionRecord Session, ItemSnapshot? Item)> rows)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var itemsSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (session, item) in rows)
            {
                if (item is not null && itemsSeen.Add(item.ItemId))
                {
                    UpsertItem(connection, tx, item);
                }

                InsertOrUpdateSession(connection, tx, session);
            }

            tx.Commit();
            return rows.Count;
        }
    }

    /// <summary>Deletes everything stored about one user.</summary>
    /// <param name="userId">User ID (N format).</param>
    /// <param name="blockReimport">
    /// True when the person deleted their own data: remember it so imports never bring their older history back.
    /// (Tracking from now on continues as normal.) False for leftovers of deleted accounts.
    /// </param>
    /// <returns>Sessions deleted.</returns>
    public int DeleteUser(string userId, bool blockReimport)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            int deleted = Execute(connection, tx, "DELETE FROM sessions WHERE user_id = $u", ("$u", userId));
            Execute(connection, tx, "DELETE FROM user_prefs WHERE user_id = $u", ("$u", userId));
            if (blockReimport)
            {
                Execute(
                    connection,
                    tx,
                    "INSERT INTO user_prefs(user_id, share_with_admin, import_blocked, updated_utc) VALUES($u, 0, 1, $t)",
                    ("$u", userId),
                    ("$t", Iso(DateTime.UtcNow)));
            }

            tx.Commit();
            return deleted;
        }
    }

    /// <summary>Gets whether a user has deleted their data, so imports must skip them.</summary>
    /// <param name="userId">User ID (N format).</param>
    /// <returns>True when blocked.</returns>
    public bool IsImportBlocked(string userId)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT import_blocked FROM user_prefs WHERE user_id = $u", ("$u", userId));
            return command.ExecuteScalar() is long value && value == 1;
        }
    }

    /// <summary>Sets whether a user shares their stats with the admin.</summary>
    /// <param name="userId">User ID (N format).</param>
    /// <param name="share">True to share.</param>
    public void SetShareWithAdmin(string userId, bool share)
    {
        lock (_lock)
        {
            using var connection = Open();
            Execute(
                connection,
                null,
                "INSERT INTO user_prefs(user_id, share_with_admin, updated_utc) VALUES($u, $s, $t) " +
                "ON CONFLICT(user_id) DO UPDATE SET share_with_admin = excluded.share_with_admin, updated_utc = excluded.updated_utc",
                ("$u", userId),
                ("$s", share ? 1 : 0),
                ("$t", Iso(DateTime.UtcNow)));
        }
    }

    /// <summary>Stores a small value (e.g. when the last import ran).</summary>
    /// <param name="key">Key.</param>
    /// <param name="value">Value.</param>
    public void SetMeta(string key, string value)
    {
        lock (_lock)
        {
            using var connection = Open();
            Execute(connection, null, "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
        }
    }

    // ---------- Reads ----------

    /// <summary>Reads a small value.</summary>
    /// <param name="key">Key.</param>
    /// <returns>The value, or null.</returns>
    public string? GetMeta(string key)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT value FROM meta WHERE key = $k", ("$k", key));
            return command.ExecuteScalar() as string;
        }
    }

    /// <summary>Gets whether a user shares their stats with the admin. Private (false) unless they opted in.</summary>
    /// <param name="userId">User ID (N format).</param>
    /// <returns>True when shared.</returns>
    public bool GetShareWithAdmin(string userId)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT share_with_admin FROM user_prefs WHERE user_id = $u", ("$u", userId));
            return command.ExecuteScalar() is long value && value == 1;
        }
    }

    /// <summary>Gets a user's totals.</summary>
    /// <param name="userId">User ID (N format).</param>
    /// <returns>Totals.</returns>
    public HistorySummary GetSummary(string userId)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = Command(
                connection,
                null,
                "SELECT " +
                "SUM(CASE WHEN source = 'live' THEN 1 ELSE 0 END), " +
                "SUM(CASE WHEN source = 'live' THEN watched_seconds ELSE 0 END), " +
                "SUM(CASE WHEN source <> 'live' THEN 1 ELSE 0 END), " +
                "MIN(started_utc) FROM sessions WHERE user_id = $u",
                ("$u", userId));
            using var reader = command.ExecuteReader();
            reader.Read();
            return new HistorySummary
            {
                LiveSessions = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                LiveSeconds = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                ImportedSessions = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                FirstUtc = reader.IsDBNull(3) ? null : ParseIso(reader.GetString(3)),
            };
        }
    }

    /// <summary>Gets a user's sessions, newest first.</summary>
    /// <param name="userId">User ID (N format).</param>
    /// <param name="limit">Maximum rows, or null for all.</param>
    /// <returns>Sessions with their items.</returns>
    public IReadOnlyList<ActivityRow> GetSessions(string userId, int? limit)
    {
        lock (_lock)
        {
            using var connection = Open();
            string sql =
                "SELECT s.id, s.user_id, s.item_id, s.source, s.started_utc, s.ended_utc, s.watched_seconds, s.position_seconds, " +
                "s.runtime_seconds, s.percent, s.finished, s.play_count, s.device_name, s.client_name, s.import_key, " +
                "i.item_type, i.title, i.series_id, i.series_title, i.season_number, i.episode_number, i.year, i.runtime_seconds, " +
                "i.tmdb_id, i.imdb_id, i.series_tmdb_id, i.original_language, i.genres, i.studios, i.countries, i.directors, i.writers, i.cast_list, " +
                "s.start_position_seconds, s.covered_seconds, s.segments " +
                "FROM sessions s LEFT JOIN items i ON i.item_id = s.item_id WHERE s.user_id = $u " +
                "ORDER BY COALESCE(s.ended_utc, s.started_utc) DESC, s.id DESC" + (limit is null ? string.Empty : " LIMIT $l");
            using var command = limit is null
                ? Command(connection, null, sql, ("$u", userId))
                : Command(connection, null, sql, ("$u", userId), ("$l", limit.Value));
            using var reader = command.ExecuteReader();
            var rows = new List<ActivityRow>();
            while (reader.Read())
            {
                var session = new SessionRecord
                {
                    Id = reader.GetInt64(0),
                    UserId = reader.GetString(1),
                    ItemId = reader.GetString(2),
                    Source = reader.GetString(3),
                    StartedUtc = reader.IsDBNull(4) ? null : ParseIso(reader.GetString(4)),
                    EndedUtc = reader.IsDBNull(5) ? null : ParseIso(reader.GetString(5)),
                    WatchedSeconds = reader.GetInt32(6),
                    PositionSeconds = NullableInt(reader, 7),
                    RuntimeSeconds = NullableInt(reader, 8),
                    Percent = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                    Finished = reader.GetInt32(10) == 1,
                    PlayCount = NullableInt(reader, 11),
                    DeviceName = NullableString(reader, 12),
                    ClientName = NullableString(reader, 13),
                    ImportKey = NullableString(reader, 14),
                    StartPositionSeconds = NullableInt(reader, 33),
                    CoveredSeconds = NullableInt(reader, 34),
                    Segments = FromJson<List<int[]>>(reader, 35) ?? [],
                };
                ItemSnapshot? item = reader.IsDBNull(15) ? null : new ItemSnapshot
                {
                    ItemId = session.ItemId,
                    ItemType = reader.GetString(15),
                    Title = NullableString(reader, 16) ?? string.Empty,
                    SeriesId = NullableString(reader, 17),
                    SeriesTitle = NullableString(reader, 18),
                    SeasonNumber = NullableInt(reader, 19),
                    EpisodeNumber = NullableInt(reader, 20),
                    Year = NullableInt(reader, 21),
                    RuntimeSeconds = NullableInt(reader, 22),
                    TmdbId = NullableString(reader, 23),
                    ImdbId = NullableString(reader, 24),
                    SeriesTmdbId = NullableString(reader, 25),
                    OriginalLanguage = NullableString(reader, 26),
                    Genres = FromJson<List<string>>(reader, 27) ?? [],
                    Studios = FromJson<List<string>>(reader, 28) ?? [],
                    Countries = FromJson<List<string>>(reader, 29) ?? [],
                    Directors = FromJson<List<CreditRecord>>(reader, 30) ?? [],
                    Writers = FromJson<List<CreditRecord>>(reader, 31) ?? [],
                    Cast = FromJson<List<CreditRecord>>(reader, 32) ?? [],
                };
                rows.Add(new ActivityRow { Session = session, Item = item });
            }

            return rows;
        }
    }

    /// <summary>Gets every user ID that has stored sessions, with how many.</summary>
    /// <returns>User ID to session count.</returns>
    public IReadOnlyDictionary<string, int> GetSessionCountsByUser()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT user_id, COUNT(*) FROM sessions GROUP BY user_id");
            using var reader = command.ExecuteReader();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                counts[reader.GetString(0)] = reader.GetInt32(1);
            }

            return counts;
        }
    }

    // ---------- Helpers ----------

    /// <summary>Formats a time as UTC ISO-8601.</summary>
    /// <param name="value">Time.</param>
    /// <returns>Text.</returns>
    public static string Iso(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static DateTime ParseIso(string text)
    {
        return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);

    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static T? FromJson<T>(SqliteDataReader reader, int index)
        where T : class
    {
        if (reader.IsDBNull(index))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(reader.GetString(index), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SqliteConnection Open()
    {
        if (Error is not null)
        {
            throw new InvalidOperationException(Error);
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, tx, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static void UpsertItem(SqliteConnection connection, SqliteTransaction? tx, ItemSnapshot item)
    {
        Execute(
            connection,
            tx,
            "INSERT INTO items(item_id, item_type, title, series_id, series_title, season_number, episode_number, year, runtime_seconds, " +
            "tmdb_id, imdb_id, series_tmdb_id, original_language, genres, studios, countries, directors, writers, cast_list, updated_utc) " +
            "VALUES($id, $type, $title, $sid, $stitle, $season, $episode, $year, $runtime, $tmdb, $imdb, $stmdb, $lang, $genres, $studios, $countries, $directors, $writers, $cast, $updated) " +
            "ON CONFLICT(item_id) DO UPDATE SET item_type = excluded.item_type, title = excluded.title, series_id = excluded.series_id, " +
            "series_title = excluded.series_title, season_number = excluded.season_number, episode_number = excluded.episode_number, " +
            "year = excluded.year, runtime_seconds = excluded.runtime_seconds, tmdb_id = excluded.tmdb_id, imdb_id = excluded.imdb_id, " +
            "series_tmdb_id = excluded.series_tmdb_id, original_language = excluded.original_language, genres = excluded.genres, " +
            "studios = excluded.studios, countries = excluded.countries, directors = excluded.directors, writers = excluded.writers, " +
            "cast_list = excluded.cast_list, updated_utc = excluded.updated_utc",
            ("$id", item.ItemId),
            ("$type", item.ItemType),
            ("$title", item.Title),
            ("$sid", item.SeriesId),
            ("$stitle", item.SeriesTitle),
            ("$season", item.SeasonNumber),
            ("$episode", item.EpisodeNumber),
            ("$year", item.Year),
            ("$runtime", item.RuntimeSeconds),
            ("$tmdb", item.TmdbId),
            ("$imdb", item.ImdbId),
            ("$stmdb", item.SeriesTmdbId),
            ("$lang", item.OriginalLanguage),
            ("$genres", JsonSerializer.Serialize(item.Genres, Json)),
            ("$studios", JsonSerializer.Serialize(item.Studios, Json)),
            ("$countries", JsonSerializer.Serialize(item.Countries, Json)),
            ("$directors", JsonSerializer.Serialize(item.Directors, Json)),
            ("$writers", JsonSerializer.Serialize(item.Writers, Json)),
            ("$cast", JsonSerializer.Serialize(item.Cast, Json)),
            ("$updated", Iso(DateTime.UtcNow)));
    }

    private static void InsertOrUpdateSession(SqliteConnection connection, SqliteTransaction? tx, SessionRecord s)
    {
        const string Columns = "user_id, item_id, source, started_utc, ended_utc, watched_seconds, position_seconds, runtime_seconds, percent, finished, play_count, device_name, client_name, import_key, start_position_seconds, covered_seconds, segments";
        const string Values = "$u, $i, $src, $start, $end, $watched, $pos, $runtime, $pct, $fin, $plays, $dev, $client, $key, $startpos, $covered, $segments";
        string sql = s.ImportKey is null
            ? $"INSERT INTO sessions({Columns}) VALUES({Values})"
            : $"INSERT INTO sessions({Columns}) VALUES({Values}) ON CONFLICT(import_key) WHERE import_key IS NOT NULL DO UPDATE SET " +
              "started_utc = excluded.started_utc, ended_utc = excluded.ended_utc, watched_seconds = excluded.watched_seconds, " +
              "position_seconds = excluded.position_seconds, runtime_seconds = excluded.runtime_seconds, percent = excluded.percent, " +
              "finished = excluded.finished, play_count = excluded.play_count, device_name = excluded.device_name, client_name = excluded.client_name";
        Execute(
            connection,
            tx,
            sql,
            ("$u", s.UserId),
            ("$i", s.ItemId),
            ("$src", s.Source),
            ("$start", s.StartedUtc is null ? null : Iso(s.StartedUtc.Value)),
            ("$end", s.EndedUtc is null ? null : Iso(s.EndedUtc.Value)),
            ("$watched", s.WatchedSeconds),
            ("$pos", s.PositionSeconds),
            ("$runtime", s.RuntimeSeconds),
            ("$pct", s.Percent),
            ("$fin", s.Finished ? 1 : 0),
            ("$plays", s.PlayCount),
            ("$dev", s.DeviceName),
            ("$client", s.ClientName),
            ("$key", s.ImportKey),
            ("$startpos", s.StartPositionSeconds),
            ("$covered", s.CoveredSeconds),
            ("$segments", s.Segments.Count == 0 ? null : JsonSerializer.Serialize(s.Segments, Json)));
    }

    private void EnsureSchema()
    {
        lock (_lock)
        {
            using var connection = Open();
            using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                wal.ExecuteNonQuery();
            }

            using var tx = connection.BeginTransaction();
            Execute(connection, tx, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)");
            Execute(
                connection,
                tx,
                "CREATE TABLE IF NOT EXISTS items(" +
                "item_id TEXT PRIMARY KEY, item_type TEXT NOT NULL, title TEXT, series_id TEXT, series_title TEXT, season_number INTEGER, " +
                "episode_number INTEGER, year INTEGER, runtime_seconds INTEGER, tmdb_id TEXT, imdb_id TEXT, series_tmdb_id TEXT, " +
                "original_language TEXT, genres TEXT, studios TEXT, countries TEXT, directors TEXT, writers TEXT, cast_list TEXT, updated_utc TEXT)");
            Execute(
                connection,
                tx,
                "CREATE TABLE IF NOT EXISTS sessions(" +
                "id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL, item_id TEXT NOT NULL, source TEXT NOT NULL, " +
                "started_utc TEXT, ended_utc TEXT, watched_seconds INTEGER NOT NULL, position_seconds INTEGER, runtime_seconds INTEGER, " +
                "percent REAL, finished INTEGER NOT NULL, play_count INTEGER, device_name TEXT, client_name TEXT, import_key TEXT, " +
                "start_position_seconds INTEGER, covered_seconds INTEGER, segments TEXT)");
            Execute(connection, tx, "CREATE INDEX IF NOT EXISTS sessions_resume ON sessions(user_id, item_id, source, finished, ended_utc)");
            Execute(connection, tx, "CREATE INDEX IF NOT EXISTS sessions_user_time ON sessions(user_id, started_utc)");
            Execute(connection, tx, "CREATE INDEX IF NOT EXISTS sessions_item ON sessions(item_id)");
            Execute(connection, tx, "CREATE UNIQUE INDEX IF NOT EXISTS sessions_import_key ON sessions(import_key) WHERE import_key IS NOT NULL");
            Execute(connection, tx, "CREATE TABLE IF NOT EXISTS user_prefs(user_id TEXT PRIMARY KEY, share_with_admin INTEGER NOT NULL DEFAULT 0, import_blocked INTEGER NOT NULL DEFAULT 0, updated_utc TEXT)");
            Execute(connection, tx, "INSERT INTO meta(key, value) VALUES('schema_version', $v) ON CONFLICT(key) DO NOTHING", ("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture)));
            Execute(connection, tx, "INSERT INTO meta(key, value) VALUES('tracking_started_utc', $t) ON CONFLICT(key) DO NOTHING", ("$t", Iso(DateTime.UtcNow)));
            tx.Commit();
        }
    }
}
