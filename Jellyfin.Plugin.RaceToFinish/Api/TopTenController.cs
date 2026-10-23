using System.Net.Mime;
using Jellyfin.Plugin.RaceToFinish.TopTen;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.Api;

/// <summary>
/// The ready-made "Top 5 movies of a year" lists, for the admin. Each list shows which films the server has, so
/// the admin can see what's missing, and can be turned on once every film is on the server (which locks its list).
/// </summary>
[ApiController]
[Route("RaceToFinish/Admin/TopTen")]
[Authorize(Policy = "RequiresElevation")]
public class TopTenController : ControllerBase
{
    /// <summary>TMDB's required attribution notice.</summary>
    public const string Attribution = "This product uses the TMDB API but is not endorsed or certified by TMDB.";

    private readonly TopTenService _topTen;
    private readonly ILogger<TopTenController> _logger;

    /// <summary>Initializes a new instance of the <see cref="TopTenController"/> class.</summary>
    /// <param name="topTen">Top 5 service.</param>
    /// <param name="logger">Logger.</param>
    public TopTenController(TopTenService topTen, ILogger<TopTenController> logger)
    {
        _topTen = topTen;
        _logger = logger;
    }

    /// <summary>The ready-made years and which TMDB key is used. Doesn't contact TMDB.</summary>
    /// <returns>Overview.</returns>
    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> GetOverview()
    {
        return new
        {
            keySource = JellyfinTmdbKey.Find(_logger).Source,
            definition = TopTenService.DefinitionText,
            attribution = Attribution,
            years = TopTenService.PresetYears.Select(y => new { year = y, label = TopTenService.LabelFor(y) }),
        };
    }

    /// <summary>A year's Top 5, with which films the server has.</summary>
    /// <param name="year">2020 to 2025.</param>
    /// <param name="refresh">True to ask TMDB again instead of using today's copy.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The list, or a message saying what to fix.</returns>
    [HttpGet("{year:int}")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<object>> GetYear([FromRoute] int year, [FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!TopTenService.PresetYears.Contains(year))
        {
            return NotFound(new { error = $"There's no ready-made list for {year}. Available: {string.Join(", ", TopTenService.PresetYears)}." });
        }

        return await Respond(() => _topTen.GetListAsync(year, refresh, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns a year's challenge on, locking in its list. Refused (409) unless every film is on the server.</summary>
    /// <param name="year">2020 to 2025.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The locked list, or why it can't be turned on.</returns>
    [HttpPost("{year:int}/TurnOn")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<object>> TurnOn([FromRoute] int year, CancellationToken cancellationToken)
    {
        if (!TopTenService.PresetYears.Contains(year))
        {
            return NotFound(new { error = $"There's no ready-made list for {year}." });
        }

        return await Respond(() => _topTen.TurnOnAsync(year, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns a year's challenge off and forgets its locked list.</summary>
    /// <param name="year">2020 to 2025.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The current list.</returns>
    [HttpPost("{year:int}/TurnOff")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> TurnOff([FromRoute] int year, CancellationToken cancellationToken)
    {
        if (!TopTenService.PresetYears.Contains(year))
        {
            return NotFound(new { error = $"There's no ready-made list for {year}." });
        }

        _topTen.TurnOff(year);
        return await Respond(() => _topTen.GetListAsync(year, false, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionResult<object>> Respond(Func<Task<TopTenList>> get, CancellationToken cancellationToken)
    {
        try
        {
            TopTenList list = await get().ConfigureAwait(false);
            return new
            {
                year = list.Year,
                label = list.Label,
                definition = list.Definition,
                minimumVotes = list.MinimumVotes,
                fetchedUtc = list.FetchedUtc,
                onServerCount = list.OnServerCount,
                missingCount = list.Movies.Count - list.OnServerCount,
                complete = list.Complete,
                turnedOn = list.TurnedOnUtc is not null,
                turnedOnUtc = list.TurnedOnUtc,
                attribution = Attribution,
                movies = list.Movies.Select(m => new
                {
                    rank = m.Rank,
                    tmdbId = m.TmdbId,
                    title = m.Title,
                    year = m.Year,
                    releaseDate = m.ReleaseDate,
                    rating = m.Rating,
                    votes = m.Votes,
                    posterUrl = m.PosterUrl,
                    onServer = m.OnServer,
                    itemId = m.ItemId,
                    matchedBy = m.MatchedBy,
                    serverTitle = m.ServerTitle,
                }),
            };
        }
        catch (TopTenRefusedException ex)
        {
            return Conflict(new { error = ex.Message, problem = "incomplete" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The admin left the page before TMDB answered. Nobody is waiting, so don't log it as an error.
            return new EmptyResult();
        }
        catch (TmdbException ex)
        {
            return ex.Kind == TmdbProblem.NoKey
                ? BadRequest(new { error = ex.Message, problem = "no-key" })
                : StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message, problem = ex.Kind == TmdbProblem.BadKey ? "bad-key" : "unavailable" });
        }
    }
}
