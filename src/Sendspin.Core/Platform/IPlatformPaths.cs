namespace Sendspin.Core.Platform;

/// <summary>
/// Platform-specific application directory paths.
/// Implementations should follow platform conventions:
/// - Linux: XDG Base Directory Specification
/// - Windows: AppData/Local, AppData/Roaming
/// - macOS: ~/Library/Application Support, ~/Library/Caches
/// - iOS/Android: App-specific sandboxed directories
/// </summary>
public interface IPlatformPaths
{
    /// <summary>
    /// Configuration directory for user settings.
    /// </summary>
    string ConfigDirectory { get; }

    /// <summary>
    /// Data directory for persistent application data.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>
    /// Cache directory for temporary/clearable data.
    /// </summary>
    string CacheDirectory { get; }

    /// <summary>
    /// Log files directory.
    /// </summary>
    string LogDirectory { get; }

    /// <summary>
    /// Album art cache directory.
    /// </summary>
    string AlbumArtCacheDirectory { get; }

    /// <summary>
    /// Path to the main configuration file.
    /// </summary>
    string ConfigFile { get; }

    /// <summary>
    /// Ensures all required directories exist, creating them if necessary.
    /// </summary>
    void EnsureDirectoriesExist();
}
