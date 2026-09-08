#!/usr/bin/env bash
# =============================================================================
# verify-packaging.sh — prove the "build on Plenipo as packages" thesis.
# -----------------------------------------------------------------------------
# Plenipo's core promise is that a product is a *thin host that installs the
# platform's NuGet packages*, not a fork. The samples consume the platform via
# ProjectReference (fast for dev) — which does NOT exercise the package path. This
# script does: it packs the platform and then builds a throwaway module project
# that consumes ONLY the produced packages, so a broken pack or bad package
# metadata fails CI instead of silently shipping.
#
# Run locally (from anywhere): eng/verify-packaging.sh
# Works on Linux/macOS and on Windows via Git Bash (paths converted with cygpath).
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FEED="$WORK/feed"
CONSUMER="$WORK/consumer"
# A unique version each run, so NuGet's immutable-version global cache can never
# serve a stale build of a previously-packed identical version.
VERSION="0.0.0-verify$(date +%s)"

# NuGet (a .NET tool) needs a native path in nuget.config; convert under Git Bash.
to_native() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

# Package validation (docs/TESTING_CONTRACT.md §4.1): with the last release's packages present,
# the pack below also compares every public surface against that baseline and fails on an
# undeclared break. This script is what CI's package job runs, so fetching here is what makes
# validation run on every pull request without touching the workflow files.
bash "$ROOT/eng/fetch-baseline.sh"

echo "==> Packing $ROOT/Plenipo.slnx  (version $VERSION)"
dotnet pack "$(to_native "$ROOT/Plenipo.slnx")" -c Release -o "$(to_native "$FEED")" -p:PackageVersion="$VERSION" >/dev/null
echo "    packed: $(ls "$FEED"/*.nupkg | wc -l) packages"

mkdir -p "$CONSUMER"

cat > "$CONSUMER/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
    <add key="plenipo-local" value="$(to_native "$FEED")" />
  </packageSources>
</configuration>
EOF

# Referencing Plenipo.AspNetCore pulls the whole platform graph (Core, Modules.Sdk,
# Application, Infrastructure, ServiceDefaults) transitively, so every package is
# proven to restore and be usable together.
cat > "$CONSUMER/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Plenipo.AspNetCore" Version="$VERSION" />
  </ItemGroup>
</Project>
EOF

cat > "$CONSUMER/DemoModule.cs" <<'EOF'
using Plenipo.Modules.Sdk;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Consumer;

// Implementing IModule against the PACKAGE (resolved transitively via Plenipo.AspNetCore)
// is the exact "build a module on Plenipo" path a downstream product follows.
public sealed class DemoModule : IModule
{
    public ModuleManifest Manifest { get; } = new()
    {
        Id = "demo",
        DisplayName = "Demo",
        Version = "1.0.0",
        SuggestedPrompts = ["Hello from a packaged module"],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
EOF

echo "==> Building a throwaway consumer module against the packed packages"
dotnet build "$(to_native "$CONSUMER/Consumer.csproj")" -c Release

# The conformance kit is a package too, and the one a product's test project consumes. A fresh
# test project deriving the four kit classes must compile against Plenipo.Testing alone — its
# transitive test dependencies (xunit, Mvc.Testing, Testcontainers) included. Compile-only: the
# fixture needs a real host and Docker to run, which the samples suite proves.
KIT="$WORK/kit-consumer"
mkdir -p "$KIT"
cp "$CONSUMER/nuget.config" "$KIT/nuget.config"

cat > "$KIT/KitConsumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Plenipo.Testing" Version="$VERSION" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
EOF

cat > "$KIT/Conformance.cs" <<'EOF'
using Plenipo.Testing;
using Plenipo.Testing.Conformance;
using Plenipo.Testing.Evals;
using Xunit;

namespace KitConsumer;

// A product host's entry point stands in here; the kit only needs the type at compile time.
public sealed class Program;

public sealed class Fixture : PlenipoHostFixture<Program>
{
    public override ProductContract Contract { get; } = new(
        ModuleId: "demo", ReadTool: "list_things", WriteTool: "record_thing",
        ReadEndpoints: ["/api/demo/things"]);
}

[CollectionDefinition("api")] public sealed class ApiCollection : ICollectionFixture<Fixture>;

[Collection("api")] public sealed class Spine(Fixture f) : PlenipoSpineConformance<Program>(f);
[Collection("api")] public sealed class Manifest(Fixture f) : PlenipoManifestConformance<Program>(f);
[Collection("api")] public sealed class Tenancy(Fixture f) : PlenipoTenancyConformance<Program>(f);
[Collection("api")] public sealed class Evals(Fixture f) : PlenipoGoldenEvals<Program>(f);
EOF

echo "==> Building a throwaway test project against the packed Plenipo.Testing kit"
dotnet build "$(to_native "$KIT/KitConsumer.csproj")" -c Release

echo ""
echo "OK — Plenipo packs cleanly, a fresh module project consumes the packages, and a fresh test project consumes the conformance kit."
