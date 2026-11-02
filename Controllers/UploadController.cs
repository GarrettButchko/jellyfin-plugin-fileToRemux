using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FileToRemux.Controllers;

/// <summary>
/// API controller providing status handshake, library discovery, and streaming MKV uploads for FileToRemux.
/// </summary>
[ApiController]
[Route("FileToRemux")]
[Produces(MediaTypeNames.Application.Json)]
public class UploadController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<UploadController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadController"/> class.
    /// </summary>
    public UploadController(
        ILibraryManager libraryManager,
        IServerConfigurationManager configManager,
        ILogger<UploadController> logger)
    {
        _libraryManager = libraryManager;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>
    /// Status check endpoint used by the FileToRemux macOS app to detect if the plugin is installed.
    /// </summary>
    /// <returns>Plugin metadata and capabilities.</returns>
    [HttpGet("Status")]
    public ActionResult<object> GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        var pluginVersion = Plugin.Instance?.Version;
        var version = pluginVersion is null
            ? "0.0.0"
            : (pluginVersion.Revision > 0 ? pluginVersion.ToString() : pluginVersion.ToString(3));
        return Ok(new
        {
            Plugin = "FileToRemux",
            Installed = true,
            Version = version,
            DirectUploadsEnabled = config?.EnableDirectUploads ?? true,
            ServerName = _configManager.Configuration.ServerName ?? "Jellyfin"
        });
    }

    /// <summary>
    /// Returns available Movie and TV libraries configured on this Jellyfin server, along with their physical server locations.
    /// </summary>
    [HttpGet("Libraries")]
    [Authorize]
    public ActionResult<object> GetLibraries()
    {
        var virtualFolders = _libraryManager.GetVirtualFolders();
        var folders = virtualFolders.Select(vf => new
        {
            Id = vf.ItemId,
            Name = vf.Name,
            CollectionType = vf.CollectionType?.ToString() ?? "unknown",
            Locations = vf.Locations,
            Path = vf.Locations.FirstOrDefault()
        }).ToList();

        return Ok(folders);
    }

    /// <summary>
    /// Streams an MKV movie directly to the destination library folder and refreshes the library item.
    /// </summary>
    [HttpPost("Upload/Movie")]
    [Authorize]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> UploadMovie(
        [FromQuery] string title,
        [FromQuery] string? year,
        [FromQuery] string? imdbId,
        [FromQuery] string? libraryId,
        [FromQuery] string? serverPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { Error = "Title is required." });
        }

        var libraryPath = ResolveLibraryPath(libraryId, serverPath, CollectionTypeOptions.movies);
        if (libraryPath == null)
        {
            return BadRequest(new { Error = "Could not resolve movie library path on server." });
        }

        var safeTitle = SanitizeFilename(title);
        if (string.IsNullOrWhiteSpace(safeTitle))
        {
            return BadRequest(new { Error = "Title does not contain a usable folder name." });
        }

        var safeYear = NormalizeYear(year);
        var safeImdb = NormalizeImdbId(imdbId);
        var folderName = ResolveMediaFolderName(libraryPath, safeTitle, safeYear, safeImdb);

        var movieFolder = Path.Combine(libraryPath, folderName);
        var destMkv = Path.Combine(movieFolder, $"{folderName}.mkv");

        _logger.LogInformation("FileToRemux: Receiving movie upload for {Title} ({Year}) into {Destination}", title, year ?? "unknown", destMkv);

        Directory.CreateDirectory(movieFolder);

        var tempPath = Path.Combine(movieFolder, $"{folderName}.uploading.tmp");

        try
        {
            await using (var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true))
            {
                await Request.Body.CopyToAsync(fileStream, cancellationToken);
            }

            if (System.IO.File.Exists(destMkv))
            {
                System.IO.File.Delete(destMkv);
            }

            System.IO.File.Move(tempPath, destMkv);
            _logger.LogInformation("FileToRemux: Upload completed. Saved to {Path}", destMkv);

            // Queue a library scan for the new folder
            _ = Task.Run(() =>
            {
                try
                {
                    _libraryManager.QueueLibraryScan();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FileToRemux: Error requesting library scan");
                }
            });

            return Ok(new
            {
                Status = "Success",
                Path = destMkv,
                Title = title,
                Year = year ?? string.Empty
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("FileToRemux: Upload canceled for {Title}", title);
            CleanUpTemp(tempPath);
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FileToRemux: Upload failed for {Title}", title);
            CleanUpTemp(tempPath);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Streams an MKV episode directly to the TV series season folder and refreshes the library item.
    /// </summary>
    [HttpPost("Upload/Episode")]
    [Authorize]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> UploadEpisode(
        [FromQuery] string seriesTitle,
        [FromQuery] string? seriesYear,
        [FromQuery] string? seriesImdbId,
        [FromQuery] int season,
        [FromQuery] int episode,
        [FromQuery] string? episodeTitle,
        [FromQuery] string? libraryId,
        [FromQuery] string? serverPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest(new { Error = "SeriesTitle is required." });
        }

        if (season < 0 || episode < 0)
        {
            return BadRequest(new { Error = "Season and episode must be zero or greater." });
        }

        var libraryPath = ResolveLibraryPath(libraryId, serverPath, CollectionTypeOptions.tvshows);
        if (libraryPath == null)
        {
            return BadRequest(new { Error = "Could not resolve TV library path on server." });
        }

        var safeSeries = SanitizeFilename(seriesTitle);
        if (string.IsNullOrWhiteSpace(safeSeries))
        {
            return BadRequest(new { Error = "SeriesTitle does not contain a usable folder name." });
        }

        var safeYear = NormalizeYear(seriesYear);
        var safeImdb = NormalizeImdbId(seriesImdbId);
        var safeEpTitle = SanitizeFilename(episodeTitle ?? string.Empty);

        var seriesFolderName = ResolveMediaFolderName(libraryPath, safeSeries, safeYear, safeImdb);
        var seriesFolder = Path.Combine(libraryPath, seriesFolderName);
        var seasonFolder = Path.Combine(seriesFolder, $"Season {season:D2}");
        var fileName = $"{safeSeries} S{season:D2}E{episode:D2} {safeEpTitle}".Trim() + ".mkv";
        var destMkv = Path.Combine(seasonFolder, fileName);

        _logger.LogInformation("FileToRemux: Receiving episode upload {FileName} into {Destination}", fileName, destMkv);

        Directory.CreateDirectory(seasonFolder);

        var tempPath = Path.Combine(seasonFolder, $"{fileName}.uploading.tmp");

        try
        {
            await using (var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true))
            {
                await Request.Body.CopyToAsync(fileStream, cancellationToken);
            }

            if (System.IO.File.Exists(destMkv))
            {
                System.IO.File.Delete(destMkv);
            }

            System.IO.File.Move(tempPath, destMkv);
            _logger.LogInformation("FileToRemux: Episode upload completed. Saved to {Path}", destMkv);

            _ = Task.Run(() =>
            {
                try
                {
                    _libraryManager.QueueLibraryScan();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FileToRemux: Error requesting library scan");
                }
            });

            return Ok(new
            {
                Status = "Success",
                Path = destMkv,
                Series = seriesTitle,
                Season = season,
                Episode = episode
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("FileToRemux: Episode upload canceled for {FileName}", fileName);
            CleanUpTemp(tempPath);
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FileToRemux: Episode upload failed for {FileName}", fileName);
            CleanUpTemp(tempPath);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
        }
    }

    private string? ResolveLibraryPath(string? libraryId, string? serverPath, CollectionTypeOptions preferredType)
    {
        // 1. Direct explicit server path if provided
        if (!string.IsNullOrWhiteSpace(serverPath))
        {
            return serverPath.Trim();
        }

        var virtualFolders = _libraryManager.GetVirtualFolders();

        // 2. Specific library ID or Name match
        if (!string.IsNullOrEmpty(libraryId))
        {
            var match = virtualFolders.FirstOrDefault(f =>
                string.Equals(f.ItemId, libraryId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Name, libraryId, StringComparison.OrdinalIgnoreCase));
            var location = match?.Locations.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (location != null)
            {
                return location;
            }
        }

        // 3. Preferred collection type match (e.g. movies or tvshows)
        var typed = virtualFolders.FirstOrDefault(f => f.CollectionType == preferredType);
        var typedLocation = typed?.Locations.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (typedLocation != null)
        {
            return typedLocation;
        }

        // 4. Any folder with a location
        return virtualFolders.SelectMany(f => f.Locations).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
    }

    private static string ResolveMediaFolderName(string libraryPath, string safeName, string safeYear, string safeImdb)
    {
        var constructed = safeName;
        if (safeYear.Length > 0)
        {
            constructed += $" ({safeYear})";
        }

        if (safeImdb.Length > 0)
        {
            constructed += $" [imdbid-{safeImdb}]";
        }

        // When the client has no metadata, reuse an existing folder for the same title so
        // repeated uploads don't create a second, split library entry.
        if (safeYear.Length > 0 || safeImdb.Length > 0 || !Directory.Exists(libraryPath))
        {
            return constructed;
        }

        foreach (var directory in Directory.EnumerateDirectories(libraryPath))
        {
            var name = Path.GetFileName(directory);
            if (name != null && MatchesNameWithOptionalMetadata(name, safeName))
            {
                return name;
            }
        }

        return constructed;
    }

    private static bool MatchesNameWithOptionalMetadata(string folderName, string name)
    {
        return string.Equals(folderName, name, StringComparison.OrdinalIgnoreCase)
            || folderName.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase)
            || folderName.StartsWith(name + " [", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();

        // "." and ".." are legal file names but would traverse directories once combined with a path.
        return cleaned is "." or ".." ? "_" : cleaned;
    }

    private static string NormalizeYear(string? year)
    {
        var value = SanitizeFilename(year ?? string.Empty);
        return value.Length == 4 && value.All(c => c >= '0' && c <= '9') ? value : string.Empty;
    }

    private static string NormalizeImdbId(string? imdbId)
    {
        var value = SanitizeFilename(imdbId ?? string.Empty);
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return value.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
            && value.Length > 2
            && value.Skip(2).All(c => c >= '0' && c <= '9')
                ? value.ToLowerInvariant()
                : string.Empty;
    }

    private static void CleanUpTemp(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch { }
    }
}
