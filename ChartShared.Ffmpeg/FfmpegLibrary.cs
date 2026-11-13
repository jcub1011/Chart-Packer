using System.Reflection;
using System.Runtime.InteropServices;
using ChartShared.Ffmpeg.Interop;

namespace ChartShared.Ffmpeg;

/// <summary>
/// Finds FFmpeg's shared libraries beside the executable and checks they're the major version the
/// bindings are written for. Video can only be decoded when <see cref="IsAvailable"/>.
/// </summary>
public static class FfmpegLibrary
{
    private static readonly Lazy<string?> Problem = new(Load);

    /// <summary>FFmpeg was found and is the version the bindings are written for.</summary>
    public static bool IsAvailable => Problem.Value is null;

    /// <summary>Why FFmpeg can't be used, or <see langword="null"/> when it can.</summary>
    public static string? UnavailableReason => Problem.Value;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Encoders = new(StringComparer.Ordinal);

    /// <summary>Whether FFmpeg's libraries loaded and have the encoder called <paramref name="name"/>.</summary>
    /// <remarks>
    /// Builds made before libvpx was added have no encoders at all, so this is how a caller tells. The VP8 encoder is
    /// <c>libvpx</c> (<c>--enable-encoder=libvpx_vp8</c> is the name of its build component, not of the encoder).
    /// </remarks>
    public static unsafe bool HasEncoder(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return IsAvailable && Encoders.GetOrAdd(name, static n => Native.avcodec_find_encoder_by_name(n) != null);
    }

    /// <exception cref="InvalidOperationException">FFmpeg can't be used.</exception>
    internal static void EnsureAvailable()
    {
        if (Problem.Value is { } problem)
            throw new InvalidOperationException(problem);
    }

    private static string? Load()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(FfmpegLibrary).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Already set; it's this resolver.
        }

        try
        {
            // Loaded in dependency order, so each finds the ones it links against.
            Native.av_log_set_level(Native.LogLevelWarning);
            return Check("libavutil", Native.avutil_version(), Native.AvUtilMajor)
                ?? Check("libswscale", Native.swscale_version(), Native.SwScaleMajor)
                ?? Check("libavcodec", Native.avcodec_version(), Native.AvCodecMajor)
                ?? Check("libavformat", Native.avformat_version(), Native.AvFormatMajor);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return $"FFmpeg's libraries weren't found: {ex.Message}";
        }
    }

    private static string? Check(string name, uint version, int major) =>
        (int)(version >> 16) == major ? null : $"{name} is version {version >> 16}, but {major} is needed.";

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        int major = libraryName switch
        {
            Native.AvUtil => Native.AvUtilMajor,
            Native.SwScale => Native.SwScaleMajor,
            Native.SwResample => Native.SwResampleMajor,
            Native.AvCodec => Native.AvCodecMajor,
            Native.AvFormat => Native.AvFormatMajor,
            _ => -1,
        };
        if (major < 0)
            return IntPtr.Zero;

        // FFmpeg's own naming: avcodec-63.dll, libavcodec.so.63, libavcodec.63.dylib.
        string fileName = OperatingSystem.IsWindows() ? $"{libraryName}-{major}.dll"
            : OperatingSystem.IsMacOS() ? $"lib{libraryName}.{major}.dylib"
            : $"lib{libraryName}.so.{major}";

        string besideApp = Path.Combine(AppContext.BaseDirectory, fileName);
        if (NativeLibrary.TryLoad(besideApp, out IntPtr handle))
            return handle;

        return NativeLibrary.TryLoad(fileName, assembly, searchPath, out handle) ? handle : IntPtr.Zero;
    }
}
