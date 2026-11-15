# DollFrame

> 面向 SPTarkov 的模組化角色替換框架 — PMX 角色模型導入、IK 重定向、PBR/SDF 著色與語音替換。

DollFrame 是 SPTarkov 的 BepInEx 客戶端模組,將自訂 PMX(MMD)角色模型以 `.gfc` 角色包導入遊戲:為每個角色建立獨立骨架(rig),逐幀鏡射原版動畫機,以兩骨 IK 重定向手臂至持械姿態,並提供語音替換、次要物理、自訂 PBR/SDF 著色與裝備視覺隱藏。另附一個僅顯示載入資訊的伺服端 companion(無遊戲邏輯)。

模組識別:`DollFrame`(BepInEx GUID/Harmony id/日誌行前綴 `[DollFrame]`),根命名空間 `DollFrame`,版本 `0.3.18`。

- **預設零干擾**:`Replace player body` 預設 off — off 時模組不對戰局玩家做任何寫入,動作與視角完全原生;主選單 3D 預覽與角色卡不受影響。
- **原版骨架零侵入**:不修改遊戲骨架的本地平移/旋轉,不覆寫攝影機與開鏡(ADS)視角。
- **零拉伸契約**:每幀僅寫入 `localRotation`,模型側只在 rig 根套用一次腿錨定單一等比縮放,手指與比例保持作者原始。

## 功能特性

- **角色替換**:選用 `.gfc` 角色包建構獨立 rig,含模型側單一等比縮放與移動膠囊補償;LRU 快取(預設保溫 4 位)降低切換卡頓。
- **手臂 IK**:`IK arm retarget to game hands`(預設 on)時,持械態以兩骨 IK 求解上臂/前臂、胸腔局部空間 pole 與 SDF 防穿模;空手態自然垂手、十指回作者原始姿態;死亡/布娃娃自動回退 FK 鏡射。
- **語音替換**:`Replace player voice`(預設 on)依所選角色家族載入語音,切回 `Vanilla (PMC)` 自動還原原版語音。
- **次要物理**:頭髮、裙擺、衣襬、胸部彈性,含頭/頸/胸腔/腰腹/骨盆/四肢的身體膠囊碰撞防穿模與嚴格零拉伸;前瀏海固定不動。
- **渲染**:全身原 `Standard` PBR 基礎 + 束內 `DollFrame/CharacterPBR` 著色器的環境自適應邊緣光(Rim Light,可調強度/銳度/色調)+ SDF 臉部陰影;第一人稱頭部切 `ShadowsOnly` 防穿相機。
- **裝備視覺隱藏**:8 個槽位(護甲/戰術背心/背包/頭盔/面罩/眼睛/耳機/臂章)資料級快照清空,實際屬性不變。
- **選單 UI**:角色屬性「全般(Overall)」畫面的 `DollFrame 角色:` ◀ ▶ 切換列(即時重掃新包,免重啟)、主選單 `PageUp` 快速循環、跳蚤市場角色卡圖示、主選單 3D 預覽。

## 環境需求

