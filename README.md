# Jellyfin.Plugin.FileToRemux

A Jellyfin plugin that enables seamless direct streaming uploads and auto-organization from [FileToRemux](https://filetoremux.com).

## Installation via Plugin Repository (Recommended)

1. In your Jellyfin server, go to **Dashboard → Plugins → Repositories**
2. Click **+** and add:
   - **Name:** `FileToRemux`
   - **URL:** `https://garrettbutchko.github.io/jellyfin-plugin-fileToRemux/manifest.json`
3. Go to **Catalog**, find **FileToRemux**, and click **Install**
4. Restart Jellyfin when prompted

Jellyfin will automatically notify you of future updates.

## Compatibility

| Plugin Version | Jellyfin Version |
|---|---|
| 1.x | 10.9.x |

## Development

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)

### Build locally

```bash
dotnet build
```

### Releasing a new version

1. Bump the `<Version>` in `Jellyfin.Plugin.FileToRemux.csproj`
2. Commit and push
3. Tag and push the tag:
   ```bash
   git tag v1.0.1
   git push origin v1.0.1
   ```

GitHub Actions will automatically:
- Build and zip the plugin
- Create a GitHub Release with the zip attached
- Update `manifest.json` on GitHub Pages so Jellyfin servers pick up the update

## License

MIT
