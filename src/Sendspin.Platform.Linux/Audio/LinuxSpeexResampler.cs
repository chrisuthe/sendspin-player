using System.Runtime.InteropServices;
using Sendspin.Core.Audio;

namespace Sendspin.Platform.Linux.Audio;

/// <summary>
/// Native SpeexDSP resampler using P/Invoke to libspeexdsp.
/// Provides high-quality audio resampling with real-time ratio adjustment for sync correction.
/// </summary>
public sealed class LinuxSpeexResampler : IDynamicResampler
{
    private IntPtr _state;
    private readonly int _channels;
    private readonly int _baseSampleRate;
    private double _ratio = 1.0;
    private bool _disposed;

    private const double MinRatio = 0.96;
    private const double MaxRatio = 1.04;

    /// <summary>
    /// Checks if the native SpeexDSP library is available on the system.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                _ = SpeexNative.speex_resampler_get_version();
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    public LinuxSpeexResampler(int sampleRate, int channels = 1, ResamplerQuality quality = ResamplerQuality.Default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);

        _baseSampleRate = sampleRate;
        _channels = channels;

        _state = SpeexNative.speex_resampler_init(
            (uint)channels, (uint)sampleRate, (uint)sampleRate, (int)quality, out int err);

        if (_state == IntPtr.Zero || err != 0)
            throw new InvalidOperationException($"Failed to initialize Speex resampler. Error: {err}");

        SpeexNative.speex_resampler_skip_zeros(_state);
    }

    public double Ratio
    {
        get => _ratio;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var clamped = Math.Clamp(value, MinRatio, MaxRatio);
            if (Math.Abs(clamped - _ratio) < 0.0001) return;

            _ratio = clamped;
            const uint denominator = 10000;
            uint inRate = (uint)(_baseSampleRate * denominator);
            uint outRate = (uint)(_baseSampleRate * denominator / _ratio);

            int err = SpeexNative.speex_resampler_set_rate_frac(
                _state, inRate, outRate, (uint)_baseSampleRate, (uint)_baseSampleRate);
            if (err != 0)
                throw new InvalidOperationException($"Failed to set rate. Error: {err}");
        }
    }

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.IsEmpty) return 0;

        uint inLen = (uint)(input.Length / _channels);
        uint outLen = (uint)(output.Length / _channels);

        int err;
        unsafe
        {
            fixed (float* inPtr = input)
            fixed (float* outPtr = output)
            {
                err = _channels == 1
                    ? SpeexNative.speex_resampler_process_float(_state, 0, inPtr, ref inLen, outPtr, ref outLen)
                    : SpeexNative.speex_resampler_process_interleaved_float(_state, inPtr, ref inLen, outPtr, ref outLen);
            }
        }
        if (err != 0) throw new InvalidOperationException($"Resampling failed. Error: {err}");
        return (int)(outLen * _channels);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SpeexNative.speex_resampler_reset_mem(_state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_state != IntPtr.Zero)
        {
            SpeexNative.speex_resampler_destroy(_state);
            _state = IntPtr.Zero;
        }
    }
}

internal static partial class SpeexNative
{
    private const string LibName = "libspeexdsp.so.1";

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr speex_resampler_init(uint nb_channels, uint in_rate, uint out_rate, int quality, out int err);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void speex_resampler_destroy(IntPtr st);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial int speex_resampler_process_float(IntPtr st, uint channel_index, float* input, ref uint in_len, float* output, ref uint out_len);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe partial int speex_resampler_process_interleaved_float(IntPtr st, float* input, ref uint in_len, float* output, ref uint out_len);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int speex_resampler_set_rate_frac(IntPtr st, uint ratio_num, uint ratio_den, uint in_rate, uint out_rate);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void speex_resampler_reset_mem(IntPtr st);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int speex_resampler_skip_zeros(IntPtr st);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr speex_resampler_get_version();
}
