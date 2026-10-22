using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.RaceToFinish.Data;
using Jellyfin.Plugin.RaceToFinish.Tracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RaceToFinish.Api;

/// <summary>Body for changing the share-with-admin setting.</summary>
public sealed class PrivacyRequest
{
    /// <summary>Gets or sets a value indicating whether the admin may see this user's viewing stats.</summary>
    public bool ShareWithAdmin { get; set; }
}

/// <summary>
/// Viewing history endpoints.
///
/// Privacy rules, enforced here on the server (the web page cannot get around them):
/// - /Me/... only ever returns the signed-in user's own data.
/// - Accounts hidden from the login screen get 403 (they are not part of the plugin), except that they can
///   still export or delete anything stored about them.
/// - The admin sees a user's history only if that user turned on "share with admin". Without it the admin
///   sees no counts and no rows, only that the user exists.
/// </summary>
[ApiController]
[Route("RaceToFinish")]
[Authorize]
public class HistoryController : ControllerBase
{
    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HistoryDatabase _database;
    private readonly HistoryImporter _importer;
    private readonly UserAccess _access;
    private readonly WatchTracker _tracker;

    /// <summary>Initializes a new instance of the <see cref="HistoryController"/> class.</summary>
    /// <param name="database">History database.</param>
    /// <param name="importer">Importer.</param>
    /// <param name="access">User access.</param>
    /// <param name="tracker">Watch tracker.</param>
    public HistoryController(HistoryDatabase database, HistoryImporter importer, UserAccess access, WatchTracker tracker)
    {
        _database = database;
        _importer = importer;
        _access = access;
        _tracker = tracker;
    }

    // ---------- The signed-in user's own data ----------

