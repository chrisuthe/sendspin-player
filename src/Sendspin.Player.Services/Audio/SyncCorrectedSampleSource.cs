using System.Buffers;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.Player.Services.Audio;

/// <summary>
/// Bridges <see cref="ITimedAudioBuffer"/> to <see cref="IAudioSampleSource"/> with external sync correction.
/// </summary>
/// <remarks>
/// <para>
/// This class reads raw samples from the buffer (no internal correction) and applies
/// drop/insert corrections based on an <see cref="ISyncCorrectionProvider"/>.
/// </para>
/// <para>
/// The drop/insert algorithm mirrors the Python CLI approach:
/// - Drop: Read two frames, output interpolated blend (skip one input frame)
/// - Insert: Output interpolated frame without reading from buffer
/// This maintains audio continuity by using interpolation to reduce crackle.
/// </para>
/// </remarks>
public sealed class SyncCorrectedSampleSource : IAudioSampleSource, IDisposable
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly ISyncCorrectionProvider _correctionProvider;
    private readonly Func<long> _getCurrentTimeMicroseconds;
    private readonly ILogger? _logger;
    private readonly int _channels;

    // State for frame-by-frame correction
    private int _framesSinceLastCorrection;
    private float[]? _lastOutputFrame;
    private bool _disposed;

    // Logging rate limiter
    private long _lastLogTicks;
    private long _totalDropped;
    private long _totalInserted;
    private const long LogIntervalTicks = TimeSpan.TicksPerSecond; // Log every second

    /// <inheritdoc/>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Gets the underlying audio buffer.
    /// Used by the player to access buffer properties and stats.
    /// </summary>
    public ITimedAudioBuffer Buffer => _buffer;

    /// <summary>
    /// Gets the sync correction provider.
    /// </summary>
    public ISyncCorrectionProvider CorrectionProvider => _correctionProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncCorrectedSampleSource"/> class.
    /// </summary>
    /// <param name="buffer">The timed audio buffer to read from.</param>
    /// <param name="correctionProvider">Provider for sync correction decisions.</param>
    /// <param name="getCurrentTimeMicroseconds">Function that returns current local time in microseconds.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SyncCorrectedSampleSource(
        ITimedAudioBuffer buffer,
        ISyncCorrectionProvider correctionProvider,
        Func<long> getCurrentTimeMicroseconds,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(correctionProvider);
        ArgumentNullException.ThrowIfNull(getCurrentTimeMicroseconds);

        _buffer = buffer;
        _correctionProvider = correctionProvider;
        _getCurrentTimeMicroseconds = getCurrentTimeMicroseconds;
        _logger = logger;
        _channels = buffer.Format.Channels;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <see cref="ArrayPool{T}.Shared"/> to avoid allocating temporary
    /// buffers on every audio callback. Audio threads are real-time sensitive.
    /// </remarks>
    public int Read(float[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var currentTime = _getCurrentTimeMicroseconds();

        // Rent a buffer from the pool to avoid GC allocations in the audio thread
        var tempBuffer = ArrayPool<float>.Shared.Rent(count);
        try
        {
            // 1. Read raw samples (no SDK correction)
            var rawRead = _buffer.ReadRaw(tempBuffer.AsSpan(0, count), currentTime);

            // 2. Update correction provider with current sync error
            _correctionProvider.UpdateFromSyncError(
                _buffer.SyncErrorMicroseconds,
                _buffer.SmoothedSyncErrorMicroseconds);

            // 3. Get current correction settings
            var dropEveryN = _correctionProvider.DropEveryNFrames;
            var insertEveryN = _correctionProvider.InsertEveryNFrames;

            // 4. Apply drop/insert correction
            var (outputCount, samplesDropped, samplesInserted) = ApplyCorrection(
                tempBuffer, rawRead, buffer.AsSpan(offset, count), dropEveryN, insertEveryN);

            // 5. Notify buffer of external corrections for accurate sync tracking
            if (samplesDropped > 0 || samplesInserted > 0)
            {
                _buffer.NotifyExternalCorrection(samplesDropped, samplesInserted);
                _totalDropped += samplesDropped;
                _totalInserted += samplesInserted;
            }

            // 6. Notify correction provider of samples processed (for startup grace period)
            if (_correctionProvider is SyncCorrectionCalculator calculator)
            {
                calculator.NotifySamplesProcessed(outputCount);
            }

            // Rate-limited logging of correction state
            LogCorrectionState(dropEveryN, insertEveryN);

            // Fill remainder with silence if underrun
            if (outputCount < count)
            {
                buffer.AsSpan(offset + outputCount, count - outputCount).Fill(0f);
            }

            // Always return requested count to keep audio backend happy
            return count;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tempBuffer, clearArray: false);
        }
    }

    /// <summary>
    /// Resets correction state. Call when buffer is cleared or playback restarts.
    /// </summary>
    public void Reset()
    {
        _framesSinceLastCorrection = 0;
        _lastOutputFrame = null;
        _totalDropped = 0;
        _totalInserted = 0;
        _correctionProvider.Reset();
    }

    /// <summary>
    /// Logs correction state at a rate-limited interval.
    /// </summary>
    private void LogCorrectionState(int dropEveryN, int insertEveryN)
    {
        if (_logger == null) return;

        var now = DateTime.UtcNow.Ticks;
        if (now - _lastLogTicks < LogIntervalTicks) return;

        _lastLogTicks = now;

        var syncError = _buffer.SmoothedSyncErrorMicroseconds / 1000.0; // Convert to ms
        var mode = _correctionProvider.CurrentMode;

        _logger.LogDebug(
            "SyncCorrection: error={SyncError:+0.00;-0.00}ms mode={Mode} dropEveryN={DropN} insertEveryN={InsertN} totalDropped={Dropped} totalInserted={Inserted}",
            syncError, mode, dropEveryN, insertEveryN, _totalDropped, _totalInserted);
    }

    /// <summary>
    /// Applies sync correction using CLI-style drop/insert with interpolation.
    /// </summary>
    /// <returns>Tuple of (output sample count, samples dropped, samples inserted).</returns>
    private (int OutputCount, int SamplesDropped, int SamplesInserted) ApplyCorrection(
        float[] input, int inputCount, Span<float> output, int dropEveryN, int insertEveryN)
    {
        var frameSamples = _channels;

        // Initialize last output frame if needed
        _lastOutputFrame ??= new float[frameSamples];

        // If no correction needed, copy directly
        if (dropEveryN == 0 && insertEveryN == 0)
        {
            var toCopy = Math.Min(inputCount, output.Length);
            input.AsSpan(0, toCopy).CopyTo(output);

            // Save last frame for potential future corrections
            if (toCopy >= frameSamples)
            {
                input.AsSpan(toCopy - frameSamples, frameSamples).CopyTo(_lastOutputFrame);
            }

            return (toCopy, 0, 0);
        }

        // Process frame by frame, applying corrections
        var inputPos = 0;
        var outputPos = 0;
        var samplesDropped = 0;
        var samplesInserted = 0;

        while (outputPos < output.Length)
        {
            var remainingInput = inputCount - inputPos;

            _framesSinceLastCorrection++;

            // Check if we should DROP a frame (read two, output one interpolated)
            if (dropEveryN > 0 && _framesSinceLastCorrection >= dropEveryN)
            {
                _framesSinceLastCorrection = 0;

                if (remainingInput >= frameSamples * 2)
                {
                    // Read both frames, output interpolated blend to reduce crackle
                    var frameAStart = inputPos;
                    var frameBStart = inputPos + frameSamples;
                    var outputSpan = output.Slice(outputPos, frameSamples);

                    // Linear interpolation: (A + B) / 2
                    for (int i = 0; i < frameSamples; i++)
                    {
                        outputSpan[i] = (input[frameAStart + i] + input[frameBStart + i]) * 0.5f;
                    }

                    // Consume both input frames
                    inputPos += frameSamples * 2;

                    // Save as last output frame
                    outputSpan.CopyTo(_lastOutputFrame);

                    outputPos += frameSamples;
                    samplesDropped += frameSamples;
                    continue;
                }
            }

            // Check if we should INSERT a frame (output interpolated without consuming)
            if (insertEveryN > 0 && _framesSinceLastCorrection >= insertEveryN)
            {
                _framesSinceLastCorrection = 0;

                if (output.Length - outputPos >= frameSamples)
                {
                    var outputSpan = output.Slice(outputPos, frameSamples);

                    // Interpolate with next input frame if available
                    if (remainingInput >= frameSamples)
                    {
                        var nextFrameStart = inputPos;

                        // Linear interpolation: (last + next) / 2
                        for (int i = 0; i < frameSamples; i++)
                        {
                            outputSpan[i] = (_lastOutputFrame[i] + input[nextFrameStart + i]) * 0.5f;
                        }

                        outputSpan.CopyTo(_lastOutputFrame);
                    }
                    else
                    {
                        // Fallback: duplicate if no input available
                        _lastOutputFrame.AsSpan().CopyTo(outputSpan);
                    }

                    outputPos += frameSamples;
                    samplesInserted += frameSamples;
                    continue;
                }
            }

            // Normal frame: read from input and output
            if (remainingInput < frameSamples) break;
            if (output.Length - outputPos < frameSamples) break;

            var frameSpan = output.Slice(outputPos, frameSamples);
            input.AsSpan(inputPos, frameSamples).CopyTo(frameSpan);
            inputPos += frameSamples;

            // Save as last output frame
            frameSpan.CopyTo(_lastOutputFrame);
            outputPos += frameSamples;
        }

        return (outputPos, samplesDropped, samplesInserted);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lastOutputFrame = null;
    }
}
