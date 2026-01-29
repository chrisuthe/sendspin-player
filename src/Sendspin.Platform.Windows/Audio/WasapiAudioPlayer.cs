using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sendspin.Core.Audio;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.Platform.Windows.Audio;

/// <summary>
/// Windows WASAPI audio player using NAudio.
/// Provides audio output via WASAPI shared mode for broad device compatibility.
/// </summary>
/// <remarks>
/// <para>
/// Uses WASAPI shared mode for broad device compatibility. While exclusive mode
/// offers lower latency, shared mode is more reliable across different audio
/// hardware configurations and allows other applications to use audio simultaneously.
/// </para>
/// <para>
/// The 100ms latency setting provides stability across different hardware while
/// accounting for Windows Audio Engine overhead in shared mode.
/// </para>
/// </remarks>
public sealed class WasapiAudioPlayer : IAudioPlayer, IAudioDeviceEnumerator, IAsyncDisposable
{
    private readonly ILogger<WasapiAudioPlayer> _logger;
    private readonly string? _deviceId;
    private WasapiOut? _wasapiOut;
    private AudioSampleProviderAdapter? _sampleProvider;
    private AudioFormat? _format;
    private float _volume = 1.0f;
    private bool _isMuted;
    private int _outputLatencyMs;

    /// <summary>
    /// Default latency to request from WASAPI (in milliseconds).
    /// 100ms provides stability across different hardware configurations.
    /// </summary>
    private const int RequestedLatencyMs = 100;

    /// <inheritdoc/>
    public int OutputLatencyMs => _outputLatencyMs;

    /// <inheritdoc/>
    public AudioFormat? OutputFormat => _format;

    /// <inheritdoc/>
    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

    /// <inheritdoc/>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_sampleProvider != null)
            {
                _sampleProvider.Volume = _volume;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (_sampleProvider != null)
            {
                _sampleProvider.IsMuted = value;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler<AudioPlayerState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    /// <summary>
    /// Initializes a new instance of the <see cref="WasapiAudioPlayer"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="deviceId">
    /// Optional device ID for a specific audio output device.
    /// If null or empty, the system default device is used.
    /// </param>
    public WasapiAudioPlayer(ILogger<WasapiAudioPlayer> logger, string? deviceId = null)
    {
        _logger = logger;
        _deviceId = deviceId;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                _format = format;

                // Get the audio device - either specific device by ID or system default
                MMDevice? device = GetDevice(_deviceId);

                // Create WASAPI output in shared mode
                if (device != null)
                {
                    _wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: RequestedLatencyMs);
                }
                else
                {
                    _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, latency: RequestedLatencyMs);
                }

                _wasapiOut.PlaybackStopped += OnPlaybackStopped;

                // Estimate output latency (WASAPI shared mode adds ~10-20ms overhead)
                _outputLatencyMs = RequestedLatencyMs + 15;

                SetState(AudioPlayerState.Stopped);
                _logger.LogInformation(
                    "WASAPI player initialized: {SampleRate}Hz {Channels}ch, latency: ~{Latency}ms, device: {Device}",
                    format.SampleRate,
                    format.Channels,
                    _outputLatencyMs,
                    device?.FriendlyName ?? "System Default");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize WASAPI player");
                SetState(AudioPlayerState.Error);
                ErrorOccurred?.Invoke(this, new AudioPlayerError("Failed to initialize audio output", ex));
                throw;
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public void SetSampleSource(IAudioSampleSource source)
    {
        if (_wasapiOut == null || _format == null)
        {
            throw new InvalidOperationException("Player not initialized. Call InitializeAsync first.");
        }

        ArgumentNullException.ThrowIfNull(source);

        // Create NAudio adapter with current volume/mute state
        _sampleProvider = new AudioSampleProviderAdapter(source, _format);
        _sampleProvider.Volume = _volume;
        _sampleProvider.IsMuted = _isMuted;

        _wasapiOut.Init(_sampleProvider);
        _logger.LogDebug("Sample source configured: {SampleRate}Hz {Channels}ch",
            _format.SampleRate, _format.Channels);
    }

    /// <inheritdoc/>
    public void Play()
    {
        if (_wasapiOut == null || _sampleProvider == null)
        {
            throw new InvalidOperationException("Player not initialized or no sample source set.");
        }

        _wasapiOut.Play();
        SetState(AudioPlayerState.Playing);
        _logger.LogInformation("Playback started");
    }

    /// <inheritdoc/>
    public void Pause()
    {
        _wasapiOut?.Pause();
        SetState(AudioPlayerState.Paused);
        _logger.LogInformation("Playback paused");
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _wasapiOut?.Stop();
        SetState(AudioPlayerState.Stopped);
        _logger.LogInformation("Playback stopped");
    }

    /// <inheritdoc/>
    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var wasPlaying = State == AudioPlayerState.Playing;
                var currentSource = _sampleProvider;

                _logger.LogInformation("Switching audio device to {Device}",
                    deviceId ?? "System Default");

                // Stop and dispose current output
                if (_wasapiOut != null)
                {
                    _wasapiOut.PlaybackStopped -= OnPlaybackStopped;
                    _wasapiOut.Stop();
                    _wasapiOut.Dispose();
                    _wasapiOut = null;
                }

                // Get the new audio device
                var device = GetDevice(deviceId);

                // Create new WASAPI output
                if (device != null)
                {
                    _wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: RequestedLatencyMs);
                }
                else
                {
                    _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, latency: RequestedLatencyMs);
                }

                _wasapiOut.PlaybackStopped += OnPlaybackStopped;

                // Re-attach sample provider if we had one
                if (currentSource != null)
                {
                    _wasapiOut.Init(currentSource);
                }

                SetState(AudioPlayerState.Stopped);

                // Resume playback if we were playing
                if (wasPlaying && currentSource != null)
                {
                    _wasapiOut.Play();
                    SetState(AudioPlayerState.Playing);
                }

                _logger.LogInformation("Audio device switched to {Device}",
                    device?.FriendlyName ?? "System Default");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to switch audio device");
                SetState(AudioPlayerState.Error);
                ErrorOccurred?.Invoke(this, new AudioPlayerError("Failed to switch audio device", ex));
                throw;
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var defaultId = defaultDevice?.ID;

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = device.ID == defaultId
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate audio devices");
        }

        return devices;
    }

    /// <inheritdoc/>
    public AudioDeviceInfo? GetDefaultDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device != null)
            {
                return new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = true
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get default audio device");
        }

        return null;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_wasapiOut != null)
        {
            _wasapiOut.PlaybackStopped -= OnPlaybackStopped;
            _wasapiOut.Stop();
            _wasapiOut.Dispose();
            _wasapiOut = null;
        }

        _sampleProvider = null;
        SetState(AudioPlayerState.Uninitialized);

        await Task.CompletedTask;
    }

    private MMDevice? GetDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            _logger.LogInformation("Using audio device: {DeviceName}", device.FriendlyName);
            return device;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get device {DeviceId}, falling back to default", deviceId);
            return null;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Playback stopped due to error");
            SetState(AudioPlayerState.Error);
            ErrorOccurred?.Invoke(this, new AudioPlayerError("Playback error", e.Exception));
        }
        else if (State == AudioPlayerState.Playing)
        {
            SetState(AudioPlayerState.Stopped);
        }
    }

    private void SetState(AudioPlayerState newState)
    {
        if (State != newState)
        {
            _logger.LogDebug("Player state: {OldState} -> {NewState}", State, newState);
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }
}
