# GF2 — 少女前線2：追放 角色導入模組（SPTarkov）

GF2 為 SPTarkov（0.16.9.5.40743）之 BepInEx / Unity **純客戶端模組**（Plugin ID: `kukki.GF2`），將《少女前線2：追放》PMX 角色模型與日文 AI 語音包導入遊戲。另附資訊性伺服器端 companion（`ServerMod/`），僅於啟動時輸出狀態，無遊戲邏輯。

---

## 核心架構與設計規範（Architecture & Invariants）

1. **遊戲骨架零侵入（Pristine FBAK）**：
   - 不修改遊戲原始骨架（`SkeletonRootJoint` / `SkeletonHands`）的 `localPosition` 或 `localRotation`，也不覆寫第一人稱攝影機或武器視角（確保原生開鏡 ADS 準確）。
   - 於玩家 `Root_Joint` 下為每個角色建立獨立 `GF2_rig_*` 骨架實例，於 `Build` 期對 rig 根套用一次性腿錨定單一等比縮放 `k`（保持作者原始全身比例與手掌大小）。
2. **純本地旋轉驅動（Zero-Stretch）**：
   - `CharacterRig.Drive()` 每幀對身體／四肢骨骼**僅寫入 `localRotation`**：
     - **持械態（`HandsController.Item is Weapon`）**：上臂／前臂由 `TwoBoneIkMath.Solve` 兩骨 IK 求解，搭配鎖骨自然前伸輔助（最多 `22°`）減少空抓；胸腔 SDF 防止手肘穿入軀幹。
     - **空手態（`!armed`）**：由 `TwoBoneIkMath.SolveRelaxed` 驅動自然垂手放鬆姿態，手指回歸作者原始放鬆姿勢；主選單預覽同步套用空手契約並隱藏展示武器。
     - **死亡／布娃娃**：自動退回純 FK 鏡射。
3. **次要物理引擎（`SpringPhysics`）**：
   - 自動辨識後髮／側髮、裙擺、外套／披風／圍巾／腰帶／衣袖與胸部骨骼，於 `LateUpdate` 每幀推進一次（`Time.frameCount` 防重複步進），嚴格投影段長維持零拉伸。
   - **前瀏海固定不動**，並以頭骨額狀面限制後髮不得向前翻越肩膀，搭配 15 組隨動身體膠囊（頭、頸、胸腔、腰腹、骨盆、雙鎖骨、雙臂、雙腿）貼合半徑防穿模。
4. **渲染與臉部 SDF 光照**：
   - 全身採用 Unity 原生 `Standard` PBR 着色器（完整相容塔科夫 Deferred/Forward 管線與室內燈光），依材質類別設定金屬度／光滑度，並僅在 `alpha < 0.90` 時啟用透明模式以杜絕半透明穿幫。
   - 臉部採用 `LightProbes` 探針主光＋太陽混合驅動之 SDF 陰影（暖膚色陰影下限 `0.76 / 0.68 / 0.65`，寬柔過渡帶 `0.26 rad`），第一人稱下頭部切換 `ShadowsOnly`（防穿透並保留地面陰影）。
5. **AI 日文語音包（`.gf2v` 無損歸檔）**：
   - 20 位角色全數配備 GPT-SoVITS v4（48kHz PCM）日文語音，去重並無損壓縮為單檔 `.gf2v`（`GF2_Voices/<角色家族>.gf2v`），選擇建模或切換 `Vanilla (PMC)` 時自動替換／還原。

---

## 模塊目錄結構

