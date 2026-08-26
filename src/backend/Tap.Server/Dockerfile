# syntax=docker/dockerfile:1.6
#
# Tap inspector image. Build context = repo root, so paths are relative to repo root.
#   docker build -t ghcr.io/philbir/tap:latest -f src/backend/Tap.Server/Dockerfile .

# 1) Build the React inspector UI (yarn / vite).
#    The repo uses Yarn 4 with default PnP, but PnP runs into resolver bugs inside Alpine
#    (path-based ESM resolution). Force the node-modules linker for the docker build —
#    it's slower but reliable, and only affects this image.
FROM node:22-alpine AS ui
WORKDIR /ui
COPY src/ui-inspector/package.json src/ui-inspector/yarn.lock* ./
RUN echo "nodeLinker: node-modules" > .yarnrc.yml \
    && corepack enable \
    && yarn install
COPY src/ui-inspector/ ./
# Re-write .yarnrc.yml in case the COPY brought one over (we want node-modules in this stage).
RUN echo "nodeLinker: node-modules" > .yarnrc.yml \
    && yarn build

# 2) Restore + publish the .NET inspector. The csproj's BuildTapUi target is suppressed
#    via SkipTapUiBuild=true; we plug the pre-built dist/ from the ui stage straight in.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/backend/Tap.Core/ ./src/Tap.Core/
# Tap.Server references Tap.Inspector.Mcp to serve the agent surface at /mcp, so the
# project has to be here or the restore cannot resolve it.
COPY src/backend/Tap.Inspector.Mcp/ ./src/Tap.Inspector.Mcp/
COPY src/backend/Tap.Server/ ./src/Tap.Server/
COPY --from=ui /ui/dist ./src/Tap.Server/wwwroot/
# Release tag, stamped into the assembly. Empty for an unstamped local build,
# which falls back to Directory.Build.props' 0.1.0-local.
ARG VERSION=
RUN dotnet publish src/Tap.Server/Tap.Server.csproj \
        -c Release -o /app \
        -p:SkipTapUiBuild=true \
        ${VERSION:+-p:Version=$VERSION}

# 3) Runtime — aspnet:10.0 chiseled is the smallest official image with ASP.NET Core.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
LABEL org.opencontainers.image.title="Tap Tunnels" \
      org.opencontainers.image.description="Local HTTP tunnels with inspection built in" \
      org.opencontainers.image.url="https://philbir.github.io/tap/#/tunnels" \
      org.opencontainers.image.source="https://github.com/philbir/tap" \
      org.opencontainers.image.licenses="MIT" \
      io.tap.product="Tap Tunnels" \
      io.tap.product.icon="/app/wwwroot/tap-tunnels-icon.svg"
# Default ports — overridable via Inspector__ProxyPort / Inspector__UiPort.
ENV ASPNETCORE_URLS=http://0.0.0.0:5299;http://0.0.0.0:5298 \
    Inspector__ProxyHost=0.0.0.0 \
    Inspector__UiHost=0.0.0.0 \
    Inspector__ProxyPort=5299 \
    Inspector__UiPort=5298 \
    Inspector__Mode=standalone
EXPOSE 5298 5299
ENTRYPOINT ["dotnet", "Tap.Server.dll"]
