namespace Plenipo.Modules.Sdk;

/// <summary>
/// Documents, at the tool method, the permission required to invoke it. Optional ergonomic aid — the
/// authoritative gate is <see cref="ModuleTool.Permission"/> — but keeping the permission next to the
/// implementation keeps the two from drifting apart.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}
