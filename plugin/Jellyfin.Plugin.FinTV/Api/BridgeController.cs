using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinTV.Api;

[ApiController]
[Route("FinTV/api/bridge")]
[Authorize(Policy = Policies.RequiresElevation)]
public class BridgeController : ControllerBase
{
    private readonly Services.CatalogSyncTask _catalogSync;
    private readonly Services.BlackframeChapterTask _blackframe;

    public BridgeController(Services.CatalogSyncTask catalogSync, Services.BlackframeChapterTask blackframe)
    {
        _catalogSync = catalogSync;
        _blackframe = blackframe;
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncNow(CancellationToken cancellationToken)
    {
        await _catalogSync.ExecuteAsync(new Progress<double>(), cancellationToken);
        return Accepted();
    }

    [HttpPost("blackframe")]
    public async Task<IActionResult> Blackframe(CancellationToken cancellationToken)
    {
        await _blackframe.ExecuteAsync(new Progress<double>(), cancellationToken);
        return Accepted();
    }
}
