using Sendspin.Core.Audio;
using Sendspin.Platform.Shared.Audio;

namespace Sendspin.Platform.Linux.Audio;

/// <summary>
/// Factory for creating dynamic resamplers on Linux.
/// Automatically selects native SpeexDSP when available, falling back to linear interpolation.
/// </summary>
public static class DynamicResamplerFactory
{
    public static IDynamicResampler Create(int sampleRate, int channels = 1, ResamplerQuality quality = ResamplerQuality.Default)
    {
        if (LinuxSpeexResampler.IsAvailable)
        {
            try
            {
                return new LinuxSpeexResampler(sampleRate, channels, quality);
            }
            catch
            {
                // Fall through to fallback
            }
        }
        return new LinearInterpolationResampler(channels);
    }
}
