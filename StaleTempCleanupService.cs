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
/// Removes upload temp and part files left behind by interrupted uploads that are older than 7 days,
/// or legacy .uploading.tmp files.
/// </summary>
public sealed class StaleTempCleanupService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StaleAgeThreshold = TimeSpan.FromDays(7);

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

        _logger.LogInformation("FileToRemux: Starting upload temp cleanup");
        RunStartupScan();
    }

    private void RunStartupScan()
    {
        var candidates = 0;
        var removed = 0;
        var now = DateTime.UtcNow;

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

                // Legacy tmp files
                foreach (var file in Directory.EnumerateFiles(root, "*.uploading.tmp", options))
                {
                    candidates++;
                    if (TryDelete(file))
                    {
                        removed++;
                    }
                }

                // Resumable part files older than 7 days and not currently in active transfer
                foreach (var file in Directory.EnumerateFiles(root, "*.upload.part", options))
                {
                    if (TempFileTracker.IsActive(file))
                    {
                        continue;
                    }

                    try
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(file);
                        if (now - lastWrite > StaleAgeThreshold)
                        {
                            candidates++;
                            if (TryDelete(file))
                            {
                                removed++;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FileToRemux: Could not scan {Root} for stale upload temp files", root);
            }
        }

        if (candidates > 0)
        {
            _logger.LogInformation(
                "FileToRemux: Startup temp cleanup removed {Removed} of {Candidates} stale temp file(s)",
                removed,
                candidates);
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
            _logger.LogDebug(ex, "FileToRemux: Stale temp file is still locked {Path}", file);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileToRemux: Could not remove stale upload temp file {Path}", file);
            return false;
        }
    }
}
