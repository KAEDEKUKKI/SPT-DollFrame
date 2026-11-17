# 角色建模質感與光影補強設計（方案 A + B + C）

## 目標與背景

目前 DollFrame 角色在實機中仍有輕微「塑料感」，且部分情境下「光影偏亂」。根因來自四個層面：
1. **材質與高光過於均勻**：`PmxRuntime.ClassifyMaterial` 與 `ShaderManager.ClassifyStylizedLook` 給予皮膚、臉部與霧面布料偏高的光滑度（`0.26–0.34`）與統一白色的掠射光澤（`_SheenIntensity`）及偏強冷色邊緣光，加上僅綁定 `_MainTex`、缺乏法線貼圖（`_BumpMap`），使表面反光像平滑塑料。
2. **雙面網格法線錯亂**：`PmxRuntime.BuildUnityMesh` 處理 `DoubleSided` 材質時僅複製反向三角形而共用原始頂點與法線，導致頭髮、裙襬、緞帶背面以正面法線受光，明暗塊錯亂。
3. **蒙皮與貼圖過濾退化**：`CharacterRig` 未強制 `SkinnedMeshRenderer.quality = SkinQuality.Bone4`（遊戲設定可能降為 2 骨骼）；`PmxRuntime.LoadTexture` 未設各向異性過濾（`anisoLevel`）；`SdfFaceShading.BuildCloneTexture` 關閉了 Mipmap，遠距易產生閃爍鋸齒。
4. **臉部 SDF 與即時光影耦合**：`SdfFaceDriver.Update` 直接切換六向 LightProbe 與太陽方向，缺少時間平滑且陰影下限固定，在明暗交錯場景易與即時光影疊加顯得混亂。

## 非目標與限制

- **不重建 `dollframe_shaders.bundle`**：著色器套束為外部預建資產，所有改動限定於 C# 執行期（`Material` 屬性與貼圖生成須先以 `HasProperty` 確認相容性）。
- **不破壞既有契約**：維持 `AGENTS.md` 硬性不變量（遊戲骨架零寫入、每幀僅寫 `localRotation`、18 行族日誌與 31 項 `Plugin.cs` 設定鍵不變、`DOLLFRAME_PMX_PARSER_ONLY` 雙編譯維持通過）。
- **不污染臉／眼平滑度**：臉部與眼睛不套用高頻程序法線噪點。
- **資源生命週期安全**：任何新產生的法線貼圖必須納入 `PmxAssets.Textures` 隨 LRU 驅逐釋放；`SdfFaceDriver.Update` 維持零每幀 GC 配置。

## 方案比較與選擇

- **方案 A（材質與光影校準）**：調整 PBR 參數、修正雙面法線、`Bone4`、Mipmap 與各向異性過濾；零額外 VRAM，但缺乏微表面凹凸細節。
- **方案 B（程序化微表面法線＋材質校準）**：在 A 的基礎上自動偵測或程序化合成 `_BumpMap`，打破平面塑料感。
- **方案 C（方案 A + B + 臉部 SDF 與環境光耦合強化）**：全面整合材質校準、雙面法線、程序化微表面法線與臉部 SDF 平滑／環境自適應。**已選定此整合方案（A + B + C）。**

## 模組設計

### 1. 雙面網格法線與貼圖過濾（`Character/PmxRuntime.cs`）
- **`BuildUnityMesh` 雙面頂點拆分**：針對 `pmx.Mats[mi].DoubleSided` 的子網格，為背面複製一份頂點緩衝（位置、UV、骨骼權重相同，法線取反 `-normal`），並更新 `indexFormat`（超過 65,000 頂點時自動使用 `UInt32`）與 `mesh.RecalculateTangents()`，使頭髮、裙襬正反面各自依正確法線受光。
- **貼圖過濾與降採樣**：`LoadTexture` 設定 `FilterMode.Trilinear` 與 `anisoLevel = 8`；`Downscale` 在縮放比小於 `0.5` 時採漸進半縮小（2x step）以減少高頻細節鋸齒。
- **PBR 類別參數校準（`ClassifyMaterial`）**：
  - 臉部（`face`）：smoothness `0.15`、metallic `0`（避免臉部油亮塑料感）。
  - 皮膚（`skin`）：smoothness `0.18`、metallic `0`。
  - 霧面布料（`cloth`/`sock`）：smoothness `0.14`、metallic `0`。
  - 頭髮（`hair`）：smoothness `0.36`、metallic `0`。
  - 皮革／靴／手套（`leather`/`boot`/`glove`）：smoothness `0.42`、metallic `0.05`。
  - 金屬（`metal`/`armor`/`buckle` 等）：smoothness `0.72`、metallic `0.82`。
  - 眼睛（`eye`/`lash`/`brow`）：維持 `0.65`（保留眼神高光）。