- [SPTarkov](https://www.sp-tarkov.com/) **~4.1.3**(伺服端 companion 宣告的測試基準)
- BepInEx + 0Harmony(隨 SPT 內建)
- 自行建置才需要:.NET SDK(客戶端 `netstandard2.1`;驗證 harness net8.0)

## 安裝

| 內容 | 遊戲目錄內的位置 |
|---|---|
| `DollFrame.dll` + `dollframe_shaders.bundle` | `BepInEx/plugins/DollFrame/` |
| 角色包 `*.gfc` | `BepInEx/plugins/DollFrame/DollFrame_Characters/` |
| 語音包 | `BepInEx/plugins/DollFrame/DollFrame_Voices/<角色家族>/*.wav` 或單檔 `*.gf2v` 歸檔 |
| 伺服端 companion(`DollFrame.Server.dll`) | `user/mods/DollFrame/`(選用,僅載入資訊) |

> 角色包與語音包也會從 `BepInEx/plugins/` 下任何含 `Characters`/`DollFrame_Characters`(或 `Voices`/`DollFrame_Voices`)子資料夾的內容包掃入。`.gfc` 圖形與語音為**預建完成資產**;重打包/語音產製不在本 repo 範疇。

## 快速上手

1. 主選單進入角色屬性的**全般(Overall)**畫面,以 `DollFrame 角色:` 列的 ◀ ▶ 選擇角色(新 `.gfc` 檔放入資料夾即出現,無需重啟)。
2. 或在主選單按 **`PageUp`** 逐個循環切換(僅主選單有效,戰局/藏身處 session 中忽略)。
3. 要在戰局中實際替換,開啟設定(`F12` → `DollFrame`)將 `Visual / Replace player body` 設為 on — 變更對下次掛載生效。
4. 其餘行為(語音、膠囊、IK、物理、邊緣光、裝備隱藏)都在設定檔分區中,見下表。

## 設定項

設定檔:`BepInEx/config/DollFrame.cfg`(BepInEx 依 GUID 自動產生;遊戲內 `F12` 可編輯)。共 31 項,四大分區:

### Scaling / Capsule

| 鍵 | 預設 | 說明 |
|---|---|---|
| `Apply to own player` | `true` | 對本地玩家骨架套用縮放 |
| `Height multiplier` | `1.0` | 推導膠囊時對模型原始高度的倍率(`0` 還原原版膠囊) |
| `Scale capsule` | `true` | 依縮放後身高補償 CharacterController 膠囊(Unity 不讀 transform scale) |

### Visual

| 鍵 | 預設 | 說明 |
|---|---|---|
| `Replace player body` | `false` | 戰局中以角色包替換身體;off 時模組對玩家零寫入 |
| `Character` | — | 所選角色(本視窗隱藏;於 Overall 切換列或 PageUp 切換,持久化於此) |
| `Switch character key` | `PageUp` | 主選單快速循環熱鍵(僅主選單生效) |
| `PMX unit scale` | `0.078` | 每個 PMX 單位的公尺數(MMD 慣例 1u = 8 cm) |
| `Model Y offset (m)` | `0` | 模型浮起/陷入地面時的垂直修正 |
| `Max texture size` | `2048` | 載入時降採樣過大紋理(控 VRAM) |
| `Flip UV` | `true` | PMX(DirectX V 軸向下)與 Unity(UV 向上)翻轉;貼圖顛倒才關 |
| `Hide model accessories` | `true` | 隱藏模型內建武器/背包等材質 |
| `Hide accessories name filter` | `gun, glock, bag, …` | 忽略大小寫子字串判定要隱藏的材質 |
| `Model facing yaw (deg)` | `180` | PMX 模型面 -Z,180 使其面向玩家前方 |
| `Idle cache size` | `4`(2–24) | 已解析角色的 LRU 快取容量;越大保溫越多、佔用越多 VRAM |
| `Scale model to game proportions` | `true` | Build 期於 rig 根一次套用腿錨定等比縮放 k |
| `Hide head in first person` | `true` | 第一人稱頭部 `ShadowsOnly`(保留地面陰影) |
| `IK arm retarget to game hands` | `true` | 持械兩骨 IK 重定向;Build 時讀取,變更需重新掛載 |
| `Replace player voice` | `true` | 依角色家族替換語音;Vanilla 時還原 |
| `Enable secondary physics` | `true` | 頭髮/裙擺/衣襬/胸部次要物理(前瀏海固定) |
| `Enable rim light` | `true` | 環境自適應邊緣光與布料/絲襪掠射光澤 |
| `Rim light intensity` | `0.65`(0–2.5) | 邊緣光強度(0 = 關) |
| `Rim light sharpness` | `3.2`(0.8–8) | 邊緣光貼合輪廓的收斂銳利度 |
| `Rim light tint` | `(0.85, 0.93, 1.0)` | 邊緣光全域色調 |

### Equipment(8 鍵,皆預設 `true`)

| 鍵 | 說明 |
|---|---|
| `Hide armor / tactical rig / backpack / headwear / face cover / eyewear / earpiece / armband on replaced bodies` | 隱藏該槽位的 PMC 裝備視覺(選單身體、戰局身體、FP viewmodel、角色卡),資料級快照、不動實際屬性 |

## 運作原理

```
Plugin.cs                 # BepInEx 入口:31 項設定、包掃描、Harmony 容器註冊、PageUp 循環
├── Character/            # 引擎核心
│   ├── PmxRuntime.cs     # PMX/貼圖載入與 LRU 快取(PmxAssets)
│   ├── MeshSplitter.cs   # 載入期拆分頭/身網格
│   ├── CharacterRig.cs   # 獨立 rig Build 與每幀 Drive(僅寫 localRotation)
│   ├── GameBones.cs      # 遊戲骨骼錨點解析(戰局與選單預覽共用)
│   ├── BoneContract.cs / BindMath.cs      # 骨骼契約與綁定座標數學
│   ├── ModelMetrics.cs / ModelScaler.cs   # 解剖跨距量測與單一等比縮放
│   ├── Kinematics/       # 純數學:TwoBoneIk、ArmGrabIk、ArmRest、RigAim、SpringPhysics
│   ├── ShaderManager.cs / SdfFaceShading.cs # 束載入、全域邊緣光、臉部 SDF
│   ├── VoiceReplacer.cs  # 語音載入與還原
│   ├── Camera/FirstPersonSMRMask.cs       # 第一人稱頭部渲染模式
│   └── Contract/  + Patches.cs / CapsulePatches.cs / IkRefresh.cs
├── Visibility/           # 原版素體隱藏、8 槽裝備隱藏、Vanilla 判準、防閃白
├── MenuUI/               # Overall 切換列、主選單預覽、角色卡
├── Shaders/              # dollframe_shaders.bundle(預建)+ UnityBuilder 來源專案
└── ServerMod/            # 伺服端 companion(僅載入資訊)
```

核心不變量(契約,不因改版放寬):

1. **遊戲骨架零寫入** — 只在玩家 `Root_Joint` 下自建 rig,原版骨架與攝影機/ADS 不被改動。
2. **每幀僅寫 `localRotation`** — 零 `localPosition`/`localScale` 幀寫;縮放唯一合法寫入點是 Build 期 rig 根。
3. **死亡安全** — 死亡/布娃娃自動回退純 FK 鏡射;切換僅在主選單允許,不會 mid-session 生效。
4. **日誌契約** — 全部日誌行以 `[DollFrame]` 前綴、行族固定,`tools/check-log.sh` 以 fixtures 逐點釘死(bad.log 對應 exit 1)。

## 疑難排解

| 症狀 | 處置 |
|---|---|
| Overall 列看不到某角色 | 確認 `.gfc` 在 `DollFrame_Characters/`(該列即掃);熱鍵循環名單需重啟遊戲後更新 |
| 模型浮起/陷入地面 | 調 `Model Y offset (m)` |
| 貼圖上下顛倒 | 關閉 `Flip UV` |
| 切換卡頓 | 提高或降低 `Idle cache size`(LRU 重解析會有 250–460 ms 停頓) |
| VRAM 偏高 | 降低 `Max texture size` 或 `Idle cache size` |
| 手臂/持槍異常 | 確認 `IK arm retarget to game hands` 設定;變更後需切換角色或重進戰局 |
| 日誌診斷 | `bash tools/check-log.sh <遊戲目錄>/BepInEx/LogOutput.log`;行前綴 `[DollFrame]` |

## 開發

```bash
# 建置(依 UserSettings.props 的 SPTTarkovDirectory 自動部署至 BepInEx/plugins/DollFrame/)
dotnet build -c Release

# 純驗證建置(不拷貝至遊戲目錄;預期 0 警告 0 錯誤)
dotnet build -c Release -p:CopyOutputToBepInEx=false

# 離線驗證 harness(15 個子命令,不需遊戲 session)
dotnet build Tests/DollFrame.Verify.csproj
DOTNET_ROLL_FORWARD=LatestMajor dotnet Tests/bin/Debug/net8.0/DollFrameVerify.dll

# 執行日誌合約檢查
bash tools/check-log.sh <遊戲目錄>/BepInEx/LogOutput.log
```

- 部署設定在 `UserSettings.props`(`SPTTarkovDirectory` 指向 SPT 目錄;`CopyOutputToBepInEx=true` 時 Build 後自動拷貝 dll 與著色束)。
- 驗證 harness 環境鈕:`DOLLFRAME_PACKAGES_DIR` / `DOLLFRAME_PACKAGES` / `DOLLFRAME_SCALE_DELTA` / `DOLLFRAME_EXPECT_DELTA`;雙編譯 define `DOLLFRAME_PMX_PARSER_ONLY`。
- 伺服端 companion:`dotnet build ServerMod/DollFrame.Server.csproj`(MSBuild 屬性 `SPT_DIR` 指向 SPT 伺服目錄,部署至 `user/mods/DollFrame/`),版本由 assembly 版本推導(單一真值)。
- 本 repo 範疇 = **執行期 + 離線驗證**;`.gfc`/語音為預建部署資產,產製鏈在 repo 外。

## 相關文件

- [`CHANGELOG.md`](CHANGELOG.md) — 完整版本演進與驗證紀錄
- [`AGENTS.md`](AGENTS.md) — AI 代理人之架構契約與硬性不變量精簡規範

## 授權

**All rights reserved** © kukki(見 `ServerMod/ModMetadata.cs` 之模組宣告)。
