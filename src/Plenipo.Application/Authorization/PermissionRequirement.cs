using Microsoft.AspNetCore.Authorization;

namespace Plenipo.Application.Authorization;

/// <summary>
/// Authorization requirement carrying a single required permission string. Paired with a handler that
/// checks it against the caller's resolved permission set via <see cref="PermissionMatcher"/>.
/// </summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;

    /// <summary>Policy name convention for a permission-based policy, used by the endpoint helper.</summary>
    public static string PolicyName(string permission) => $"perm:{permission}";
}
