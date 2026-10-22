using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.RaceToFinish.Tracking;

/// <summary>
/// Who may use the plugin: everyone shown on the login screen. Accounts set to
/// "Hide this user from login screens" get no access and are not tracked.
/// </summary>
public class UserAccess
{
    /// <summary>The claim Jellyfin puts the signed-in user's ID in.</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    private readonly IUserManager _userManager;

    /// <summary>Initializes a new instance of the <see cref="UserAccess"/> class.</summary>
    /// <param name="userManager">User manager.</param>
    public UserAccess(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <summary>Whether this user takes part (is shown on the login screen).</summary>
    /// <param name="user">User.</param>
    /// <returns>True when allowed.</returns>
    public static bool IsParticipant(User? user) => user is not null && !user.HasPermission(PermissionKind.IsHidden);

    /// <summary>Gets the signed-in user for a request.</summary>
    /// <param name="principal">Request user.</param>
    /// <returns>The user, or null.</returns>
    public User? GetCaller(ClaimsPrincipal principal)
    {
        string? raw = principal.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(raw, out Guid id) ? _userManager.GetUserById(id) : null;
    }

    /// <summary>Looks a user up by ID.</summary>
    /// <param name="id">User ID.</param>
    /// <returns>The user, or null when deleted.</returns>
    public User? GetById(Guid id) => _userManager.GetUserById(id);

    /// <summary>Gets all users.</summary>
    /// <returns>Users.</returns>
    public IEnumerable<User> GetUsers() => _userManager.GetUsers();
}
