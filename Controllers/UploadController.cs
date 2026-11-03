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
/// API controller providing status handshake, library discovery, progress checking,
/// and resumable streaming MKV uploads for FileToRemux.
/// </summary>
[ApiController]
[Route("FileToRemux")]
[Produces(MediaTypeNames.Application.Json)]
public class UploadController : ControllerBase
{
    private const string UploadPartExtension = ".upload.part";
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
    /// Status check endpoint used by the FileToRemux macOS app.
    /// </summary>
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
            ServerName = _configManager.Configuration.ServerName ?? "Jellyfin",
            SupportsResumableUploads = true
        });
    }

    /// <summary>
    /// Returns available Movie and TV libraries configured on this Jellyfin server.
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
    /// Checks the current upload progress/offset for a movie or TV episode on the server.
    /// </summary>
    [HttpGet("Upload/Progress")]
    [Authorize]
    public ActionResult<object> GetUploadProgress(
        [FromQuery] string? title,
        [FromQuery] string? year,
        [FromQuery] string? imdbId,
        [FromQuery] string? seriesTitle,
        [FromQuery] string? seriesYear,
        [FromQuery] string? seriesImdbId,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        [FromQuery] string? episodeTitle,
        [FromQuery] string? libraryId,
        [FromQuery] string? serverPath)
    {
        bool isMovie = !string.IsNullOrWhiteSpace(title);
        bool isTv = !string.IsNullOrWhiteSpace(seriesTitle);

        if (!isMovie && !isTv)
        {
            return BadRequest(new { Error = "Either title or seriesTitle is required." });
        }

        string destMkv;
        string partPath;

        if (isMovie)
        {
            var libraryPath = ResolveLibraryPath(libraryId, serverPath, CollectionTypeOptions.movies);
            if (libraryPath == null)
            {
                return BadRequest(new { Error = "Could not resolve movie library path." });
            }

            var safeTitle = SanitizeFilename(title!);
            var safeYear = NormalizeYear(year);
            var safeImdb = NormalizeImdbId(imdbId);
            var folderName = ResolveMediaFolderName(libraryPath, safeTitle, safeYear, safeImdb);

            var movieFolder = Path.Combine(libraryPath, folderName);
            destMkv = Path.Combine(movieFolder, $"{folderName}.mkv");
            partPath = Path.Combine(movieFolder, $"{folderName}.mkv{UploadPartExtension}");
        }
        else
        {
            if (season == null || episode == null || season < 0 || episode < 0)
            {
                return BadRequest(new { Error = "Valid season and episode numbers are required for TV episode." });
            }

            var libraryPath = ResolveLibraryPath(libraryId, serverPath, CollectionTypeOptions.tvshows);
            if (libraryPath == null)
            {
                return BadRequest(new { Error = "Could not resolve TV library path." });
            }

            var safeSeries = SanitizeFilename(seriesTitle!);
            var safeYear = NormalizeYear(seriesYear);
            var safeImdb = NormalizeImdbId(seriesImdbId);
            var safeEpTitle = SanitizeFilename(episodeTitle ?? string.Empty);

            var seriesFolderName = ResolveMediaFolderName(libraryPath, safeSeries, safeYear, safeImdb);
            var seriesFolder = Path.Combine(libraryPath, seriesFolderName);
            var seasonFolder = Path.Combine(seriesFolder, $"Season {season.Value:D2}");
            var fileName = $"{safeSeries} S{season.Value:D2}E{episode.Value:D2} {safeEpTitle}".Trim() + ".mkv";

            destMkv = Path.Combine(seasonFolder, fileName);
            partPath = Path.Combine(seasonFolder, $"{fileName}{UploadPartExtension}");
        }

        if (System.IO.File.Exists(destMkv))
        {
            var fi = new FileInfo(destMkv);
            return Ok(new
            {
                Completed = true,
                Offset = fi.Length,
                Path = destMkv
            });
        }

        if (System.IO.File.Exists(partPath))
        {
            var fi = new FileInfo(partPath);
            return Ok(new
            {
                Completed = false,
                Offset = fi.Length,
                PartPath = partPath
            });
        }

        return Ok(new
        {
            Completed = false,
            Offset = 0L
        });
    }

    /// <summary>
    /// Streams an MKV movie directly to the destination library folder and refreshes the library item.
    /// Supports resumable uploads via the offset query parameter.
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
        [FromQuery] long offset = 0,
        [FromQuery] long totalBytes = 0,
        CancellationToken cancellationToken = default)
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
        var partPath = Path.Combine(movieFolder, $"{folderName}.mkv{UploadPartExtension}");

        _logger.LogInformation(
            "FileToRemux: Receiving movie upload for {Title} ({Year}) into {Destination} (offset: {Offset}, total: {Total})",
            title, year ?? "unknown", destMkv, offset, totalBytes);

        Directory.CreateDirectory(movieFolder);

        if (System.IO.File.Exists(destMkv))
        {
            _logger.LogWarning("FileToRemux: Movie upload rejected; destination already exists at {Path}", destMkv);
            return Conflict(new { Error = "A file with this name already exists on the server.", Path = destMkv, Completed = true });
        }

        return await ProcessStreamingUploadAsync(partPath, destMkv, offset, totalBytes, title, year, cancellationToken);
    }

    /// <summary>
    /// Streams an MKV episode directly to the TV series season folder and refreshes the library item.
    /// Supports resumable uploads via the offset query parameter.
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
        [FromQuery] long offset = 0,
        [FromQuery] long totalBytes = 0,
        CancellationToken cancellationToken = default)
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
        var partPath = Path.Combine(seasonFolder, $"{fileName}{UploadPartExtension}");

        _logger.LogInformation(
            "FileToRemux: Receiving episode upload {FileName} into {Destination} (offset: {Offset}, total: {Total})",
            fileName, destMkv, offset, totalBytes);

        Directory.CreateDirectory(seasonFolder);

        if (System.IO.File.Exists(destMkv))
        {
            _logger.LogWarning("FileToRemux: Episode upload rejected; destination already exists at {Path}", destMkv);
            return Conflict(new { Error = "A file with this name already exists on the server.", Path = destMkv, Completed = true });
        }

        return await ProcessStreamingUploadAsync(partPath, destMkv, offset, totalBytes, fileName, null, cancellationToken);
    }

    private async Task<IActionResult> ProcessStreamingUploadAsync(
        string partPath,
        string destMkv,
        long offset,
        long totalBytes,
        string titleOrFile,
        string? year,
        CancellationToken cancellationToken)
    {
        TempFileTracker.RegisterActive(partPath);

        try
        {
            FileMode mode;
            if (offset == 0)
            {
                mode = FileMode.Create;
            }
            else
            {
                if (!System.IO.File.Exists(partPath))
                {
                    _logger.LogWarning("FileToRemux: Resume requested at offset {Offset} but part file does not exist {PartPath}", offset, partPath);
                    return Conflict(new { Error = "Part file does not exist on server.", ServerOffset = 0L });
                }

                var existingLen = new FileInfo(partPath).Length;
                if (existingLen != offset)
                {
                    _logger.LogWarning("FileToRemux: Offset mismatch for {Path}. Client sent {ClientOffset}, server has {ServerOffset}",
                        partPath, offset, existingLen);
                    return Conflict(new { Error = "Offset mismatch.", ServerOffset = existingLen });
                }

                mode = FileMode.Open;
            }

            await using (var fileStream = new FileStream(
                partPath,
                mode,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true))
            {
                if (offset > 0)
                {
                    fileStream.Seek(offset, SeekOrigin.Begin);
                }

                await Request.Body.CopyToAsync(fileStream, cancellationToken);
                await fileStream.FlushAsync(cancellationToken);
            }

            // Check if final destination was created concurrently
            if (System.IO.File.Exists(destMkv))
            {
                _logger.LogWarning("FileToRemux: Upload destination already exists at {Path}", destMkv);
                CleanUpFile(partPath);
                return Conflict(new { Error = "A file with this name already exists on the server.", Path = destMkv });
            }

            // Verify final length if totalBytes was provided
            var finalPartLen = new FileInfo(partPath).Length;
            if (totalBytes > 0 && finalPartLen < totalBytes)
            {
                _logger.LogWarning("FileToRemux: Upload ended short of totalBytes. Expected {Total}, got {Actual}", totalBytes, finalPartLen);
                return Ok(new
                {
                    Status = "Partial",
                    Offset = finalPartLen,
                    TotalBytes = totalBytes
                });
            }

            // Atomic rename to final MKV
            System.IO.File.Move(partPath, destMkv);
            _logger.LogInformation("FileToRemux: Upload completed successfully. Saved to {Path} ({Size} bytes)", destMkv, finalPartLen);

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
                Title = titleOrFile,
                Year = year ?? string.Empty,
                Size = finalPartLen
            });
        }
        catch (OperationCanceledException)
        {
            long savedOffset = 0;
            if (System.IO.File.Exists(partPath))
            {
                savedOffset = new FileInfo(partPath).Length;
            }
            _logger.LogInformation("FileToRemux: Upload stream paused/interrupted for {Title}. Preserved {Bytes} bytes on server.", titleOrFile, savedOffset);
            return StatusCode(StatusCodes.Status499ClientClosedRequest, new
            {
                Status = "Paused",
                Offset = savedOffset
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FileToRemux: Upload failed for {Title}", titleOrFile);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
        }
        finally
        {
            TempFileTracker.UnregisterActive(partPath);
        }
    }

    private string? ResolveLibraryPath(string? libraryId, string? serverPath, CollectionTypeOptions preferredType)
    {
        if (!string.IsNullOrWhiteSpace(serverPath))
        {
            return serverPath.Trim();
        }

        var virtualFolders = _libraryManager.GetVirtualFolders();

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

        var typed = virtualFolders.FirstOrDefault(f => f.CollectionType == preferredType);
        var typedLocation = typed?.Locations.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (typedLocation != null)
        {
            return typedLocation;
        }

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

        if (!Directory.Exists(libraryPath))
        {
            return constructed;
        }

        string? compatible = null;
        foreach (var directory in Directory.EnumerateDirectories(libraryPath))
        {
            var folderName = Path.GetFileName(directory);
            if (folderName == null ||
                !TryParseMediaFolderName(folderName, out var existingName, out var existingYear, out var existingImdb) ||
                !string.Equals(existingName, safeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (existingYear.Length > 0 && safeYear.Length > 0 &&
                !string.Equals(existingYear, safeYear, StringComparison.Ordinal))
            {
                continue;
            }

            if (existingImdb.Length > 0 && safeImdb.Length > 0 &&
                !string.Equals(existingImdb, safeImdb, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(folderName, constructed, StringComparison.OrdinalIgnoreCase))
            {
                return folderName;
            }

            compatible ??= folderName;
        }

        return compatible ?? constructed;
    }

    private static bool TryParseMediaFolderName(string folderName, out string name, out string year, out string imdb)
    {
        name = folderName.Trim();
        year = string.Empty;
        imdb = string.Empty;

        const string imdbMarker = " [imdbid-";
        var imdbIndex = name.LastIndexOf(imdbMarker, StringComparison.OrdinalIgnoreCase);
        if (imdbIndex >= 0 && name.EndsWith("]", StringComparison.Ordinal))
        {
            var value = name[(imdbIndex + imdbMarker.Length)..^1].Trim();
            imdb = value.Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;
            name = name[..imdbIndex].TrimEnd();
        }

        const string yearMarker = " (";
        var yearIndex = name.LastIndexOf(yearMarker, StringComparison.Ordinal);
        if (yearIndex >= 0 && name.EndsWith(")", StringComparison.Ordinal))
        {
            var value = name[(yearIndex + yearMarker.Length)..^1];
            if (value.Length == 4 && value.All(c => c >= '0' && c <= '9'))
            {
                year = value;
                name = name[..yearIndex].TrimEnd();
            }
        }

        return name.Length > 0;
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
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

    private static void CleanUpFile(string path)
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
