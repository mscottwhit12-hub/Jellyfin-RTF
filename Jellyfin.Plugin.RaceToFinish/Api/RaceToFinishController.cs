using System.Net.Mime;
using System.Reflection;
using Jellyfin.Plugin.RaceToFinish.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RaceToFinish.Api;

/// <summary>
/// HTTP endpoints. v0.1 serves the web assets, the client settings and a status check for the admin page.
/// </summary>
[ApiController]
[Route("RaceToFinish")]
public class RaceToFinishController : ControllerBase
{
    private static readonly Dictionary<string, string> WebAssets = new(StringComparer.Ordinal)
    {
        ["rtf.js"] = "application/javascript; charset=utf-8",
        ["rtf.css"] = "text/css; charset=utf-8",
    };

    /// <summary>Serves rtf.js and rtf.css. Anonymous because index.html loads them before sign-in.</summary>
    /// <param name="name">File name.</param>
    /// <returns>The file.</returns>
    [HttpGet("web/{name}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetWebAsset([FromRoute] string name)
    {
        if (!WebAssets.TryGetValue(name, out string? contentType))
        {
            return NotFound();
        }

        Assembly assembly = typeof(RaceToFinishController).Assembly;
        Stream? stream = assembly.GetManifestResourceStream($"Jellyfin.Plugin.RaceToFinish.Web.{name}");
        if (stream is null)
        {
            return NotFound();
        }

        // The URL carries ?v=<version>, so a new release always fetches fresh files.
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(stream, contentType);
    }

    /// <summary>Settings the web client needs. Nothing private: safe to serve before sign-in.</summary>
    /// <returns>Client settings.</returns>
    [HttpGet("ClientConfig")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> GetClientConfig()
    {
        var config = Plugin.Instance?.Configuration;
        return new
        {
            useBlur = config?.UseBlur ?? true,
            version = typeof(RaceToFinishController).Assembly.GetName().Version?.ToString(),
        };
    }

    /// <summary>Health check for the admin settings page.</summary>
    /// <returns>Whether the web client injection is active.</returns>
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> GetStatus()
    {
        return new
        {
            injectionRegistered = StartupTask.InjectionRegistered,
            injectionError = StartupTask.InjectionError,
            version = typeof(RaceToFinishController).Assembly.GetName().Version?.ToString(),
        };
    }
}
