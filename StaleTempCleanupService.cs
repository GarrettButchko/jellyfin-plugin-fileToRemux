using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FileToRemux;

/// <summary>
/// Removes upload temp files left behind by interrupted uploads. On Windows a temp file that
/// was still open could not be deleted by the upload request itself, so the cleaner retries
/// it later instead of leaving it in the library forever.
/// </summary>
public sealed class StaleTempCleanupService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FolderScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FullScanInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RecentFileAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecentFolderFileAge = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StaleTempCleanupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StaleTempCleanupService"/> class.
    /// </summary>
    public StaleTempCleanupService(ILibraryManager libraryManager, ILogger<StaleTempCleanupService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Nothing can be uploading across a server restart, so every temp file is stale now.
        RunFullScan(TimeSpan.Zero);

        var lastFullScan = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FolderScanInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                ScanTrackedFolders();

                if (DateTime.UtcNow - lastFullScan >= FullScanInterval)
                {
                    RunFullScan(RecentFileAge);
                    lastFullScan = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FileToRemux: Error while cleaning upload temp files");
            }
        }
    }

    private void RunFullScan(TimeSpan minimumAge)
    {
        foreach (var root in GetLibraryRoots())
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                };

                foreach (var file in Directory.EnumerateFiles(root, "*.uploading.tmp", options))
                {
                    if (TempFileTracker.IsActive(file))
                    {
                        continue;
                    }

                    if (DateTime.UtcNow - SafeLastWriteTimeUtc(file) < minimumAge)
                    {
                        continue;
                    }

                    if (!TryDelete(file))
                    {
                        // Still open by a lingering request; retry this folder every 30 seconds.
                        var folder = Path.GetDirectoryName(file);
                        if (!string.IsNullOrEmpty(folder))
                        {
                            TempFileTracker.TrackFolder(folder);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FileToRemux: Could not scan {Root} for stale upload temp files", root);
            }
        }
    }

    private void ScanTrackedFolders()
    {
        foreach (var folder in TempFileTracker.GetTrackedFolders())
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(folder, "*.uploading.tmp"))
                {
                    if (TempFileTracker.IsActive(file))
                    {
                        continue;
                    }

                    if (DateTime.UtcNow - SafeLastWriteTimeUtc(file) < RecentFolderFileAge)
                    {
                        continue;
                    }

                    TryDelete(file);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Folder was removed; nothing to clean.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FileToRemux: Could not scan {Folder} for stale upload temp files", folder);
            }
        }
    }

    private IEnumerable<string> GetLibraryRoots()
    {
        try
        {
            return _libraryManager.GetVirtualFolders()
                .SelectMany(f => f.Locations)
                .Where(location => !string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileToRemux: Could not enumerate library paths for temp cleanup");
            return Array.Empty<string>();
        }
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            _logger.LogInformation("FileToRemux: Removed stale upload temp file {Path}", file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileToRemux: Could not remove stale upload temp file {Path}", file);
            return false;
        }
    }
}
