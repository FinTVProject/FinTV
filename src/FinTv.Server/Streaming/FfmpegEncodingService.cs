namespace FinTv.Streaming;

/// <summary>
/// Software (and optional hardware) H.264 encoding for MPEG-TS output.
/// Replaces Jellyfin dashboard transcoding settings.
/// </summary>
public class FfmpegEncodingService
{
    public string Encoder { get; }

    public IReadOnlyList<string> HardwareDeviceArgs { get; }

    public FfmpegEncodingService(IConfiguration configuration)
    {
        Encoder = configuration["FFMPEG_VIDEO_ENCODER"]
            ?? Environment.GetEnvironmentVariable("FFMPEG_VIDEO_ENCODER")
            ?? "libx264";

        var hw = configuration["FFMPEG_HWACCEL"] ?? Environment.GetEnvironmentVariable("FFMPEG_HWACCEL");
        if (string.Equals(hw, "vaapi", StringComparison.OrdinalIgnoreCase))
        {
            var device = configuration["FFMPEG_VAAPI_DEVICE"] ?? "/dev/dri/renderD128";
            HardwareDeviceArgs = ["-vaapi_device", device];
            if (Encoder == "libx264")
            {
                Encoder = "h264_vaapi";
            }
        }
        else if (string.Equals(hw, "nvenc", StringComparison.OrdinalIgnoreCase))
        {
            HardwareDeviceArgs = ["-hwaccel", "cuda"];
            if (Encoder == "libx264")
            {
                Encoder = "h264_nvenc";
            }
        }
        else
        {
            HardwareDeviceArgs = [];
        }
    }

    public bool IsHardwareVideoEncoder =>
        !Encoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase);

    public string AdaptVideoFilterForEncoder(string filter, string videoEncoder)
    {
        if (string.IsNullOrWhiteSpace(filter) || !IsHardware(videoEncoder))
        {
            return filter;
        }

        if (filter.Contains("yuv420p", StringComparison.OrdinalIgnoreCase))
        {
            return filter.Replace("yuv420p", "nv12", StringComparison.OrdinalIgnoreCase);
        }

        if (filter.Contains("format=", StringComparison.OrdinalIgnoreCase))
        {
            return filter;
        }

        return filter + ",format=nv12";
    }

    public string AdaptFilterComplexForEncoder(string filter, string videoEncoder)
    {
        if (string.IsNullOrWhiteSpace(filter) || !IsHardware(videoEncoder))
        {
            return filter;
        }

        if (filter.Contains("yuv420p", StringComparison.OrdinalIgnoreCase))
        {
            return filter.Replace("yuv420p", "nv12", StringComparison.OrdinalIgnoreCase);
        }

        return filter.Replace("[vout]", "format=nv12[vout]", StringComparison.Ordinal);
    }

    public IReadOnlyList<string> GetVideoEncoderArguments(bool stillImage)
    {
        if (Encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
        {
            return ["-b:v", stillImage ? "1500k" : "4000k", "-maxrate", "5000k", "-bufsize", "8000k"];
        }

        if (Encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return ["-preset", "p4", "-b:v", stillImage ? "1500k" : "4000k", "-maxrate", "5000k"];
        }

        var args = new List<string>
        {
            "-preset", stillImage ? "veryfast" : "veryfast",
            "-tune", stillImage ? "stillimage" : "film",
            "-crf", stillImage ? "23" : "21",
            "-pix_fmt", "yuv420p",
            "-g", stillImage ? "12" : "30",
            "-bf", "0"
        };
        return args;
    }

    private static bool IsHardware(string encoder)
        => !string.IsNullOrWhiteSpace(encoder)
           && !encoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase);
}
