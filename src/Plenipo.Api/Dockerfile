# Multi-stage build for the Plenipo API (the bare platform host — no domain modules).
# Build context is the REPOSITORY ROOT:
#   docker build -f src/Plenipo.Api/Dockerfile -t plenipo-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore using only the project graph + central package management for layer caching.
# .editorconfig is required at build time: it tunes analyzer severities the projects
# rely on under TreatWarningsAsErrors (without it, publish fails on CA1716/CA1711/...).
COPY .editorconfig Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY src/Plenipo.Core/Plenipo.Core.csproj                       src/Plenipo.Core/
COPY src/Plenipo.Modules.Sdk/Plenipo.Modules.Sdk.csproj         src/Plenipo.Modules.Sdk/
COPY src/Plenipo.Application/Plenipo.Application.csproj          src/Plenipo.Application/
COPY src/Plenipo.Infrastructure/Plenipo.Infrastructure.csproj   src/Plenipo.Infrastructure/
COPY src/Plenipo.AspNetCore/Plenipo.AspNetCore.csproj           src/Plenipo.AspNetCore/
COPY src/Plenipo.ServiceDefaults/Plenipo.ServiceDefaults.csproj src/Plenipo.ServiceDefaults/
COPY src/Plenipo.Api/Plenipo.Api.csproj                         src/Plenipo.Api/
RUN dotnet restore src/Plenipo.Api/Plenipo.Api.csproj

# Copy the rest and publish.
COPY src/ src/
RUN dotnet publish src/Plenipo.Api/Plenipo.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
# Run as the built-in non-root user provided by the aspnet image.
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Plenipo.Api.dll"]
