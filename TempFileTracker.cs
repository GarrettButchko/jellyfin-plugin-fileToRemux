using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyBoiUploadFile;

/// <summary>
/// Tracks in-flight upload temp files so the startup cleaner never deletes an active transfer.
/// </summary>
internal static class TempFileTracker
{
    private static readonly ConcurrentDictionary<string, byte> ActiveTemps = new(StringComparer.OrdinalIgnoreCase);

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
}