### 2. 程序化與伴隨法線貼圖（`Character/PmxRuntime.cs` + `Character/ShaderManager.cs`）
- 若材質為不透明且非臉部／眼睛／睫毛，優先於 `textureBytes` 搜尋常見法線貼圖命名（如 `<texBase>_n` / `_normal` / `_bump` 等）；若無伴隨法線圖且材質支援 `_BumpMap`（`m.HasProperty("_BumpMap")`），則從漫反射貼圖生成輕量級 Tangent-Space 法線貼圖（受 `MaxTextureSize` 與上限 1024 控制，以降低載入成本與 VRAM）：
  - 以亮度梯度（Sobel）提取貼圖既有褶皺與接縫立體感；
  - 依材質類別疊加微弱程序微紋理（布料經緯編織、皮革細顆粒、髮絲縱向紋理、金屬細拉絲）；
  - 皮膚僅保留極輕微梯度、不加粗糙噪點。
- `ShaderManager.ConfigureMaterial`：綁定 `_BumpMap` 時設定類別化的 `_BumpScale`（布料 `0.45`、皮革 `0.55`、頭髮 `0.35`、金屬 `0.40`、皮膚 `0.15`）並啟用 `_NORMALMAP` keyword。
- 生成的法線貼圖加入 `PmxAssets.Textures` 清單，於 `EvictEntry` / `PurgeDeadEntry` 時統一銷毀。

### 3. 邊緣光與掠射光澤平衡（`Character/ShaderManager.cs`）
- 調整 `ClassifyStylizedLook`：
  - 臉部／皮膚：改用暖膚色邊緣光（`(1.0, 0.86, 0.78)`）、大幅收斂強度（`0.24–0.30`）並提高收斂銳利度（`4.0–4.2`），降低 `_SheenIntensity`（`0.03–0.04`），消除蠟像／塑料白霧感。
  - 布料／預設：適度降低 `_RimIntensity`（`0.55`）與 `_SheenIntensity`（`0.08`），保留輪廓層次而不搶走主光影。
  - 皮革／絲襪：維持微掠射光澤（`0.16`）與適中邊緣光（`0.68`）。
  - 頭髮／金屬：保留較清晰的高光輪廓（頭髮 `0.72` / sheen `0.14`，金屬 `0.85` / sheen `0.04`）。

### 4. 4 骨骼蒙皮品質（`Character/CharacterRig.cs`）
- `CharacterRig.Build` 建立 `Body_SMR`（`smr`）與 `Head_SMR`（`headSmr`）時，明確設定 `quality = SkinQuality.Bone4`，確保 4 權重頂點在任何遊戲畫質設定下不退化。

### 5. 臉部 SDF Mipmap 與環境光平滑耦合（`Character/SdfFaceShading.cs`）
- `BuildCloneTexture`：開啟 `mipChain: true`、`FilterMode.Trilinear`、`anisoLevel = 4`，並於 `Composite` 的 `tex.Apply(true, false)` 重新產生 Mipmap，消除遠距臉部貼圖閃爍。
- `SdfFaceDriver.Update`：對目標光向 `L` 加入幀間平滑插值（`Vector3.Slerp`），避免角色轉頭或跨越 LightProbe 邊界時陰影方向跳動；並依環境亮度動態調節臉部 SDF 陰影下限（暗處保留較柔的陰影對比，避免與即時陰影疊加過黑）。

## 驗證方式

1. `dotnet build -c Release -p:CopyOutputToBepInEx=false`（0 警告 0 錯誤）。
2. `Tests/DollFrame.Verify.csproj`（`DollFrameVerify` 全部子命令通過，確保 `MeshSplitter` 雙編譯與既有幾何／骨架／遙測測試全綠；可補入雙面法線翻轉與法線貼圖生成邏輯的單元驗證）。
3. `bash tools/check-log.sh tools/fixtures/good.log`（exit 0）與 `bad.log`（exit 1）。
