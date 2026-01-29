namespace Sendspin.Core.Discord;

/// <summary>
/// Service for integrating Discord Rich Presence to show current playback status.
/// </summary>
public interface IDiscordRichPresenceService : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the service is currently connected to Discord.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Initializes the Discord Rich Presence connection.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous initialization operation.</returns>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates the Discord presence with current playback information.
    /// </summary>
    /// <param name="trackTitle">The current track title, or null if no track.</param>
    /// <param name="artist">The artist name, or null if unknown.</param>
    /// <param name="serverName">The Sendspin server name, or null if not connected.</param>
    /// <param name="isPlaying">True if currently playing, false if paused/stopped.</param>
    void UpdatePresence(string? trackTitle, string? artist, string? serverName, bool isPlaying);

    /// <summary>
    /// Clears the Discord presence.
    /// </summary>
    void ClearPresence();
}
