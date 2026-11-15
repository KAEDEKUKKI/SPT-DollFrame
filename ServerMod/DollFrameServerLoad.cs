using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace DollFrame.Server;

/// <summary>
/// 伺服器主控台的載入資訊行（client 端本體仍是 BepInEx 用的 DollFrame.dll；此 companion 只有資訊顯示職責）。
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 10)]
public sealed class DollFrameServerLoad(ISptLogger<DollFrameServerLoad> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        logger.Info("[DollFrame] server companion loaded — client mod at BepInEx/plugins/DollFrame (DollFrame.dll + Characters/<name>/*.pmx)");
        return Task.CompletedTask;
    }
}
