using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Sendspin.Core.Notifications;

namespace Sendspin.Platform.Windows.Notifications;

/// <summary>
/// Windows Toast notification service using the Windows Community Toolkit.
/// </summary>
/// <remarks>
/// Uses the Microsoft.Toolkit.Uwp.Notifications package to display native
/// Windows 10/11 toast notifications in the system notification area and Action Center.
/// </remarks>
public sealed class WindowsNotificationService : INotificationService
{
    private readonly ILogger<WindowsNotificationService> _logger;
    private readonly object _lock = new();
    private bool _isInitialized;
    private bool _disposed;

    /// <summary>
    /// Tag used for track change notifications, allowing replacement of previous track notifications.
    /// </summary>
    private const string TrackNotificationTag = "track";

    /// <summary>
    /// Tag used for connection notifications.
    /// </summary>
    private const string ConnectionNotificationTag = "connection";

    public WindowsNotificationService(ILogger<WindowsNotificationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsNotificationService));

        lock (_lock)
        {
            if (_isInitialized) return Task.CompletedTask;

            try
            {
                // Clear any old notifications from previous sessions
                ToastNotificationManagerCompat.History.Clear();
                _isInitialized = true;
                _logger.LogInformation("Windows Toast notification service initialized");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize Toast notifications");
                // Continue anyway - notifications will just fail silently
                _isInitialized = true;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ShowTrackChangeAsync(string title, string artist, string? albumArtPath = null)
    {
        if (_disposed || !_isInitialized) return Task.CompletedTask;

        try
        {
            var builder = new ToastContentBuilder()
                .AddText(string.IsNullOrWhiteSpace(title) ? "Now Playing" : title);

            if (!string.IsNullOrWhiteSpace(artist))
            {
                builder.AddText(artist);
            }

            // Add album art if available
            if (!string.IsNullOrEmpty(albumArtPath) && File.Exists(albumArtPath))
            {
                builder.AddAppLogoOverride(new Uri(albumArtPath));
            }

            builder.Show(toast =>
            {
                toast.Tag = TrackNotificationTag;
                toast.ExpirationTime = DateTimeOffset.Now.AddSeconds(10);
            });

            _logger.LogDebug("Track notification shown: {Title} - {Artist}", title, artist);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show track notification");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ShowConnectionStatusAsync(string serverName, bool connected)
    {
        if (_disposed || !_isInitialized) return Task.CompletedTask;

        try
        {
            var title = connected ? "Connected" : "Disconnected";
            var body = connected
                ? $"Connected to {serverName}"
                : $"Disconnected from {serverName}";

            new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .Show(toast =>
                {
                    toast.Tag = ConnectionNotificationTag;
                    toast.ExpirationTime = DateTimeOffset.Now.AddSeconds(5);
                });

            _logger.LogDebug("Connection notification shown: {Status} - {Server}", title, serverName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show connection notification");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task CloseNotificationAsync(uint notificationId)
    {
        // Windows Toast notifications don't support closing by numeric ID
        // We use tags for notification management instead
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;

        lock (_lock)
        {
            _disposed = true;

            try
            {
                // Clear notifications on dispose
                ToastNotificationManagerCompat.History.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clear notifications on dispose");
            }
        }

        return ValueTask.CompletedTask;
    }
}
