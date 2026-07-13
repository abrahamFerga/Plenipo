namespace Plenipo.Modules.Sdk;

/// <summary>
/// A module's first-run setup wizard, declared in the manifest like tabs and tools — the shell
/// renders the whole guided experience (stepper, forms, uploads, progress) from this data; the
/// module ships zero custom UI. The wizard is offered when <see cref="ProbeEndpoint"/> returns
/// an empty array (the module has no data yet) to a caller holding <see cref="Permission"/>;
/// every step posts to endpoints that stay authorization-gated server-side regardless.
/// </summary>
public sealed record OnboardingDescriptor
{
    /// <summary>GET endpoint returning a JSON array; empty = the module is unconfigured and the
    /// shell offers the wizard (e.g. the accounts list).</summary>
    public required string ProbeEndpoint { get; init; }

    /// <summary>Permission required to see and run the wizard (setup writes data).</summary>
    public required string Permission { get; init; }

    /// <summary>Headline on the wizard's entry points (e.g. "Set up your household finances").</summary>
    public required string Title { get; init; }

    public IReadOnlyList<OnboardingStep> Steps { get; init; } = [];
}

/// <summary>One wizard step. Three kinds, all rendered generically by the shell:
/// <c>info</c> (copy only), <c>form</c> (fields posted to an endpoint, repeatable so the user
/// can add several), <c>upload</c> (file picker → the platform file store → a follow-up POST
/// that hands the stored file id to a module endpoint).</summary>
public sealed record OnboardingStep
{
    public required string Id { get; init; }

    /// <summary>Step title, e.g. "Add your accounts".</summary>
    public required string Title { get; init; }

    /// <summary>One or two sentences of guidance shown under the title — why this step matters.</summary>
    public required string Blurb { get; init; }

    /// <summary>info | form | upload.</summary>
    public required string Kind { get; init; }

    /// <summary>form: POST target for the fields. upload: the follow-up POST that receives the file id.</summary>
    public string? Endpoint { get; init; }

    /// <summary>form/upload: the visible fields (reuses the tab editor field shape).</summary>
    public IReadOnlyList<TabEditorField> Fields { get; init; } = [];

    /// <summary>Constant values merged into every POST body (e.g. direction=income) — lets one
    /// generic endpoint serve a specialized step without a specialized form.</summary>
    public IReadOnlyDictionary<string, string> Preset { get; init; } =
        new Dictionary<string, string>();

    /// <summary>upload: the follow-up body field that carries the stored file id (e.g. "fileId").</summary>
    public string? FileIdField { get; init; }

    /// <summary>upload: accepted file types for the picker, e.g. ".csv,.ofx,.qfx,.pdf".</summary>
    public string? Accept { get; init; }

    /// <summary>Steps are skippable by default — setup must never trap the user.</summary>
    public bool Optional { get; init; } = true;
}
