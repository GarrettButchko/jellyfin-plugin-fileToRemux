using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FileToRemux;

/// <summary>
/// Plugin configuration for FileToRemux.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether direct uploads from FileToRemux are enabled.
    /// </summary>
    public bool EnableDirectUploads { get; set; } = true;

    /// <summary>
    /// Gets or sets the temporary upload buffer path. If null, the system temp directory is used.
    /// </summary>
    public string? CustomTempUploadPath { get; set; }
}