    /// <summary>The signed-in user's totals and privacy setting.</summary>
    /// <returns>Summary.</returns>
    [HttpGet("Me")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> GetMe()
    {
        if (!TryGetParticipant(out User? user, out ActionResult? denied))
        {
            return denied!;
        }

        string id = ItemSnapshotBuilder.Id(user!.Id);
        HistorySummary summary = _database.GetSummary(id);
        return new
        {
            userId = id,
            name = user.Username,
            shareWithAdmin = _database.GetShareWithAdmin(id),
            liveSessions = summary.LiveSessions,
            liveHours = Math.Round(summary.LiveSeconds / 3600.0, 1),
            importedSessions = summary.ImportedSessions,
            firstUtc = summary.FirstUtc,
        };
    }

    /// <summary>The signed-in user's most recent sessions.</summary>
    /// <param name="limit">How many (1 to 200).</param>
    /// <returns>Sessions, newest first.</returns>
    [HttpGet("Me/Activity")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> GetMyActivity([FromQuery] int limit = 25)
    {
        if (!TryGetParticipant(out User? user, out ActionResult? denied))
        {
            return denied!;
        }

        return _database.GetSessions(ItemSnapshotBuilder.Id(user!.Id), Math.Clamp(limit, 1, 200)).Select(ToDto).ToList();
    }

    /// <summary>Turns sharing with the admin on or off.</summary>
    /// <param name="request">New setting.</param>
    /// <returns>The setting now in effect.</returns>
    [HttpPut("Me/Privacy")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> SetMyPrivacy([FromBody] PrivacyRequest request)
    {
        if (!TryGetParticipant(out User? user, out ActionResult? denied))
        {
            return denied!;
        }

        string id = ItemSnapshotBuilder.Id(user!.Id);
        _database.SetShareWithAdmin(id, request.ShareWithAdmin);
        return new { shareWithAdmin = _database.GetShareWithAdmin(id) };
    }

    /// <summary>Downloads everything stored about the signed-in user, as JSON.</summary>
    /// <returns>A file.</returns>
    [HttpGet("Me/Export")]
    public ActionResult ExportMyData()
    {
        User? user = _access.GetCaller(User);
        if (user is null)
        {
            return Unauthorized();
        }

        string id = ItemSnapshotBuilder.Id(user.Id);
        var export = new
        {
            exportedUtc = DateTime.UtcNow,
            user = user.Username,
            userId = id,
            shareWithAdmin = _database.GetShareWithAdmin(id),
            sessions = _database.GetSessions(id, null).Select(r => new { session = r.Session, item = r.Item }),
        };
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(export, ExportJson));
        return File(bytes, MediaTypeNames.Application.Json, $"race-to-finish-{user.Username}-{DateTime.UtcNow:yyyy-MM-dd}.json");
    }

    /// <summary>Deletes everything stored about the signed-in user. Cannot be undone.</summary>
    /// <returns>How many sessions were deleted.</returns>
    [HttpDelete("Me/Data")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> DeleteMyData()
    {
        User? user = _access.GetCaller(User);
        if (user is null)
        {
            return Unauthorized();
        }

        return new { deletedSessions = _database.DeleteUser(ItemSnapshotBuilder.Id(user.Id), blockReimport: true) };
    }

    // ---------- Admin ----------

    /// <summary>Overview for the admin page. Counts appear only for users who share, or accounts that were deleted.</summary>
    /// <returns>Overview.</returns>
    [HttpGet("Admin/History")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> GetAdminOverview()
    {
        IReadOnlyDictionary<string, int> counts = _database.GetSessionCountsByUser();
        var users = _access.GetUsers().ToList();
        var known = users.Select(u => ItemSnapshotBuilder.Id(u.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = users
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u =>
            {
                string id = ItemSnapshotBuilder.Id(u.Id);
                bool sharing = _database.GetShareWithAdmin(id);
                return new
                {
                    userId = id,
                    name = u.Username,
                    exists = true,
                    participant = UserAccess.IsParticipant(u),
                    sharing,
                    sessions = sharing ? counts.GetValueOrDefault(id) : (int?)null,
                };
            })
            .Concat(counts.Keys
                .Where(id => !known.Contains(id))
                .Select(id => new
                {
                    userId = id,
                    name = "Deleted account",
                    exists = false,
                    participant = false,
                    sharing = false,
                    sessions = (int?)counts[id],
                }))
            .ToList();

        return new
        {
            lastImportUtc = _database.GetMeta("last_import_utc"),
            lastImportCounts = _database.GetMeta("last_import_counts"),
            activeSessions = _tracker.ActiveCount,
            users = rows,
        };
    }

    /// <summary>Re-runs the history import. Safe to repeat: rows are updated, not duplicated.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Counts per source.</returns>
    [HttpPost("Admin/Import")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<object>> RunImport(CancellationToken cancellationToken)
    {
        ImportResult r = await _importer.RunAsync(cancellationToken).ConfigureAwait(false);

        // Spelled out so the field names match the web page (Jellyfin would otherwise capitalize them).
        return new
        {
            ranUtc = r.RanUtc,
            userData = r.UserData,
            activityLog = r.ActivityLog,
            playbackReporting = r.PlaybackReporting,
            playbackReportingFound = r.PlaybackReportingFound,
            notes = r.Notes,
        };
    }

    /// <summary>Removes the stored data of an account that no longer exists.</summary>
    /// <param name="userId">The deleted account's ID.</param>
    /// <returns>How many sessions were removed.</returns>
    [HttpDelete("Admin/History/{userId}")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<object> DeleteDeletedUserData([FromRoute] string userId)
    {
        if (!Guid.TryParse(userId, out Guid id))
        {
            return BadRequest();
        }

        if (_access.GetById(id) is not null)
        {
            // An existing person's data is theirs to delete, from their own My Activity tab.
            return Conflict(new { error = "This account still exists. Only its owner can delete its viewing data." });
        }

        return new { deletedSessions = _database.DeleteUser(ItemSnapshotBuilder.Id(id), blockReimport: false) };
    }

    /// <summary>A user's recent sessions, only if that user shares with the admin.</summary>
    /// <param name="userId">User ID.</param>
    /// <param name="limit">How many (1 to 200).</param>
    /// <returns>Sessions, newest first.</returns>
    [HttpGet("Admin/History/{userId}/Activity")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<object> GetSharedActivity([FromRoute] string userId, [FromQuery] int limit = 25)
    {
        if (!Guid.TryParse(userId, out Guid id))
        {
            return BadRequest();
        }

        string key = ItemSnapshotBuilder.Id(id);
        if (!_database.GetShareWithAdmin(key))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "This person keeps their viewing stats private." });
        }

        return _database.GetSessions(key, Math.Clamp(limit, 1, 200)).Select(ToDto).ToList();
    }

    // ---------- Helpers ----------

    private bool TryGetParticipant(out User? user, out ActionResult? denied)
    {
        user = _access.GetCaller(User);
        if (user is null)
        {
            denied = Unauthorized();
            return false;
        }

        if (!UserAccess.IsParticipant(user))
        {
            denied = StatusCode(StatusCodes.Status403Forbidden, new { error = "Accounts hidden from the login screen don't take part in Race to Finish." });
            return false;
        }

        denied = null;
        return true;
    }

    private static object ToDto(ActivityRow row)
    {
        SessionRecord s = row.Session;
        ItemSnapshot? i = row.Item;
        return new
        {
            title = i?.Title ?? "Removed item",
            seriesTitle = i?.SeriesTitle,
            season = i?.SeasonNumber,
            episode = i?.EpisodeNumber,
            type = i?.ItemType,
            year = i?.Year,
            startedUtc = s.StartedUtc,
            endedUtc = s.EndedUtc,
            watchedMinutes = Math.Round(s.WatchedSeconds / 60.0, 1),
            percent = s.Percent,
            finished = s.Finished,
            source = s.Source,
            playCount = s.PlayCount,
            device = s.DeviceName,
            client = s.ClientName,
        };
    }
}
