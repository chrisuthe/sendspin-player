using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Sendspin.Core.Discord;
using Sendspin.Core.Notifications;
using Sendspin.Core.Platform;
using Sendspin.Platform.Shared.Discord;
using Sendspin.Platform.Windows.Audio;
using Sendspin.Platform.Windows.Notifications;
using Sendspin.SDK.Audio;

namespace Sendspin.Platform.Windows.Platform;

/// <summary>
/// Windows platform initializer that registers Windows-specific service implementations.
/// </summary>
public sealed class WindowsPlatformInitializer : IPlatformInitializer
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection services)
    {
        // Platform paths (Windows AppData)
        services.AddSingleton<IPlatformPaths, WindowsPaths>();

        // Audio services - WASAPI via NAudio
        services.AddTransient<IAudioPlayer>(sp =>
            new WasapiAudioPlayer(sp.GetRequiredService<ILogger<WasapiAudioPlayer>>()));
        services.AddSingleton<IAudioDeviceEnumerator>(sp =>
            new WasapiAudioPlayer(sp.GetRequiredService<ILogger<WasapiAudioPlayer>>()));

        // Notification service - Windows Toast notifications
        services.AddSingleton<INotificationService, WindowsNotificationService>();

        // Discord Rich Presence (cross-platform implementation from Platform.Shared)
        services.AddSingleton<IDiscordRichPresenceService, DiscordRichPresenceService>();
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // Windows-specific initialization (if needed in the future)
        // Examples: Audio device change notification registration, COM initialization, etc.
        return Task.CompletedTask;
    }
}
