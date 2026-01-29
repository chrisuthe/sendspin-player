using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using SendspinClient.Linux.Services.Audio;

namespace SendspinClient.Linux.Services.Client;

/// <summary>
/// Manages Sendspin server discovery, connection, and audio playback.
/// Coordinates between the SDK components and the Linux audio player.
/// </summary>
public sealed class SendspinClientManager : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SendspinClientManager> _logger;
    private readonly Func<IAudioPlayer> _playerFactory;

    private MdnsServerDiscovery? _discovery;
    private SendspinConnection? _connection;
    private IClockSynchronizer? _clockSync;
    private IAudioPipeline? _audioPipeline;
    private SendspinClientService? _client;
    private HttpClient? _httpClient;
    private CancellationTokenSource? _shutdownCts;
    private string? _lastArtworkUrl;
    private bool _isDisposed;

    /// <summary>
    /// Fired when a previously discovered server is lost.
    /// </summary>
    public event EventHandler<DiscoveredServer>? ServerLost;

    /// <summary>
    /// Fired when connection state changes.
    /// </summary>
    public event EventHandler<ConnectionStateEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Fired when track metadata changes.
    /// </summary>
    public event EventHandler<TrackMetadataEventArgs>? TrackChanged;

    /// <summary>
    /// Fired when album artwork is received or cleared.
    /// </summary>
    public event EventHandler<ArtworkEventArgs>? ArtworkChanged;

    /// <summary>
    /// Fired when playback state changes (playing/paused/stopped).
    /// </summary>
    public event EventHandler<PlaybackStateEventArgs>? PlaybackStateChanged;

    /// <summary>
    /// Fired when a server has been verified via discovery handshake.
    /// Only verified (reachable) servers should be shown to users.
    /// </summary>
    public event EventHandler<ProbedServerInfo>? ServerVerified;

    /// <summary>
    /// Gets whether the client is currently connected to a server.
    /// </summary>
    public bool IsConnected => _connection?.State == ConnectionState.Connected;

    /// <summary>
    /// Gets the current audio buffer statistics for sync monitoring.
    /// </summary>
    public AudioBufferStats? BufferStats => _audioPipeline?.BufferStats;

    /// <summary>
    /// Gets the current server name if connected.
    /// </summary>
    public string? ServerName => _client?.ServerName;

    public SendspinClientManager(
        ILoggerFactory loggerFactory,
        Func<IAudioPlayer> playerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<SendspinClientManager>();
        _playerFactory = playerFactory ?? throw new ArgumentNullException(nameof(playerFactory));
    }

    /// <summary>
    /// Starts discovering Sendspin servers on the local network.
    /// </summary>
    public async Task StartDiscoveryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _logger.LogInformation("Starting server discovery...");

        _discovery = new MdnsServerDiscovery(_loggerFactory.CreateLogger<MdnsServerDiscovery>());
        _discovery.ServerFound += OnServerFound;
        _discovery.ServerLost += OnServerLost;

        await _discovery.StartAsync(ct);
    }

    /// <summary>
    /// Stops server discovery.
    /// </summary>
    public async Task StopDiscoveryAsync()
    {
        if (_isDisposed || _discovery == null) return;

        _discovery.ServerFound -= OnServerFound;
        _discovery.ServerLost -= OnServerLost;
        await _discovery.StopAsync();
        _discovery = null;

        _logger.LogInformation("Server discovery stopped");
    }

    /// <summary>
    /// Gets the list of currently discovered servers.
    /// </summary>
    public IReadOnlyList<DiscoveredServer> GetDiscoveredServers()
    {
        return _discovery?.Servers.ToList() ?? [];
    }

    /// <summary>
    /// Probes a discovered server to verify it's reachable and get authoritative info.
    /// Uses raw WebSocket to perform lightweight handshake without full SDK setup.
    /// </summary>
    public async Task<ProbedServerInfo?> ProbeServerAsync(DiscoveredServer server, CancellationToken ct = default)
    {
        var host = server.IpAddresses.FirstOrDefault() ?? server.Host;
        var uri = new Uri($"ws://{host}:{server.Port}/sendspin");

        _logger.LogDebug("Probing server at {Uri} (IPs: {IpAddresses})", uri, string.Join(", ", server.IpAddresses));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5)); // 5 second timeout for probe

        using var ws = new System.Net.WebSockets.ClientWebSocket();

        try
        {
            // Connect
            _logger.LogDebug("Connecting WebSocket to {Uri}...", uri);
            await ws.ConnectAsync(uri, cts.Token);
            _logger.LogDebug("WebSocket connected, sending client/hello...");

            // Send client/hello
            var clientHello = CreateClientHelloJson();
            _logger.LogDebug("Sending: {Message}", clientHello);
            var sendBuffer = System.Text.Encoding.UTF8.GetBytes(clientHello);
            await ws.SendAsync(sendBuffer, System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);
            _logger.LogDebug("client/hello sent, waiting for server/hello...");

            // Receive server/hello
            var receiveBuffer = new byte[4096];
            var result = await ws.ReceiveAsync(receiveBuffer, cts.Token);
            _logger.LogDebug("Received message: type={Type}, count={Count}", result.MessageType, result.Count);

            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
            {
                var json = System.Text.Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                return ParseServerHello(json, server);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Probe timed out for {Server}", server.Name);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe failed for {Server}", server.Name);
            return null;
        }
        finally
        {
            if (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                        "probe complete",
                        CancellationToken.None);
                }
                catch
                {
                    // Ignore close errors
                }
            }
        }
    }

    /// <summary>
    /// Creates a minimal client/hello JSON for probing.
    /// </summary>
    private static string CreateClientHelloJson()
    {
        var clientId = $"sendspin-linux-{Environment.MachineName.ToLowerInvariant()}";
        return $@"{{""type"":""client/hello"",""payload"":{{""client_id"":""{clientId}"",""name"":""Sendspin Linux"",""version"":1,""supported_roles"":[""player@v1""]}}}}";
    }

    /// <summary>
    /// Parses a server/hello message to extract server info.
    /// </summary>
    private ProbedServerInfo? ParseServerHello(string json, DiscoveredServer server)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) ||
                typeProp.GetString() != "server/hello")
            {
                _logger.LogDebug("Expected server/hello but got: {Type}", typeProp.GetString());
                return null;
            }

            var serverId = root.TryGetProperty("server_id", out var idProp)
                ? idProp.GetString() ?? server.ServerId
                : server.ServerId;

            var name = root.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString() ?? server.Name
                : server.Name;

            string? connectionReason = null;
            if (root.TryGetProperty("payload", out var payload) &&
                payload.TryGetProperty("connection_reason", out var reasonProp))
            {
                connectionReason = reasonProp.GetString();
            }

            _logger.LogDebug("Parsed server/hello: id={ServerId}, name={Name}", serverId, name);

            return new ProbedServerInfo(
                serverId,
                name,
                server.Host,
                server.Port,
                server.IpAddresses.ToList(),
                connectionReason,
                server);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse server/hello JSON");
            return null;
        }
    }

    /// <summary>
    /// Connects to a discovered server and starts audio playback.
    /// </summary>
    public async Task ConnectAsync(DiscoveredServer server, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (IsConnected)
        {
            _logger.LogWarning("Already connected. Disconnect first.");
            return;
        }

        _logger.LogInformation("Connecting to {ServerName} at {Host}:{Port}",
            server.Name, server.Host, server.Port);

        try
        {
            // Create HTTP client for artwork fetching
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            _shutdownCts = new CancellationTokenSource();
            _lastArtworkUrl = null;

            // Create clock synchronizer
            _clockSync = new KalmanClockSynchronizer(_loggerFactory.CreateLogger<KalmanClockSynchronizer>());

            // Create audio pipeline with factories
            _audioPipeline = CreateAudioPipeline();

            // Create connection
            _connection = new SendspinConnection(_loggerFactory.CreateLogger<SendspinConnection>());

            // Create client capabilities with artwork support
            var capabilities = new ClientCapabilities
            {
                ClientId = $"sendspin-linux-{Environment.MachineName.ToLowerInvariant()}",
                ClientName = "Sendspin Linux",
                ProductName = "Sendspin Linux Client",
                Manufacturer = "Sendspin Contributors",
                SoftwareVersion = "1.0.0",
                ArtworkFormats = ["jpeg", "png"],
                ArtworkMaxSize = 320
            };

            // Create client service with audio pipeline
            _client = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                _connection,
                _clockSync,
                capabilities,
                _audioPipeline);

            // Subscribe to events
            _client.ConnectionStateChanged += OnConnectionStateChanged;
            _client.GroupStateChanged += OnGroupStateChanged;
            _client.ArtworkReceived += OnArtworkReceived;

            // Build WebSocket URI and connect
            // Prefer IP address over hostname since mDNS hostnames may not be resolvable
            var host = server.IpAddresses.FirstOrDefault() ?? server.Host;
            var uri = new Uri($"ws://{host}:{server.Port}/sendspin");
            _logger.LogDebug("Connecting to URI: {Uri}", uri);
            await _client.ConnectAsync(uri, ct);

            // Use the authoritative server name from the handshake, falling back to mDNS name
            var serverName = _client.ServerName ?? server.Name;
            _logger.LogInformation("Connected to {ServerName}", serverName);
            ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs(true, serverName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to {ServerName}", server.Name);
            await DisconnectAsync();
            throw;
        }
    }

    /// <summary>
    /// Connects to a server by host and port.
    /// </summary>
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        // Create a minimal DiscoveredServer for manual connection
        var server = new DiscoveredServer
        {
            ServerId = $"{host}:{port}",
            Name = host,
            Host = host,
            Port = port,
            IpAddresses = [host]
        };
        await ConnectAsync(server, ct);
    }

    /// <summary>
    /// Creates the audio pipeline with all required factories.
    /// </summary>
    private IAudioPipeline CreateAudioPipeline()
    {
        var bufferCapacityMs = 8000; // 8 seconds of buffer capacity
        var bufferTargetMs = 250.0; // 250ms target buffer for low latency

        return new AudioPipeline(
            _loggerFactory.CreateLogger<AudioPipeline>(),
            new AudioDecoderFactory(),
            _clockSync!,
            bufferFactory: (format, sync) =>
            {
                var buffer = new TimedAudioBuffer(
                    format,
                    sync,
                    bufferCapacityMs,
                    syncOptions: SyncCorrectionOptions.CliDefaults,
                    _loggerFactory.CreateLogger<TimedAudioBuffer>());
                buffer.TargetBufferMilliseconds = bufferTargetMs;
                return buffer;
            },
            playerFactory: _playerFactory,
            sourceFactory: (buffer, timeFunc) =>
            {
                // Create sync correction calculator for SDK 5.x external correction
                var correctionCalculator = new SyncCorrectionCalculator(
                    SyncCorrectionOptions.CliDefaults,
                    buffer.Format.SampleRate,
                    buffer.Format.Channels);

                return new SyncCorrectedSampleSource(
                    buffer,
                    correctionCalculator,
                    timeFunc,
                    _loggerFactory.CreateLogger<SyncCorrectedSampleSource>());
            },
            precisionTimer: null,
            waitForConvergence: true,
            convergenceTimeoutMs: 5000);
    }

    /// <summary>
    /// Disconnects from the current server and stops playback.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _logger.LogInformation("Disconnecting...");

        try
        {
            if (_client != null)
            {
                _client.ConnectionStateChanged -= OnConnectionStateChanged;
                _client.GroupStateChanged -= OnGroupStateChanged;
                _client.ArtworkReceived -= OnArtworkReceived;
                await _client.DisconnectAsync();
                await _client.DisposeAsync();
                _client = null;
            }

            if (_audioPipeline != null)
            {
                await _audioPipeline.DisposeAsync();
                _audioPipeline = null;
            }

            // Cancel any in-flight artwork fetches before disposing HttpClient
            _shutdownCts?.Cancel();
            _shutdownCts?.Dispose();
            _shutdownCts = null;

            _httpClient?.Dispose();
            _httpClient = null;
            _lastArtworkUrl = null;

            _connection = null;
            _clockSync = null;

            ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs(false, null));
            _logger.LogInformation("Disconnected");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during disconnect");
        }
    }

    /// <summary>
    /// Sends a play command to the server.
    /// </summary>
    public async Task PlayAsync()
    {
        if (_client != null)
            await _client.SendCommandAsync("play");
    }

    /// <summary>
    /// Sends a pause command to the server.
    /// </summary>
    public async Task PauseAsync()
    {
        if (_client != null)
            await _client.SendCommandAsync("pause");
    }

    /// <summary>
    /// Sends a next track command to the server.
    /// </summary>
    public async Task NextAsync()
    {
        if (_client != null)
            await _client.SendCommandAsync("next");
    }

    /// <summary>
    /// Sends a previous track command to the server.
    /// </summary>
    public async Task PreviousAsync()
    {
        if (_client != null)
            await _client.SendCommandAsync("previous");
    }

    /// <summary>
    /// Sends a switch group command to the server.
    /// </summary>
    public async Task SwitchGroupAsync()
    {
        if (_client != null)
            await _client.SendCommandAsync("switch_group");
    }

    /// <summary>
    /// Sets the playback volume (0-100).
    /// </summary>
    public async Task SetVolumeAsync(int volume)
    {
        if (_client != null)
            await _client.SetVolumeAsync(volume);
    }

    /// <summary>
    /// Sets the mute state.
    /// </summary>
    public void SetMuted(bool muted)
    {
        _audioPipeline?.SetMuted(muted);
    }

    private void OnServerFound(object? sender, DiscoveredServer server)
    {
        _logger.LogInformation("Discovered server via mDNS: {Name} at {Host}:{Port}",
            server.Name, server.Host, server.Port);

        // Immediately show server with mDNS info
        var initialInfo = new ProbedServerInfo(
            server.ServerId,
            server.Name,
            server.Host,
            server.Port,
            server.IpAddresses.ToList(),
            null,  // Connection reason not yet known
            server);
        ServerVerified?.Invoke(this, initialInfo);

        // Probe in background to get authoritative name (optional enhancement)
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await ProbeServerAsync(server);
                if (info != null && info.Name != server.Name)
                {
                    _logger.LogInformation("Probe updated server name: {OldName} -> {NewName}",
                        server.Name, info.Name);
                    // Fire event again with updated info - UI can update the name
                    ServerVerified?.Invoke(this, info);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background probe failed for {Server}", server.Name);
            }
        });
    }

    private void OnServerLost(object? sender, DiscoveredServer server)
    {
        _logger.LogInformation("Lost server: {Name}", server.Name);
        ServerLost?.Invoke(this, server);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _logger.LogInformation("Connection state: {OldState} -> {NewState}", e.OldState, e.NewState);

        if (e.NewState == ConnectionState.Disconnected)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs(false, null));
        }
    }

    private void OnGroupStateChanged(object? sender, GroupState group)
    {
        // Update playback state
        var isPlaying = group.PlaybackState == PlaybackState.Playing;
        _logger.LogDebug("Playback state: {State} (isPlaying={IsPlaying})", group.PlaybackState, isPlaying);
        PlaybackStateChanged?.Invoke(this, new PlaybackStateEventArgs(group.PlaybackState, isPlaying));

        // Update track metadata
        if (group.Metadata != null)
        {
            _logger.LogDebug("Track: {Title} by {Artist}", group.Metadata.Title, group.Metadata.Artist);
            TrackChanged?.Invoke(this, new TrackMetadataEventArgs(
                group.Metadata.Title ?? "",
                group.Metadata.Artist ?? "",
                group.Metadata.Album));

            // Fetch artwork from URL if changed
            var artworkUrl = group.Metadata.ArtworkUrl;
            if (!string.IsNullOrEmpty(artworkUrl) && artworkUrl != _lastArtworkUrl)
            {
                _lastArtworkUrl = artworkUrl;
                var token = _shutdownCts?.Token ?? CancellationToken.None;
                _ = FetchArtworkAsync(artworkUrl, token);
            }
            else if (string.IsNullOrEmpty(artworkUrl) && _lastArtworkUrl != null)
            {
                // Artwork cleared
                _lastArtworkUrl = null;
                ArtworkChanged?.Invoke(this, new ArtworkEventArgs(0, null));
            }
        }
    }

    private async Task FetchArtworkAsync(string url, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Fetching artwork from: {Url}", url);
            var imageData = await _httpClient!.GetByteArrayAsync(url, ct);
            _logger.LogDebug("Artwork fetched: {Size} bytes", imageData.Length);
            ArtworkChanged?.Invoke(this, new ArtworkEventArgs(0, imageData));
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown - don't log as warning
            _logger.LogDebug("Artwork fetch cancelled for {Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch artwork from {Url}", url);
        }
    }

    private void OnArtworkReceived(object? sender, byte[] imageData)
    {
        _logger.LogDebug("Artwork received: size={Size} bytes", imageData?.Length ?? 0);
        ArtworkChanged?.Invoke(this, new ArtworkEventArgs(0, imageData));
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await DisconnectAsync();

        if (_discovery != null)
        {
            _discovery.ServerFound -= OnServerFound;
            _discovery.ServerLost -= OnServerLost;
            await _discovery.StopAsync();
        }
    }
}

