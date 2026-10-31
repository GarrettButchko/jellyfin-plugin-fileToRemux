# FileToRemux Jellyfin Plugin

Direct high-speed streaming upload and auto-organization plugin for Jellyfin.

## Features
- **Auto-Detection**: The FileToRemux macOS app automatically detects when this plugin is installed on your Jellyfin server.
- **Direct HTTP Streaming**: Streams large MKVs directly to disk chunk-by-chunk without loading the entire movie into server RAM.
- **Smart Organization**: Automatically creates standard Jellyfin folder structures (`Movie (Year) [imdbid-tt...]/...mkv` and `TV/Season XX/...mkv`).
- **Instant Indexing**: Automatically triggers Jellyfin's internal library scanner the moment an upload completes.

## How to Build

### Option A: Using the Build Script (Automatic)
Run the included build script in Terminal:
```bash
chmod +x build.sh
./build.sh
```
*Note: If you have Docker installed, the script will automatically use a .NET 8 container to build without needing to install anything else!*

### Option B: Using .NET SDK Directly
```bash
dotnet publish Jellyfin.Plugin.FileToRemux.csproj -c Release -o bin/Publish
```

## How to Install into Jellyfin

1. Locate your Jellyfin `plugins` folder:
   - **Docker / Linux**: `/config/plugins/` or `/var/lib/jellyfin/plugins/`
   - **Windows**: `C:\ProgramData\Jellyfin\Server\plugins\`
   - **macOS**: `~/.local/share/jellyfin/plugins/`
2. Create a folder named `FileToRemux`:
   ```bash
   mkdir -p /path/to/jellyfin/plugins/FileToRemux
   ```
3. Copy `bin/Publish/Jellyfin.Plugin.FileToRemux.dll` into that folder.
4. Restart your Jellyfin server.
5. In FileToRemux on macOS, enter your Jellyfin URL and API key in **Settings**. The green "Plugin Detected" badge will light up!
