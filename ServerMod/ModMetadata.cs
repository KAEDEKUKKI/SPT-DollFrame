using SPTarkov.Server.Core.Models.Spt.Mod;
using SemVerVersion = SemanticVersioning.Version;
using SemVerRange = SemanticVersioning.Range;

namespace DollFrame.Server;

/// <summary>
/// 伺服器端 companion 的識別資料：讓伺服器主控台與其他 mod 一樣，
/// 啟動時列出「模組：名稱 版本 (GUID)」載入資訊。版本由 assembly 版本（csproj
/// `<Version>`）推導——單一真值，隨 client csproj 版本同步，無字面值漂移。
/// </summary>
public record DollFrameServerMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "DollFrame";
    public string Name { get; init; } = "DollFrame";
    public string Author { get; init; } = "kukki";
    public List<string>? Contributors { get; init; } = [];
    public SemVerVersion Version { get; init; } =
        new(typeof(DollFrameServerMetadata).Assembly.GetName().Version!.ToString(3));
    public SemVerRange SptVersion { get; init; } = new("~4.1.3");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; } = [];
    public Dictionary<string, SemVerRange>? ModDependencies { get; init; } = [];
    public string? Url { get; init; }
    public string License { get; init; } = "All rights reserved";
    public string? WWWRootUrl { get; init; }
    public string? HomePage { get; init; }
    public string? HomePageDescription { get; init; } = "SPT-DollFrame character & voice replacement framework companion.";
}
