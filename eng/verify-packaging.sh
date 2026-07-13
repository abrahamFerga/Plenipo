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

echo ""
echo "OK — Plenipo packs cleanly and a fresh module project consumes the packages."
