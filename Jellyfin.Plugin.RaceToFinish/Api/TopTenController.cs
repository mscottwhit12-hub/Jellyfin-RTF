using System.Net.Mime;
using Jellyfin.Plugin.RaceToFinish.TopTen;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.Api;

/// <summary>
/// The ready-made "Top 10 movies of a year" lists, for the admin. Each list shows which films the server has, so
/// the admin can see what's missing before starting a challenge. Starting the challenge itself comes with the
/// challenge features; these endpoints only read.
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
    /// <param name="topTen">Top 10 service.</param>
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

    /// <summary>A year's Top 10, with which films the server has.</summary>
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

        try
        {
            TopTenList list = await _topTen.GetListAsync(year, refresh, cancellationToken).ConfigureAwait(false);
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
                }),
            };
        }
        catch (TmdbException ex)
        {
            return ex.Kind == TmdbProblem.NoKey
                ? BadRequest(new { error = ex.Message, problem = "no-key" })
                : StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message, problem = ex.Kind == TmdbProblem.BadKey ? "bad-key" : "unavailable" });
        }
    }
}
