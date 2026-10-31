using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
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
        return Ok(new
        {
            Plugin = "FileToRemux",
            Installed = true,
            Version = "1.0.0",
            DirectUploadsEnabled = config?.EnableDirectUploads ?? true,
            ServerName = _configManager.Configuration.ServerName ?? "Jellyfin"
        });
    }

    /// <summary>
    /// Returns available Movie and TV libraries configured on this Jellyfin server.
    /// </summary>
    [HttpGet("Libraries")]
    [Authorize(Policy = "DefaultAuthorization")]
    public ActionResult<object> GetLibraries()
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Select(f => new
            {
                Id = f.ItemId,
                Name = f.Name,
                CollectionType = f.CollectionType?.ToString() ?? "unknown",
                Path = f.Locations.FirstOrDefault() ?? string.Empty
            })
            .ToList();

        return Ok(folders);
    }

    /// <summary>
    /// Streams an MKV movie directly to the destination library folder and refreshes the library item.
    /// </summary>
    [HttpPost("Upload/Movie")]
    [Authorize(Policy = "DefaultAuthorization")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> UploadMovie(
        [FromQuery] string title,
        [FromQuery] string year,
        [FromQuery] string imdbId,
        [FromQuery] string? libraryId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(year))
        {
            return BadRequest(new { Error = "Title and Year are required." });
        }

        var libraryPath = ResolveLibraryPath(libraryId, "movies");
        if (libraryPath == null)
        {
            return BadRequest(new { Error = "Could not resolve movie library path on server." });
        }

        var safeTitle = SanitizeFilename(title);
        var safeImdb = SanitizeFilename(imdbId ?? "none");
        var folderName = $"{safeTitle} ({year}) [imdbid-{safeImdb}]";
        var movieFolder = Path.Combine(libraryPath, folderName);
        var destMkv = Path.Combine(movieFolder, $"{folderName}.mkv");

        _logger.LogInformation("FileToRemux: Receiving movie upload for {Title} ({Year}) into {Destination}", title, year, destMkv);

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
                    _logger.LogError(ex, "FileToRemux: Error refreshing library scan for {Path}", movieFolder);
                }
            });

            return Ok(new
            {
                Status = "Success",
                Path = destMkv,
                Title = title,
                Year = year
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
    [Authorize(Policy = "DefaultAuthorization")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> UploadEpisode(
        [FromQuery] string seriesTitle,
        [FromQuery] string seriesYear,
        [FromQuery] string seriesImdbId,
        [FromQuery] int season,
        [FromQuery] int episode,
        [FromQuery] string episodeTitle,
        [FromQuery] string? libraryId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest(new { Error = "SeriesTitle is required." });
        }

        var libraryPath = ResolveLibraryPath(libraryId, "tvshows");
        if (libraryPath == null)
        {
            return BadRequest(new { Error = "Could not resolve TV library path on server." });
        }

        var safeSeries = SanitizeFilename(seriesTitle);
        var safeImdb = SanitizeFilename(seriesImdbId ?? "none");
        var safeEpTitle = SanitizeFilename(episodeTitle ?? "");
        var seriesFolder = Path.Combine(libraryPath, $"{safeSeries} ({seriesYear}) [imdbid-{safeImdb}]");
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
                    _logger.LogError(ex, "FileToRemux: Error refreshing library scan for {Path}", seasonFolder);
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

    private string? ResolveLibraryPath(string? libraryId, string preferredType)
    {
        var folders = _libraryManager.GetVirtualFolders();

        if (!string.IsNullOrEmpty(libraryId))
        {
            var match = folders.FirstOrDefault(f => string.Equals(f.ItemId, libraryId, StringComparison.OrdinalIgnoreCase));
            if (match?.Locations?.Length > 0)
            {
                return match.Locations[0];
            }
        }

        var typed = folders.FirstOrDefault(f => string.Equals(f.CollectionType?.ToString(), preferredType, StringComparison.OrdinalIgnoreCase));
        return typed?.Locations?.FirstOrDefault() ?? folders.FirstOrDefault()?.Locations?.FirstOrDefault();
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
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
