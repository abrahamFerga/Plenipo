# Multi-stage build for the Plenipo SAMPLE host — the full runnable product: platform +
# Finance/Nutrition/Legal modules + connectors + the brand-voice sample skill.
# Build context is the REPOSITORY ROOT:
#   docker build -f samples/Plenipo.Sample.Host/Dockerfile -t plenipo-sample .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore using only the project graph + central package management for layer caching.
# .editorconfig is required at build time: it tunes analyzer severities the projects
# rely on under TreatWarningsAsErrors (without it, publish fails on CA1716/CA1711/...).
COPY .editorconfig Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY src/Plenipo.Core/Plenipo.Core.csproj                             src/Plenipo.Core/
COPY src/Plenipo.Modules.Sdk/Plenipo.Modules.Sdk.csproj               src/Plenipo.Modules.Sdk/
COPY src/Plenipo.Application/Plenipo.Application.csproj               src/Plenipo.Application/
COPY src/Plenipo.Infrastructure/Plenipo.Infrastructure.csproj         src/Plenipo.Infrastructure/
COPY src/Plenipo.AspNetCore/Plenipo.AspNetCore.csproj                 src/Plenipo.AspNetCore/
COPY src/Plenipo.ServiceDefaults/Plenipo.ServiceDefaults.csproj       src/Plenipo.ServiceDefaults/
COPY src/Plenipo.Connectors.Sdk/Plenipo.Connectors.Sdk.csproj         src/Plenipo.Connectors.Sdk/
COPY src/Plenipo.Connectors/Plenipo.Connectors.csproj                 src/Plenipo.Connectors/
COPY samples/Plenipo.Modules.Finance/Plenipo.Modules.Finance.csproj     samples/Plenipo.Modules.Finance/
COPY samples/Plenipo.Modules.Nutrition/Plenipo.Modules.Nutrition.csproj samples/Plenipo.Modules.Nutrition/
COPY samples/Plenipo.Modules.Legal/Plenipo.Modules.Legal.csproj         samples/Plenipo.Modules.Legal/
COPY samples/Plenipo.Sample.Host/Plenipo.Sample.Host.csproj             samples/Plenipo.Sample.Host/
RUN dotnet restore samples/Plenipo.Sample.Host/Plenipo.Sample.Host.csproj

# Copy the rest and publish (the skills/ directory rides along as Content).
COPY src/ src/
COPY samples/Plenipo.Modules.Finance/   samples/Plenipo.Modules.Finance/
COPY samples/Plenipo.Modules.Nutrition/ samples/Plenipo.Modules.Nutrition/
COPY samples/Plenipo.Modules.Legal/     samples/Plenipo.Modules.Legal/
COPY samples/Plenipo.Sample.Host/       samples/Plenipo.Sample.Host/
RUN dotnet publish samples/Plenipo.Sample.Host/Plenipo.Sample.Host.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
# Run as the built-in non-root user provided by the aspnet image.
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Plenipo.Sample.Host.dll"]
