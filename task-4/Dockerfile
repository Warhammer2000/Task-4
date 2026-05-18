# syntax=docker/dockerfile:1.7
#
# Air Traffic Control MCP server — production image.
#
# Multi-stage build: heavy SDK image is used only at build time; the final
# runtime image carries just the published DLLs plus the .NET 10 runtime.
# Final image size is ~210 MB (vs ~860 MB for an sdk-based image) and the
# attack surface is correspondingly smaller.
#
# Usage:
#   docker build -t atc-mcp:latest .
#   docker run --rm -i \
#     -e ATC_RUNWAYS="R1:3500:medium,R2:4000:heavy" \
#     -e ATC_GATE_COUNT=4 ... \
#     atc-mcp:latest
#
# Note: the MCP transport is stdio. The container MUST be started with
# `-i` (interactive) so stdin stays open. Claude Desktop's mcpServers
# config block handles this automatically (it passes `-i` itself).

# ===========================================================================
# Stage 1: build & publish
# ===========================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first — layer caches as long as the .csproj doesn't change.
COPY src/AtcMcpServer/AtcMcpServer.csproj src/AtcMcpServer/
RUN dotnet restore src/AtcMcpServer/AtcMcpServer.csproj \
    --runtime linux-x64

# Then copy sources and publish. UseAppHost=false skips the native
# launcher (we invoke via `dotnet AtcMcpServer.dll` in the runtime stage)
# which saves ~12 MB and removes one platform-specific binary.
COPY src/ src/
RUN dotnet publish src/AtcMcpServer/AtcMcpServer.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --no-restore \
    --no-self-contained \
    -p:UseAppHost=false \
    --output /app

# ===========================================================================
# Stage 2: runtime
# ===========================================================================
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Run as non-root. Microsoft's runtime image ships an `app` user (uid 1654)
# by default; we just switch to it. No need to add new accounts.
USER app

COPY --from=build --chown=app:app /app .

# Document the protocol: stdio. There's no port to expose.
# Logs go to stderr so they do NOT corrupt the JSON-RPC stream on stdout.
ENTRYPOINT ["dotnet", "/app/AtcMcpServer.dll"]
