using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.FileToRemux;

/// <summary>
/// Tracks in-flight upload temp files and recently used upload folders so the background
/// cleaner never deletes an active transfer.
/// </summary>
internal static class TempFileTracker
{
    private static readonly ConcurrentDictionary<string, byte> ActiveTemps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> RecentFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks a temp file as belonging to an in-flight upload.
    /// </summary>
    public static void RegisterActive(string path) => ActiveTemps[path] = 0;

    /// <summary>
    /// Clears the in-flight marker once the upload has finished or failed.
    /// </summary>
    public static void UnregisterActive(string path) => ActiveTemps.TryRemove(path, out _);

    /// <summary>
    /// Returns whether the given temp file is still being written by an upload.
    /// </summary>
    public static bool IsActive(string path) => ActiveTemps.ContainsKey(path);

    /// <summary>
    /// Remembers a folder that received an upload so the cleaner can retry locked files there.
    /// </summary>
    public static void TrackFolder(string folder) => RecentFolders[folder] = DateTime.UtcNow;

    /// <summary>
    /// Returns the folders touched by uploads in the last 24 hours.
    /// </summary>
    public static IReadOnlyCollection<string> GetTrackedFolders()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var pair in RecentFolders)
        {
            if (pair.Value < cutoff)
            {
                RecentFolders.TryRemove(pair.Key, out _);
            }
        }

        return RecentFolders.Keys.ToList();
    }
}