/// <summary>
/// Event args for connection state changes.
/// </summary>
public sealed class ConnectionStateEventArgs : EventArgs
{
    public bool IsConnected { get; }
    public string? ServerName { get; }

    public ConnectionStateEventArgs(bool isConnected, string? serverName)
    {
        IsConnected = isConnected;
        ServerName = serverName;
    }
}

/// <summary>
/// Event args for track metadata changes.
/// </summary>
public sealed class TrackMetadataEventArgs : EventArgs
{
    public string Title { get; }
    public string Artist { get; }
    public string? Album { get; }

    public TrackMetadataEventArgs(string title, string artist, string? album)
    {
        Title = title;
        Artist = artist;
        Album = album;
    }
}

/// <summary>
/// Event args for artwork changes.
/// </summary>
public sealed class ArtworkEventArgs : EventArgs
{
    /// <summary>
    /// Gets the artwork channel (0-3).
    /// </summary>
    public int Channel { get; }

    /// <summary>
    /// Gets the image data, or null if artwork was cleared.
    /// </summary>
    public byte[]? ImageData { get; }

    public ArtworkEventArgs(int channel, byte[]? imageData)
    {
        Channel = channel;
        ImageData = imageData;
    }
}

/// <summary>
/// Event args for playback state changes.
/// </summary>
public sealed class PlaybackStateEventArgs : EventArgs
{
    /// <summary>
    /// Gets the current playback state.
    /// </summary>
    public PlaybackState State { get; }

    /// <summary>
    /// Gets whether playback is currently active (playing).
    /// </summary>
    public bool IsPlaying { get; }

    public PlaybackStateEventArgs(PlaybackState state, bool isPlaying)
    {
        State = state;
        IsPlaying = isPlaying;
    }
}
