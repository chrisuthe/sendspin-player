using Microsoft.Extensions.Logging;
using Sendspin.Core.Platform;

namespace Sendspin.Platform.Linux.Platform;

/// <summary>
/// Linux implementation of platform paths following the XDG Base Directory Specification.
/// </summary>
/// <remarks>
/// Paths follow XDG conventions:
/// - Config: ~/.config/sendspin or $XDG_CONFIG_HOME/sendspin
/// - Data: ~/.local/share/sendspin or $XDG_DATA_HOME/sendspin
/// - Cache: ~/.cache/sendspin or $XDG_CACHE_HOME/sendspin
/// </remarks>
public sealed class LinuxPaths : IPlatformPaths
{
    private const string AppName = "sendspin";
    private readonly ILogger<LinuxPaths>? _logger;

    public LinuxPaths()
    {
        // Design-time or test constructor
    }

    public LinuxPaths(ILogger<LinuxPaths> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string ConfigDirectory => GetXdgPath("XDG_CONFIG_HOME", ".config");

    /// <inheritdoc/>
    public string DataDirectory => GetXdgPath("XDG_DATA_HOME", ".local/share");

    /// <inheritdoc/>
    public string CacheDirectory => GetXdgPath("XDG_CACHE_HOME", ".cache");

    /// <inheritdoc/>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <inheritdoc/>
    public string AlbumArtCacheDirectory => Path.Combine(CacheDirectory, "album-art");

    /// <inheritdoc/>
    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>
    /// Gets the path to the server bookmarks file.
    /// </summary>
    public string BookmarksFile => Path.Combine(ConfigDirectory, "servers.json");

    /// <inheritdoc/>
    public void EnsureDirectoriesExist()
    {
        var directories = new[]
        {
            ConfigDirectory,
            DataDirectory,
            CacheDirectory,
            LogDirectory,
            AlbumArtCacheDirectory
        };

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger?.LogDebug("Created directory: {Directory}", directory);
            }
        }

        _logger?.LogInformation("XDG directories initialized");
    }

    /// <summary>
    /// Gets the XDG-compliant path for the specified environment variable.
    /// </summary>
    private string GetXdgPath(string envVariable, string defaultRelativePath)
    {
        var xdgPath = Environment.GetEnvironmentVariable(envVariable);

        if (!string.IsNullOrEmpty(xdgPath))
        {
            return Path.Combine(xdgPath, AppName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, defaultRelativePath, AppName);
    }
}
