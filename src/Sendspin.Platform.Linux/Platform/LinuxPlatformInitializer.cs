using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Sendspin.Core.Discord;
using Sendspin.Core.Notifications;
using Sendspin.Core.Platform;
using Sendspin.Platform.Linux.Audio;
using Sendspin.Platform.Linux.Notifications;
using Sendspin.Platform.Shared.Discord;
using Sendspin.SDK.Audio;

namespace Sendspin.Platform.Linux.Platform;

/// <summary>
/// Linux platform initializer that registers Linux-specific service implementations.
/// </summary>
public sealed class LinuxPlatformInitializer : IPlatformInitializer
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection services)
    {
        // Platform paths (XDG compliant)
        services.AddSingleton<IPlatformPaths, LinuxPaths>();

        // Audio services
        services.AddTransient<IAudioPlayer>(sp =>
            new OpenALAudioPlayer(sp.GetRequiredService<ILogger<OpenALAudioPlayer>>()));
        services.AddSingleton<IAudioDeviceEnumerator>(sp =>
            new OpenALAudioPlayer(sp.GetRequiredService<ILogger<OpenALAudioPlayer>>()));

        // Notification service
        services.AddSingleton<INotificationService, LinuxNotificationService>();

        // Discord Rich Presence (cross-platform implementation from Platform.Shared)
        services.AddSingleton<IDiscordRichPresenceService, DiscordRichPresenceService>();
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // Linux-specific initialization (if needed in the future)
        // Examples: D-Bus session verification, PipeWire/PulseAudio detection, etc.
        return Task.CompletedTask;
    }
}
