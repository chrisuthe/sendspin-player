namespace Sendspin.Core.Audio;

/// <summary>
/// Interface for dynamic audio resamplers that support real-time rate adjustment.
/// Used for audio sync correction in multi-room audio systems.
/// </summary>
public interface IDynamicResampler : IDisposable
{
    /// <summary>
    /// Current resampling ratio (1.0 = no change, &lt;1.0 = slower, &gt;1.0 = faster).
    /// Valid range: 0.96 to 1.04 (plus or minus 4% for drift correction).
    /// </summary>
    double Ratio { get; set; }

    /// <summary>
    /// Process input samples and write to output buffer.
    /// </summary>
    /// <param name="input">Input audio samples (mono float32).</param>
    /// <param name="output">Output buffer for resampled audio.</param>
    /// <returns>Number of samples written to output buffer.</returns>
    int Process(ReadOnlySpan<float> input, Span<float> output);

    /// <summary>
    /// Reset the resampler state, clearing any internal buffers.
    /// </summary>
    void Reset();
}

/// <summary>
/// Quality levels for audio resamplers (1-10 scale).
/// Higher quality means better audio but more CPU usage.
/// </summary>
public enum ResamplerQuality
{
    Fastest = 0,
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Level4 = 4,
    Default = 5,
    Level6 = 6,
    Level7 = 7,
    Level8 = 8,
    Level9 = 9,
    Best = 10
}
