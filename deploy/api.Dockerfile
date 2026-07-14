# Build context is the repository root.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution-wide build config first for better layer caching.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ ./src/

RUN dotnet restore src/WireHQ.Api/WireHQ.Api.csproj

# Stamp the running version FROM THE RELEASE TAG (docs/30 U-5 / I2), not the hand-bumped <VersionPrefix> —
# the CE update check compares its own version to the signed manifest, so it must be authoritative. The release
# pipeline passes WIREHQ_VERSION=<tag>; a local build leaves it empty and falls back to <VersionPrefix>.
ARG WIREHQ_VERSION
RUN dotnet publish src/WireHQ.Api/WireHQ.Api.csproj -c Release -o /app /p:UseAppHost=false \
    ${WIREHQ_VERSION:+-p:Version=$WIREHQ_VERSION}

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app ./

# The container listens on 8080 internally; compose publishes it on an obscure host port (28080). The
# opt-in agent mTLS gateway binds AgentGateway:Port (default 28443) — published as-is when enabled.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080 28443

# Run as the non-root user provided by the base image.
USER $APP_UID

ENTRYPOINT ["dotnet", "WireHQ.Api.dll"]
