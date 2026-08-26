# syntax=docker/dockerfile:1.6
#
# Tap Studio image. Build context = repo root, so paths are relative to repo root.
#   docker build -t ghcr.io/philbir/tap-studio:latest -f src/backend/Tap.Studio/Dockerfile .
#
# The workspace is a bind mount, never baked in: Studio edits the files it opens, and a
# workspace lives in the consumer's repository. Mount it at /workspace (the default
# Studio__WorkspaceRoot) and the user-level state directory at /state.

# 1) Build the Studio React UI (yarn 4 / vite). The repo already pins the node-modules
#    linker for this workspace (Rolldown vs PnP zip paths); repeat it here so the build
#    doesn't depend on the .yarnrc.yml landing in the right layer.
FROM node:22-alpine AS ui
WORKDIR /ui
COPY src/ui-studio/package.json src/ui-studio/yarn.lock ./
RUN echo "nodeLinker: node-modules" > .yarnrc.yml \
    && corepack enable \
    && yarn install
COPY src/ui-studio/ ./
RUN echo "nodeLinker: node-modules" > .yarnrc.yml \
    && yarn build

# 2) Restore + publish the .NET Studio. BuildStudioUi is suppressed via
#    SkipStudioUiBuild=true; the pre-built dist/ from the ui stage is plugged straight
#    into wwwroot/, which keeps yarn out of the SDK layer entirely.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props nuget.config README.md ./
COPY assets/ ./assets/
COPY src/backend/Tap.Workspace/ ./src/Tap.Workspace/
COPY src/backend/Tap.Execution/ ./src/Tap.Execution/
COPY src/backend/Tap.Studio.Mcp/ ./src/Tap.Studio.Mcp/
COPY src/backend/Tap.Studio/ ./src/Tap.Studio/
COPY --from=ui /ui/dist ./src/Tap.Studio/wwwroot/
# Release tag, stamped into the assembly so /api/workspace (and the brand menu's
# version line, and the User-Agent) report it. Empty for an unstamped local build,
# which falls back to Directory.Build.props' 0.1.0-local.
ARG VERSION=
RUN dotnet publish src/Tap.Studio/Tap.Studio.csproj \
        -c Release -o /app \
        -p:SkipStudioUiBuild=true \
        ${VERSION:+-p:Version=$VERSION}

# 3) Runtime — the same ASP.NET Core base the inspector image uses.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
LABEL org.opencontainers.image.title="Tap Studio" \
      org.opencontainers.image.description="The HTTP request and auth credential crafter" \
      org.opencontainers.image.url="https://philbir.github.io/tap/#/studio" \
      org.opencontainers.image.source="https://github.com/philbir/tap" \
      org.opencontainers.image.licenses="MIT" \
      io.tap.product="Tap Studio" \
      io.tap.product.icon="/app/wwwroot/tap-studio-icon.svg"
# Both directories are mount points. Created here so a container started without them
# still boots (onto an empty workspace it scaffolds, and a state dir it owns) rather
# than failing on a missing path.
RUN mkdir -p /workspace /state
# Studio__* mirrors the Studio:* configuration keys. The host allowlist is left to the
# caller: a wildcard bind with no allowlist accepts any Host header, which the AppHost
# integration pins to loopback and a hand-run container should pin to its own hostname.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Studio__Host=0.0.0.0 \
    Studio__Port=8080 \
    Studio__WorkspaceRoot=/workspace \
    TAP_SYSTEM_DIR=/state
EXPOSE 8080
ENTRYPOINT ["dotnet", "Tap.Studio.dll"]
