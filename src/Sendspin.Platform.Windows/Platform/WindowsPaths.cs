using Sendspin.Core.Platform;

namespace Sendspin.Platform.Windows.Platform;

/// <summary>
/// Windows implementation of platform paths using %LocalAppData%.
/// All application data is stored under %LocalAppData%\Sendspin.
/// </summary>
public sealed class WindowsPaths : IPlatformPaths
{
    private const string AppName = "Sendspin";
    private readonly string _baseDirectory;

    public WindowsPaths()
    {
        // Use LocalAppData for all app data (non-roaming)
        // This is the standard location for Windows desktop apps
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _baseDirectory = Path.Combine(localAppData, AppName);
    }

    /// <inheritdoc/>
    public string ConfigDirectory => Path.Combine(_baseDirectory, "config");

    /// <inheritdoc/>
    public string DataDirectory => Path.Combine(_baseDirectory, "data");

    /// <inheritdoc/>
    public string CacheDirectory => Path.Combine(_baseDirectory, "cache");

    /// <inheritdoc/>
    public string LogDirectory => Path.Combine(_baseDirectory, "logs");

    /// <inheritdoc/>
    public string AlbumArtCacheDirectory => Path.Combine(CacheDirectory, "artwork");

    /// <inheritdoc/>
    public string ConfigFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <inheritdoc/>
    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(AlbumArtCacheDirectory);
    }
}
