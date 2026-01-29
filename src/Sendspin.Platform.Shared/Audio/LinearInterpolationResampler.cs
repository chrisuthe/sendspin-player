using Sendspin.Core.Audio;

namespace Sendspin.Platform.Shared.Audio;

/// <summary>
/// Linear interpolation resampler for when native high-quality resamplers are unavailable.
/// This is a cross-platform fallback that provides acceptable quality for small ratio adjustments.
/// </summary>
public sealed class LinearInterpolationResampler : IDynamicResampler
{
    private readonly int _channels;
    private double _ratio = 1.0;
    private double _fractionalPosition;
    private float[] _lastSample;
    private bool _disposed;

    public LinearInterpolationResampler(int channels = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _channels = channels;
        _lastSample = new float[channels];
    }

    public double Ratio
    {
        get => _ratio;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ratio = Math.Clamp(value, 0.96, 1.04);
        }
    }

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.IsEmpty) return 0;

        int inputFrames = input.Length / _channels;
        int maxOutputFrames = output.Length / _channels;
        int outputFrameCount = 0;
        double position = _fractionalPosition;

        while (position < inputFrames - 1 && outputFrameCount < maxOutputFrames)
        {
            int index0 = (int)position;
            int index1 = index0 + 1;
            double frac = position - index0;

            for (int ch = 0; ch < _channels; ch++)
            {
                float sample0 = index0 >= 0 ? input[index0 * _channels + ch] : _lastSample[ch];
                float sample1 = input[index1 * _channels + ch];
                output[outputFrameCount * _channels + ch] = (float)(sample0 + (sample1 - sample0) * frac);
            }
            outputFrameCount++;
            position += _ratio;
        }

        if (inputFrames > 0)
        {
            int lastFrameStart = (inputFrames - 1) * _channels;
            for (int ch = 0; ch < _channels; ch++)
                _lastSample[ch] = input[lastFrameStart + ch];
        }

        _fractionalPosition = Math.Max(0, position - (inputFrames - 1));
        return outputFrameCount * _channels;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fractionalPosition = 0;
        Array.Clear(_lastSample);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lastSample = null!;
    }
}
