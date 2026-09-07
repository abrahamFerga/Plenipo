using Plenipo.Testing.Conformance;

namespace Plenipo.Sample.Host.IntegrationTests;

// The platform is the first consumer of its own conformance kit: the three invariant packs a product
// derives from Plenipo.Testing run here against the sample host's finance module, exactly as they
// will run against every product. A kit test that cannot pass on the sample host never ships.

[Collection("api")]
public sealed class SpineConformance(IntegrationFixture fixture) : PlenipoSpineConformance<Program>(fixture);

[Collection("api")]
public sealed class ManifestConformance(IntegrationFixture fixture) : PlenipoManifestConformance<Program>(fixture);

[Collection("api")]
public sealed class TenancyConformance(IntegrationFixture fixture) : PlenipoTenancyConformance<Program>(fixture);
