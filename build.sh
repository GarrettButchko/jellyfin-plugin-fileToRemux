#!/bin/bash
set -e

# Jellyfin.Plugin.FileToRemux Build Script
# This builds the plugin DLL for Jellyfin 10.9.x+

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="${SCRIPT_DIR}/bin/Publish"
mkdir -p "${OUTPUT_DIR}"

echo "=========================================="
echo " Building Jellyfin.Plugin.FileToRemux "
echo "=========================================="

if command -v dotnet >/dev/null 2>&1; then
    echo "Found local .NET SDK: $(dotnet --version)"
    dotnet publish "${SCRIPT_DIR}/Jellyfin.Plugin.FileToRemux.csproj" -c Release -o "${OUTPUT_DIR}"
elif command -v docker >/dev/null 2>&1; then
    echo "Local dotnet CLI not found, using Docker (.NET 8 SDK container)..."
    docker run --rm \
        -v "${SCRIPT_DIR}":/src \
        -w /src \
        mcr.microsoft.com/dotnet/sdk:8.0 \
        dotnet publish Jellyfin.Plugin.FileToRemux.csproj -c Release -o /src/bin/Publish
else
    echo "Error: Neither 'dotnet' nor 'docker' was found on your system."
    echo "Please install the .NET 8.0 SDK (https://dotnet.microsoft.com/download) or Docker to compile this plugin."
    exit 1
fi

echo ""
echo " Build successful!"
echo "Plugin DLL located at: ${OUTPUT_DIR}/Jellyfin.Plugin.FileToRemux.dll"
echo ""
echo "Installation:"
echo "1. Copy 'Jellyfin.Plugin.FileToRemux.dll' into your Jellyfin 'plugins/FileToRemux/' directory."
echo "2. Restart your Jellyfin server."
echo "3. FileToRemux Mac app will automatically detect it!"
