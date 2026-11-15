using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace GF2.Server;

/// <summary>
/// 伺服器主控台的載入資訊行（client 端本體仍是 BepInEx 用的 GF2.dll；此 companion 只有資訊顯示職責）。
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 10)]
public sealed class Gf2ServerLoad(ISptLogger<Gf2ServerLoad> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        logger.Info("[GF2] server companion loaded — client mod at BepInEx/plugins/kukki.GF2 (GF2.dll + GF2_Characters/*.gfc)");
        return Task.CompletedTask;
    }
}
