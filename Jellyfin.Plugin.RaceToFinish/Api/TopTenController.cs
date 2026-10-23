using System.Net.Mime;
using Jellyfin.Plugin.RaceToFinish.TopTen;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RaceToFinish.Api;

/// <summary>
/// The ready-made "Top 10 movies of a year" lists (from IMDb, built into the plugin), for the admin. Each list
/// shows which films the server has, so the admin can see what's missing before starting a challenge. Starting
/// the challenge itself comes with the challenge features; these endpoints only read.
/// </summary>
[ApiController]
[Route("RaceToFinish/Admin/TopTen")]
[Authorize(Policy = "RequiresElevation")]
public class TopTenController : ControllerBase
{
    private readonly TopTenService _topTen;

    /// <summary>Initializes a new instance of the <see cref="TopTenController"/> class.</summary>
    /// <param name="topTen">Top 10 service.</param>
    public TopTenController(TopTenService topTen)
    {
        _topTen = topTen;
    }

    /// <summary>Every ready-made year, with how many of its films the server has.</summary>
    /// <returns>Overview.</returns>
    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> GetOverview()
    {
        var lists = _topTen.GetLists(TopTenService.PresetYears).ToDictionary(l => l.Year);
        return new
        {
            source = "IMDb",
            listsMadeUtc = _topTen.GeneratedUtc,
            definition = TopTenService.DefinitionText,
            attribution = TopTenService.Attribution,
            years = TopTenService.PresetYears.Select(y => lists.TryGetValue(y, out TopTenList? list)
                ? (object)new { year = y, label = list.Label, available = true, onServerCount = list.OnServerCount, total = list.Movies.Count, complete = list.Complete }
                : new { year = y, label = TopTenService.LabelFor(y), available = false }),
        };
    }

    /// <summary>A year's Top 10, with which films the server has.</summary>
    /// <param name="year">2020 to 2025.</param>
    /// <returns>The list, or a message saying why there isn't one.</returns>
    [HttpGet("{year:int}")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> GetYear([FromRoute] int year)
    {
        if (!TopTenService.PresetYears.Contains(year))
        {
            return NotFound(new { error = $"There's no ready-made list for {year}. Available: {string.Join(", ", TopTenService.PresetYears)}." });
        }

        TopTenList? list = _topTen.GetList(year);
        if (list is null)
        {
            return NotFound(new { error = $"This version of Race to Finish doesn't include the {year} list. Update the plugin." });
        }

        return new
        {
            year = list.Year,
            label = list.Label,
            source = "IMDb",
            definition = TopTenService.DefinitionText,
            minimumVotes = list.MinimumVotes,
            listsMadeUtc = _topTen.GeneratedUtc,
            onServerCount = list.OnServerCount,
            missingCount = list.Movies.Count - list.OnServerCount,
            complete = list.Complete,
            attribution = TopTenService.Attribution,
            movies = list.Movies.Select(m => new
            {
                rank = m.Rank,
                imdbId = m.ImdbId,
                imdbUrl = "https://www.imdb.com/title/" + m.ImdbId + "/",
                title = m.Title,
                year = m.Year,
                rating = m.Rating,
                votes = m.Votes,
                runtimeMinutes = m.RuntimeMinutes,
                onServer = m.OnServer,
                itemId = m.ItemId,
                matchedBy = m.MatchedBy,
            }),
        };
    }
}
