# AGENTS.md — DollFrame 代理人協作規範

面向 AI 代理人的精簡契約:本 repo 是 SPTarkov 的 BepInEx C# 客戶端模組(識別 `DollFrame`,版本隨 `DollFrame.csproj`)。**範疇 = 執行期 + 離線驗證**;`.gfc` 與語音為預建完成資產,產製鏈在 repo 外(Packager 與 tools 製作面腳本已移除,勿重建或引用)。

## 硬性不變量(任何改動不得違反)

1. **遊戲骨架零寫入**:不改 `SkeletonRootJoint`/`SkeletonHands` 的 `localPosition`/`localRotation`,不覆寫攝影機或 ADS 視角;rig 只自建於玩家 `Root_Joint` 之下。
2. **每幀僅寫 `localRotation`**:零 `localPosition`/`localScale` 幀寫(零拉伸契約);縮放唯一合法寫入點 = Build 期 rig 根的單一等比 k(腿錨定,臂鏈個別縮放已退役勿復活)。
3. **日誌契約**:行前綴 `[DollFrame]`,18 行族 + 4 失敗標記語意不變;每行族單一生產者;改動須同步 `tools/check-log.sh` 前綴釘點與 `tools/fixtures/{good,bad}.log`。
4. **切換安全**:角色切換僅主選單生效(需 menu preview 在場且無戰局);死亡/布娃娃一律回退純 FK 鏡射;`Replace player body` off 時對戰局玩家**零寫入**,不允許引入旁路掛鉤。
5. **驗證先於完成**:任何行為改動以 verify harness 與 `check-log.sh` 為準,綠燈前不完成宣告;不得刪除/弱化失敗測試。

## 模組地圖(改動定位用)

| 路徑 | 職責 |
|---|---|
| `Plugin.cs` | BepInEx 入口:31 項設定、包掃描(含 siblings)、Harmony 容器註冊、PageUp 循環 |
| `Character/PmxRuntime.cs` | PMX 载入、材質分類、`PmxAssets` LRU 快取 |
| `Character/MeshSplitter.cs` | 載入期拆頭/身網格(雙編譯:main + `DOLLFRAME_PMX_PARSER_ONLY`) |
| `Character/CharacterRig.cs` | rig Build + 每幀 `Drive`(唯一旋轉寫入面) |
| `Character/GameBones.cs` | 遊戲骨骼錨點解析(戰局/預覽共用;排 `Weapon_root*` 子樹) |
| `Character/BoneContract.cs`、`BindMath.cs` | 骨骼契約(PureDelta/PureRel)與綁定座標純數學 |
| `Character/ModelMetrics.cs`、`ModelScaler.cs` | 跨距量測與 rig 根單一等比縮放(`ModelScaleMath`) |
| `Character/Kinematics/` | 純數學:`TwoBoneIk`(含 `SolveRelaxed`)、`ArmGrabIk`、`ArmRest`、`RigAim`、`SpringPhysics` |
| `Character/SdfFaceShading.cs`、`ShaderManager.cs` | 臉部 SDF(烘焙端在 repo 外)、束載入 + 全域邊緣光 |
| `Character/VoiceReplacer.cs` | 語音載入(資料夾 `.wav` 或 `.gf2v` 歸檔)與 Vanilla 還原 |
| `Character/Patches.cs`、`CapsulePatches.cs`、`IkRefresh.cs`、`Camera/FirstPersonSMRMask.cs` | Harmony patch、膠囊補償、IK 刷新、FP 頭部 `ShadowsOnly` |
| `Character/Contract/` | `ModelProfile`、`RigPairing`、`TelemetryRegistry` |
| `Visibility/` | `VanillaBodyHider`、`EquipmentHide`(EFT.Transmog 承接,資料級)、`VanillaSelection`、`VisualShield`、`AccessoryFilter` |
| `MenuUI/` | `CharacterSwitchUI`(Overall 列)、`PreviewProxy`(預覽 + 空手契約)、`TradingCardIcon` |
| `ServerMod/` | 伺服端 companion(僅載入資訊;SPT `~4.1.3`;版本由 assembly 推導) |

## 命令

```bash
dotnet build -c Release -p:CopyOutputToBepInEx=false   # 純驗證建置(0 警告 0 錯誤)
dotnet build -c Release                                # 依 UserSettings.props 部署到 SPT
dotnet build Tests/DollFrame.Verify.csproj && \
  DOTNET_ROLL_FORWARD=LatestMajor dotnet Tests/bin/Debug/net8.0/DollFrameVerify.dll
bash tools/check-log.sh <遊戲目錄>/BepInEx/LogOutput.log   # fixtures: good=exit 0 / bad=exit 1
```

harness 環境鈕:`DOLLFRAME_PACKAGES_DIR` / `DOLLFRAME_PACKAGES` / `DOLLFRAME_SCALE_DELTA` / `DOLLFRAME_EXPECT_DELTA`。

## 慣例

- 命名空間一律 `DollFrame[*]`(`DollFrame.Visibility`/`DollFrame.Tests`/`DollFrame.Server`);日誌/GUID/資產前綴皆 `DollFrame`。烘培束內 Shader 名 `DollFrame/CharacterPBR*` 為新名優先、舊名(GF2)fallback 相容 — 束資產名勿改。
- 版本單一真值 = `DollFrame.csproj` `<Version>`;ServerMod 由 assembly 推導,不寫字面值。
- 行為變更同步 `CHANGELOG.md`(逐版條目含驗證證據);文件變更同步 `README.md` 與本檔。
- 修改 `Plugin.cs` 設定鍵時:README 設定表、harness telemetry 計數、說明文案三處同步。

## 進一步閱讀

`README.md`(使用者面:功能/安裝/設定/排解)、`CHANGELOG.md`(演進史與既有決策;重大改約理由多在其中,先查再動)。
