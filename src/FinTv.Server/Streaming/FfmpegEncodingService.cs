namespace FinTv.Streaming;

/// <summary>
/// Software or Intel VAAPI H.264 encoding/decoding for MPEG-TS output.
/// </summary>
public class FfmpegEncodingService
{
    public string Encoder { get; }

    public string? VaapiDevice { get; }

    public bool UseVaapi { get; }

    /// <summary>
    /// Global VAAPI device for the encoder (must appear before inputs).
    /// </summary>
    public IReadOnlyList<string> HardwareDeviceArgs { get; }

    /// <summary>
    /// Hardware decode flags placed immediately before a real video <c>-i</c>.
    /// Frames are downloaded to NV12 so existing software filters (overlay, scanlines) still work.
    /// </summary>
    public IReadOnlyList<string> HardwareDecodeArgs { get; }

    public FfmpegEncodingService(IConfiguration configuration)
    {
        var requestedEncoder = configuration["FFMPEG_VIDEO_ENCODER"]
            ?? Environment.GetEnvironmentVariable("FFMPEG_VIDEO_ENCODER")
            ?? "libx264";
        var hw = configuration["FFMPEG_HWACCEL"] ?? Environment.GetEnvironmentVariable("FFMPEG_HWACCEL");
        var device = configuration["FFMPEG_VAAPI_DEVICE"]
            ?? Environment.GetEnvironmentVariable("FFMPEG_VAAPI_DEVICE")
            ?? "/dev/dri/renderD128";

        var wantVaapi = string.Equals(hw, "vaapi", StringComparison.OrdinalIgnoreCase)
            || requestedEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase);
        UseVaapi = wantVaapi && File.Exists(device);

        if (UseVaapi)
        {
            VaapiDevice = device;
            Encoder = requestedEncoder == "libx264" || string.IsNullOrWhiteSpace(requestedEncoder)
                ? "h264_vaapi"
                : requestedEncoder;
            HardwareDeviceArgs = ["-vaapi_device", device];
            HardwareDecodeArgs =
            [
                "-hwaccel", "vaapi",
                "-hwaccel_device", device
            ];
        }
        else if (string.Equals(hw, "nvenc", StringComparison.OrdinalIgnoreCase))
        {
            VaapiDevice = null;
            Encoder = requestedEncoder == "libx264" ? "h264_nvenc" : requestedEncoder;
            HardwareDeviceArgs = ["-hwaccel", "cuda"];
            HardwareDecodeArgs = ["-hwaccel", "cuda"];
        }
        else
        {
            VaapiDevice = null;
            Encoder = string.IsNullOrWhiteSpace(requestedEncoder) ? "libx264" : requestedEncoder;
            HardwareDeviceArgs = [];
            HardwareDecodeArgs = [];
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
            return
            [
                "-b:v", stillImage ? "1500k" : "4000k",
                "-maxrate", stillImage ? "2500k" : "5000k",
                "-bufsize", stillImage ? "4000k" : "8000k",
                "-profile:v", "high",
                "-level", "4.1",
                "-g", stillImage ? "12" : "30",
                "-bf", "0"
            ];
        }

        if (Encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return ["-preset", "p4", "-b:v", stillImage ? "1500k" : "4000k", "-maxrate", "5000k"];
        }

        return
        [
            "-preset", "veryfast",
            "-tune", stillImage ? "stillimage" : "film",
            "-crf", stillImage ? "23" : "21",
            "-pix_fmt", "yuv420p",
            "-g", stillImage ? "12" : "30",
            "-bf", "0"
        ];
    }

    public void AppendVideoEncoder(List<string> args, bool stillImage = false)
    {
        args.Add("-c:v");
        args.Add(Encoder);
        args.AddRange(GetVideoEncoderArguments(stillImage));
    }

    private static bool IsHardware(string encoder)
        => !string.IsNullOrWhiteSpace(encoder)
           && !encoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase);
}
