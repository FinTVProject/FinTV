using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinTV.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ServerUrl { get; set; } = "http://FinTV-Server:8097";

    public string ApiKey { get; set; } = string.Empty;

    public bool AutoRegisterLiveTv { get; set; } = true;

    public bool WriteChaptersToJellyfin { get; set; } = true;

    public string? CommercialLibraryTag { get; set; } = "fintv-commercial";

    public BlackframeTaskState BlackframeTaskState { get; set; } = new();
}

public class BlackframeTaskState
{
    public bool IsRunning { get; set; }

    public int TotalItems { get; set; }

    public int ProcessedItems { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}
