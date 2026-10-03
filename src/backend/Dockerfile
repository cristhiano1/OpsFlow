# =============================================================================
# OpsFlow API — multi-stage production image
#
# Build context: repository root (so all projects and Directory.*.props resolve)
#   docker build -f src/backend/Dockerfile -t opsflow-api .
# =============================================================================

# --- restore ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/backend/OpsFlow.Api/OpsFlow.Api.csproj                   src/backend/OpsFlow.Api/
COPY src/backend/OpsFlow.Application/OpsFlow.Application.csproj   src/backend/OpsFlow.Application/
COPY src/backend/OpsFlow.Contracts/OpsFlow.Contracts.csproj       src/backend/OpsFlow.Contracts/
COPY src/backend/OpsFlow.Domain/OpsFlow.Domain.csproj             src/backend/OpsFlow.Domain/
COPY src/backend/OpsFlow.Infrastructure/OpsFlow.Infrastructure.csproj src/backend/OpsFlow.Infrastructure/

RUN dotnet restore src/backend/OpsFlow.Api/OpsFlow.Api.csproj

# --- build ---
FROM restore AS build
COPY src/backend/ src/backend/
RUN dotnet publish src/backend/OpsFlow.Api/OpsFlow.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# --- runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && apt-get clean && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 opsflow \
    && useradd --uid 10001 --gid opsflow --shell /bin/false --no-create-home opsflow \
    && mkdir -p /app/storage && chown opsflow:opsflow /app/storage

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER opsflow
ENTRYPOINT ["dotnet", "OpsFlow.Api.dll"]
