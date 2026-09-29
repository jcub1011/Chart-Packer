using System.Reflection;
using System.Runtime.InteropServices;
using ChartPacker.Video.Interop;

namespace ChartPacker.Video;

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
            return Check("libavutil", Ffmpeg.avutil_version(), Ffmpeg.AvUtilMajor)
                ?? Check("libswscale", Ffmpeg.swscale_version(), Ffmpeg.SwScaleMajor)
                ?? Check("libavcodec", Ffmpeg.avcodec_version(), Ffmpeg.AvCodecMajor)
                ?? Check("libavformat", Ffmpeg.avformat_version(), Ffmpeg.AvFormatMajor);
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
            Ffmpeg.AvUtil => Ffmpeg.AvUtilMajor,
            Ffmpeg.SwScale => Ffmpeg.SwScaleMajor,
            Ffmpeg.AvCodec => Ffmpeg.AvCodecMajor,
            Ffmpeg.AvFormat => Ffmpeg.AvFormatMajor,
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
