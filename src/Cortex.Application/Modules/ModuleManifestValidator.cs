using Cortex.Modules.Sdk;

namespace Cortex.Application.Modules;

/// <summary>
/// Validates the set of module manifests a host has registered so a misconfiguration fails fast at
/// startup with an actionable message — rather than surfacing later as a cryptic dictionary crash
/// (duplicate module id), an invisible tab (duplicate tab id), or an ambiguous route (two tabs sharing
/// a client route, which the shell resolves the active module from). Pure and side-effect free.
/// </summary>
public static class ModuleManifestValidator
{
    /// <summary>
    /// Returns every problem found across <paramref name="manifests"/> (empty when the set is valid).
    /// Each entry is a complete, human-readable diagnostic.
    /// </summary>
    public static IReadOnlyList<string> Validate(IEnumerable<ModuleManifest> manifests)
    {
        var list = manifests as IReadOnlyList<ModuleManifest> ?? manifests.ToList();
        var errors = new List<string>();

        // Module ids: present and unique (a duplicate is what makes the catalog's dictionary throw).
        var seenModuleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in list)
        {
            if (string.IsNullOrWhiteSpace(module.Id))
            {
                errors.Add($"A registered module has an empty id (DisplayName: '{module.DisplayName}').");
            }
            else if (!seenModuleIds.Add(module.Id))
            {
                errors.Add($"Duplicate module id '{module.Id}' — every registered module must have a unique id.");
            }
        }

        // Tabs: ids unique within their module; labels and routes present.
        foreach (var module in list)
        {
            var seenTabIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tab in module.Tabs)
            {
                var where = $"module '{module.Id}'";
                if (string.IsNullOrWhiteSpace(tab.Id))
                {
                    errors.Add($"A tab in {where} has an empty id.");
                }
                else if (!seenTabIds.Add(tab.Id))
                {
                    errors.Add($"Duplicate tab id '{tab.Id}' in {where} — tab ids must be unique within a module.");
                }

                if (string.IsNullOrWhiteSpace(tab.Label))
                {
                    errors.Add($"Tab '{tab.Id}' in {where} has an empty label.");
                }
                if (string.IsNullOrWhiteSpace(tab.Route))
                {
                    errors.Add($"Tab '{tab.Id}' in {where} has an empty route.");
                }

                ValidateRowActions(module, tab, errors);
            }
        }

        // Admin tabs: ids unique within their module, labels present, and — because an admin
        // surface must never be visible by default — a permission is REQUIRED, not optional.
        foreach (var module in list)
        {
            var seenAdminTabIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tab in module.AdminTabs)
            {
                var where = $"module '{module.Id}'";
                if (string.IsNullOrWhiteSpace(tab.Id))
                {
                    errors.Add($"An admin tab in {where} has an empty id.");
                }
                else if (!seenAdminTabIds.Add(tab.Id))
                {
                    errors.Add($"Duplicate admin tab id '{tab.Id}' in {where} — admin tab ids must be unique within a module.");
                }

                if (string.IsNullOrWhiteSpace(tab.Label))
                {
                    errors.Add($"Admin tab '{tab.Id}' in {where} has an empty label.");
                }
                if (string.IsNullOrWhiteSpace(tab.Permission))
                {
                    errors.Add(
                        $"Admin tab '{tab.Id}' in {where} declares no Permission — admin pages are " +
                        "never visible by default, so every admin tab must be permission-gated.");
                }

                ValidateRowActions(module, tab, errors);
            }
        }

        // Tab routes must be unique across ALL modules: the shell resolves the active module from the
        // current route, so two tabs sharing a route would make navigation and deep-linking ambiguous.
        var routeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in list)
        {
            foreach (var tab in module.Tabs)
            {
                if (string.IsNullOrWhiteSpace(tab.Route))
                {
                    continue; // already reported above
                }

                var owner = $"module '{module.Id}' tab '{tab.Id}'";
                if (routeOwners.TryGetValue(tab.Route, out var firstOwner))
                {
                    errors.Add(
                        $"Duplicate tab route '{tab.Route}' — declared by both {firstOwner} and {owner}. " +
                        "Tab routes must be unique across all modules.");
                }
                else
                {
                    routeOwners[tab.Route] = owner;
                }
            }
        }

        return errors;
    }

    // Row actions: ids unique within their tab, labels present, and the endpoint template must
    // actually carry a {field} placeholder — a fixed URL would hit the same target for every row,
    // which is what a tab-level Action is for.
    private static void ValidateRowActions(ModuleManifest module, TabDescriptor tab, List<string> errors)
    {
        var seenActionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in tab.RowActions)
        {
            var where = $"tab '{tab.Id}' in module '{module.Id}'";
            if (string.IsNullOrWhiteSpace(action.Id))
            {
                errors.Add($"A row action on {where} has an empty id.");
            }
            else if (!seenActionIds.Add(action.Id))
            {
                errors.Add($"Duplicate row action id '{action.Id}' on {where} — row action ids must be unique within a tab.");
            }

            if (string.IsNullOrWhiteSpace(action.Label))
            {
                errors.Add($"Row action '{action.Id}' on {where} has an empty label.");
            }

            if (string.IsNullOrWhiteSpace(action.EndpointTemplate) || !action.EndpointTemplate.Contains('{', StringComparison.Ordinal))
            {
                errors.Add(
                    $"Row action '{action.Id}' on {where} needs an EndpointTemplate containing a " +
                    "{field} placeholder (e.g. '/api/finance/imports/{id}/approve') — without one " +
                    "every row would POST to the same URL; declare a tab-level Action for that.");
            }
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> — with every problem aggregated into one message —
    /// when <paramref name="manifests"/> is invalid; otherwise returns.
    /// </summary>
    public static void ThrowIfInvalid(IEnumerable<ModuleManifest> manifests)
    {
        var errors = Validate(manifests);
        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Invalid Cortex module registration:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(e => "  • " + e)));
    }
}
