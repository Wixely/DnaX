# syntax=docker/dockerfile:1.7
#
# Shared MCPSharp server Dockerfile. Copy into a server repository root, or reference it
# via the reusable workflow's `dockerfile` input. Replaces 19 near-identical copies whose
# only real differences were the project name, the environment-variable prefix, and the port.
#
# Build args:
#   PROJECT      server project file name, e.g. RedisMCPSharp.csproj
#   PORT         listening port, e.g. 5713
#
#   docker build --build-arg PROJECT=RedisMCPSharp.csproj \
#                --build-arg PORT=5713 .

ARG PROJECT
ARG PORT=5700

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG PROJECT
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Packages.props ./
COPY ${PROJECT} ./
ARG TARGETARCH
# DnaX.MCPFab comes from GitHub Packages, which refuses anonymous downloads. Pass a token with
# read:packages as a BuildKit secret, so it is never recorded in the image history:
#   docker build --secret id=nuget_github_token,env=GITHUB_TOKEN .
RUN --mount=type=secret,id=nuget_github_token \
    arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    if [ -s /run/secrets/nuget_github_token ]; then \
    dotnet nuget update source GitHub-Wixely-Packages \
    --username token \
    --password "$(cat /run/secrets/nuget_github_token)" \
    --store-password-in-clear-text \
    --configfile NuGet.config; \
    fi; \
    dotnet restore "${PROJECT}" \
    -r "$rid" \
    -p:PublishSingleFile=true \
    -p:SelfContained=false \
    -p:EnableCompressionInSingleFile=false

COPY . .
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet publish "${PROJECT}" \
    -c Release \
    --no-restore \
    -r "$rid" \
    --self-contained false \
    -o /app/publish \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:IsTransformWebConfigDisabled=true \
    -p:StaticWebAssetsEnabled=false \
    -p:DebugType=none \
    -p:DebugSymbols=false && \
    # The entrypoint is fixed below, so record the built executable name for it.
    basename "${PROJECT}" .csproj > /app/publish/.apphost

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
ARG PORT
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

# Unprefixed keys deliberately: Docker expands variables in ENV values but not in ENV
# keys, so a "${ENV_PREFIX}Server__Host" key would be taken literally. The servers add
# unprefixed environment variables to the configuration chain before the prefixed ones,
# so these bind correctly for every server without needing the prefix at all.
# Bind to all interfaces inside the container; the port is published by the host.
ENV Server__Host=0.0.0.0 \
    Server__Port=${PORT} \
    Server__Path=/mcp

RUN mkdir -p /app/logs && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

USER $APP_UID
EXPOSE ${PORT}
VOLUME ["/app/logs"]

ENTRYPOINT ["/bin/sh", "-c", "exec ./$(cat .apphost)"]