```
GF2/
├── Plugin.cs                         # BepInEx 入口、26 項設定鍵、Harmony 容器註冊
├── Character/                        # 【主模塊一】角色建模、驅動與語音
│   ├── PmxRuntime.cs                 # .gfc 解析、材質分類與 LRU 資產快取
│   ├── BoneContract.cs               # 骨骼映射與 PureDelta / PureRel 契約
│   ├── GameBones.cs                  # 遊戲骨架錨點解析與契約驗證
│   ├── ModelMetrics.cs               # 模型解剖跨距量測
│   ├── BindMath.cs                   # 綁定座標變換純數學
│   ├── ModelScaler.cs                # 模型側單一等比縮放（ModelScaleMath）
│   ├── CharacterRig.cs               # 獨立骨架建構與每幀 Drive 管線
│   ├── BodyReplacer.cs               # 玩家掛載宿主、LateUpdate / onBeforeRender 驅動
│   ├── SdfFaceShading.cs             # 臉部 SDF 陰影與 LightProbe 光照驅動
│   ├── VoiceReplacer.cs              # .gf2v / .wav 語音包同步載入與還原
│   ├── Patches.cs / CapsulePatches.cs / IkRefresh.cs
│   ├── Camera/FirstPersonSMRMask.cs  # 第一人稱頭部 ShadowsOnly 遮罩
│   ├── Contract/                     # RigPairing、ModelProfile、TelemetryRegistry
│   └── Kinematics/                   # RigAim、ArmRest、TwoBoneIk、SpringPhysics
├── Visibility/                       # 【主模塊二】隱藏系統
│   ├── VanillaBodyHider.cs           # 原版素體隱藏與還原
│   ├── VisualShield.cs               # 0.25s 防閃白保護
│   ├── VanillaSelection.cs           # Vanilla (PMC) 判準
│   ├── EquipmentHide.cs              # 8 槽裝備資料級隱藏
│   └── AccessoryFilter.cs            # 模型自帶武器/背包材質過濾
├── MenuUI/                           # 【主模塊三】選單與角色卡
│   ├── PreviewProxy.cs               # 主選單 3D 預覽宿主與空手契約
│   ├── CharacterSwitchUI.cs          # 人物介面切換列
│   └── TradingCardIcon.cs            # 角色卡圖示生成
├── Packager/                         # PMX → .gfc 打包與 Head/Body 網格分離工具
├── Tests/                            # GF2Verify 離線數學與契約驗證工具（15 子命令）
└── ServerMod/                        # 資訊性伺服器端 companion
```

---

## 安裝與使用

1. **插件本體**：將 `GF2.dll` 放置於 `BepInEx/plugins/kukki.GF2/`。
2. **角色包（41 套）**：將 `.gfc` 角色包置於 `BepInEx/plugins/kukki.GF2/GF2_Characters/`。
3. **語音包（20 位角色）**：將 `.gf2v` 語音歸檔置於 `BepInEx/plugins/kukki.GF2/GF2_Voices/`。
4. **遊戲內切換**：於人物屬性畫面（Overall）上方點擊 `＜ GF2 角色 ＞` 箭頭，或於主選單按 `PageUp` 循環切換（含 `Vanilla (PMC)`）。

---

## 常用設定項（BepInEx / F12）

| 分區 | 設定鍵 | 預設值 | 說明 |
|---|---|---|---|
| **Scaling** | `Apply to own player` / `Apply to observed players` | `true` | 本地／Fika 隊友套用建模替換 |
| **Capsule** | `Scale capsule` / `Height multiplier` | `true` / `1.0` | 依縮放後身高補償碰撞膠囊 |
| **Visual** | `Replace player body` | `false` | 戰局玩家替換開關（主選單預覽常駐有效） |
| | `Switch character key` | `PageUp` | 主選單快速切換角色熱鍵 |
| | `Scale model to game proportions` | `true` | 腿錨定全模型單一等比縮放 |
| | `Hide head in first person` | `true` | 第一人稱頭部切換 `ShadowsOnly` |
| | `IK arm retarget to game hands` | `true` | 持械手臂兩骨 IK 與鎖骨輔助重定向 |
| | `Enable secondary physics` | `true` | 頭髮、裙擺、衣物配件與胸部次要物理（前瀏海固定） |
| | `Replace player voice` | `true` | 依角色自動替換／還原日文語音包 |
| **Equipment** | `Hide armor / tactical rig / ...`（共 8 鍵） | `true` | 隱藏原版裝備視覺模型（不影響實際屬性） |

---

## 建置與離線驗證

```bash
# 建置並部署至遊戲 plugins 目錄
dotnet build -c Release

# 純驗證建置（不拷貝至遊戲目錄）
dotnet build -c Release -p:CopyOutputToBepInEx=false

# 離線數學與契約驗證（15 項全檢查）
dotnet build Tests/GF2.Verify.csproj
DOTNET_ROLL_FORWARD=LatestMajor dotnet Tests/bin/Debug/net8.0/GF2Verify.dll

# 相容矩陣與日誌檢查
python3 tools/check_matrix.py
bash tools/check-log.sh <遊戲目錄>/BepInEx/LogOutput.log
```

## 相關文件
- [`AGENTS.md`](AGENTS.md)：AI 代理人架構契約與硬性不變量規範。
- [`CHANGELOG.md`](CHANGELOG.md)：版本演進紀錄。
- [`docs/DATA.md`](docs/DATA.md)：`analysis/*.json` 基準資料說明。
# SPT-DollFrame
