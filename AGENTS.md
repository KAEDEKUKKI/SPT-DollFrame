# AGENTS.md — GF2（SPTarkov 的 GFL2 角色獨立建模導入：模型側全模型單一等比縮放架構）

## 專案概述（Overview）

GF2 為 SPTarkov（0.16.9.5.40743）之 BepInEx/Unity **純客戶端模組**（Plugin ID: `kukki.GF2`，C#），將 GFL2（少前2）PMX 角色模型導入遊戲。

核心路線（**2026-11-14 架構反轉；2026-11-14-6 全模型單一等比改約**）：在玩家 `Root_Joint` 下建立**每角色獨立 PMX Rig 實例**；**遊戲骨架全程 pristine（FBAK 純度），模型側於 Build 時對 rig 根一次性乘入單一等比因子 k（腿錨定：k＝遊戲腿鏈長/模型腿鏈長）**，全身（含手指）**逐位保持作者原始比例**且腳跨距貼齊遊戲腿鏈；每幀仍**僅寫 localRotation**。臂鏈個別縮放**已退役**（其曾使手臂/手指相對軀幹拉長 arm k 1.29–1.41）。次要動作（1C SpringBone、1B 姿態補償、手到握把 IK 連動）與視角模塊（1D 相機/FP 手臂/Sightline）**已全部移除**，動作與視角回歸遊戲原生；唯一保留的視角面是第一人稱頭部 SMR 開關（`FirstPersonSMRMask`，ShadowsOnly 防頭部穿透、保地面陰影）。附帶資訊性伺服器端 companion（`ServerMod/`），僅輸出啟動資訊，無遊戲邏輯。

**2026-11-14-13（0.3.13 空手放鬆驅動＋onBeforeRender 時序對齊＋Ribcage 縮放補償）**：反編譯 `Assembly-CSharp.dll`（`EFT.Player.VisualPass`、`HandPoser.SimpleGrip`、`Player_anim_controller`）與 `DuckCam.dll`（`RelaxedArms`、`PoseHold`）交叉實機日誌定位四項根因：① `Player.LateUpdate()` 尾段 `VisualPass()` 每幀必跑 `_limbs[0..1].solver.Update()`（`LimbIK` 將 TP 雙掌拉向 `empty_hands_container` 胸前 IK markers）與 `HandPosers[0..1].ManualUpdate()`（`HandPoser` 逐幀覆寫 TP 全部 30 根指骨為「右手食指扣扳機＋左手托護木」），且 `Player_anim_controller` 第 0/1 層無空手垂手動畫，故 `!armed` 回退純 FK 鏡射時實為忠實複製 `VisualPass` 架槍與扣扳機姿勢；② `DuckCam.RelaxedArms.Apply` 註冊於 `Application.onBeforeRender`（在所有 `LateUpdate` 之後執行），原版 PMC 讀渲染前垂手骨架而 GF2 停留在 `LateUpdate` 的 `VisualPass` 架槍姿勢；③ 持械態於 `PointOfView != ThirdPerson`（含 `FirstPerson` 與藏身處 `FreeCamera`）時 EFT 將 `Ribcage.localScale` 縮為 `0.65`，致世界座標肩掌距 `gameReach` 萎縮（`0.40m→0.26m`）；④ `SkeletonHands` 位於 `Ribcage` 下致 `handsRoot.IsChildOf(rootJoint)` 恆真。對策（零遊戲骨骼寫入、純 `localRotation` 幀寫不變量不變）：① `TwoBoneIkMath.SolveRelaxed`＋`CharacterRig.UnarmedRelaxActive`（`alive && !armed` 與主選單預覽啟用）：由 `ArmRestMath.TryCalibrate` 以 `rigRoot.TransformDirection` 標定胸腔局部垂手軸（並修正 `PoleLocalChest` 的 `rigRoot` 世界轉換），上臂/前臂隨 `上半身2` 呼吸/擺動/轉向協變垂手並經 3 次胸腔 SDF 防穿模，肩、手腕與 30 指骨回 PMX 作者原始放鬆姿態（`Quaternion.identity`）；② 持械 `ArmIkActive` 時以 `en.GameBone.lossyScale.x / _rootJoint.lossyScale.x` 補償 `Ribcage` FOV 縮放；③ `BodyReplacer.LateUpdate` 尾段重排 `Application.onBeforeRender` 並於 `OnBeforeRender` 再跑 `Drive()`；④ `EmitRigSourceLine` 改判 `rootJoint.IsChildOf(handsRoot)`，`GameBones.IsUnderWeaponSubtree` 隔離 `Weapon_root*`／`WeaponPrefab` 同名克隆骨。

**2026-11-14-10（0.3.10 狀態感知手臂治理：空手凍結根治——姿勢源狀態契約，非 IK 數學）**：取證反編譯（itinybad/SPT-Client-400@54e455a9）證實「選單/藏身處手臂卡死架槍」兩現象根因全在**遊戲端姿勢源**：①主選單 `PlayerModelLoader` 依「展示武器」（第一件可見 primary/holster/scabbard）寫 `WeaponType` 並由 `MenuPlayerPoser.LateUpdate` 每幀手動 LimbIK 指向武器 markers，GF2 藏走原版身體後構圖只剩「孤兒架槍姿勢」被 rig 忠實鏡射；②藏身處 FP 時 `HideoutPlayerOwner.SetPointOfView` 直接把 `BodyAnimatorCommon.enabled=false`（原版玩家看不到自己的 TP 身體故無人察覺），FreeCam 仍讀凍結骨架；③FP 手正常＝`EmptyHandsController` 自帶 prefab 動畫機（prefab adoption，與 TP body animator 分離）。對策三面（**零硬寫座標，全走遊戲契約面**）：**層 1-a 選單空手契約**——`PreviewProxyDriver.ApplyUnarmedContract` 於 attach 寫 `WeaponType=-1`（原版無武器編碼＝`EFT.PlayerAnimator.WEAPON_TYPE/WEAPON_TYPE_FLOAT/UI_WEAPON_TYPE` 三參數，偏離才寫、逐幀強制）＋**停用 MenuPlayerPoser**（其 LateUpdate 為手動 LimbIK/HandPoser 唯一生產點；本體動畫機由引擎推進不受影響；代價＝選單失去隨機查膛 idle beats）；掛載前原值存檔、`Teardown`/`OnDestroy` 反轉以保 `Vanilla (PMC)` 預覽原構圖；`PreviewProxyDriver` 補 `[DefaultExecutionOrder(32100)]`、每幀 `ArmIkActive=false`。**層 1-b 藏身處復活**——`Patches.PostHideoutSetPointOfView`（postfix `HideoutPlayerOwner.SetPointOfView`）於被替換玩家恢復 `BodyAnimatorCommon.enabled=true`，未替換者零介入。**層 3 狀態閘**——`BodyReplacer.IsArmed`：`player.HandsController is Player.EmptyHandsController`＝空手（Fika/SIT/SPT-VR 同款 idiom），`ArmIkActive = alive ∧ armed`；空手＝臂鏈面整個停用、rig 純鏡射（原版空手手臂本就純動畫機 FK），持械＝0.3.9 兩骨 IK 照舊（握把真接觸＝prefab `HumanLPalm` 真錨，列後續）；翻轉行 `arm gate: alive=… armed=… ik=… hands=…`（狀態變化才發）。telemetry 行族 14→17（新增 `arm gate:`／`preview unarmed:`／`hideout body animator:`，皆單一生產者、零新配置鍵）；主 csproj 補引用 `UnityEngine.AnimationModule.dll`／`AnimationSystem.Types.dll`（`IAnimator` 所在）。已失效語意：0.3.9「`ArmIkActive`＝純生存關（預覽路徑保持 true）」改約為狀態閘。待實測（S1）：戰局 FP 自體 TP 動畫機是否同被停用——`ik target:` 行於戰局 FP 空手時若恆定即中籤，屆時以同款 postfix 治理對應 class。

**2026-11-14-12（0.3.12 Armed 判準改約＋綁定源頭執行期驗證）**：0.3.11 藏身處 FreeCam 實測反證——原版 PMC 垂手擺臂對照 GF2 模型仍「右手食指扣扳機＋左手向前托護木」**連手指都未變**（指骨走純 FK delta 鏡射，源動則模型必動 ⇒ 鏡射源骨架的指骨同處持槍靜態）。依用戶指令兩項硬性修正：**①Armed 判準改約**——退役 `HandsController is Player.EmptyHandsController` 身分判定（Fika/SIT/SPT-VR idiom 在藏身處情境存疑），改唯一判準 `HandsController?.Item is EFT.InventoryLogic.Weapon`（反編譯核實 `Weapon : CompoundItem` 為 runtime 項類基類、`PistolItemClass : Weapon` 全槍械子類命中；Knife/grenade/道具→空手態；`arm gate:` 行加 `item=<ItemClass>` token）。**②綁定源頭驗證面**——`BodyReplacer.EmitRigSourceLine`（attach 一次性，`rig source:` 行族）：印 `root@id/parent`＋`underTPBody`（Root_Joint 是否在 `PlayerBody` GO 子樹）＋`fpHandsInside`（是否圈住 `PlayerBody.SkeletonHands` FP 手部骨架子樹——反編譯核實 PlayerBody 兼持 `SkeletonRootJoint`（TP）與 `SkeletonHands`（FP）兩套骨架，:492/:495），綁定錯源在日誌立判。**判定收斂規則**：若日誌呈 `armed=False ik=False underTPBody=True fpHandsInside=False` 而模型仍凍結 ⇒ 凍結源在遊戲端姿勢產出層（IK 組件/動畫機），依 UnityExplorer 實測值精準治理；`armed=True` ⇒ 帶回 `item=` 型別名對症。行族 17→18（`rig source:`）。

**2026-11-14-11（0.3.11 兩態收斂：展示武器網格隱藏＋架構簡化原則）**：0.3.10 實測——選單手臂垂下成功，但**展示武器網格**（`PlayerModelLoader` 父掛 `PBones.Ribcage.Original` 的 `WeaponPrefab`，剛性 `MeshRenderer` 不蒙皮骨架故 `HideVanillaSkins` 天然漏掉）仍插在胸口。補丁：`PreviewProxyDriver.HideShowcaseWeapon`——沿 `_skeleton` 子樹掃 `WeaponPrefab` 元件、其下全部渲染器收進既有 `_hidden` 面（逐幀強制＋`RestoreAllVanilla` 還原面自動覆蓋，零新狀態；`weaponMesh=<n>` token 併入 `preview unarmed:` 行；0.5s 重掃描補 loader 刷新晚到武器）。**架構簡化原則（用戶決策）**：统一收斂為「只認手上是否有武器實體」兩態——無武器＝純空手垂手（含殘留展示武器隱藏），有武器＝`TwoBoneIk`；不再遷就遊戲原版選單/藏身處特殊姿勢源差異。**藏身處 S1 未竟改為用戶 UnityExplorer 實測回報**：本版零新增藏身處 guess patch、`PostHideoutSetPointOfView` postfix 維持現狀，待實測確認真正凍結手臂的組件名後再精準修改。

**2026-11-14-9（臂鏈 IK 重定向：混合 FK＋臂鏈 IK，握把對位回復）**：uniform 改約後 TP 手搆不到握把的取捨，由**兩骨 IK 求解**回復——上臂/前臂面骨（左/右腕、左/右ひじ）改由 `Kinematics/TwoBoneIk.cs`（`TwoBoneIkMath` 純數學雙編譯權威，同日對齊重寫拆 `Kinematics/ArmRest` 校準）每幀求解：目標＝遊戲肩→掌方向 × gameReach×ArmRatio（ArmRatio＝模型臂長/遊戲臂鏈 0.54746m＝|LForearm1|+|LForearm2|+|LForearm3|+|LPalm|，實帶 0.83–0.95），手端自然伸展至模型臂長的持槍落點；reach 箝制 [max(|L1−L2|+0.005, 0.02), (L1+L2)×0.9995]。**零位置寫入不變量不變**：IK 只產 world 旋轉（Drive 經 `LocalFromWorld` 寫 localRotation，零 localPosition/localScale 幀寫），手腕/十指維持 palmL/R 原複製；**掌扭量 50% 落前臂**（`TwistAngle` 量遊戲掌相對 attach 掌繞臂軸扭量，腕端由 LocalFromWorld 逐幀補償父層 roll，手部姿態不變）；胸腔 SDF（圓球心＝上半身2 rig 節點，半徑＝0.5×|左腕−右腕 attach|＋0.02 箝 [0.08,0.25]）沿球面擠出肘/掌（≤3 迭代，|E−S|/|H−E| 不變量恆等）；肘 pole＝rig 根空間「下 −Y×1.0／外 ×0.6／後 −Z×0.4」（X 側符按 attach 肘位定側）；pole 近平行臂軸（sin<0.12）走 `RigAim.Perpendicular` 決定性 fallback，防近退化 1/|cross| 方向放大致彎曲平面逐幀翻面——站立靜止 IK 旋轉逐位恆定；swing/rest/扭量基準錨定本幀父鏈世界像（旋轉協變），轉動人物整臂剛性跟轉零自旋。生存關 `ArmIkActive`：`BodyReplacer.LateUpdate` 逐幀寫 alive 值，死亡/布娃娃回退 FK 複製；cfg `IK arm retarget to game hands`（25 鍵，預設 true，Build 時讀取）off 時面不掛。telemetry 新增 `ik:` 行族（attach 一次性，`cfg=on L=ok ratio=0.875 len=0.477m R=ok...`／`skip:<reason>`／`cfg=off`，單一生產者 `CharacterRig.SetupArmRest`）；行族 13→14、配置 24→25 鍵、harness 14→15 子命令（新增 `ik`：8 場景＋GameArmChainM 對凍結 dump 釘點）。ArmRatio 求解公式（頂點 S 夾角）為 cosθ = (L1² + |H−S|² − L2²) / (2·L1·|H−S|)。同日內部重寫（版本不動）：模塊拆 4 面——`Kinematics/TwoBoneIk.cs`（`TwoBoneIkMath` 求解權威，取代 `ArmIk`）＋`Kinematics/ArmRest.cs`（rest 校準 `ArmRestChain`）；掌錨改 **具名解析**（prefer 精確名 `Base HumanLPalm/RPalm`、raid/預覽統一，缺漏回退契約錨）；pole 改 **chest 局部空間**（隨上半身2 姿態攜行）；新增 ≥0.25s 節流的 `[GF2] ik target:` 觀測行（單一生產者 `CharacterRig.EmitIkTargetLine`）。

**2026-11-14-6（全模型單一等比改約：手指/手臂拉長根治）**：原始比例包的執行期四鏈根貼合曾對**左肩/右肩 pivot 寫臂鏈 k（live log 實測 1.2888）**——整個手臂子樹含手掌手指被相對軀幹放大約 23%，即用戶回報「手指與手臂被拉長」之根因。依用戶決策（**按照原比例正常縮放不變形**）改約：`ModelScaleMath.Solve` 只由腿鏈因子推**單一 uniform k**，`ModelScaler.Apply` 改寫**rig 根** localScale（`unitScale×k`，一次、逐位等比）——腿跨距貼齊遊戲腿鏈（接地/腳軌跡/膠囊錨定零回歸），全身比例 100% 作者原始。臂鏈常數/`ModelArmSpanM`/臂閘斷言（`proportions` 的 armK 閘）隨機制退役；armK 保留於 `proportion_scale.json` 作歷史比例資料。已知取捨：模型手臂自然短於遊戲手臂（k_arm/k_leg ≈ 0.83–0.95），TP 握把對位不再由縮放保證（FP 原生 viewmodel 不受影響）。

**2026-11-14-7（手腕對位：握把對位恢復；已於 2026-11-14-8 退役）**：uniform 改約後用戶實測「手會集中在胸前」——pose-parity 實證旋轉鏈 a0 b0 全對位，缺的是**鏈長**（原比例臂 0.603m 對遊戲臂 0.681m，差 7.8cm），純旋轉驅動物理上補不了。折衷實作：`Drive` 每幀於旋轉寫入後對 `左手首`/`右手首` 節點寫 `localPosition` 貼上遊戲掌錨（pmx 手腕→palmL/R 契約列本就指掌錨），位移 `HandAlignMaxM=0.15m` 箝制——手落在 PMC 手持槍的真實位置＝握把對位恢復，手指/手堂維持作者原始尺寸，僅前臂段視覺拉伸（≈3cm 正常帶）。零拉伸不變量同步改約（骨 localPosition 幀寫唯一核准點）；`zerostretch` sim 為自建 3 骨模擬不受影響，harness 全綠。

**2026-11-14-8（手腕對位移除：純本地旋轉契約回歸）**：藏身處/預覽實測腕端位移 8–15cm（程式註解自證「正常貼合差 ≈0.08m」；0.3.7 的「≈3cm 正常帶」宣稱低估近 3 倍）呈可見「手掌變形位移」；且臂 idle 不跟屬**遊戲端源頭靜態**（預覽/藏身處 idle 的臂骨近乎不動，模型忠實鏡射，非驅動缺漏——骨架契約完整：21/21 anchors＋digits 30）。依用戶決策**移除 hand-align**：`CharacterRig.Drive` 回復**每幀僅寫 localRotation**（骨 localPosition 幀寫歸零，-7 的唯一例外條款撤銷），腕旋轉仍逐幀複製 palmL/R；TP 手端位置由 FK 臂鏈唯一決定，握把對位失效（回 0.3.6 後期取捨——模型臂短，TP 手搆不到握把）。`HandAlignMaxM` 常數與 `DriveEntry.HandAlign` 旗標退役；`contract:`/`scale:`/`pose-parity` 行族語意不變。

**2026-11-14-3（模型 RBF 重製）**：`tools/remodel_pmx.py`——地標 RBF＋線性多項式靜態網格形變（無 AI 重拓撲/重生成；權重/材質/UV 零觸碰，位元組精準修補）。19 包重製至遊戲比例（臂 0.6808m 肘 63.1%、腿 1.0710m、趾 0.1545m、手堂 ×1.7–1.8），model_matrix 全段因子 worst |k−1|=0，重製後包可於 `Replace player body` 啟用後進 raid。守門員：RBF 插值自檢、頂點過衝守門員、hand scale 帶、無效 bone 索引歸零。

**2026-11-14-2（raid 建模停用）**：MMD 原始比例的模型對 pristine FBAK 骨架，手臂/手在遊戲內呈拉長變形（臂鏈等比 k 1.0–1.41 的體積斷差＋原生 PMC 手對不上握把）；依用戶決策 **`Replace player body` 預設改為 false**——off 時模組對玩家**零寫入**（Harmony 全掛鉤審計證明：4 族 ReplaceBody 短路＋5 個膠囊 postfix 空字典失效＋3 個 MenuUI 純選單面），動作與視角完全還給遊戲原生；raid 側建模**待模型重新建模（比例貼合遊戲骨架）後才可重新啟用**。選單 3D 預覽與角色卡（非玩家本體）照常。

**2026-11-13（SDF 臉部陰影＋寫實渲染＋41 包上架＋等比回歸）**：`tools/bake_sdf_face.py`——Face 材質區域之角度域 SDF 烘焙（每紋素終止線方位角 θ* 場，R=(ψ*+π)/2π×255 編碼＋coverage）；執行期 `Character/SdfFaceShading.cs` 以頭骨基座×光向量量 ψ、LUT 合成臉漫貼副本（臉材質 per-rig 實例，共享材質零污染）；其餘材質寫實化（中性 tint＋類別化 smoothness，金屬零）。**41 檔以「原始幾何/UV 零觸碰」重打包（無 warp）**——拉伸建模（warp 靜態網格形變／手堂徑向縮放）依用戶決策**全面退役**（拉伸側 UV 貼圖拉花；包內 PMX 與原始檔 MD5 逐位相同），比例貼合回到執行期 `ModelScaler` 四鏈根**等比縮放**（41 檔原始 arm k 1.015–1.412 皆在閘內）；伊格蕾塔/佩莉/索米/芙洛倫/芙鈴/維爾德維持淘汰（arm k 1.52–1.66）。

**出貨包集（41 包；包內＝原始幾何，比例由執行期全模型 uniform 等比貼合，全身原比例不變形）**：19 包家族原皮＋休息室/衣裝變體（克萊妲/代理人/可露凱/埃芙/妮基塔/威瑪西娜/安朵絲/希麗雅/托洛洛/春田/洛貝拉/琳德/瑪綺朵/索普/絳雨/萊婭/阿斯緹亞）＋ 黛煙原皮 ＋ 萊娜-秘夜獵影 ＋ **海倫（休息室/禮服）**。**依用戶決策淘汰上架**：伊格蕾塔/佩莉/索米/芙洛倫/芙鈴/維爾德 系（臂鏈 k 1.52–1.66，統一縮放產生可見體積斷差）。閘門資料凍結於 `analysis/proportion_scale.json`（41 條：legK＝uniform 貼合因子、armK＝歷史臂/腿比例資料），harness `proportions` 子命令斷言（legK 逐包重現＋raw 帶檢查；armK 閘已退役）。

**建置環境依賴（皆為已提交的機器本機實值路徑，改動需自負其責）**：
* `UserSettings.props` → `SPTTarkovDirectory` 與 `CopyOutputToBepInEx`：主模組所有遊戲 DLL 引用皆由該目錄解析；目錄為空時引用群組整組消失，建置必然失敗。
* `ServerMod/GF2.Server.csproj` → `SPT_DIR`：建置前 `EnsureSptDir` Target 檢查 `SPTarkov.Server.Core.dll` 存在，缺漏直接 Error 終止。

---

## 模組劃分與檔案對應（Module Inventory）

| 路徑 / 模組 | 職責與關鍵檔案 |
|---|---|
| `Plugin.cs` | BepInEx 入口：26 個配置鍵契約（0.3.10 勘誤：實綁 26 鍵，原載 25；含 `IK arm retarget to game hands`／`Switch character key` 等）、套件探測、7 個容錯 Harmony 容器安全註冊 |
| `Character/`（核心底座） | **Primary Module 1 (Character Core)**：`PmxRuntime`（解析與 LRU 快取）、`BoneContract`（骨骼映射）、`GameBones`（遊戲錨點解析、契約自檢與 pristine 基線）、`ModelMetrics`（12 段因子量測）、`BindMath`、`CharacterRig`（Unity 骨架宿主＋FK 重定向＋臂鏈 IK 面骨驅動）、**`Kinematics/ArmRest`（M1 rest 校準：`ArmRestChain`＝TwoBoneIkChain＋具名掌錨＋chest 節點＋收緊半徑）＋`Kinematics/TwoBoneIk`（`TwoBoneIkMath` 臂鏈兩骨 IK 純數學雙編譯權威，0.3.9）**、`BodyReplacer`（生命週期宿主＋0.3.10 狀態閘 alive∧armed＋`arm gate:` 行）、`Patches`（InitVisuals 掛鉤＋0.3.10 `HideoutPlayerOwner.SetPointOfView` postfix＝藏身處 FP 身體動畫機復活）、**`ModelScaler`（模型側全模型單一等比縮放：`ModelScaleMath` 純數學雙編譯權威＋Build 期 rig 根一次性 uniform localScale 套用器）**、**`SdfFaceShading`（SDF 臉部陰影＋寫實主體材質：包級 `sdf_face.png` 解碼、per-rig 臉材質實例與貼圖副本，共享材質零污染）** |
| `CapsulePatches.cs` | 膠囊高度補償（輸入＝縮放後模型身高） |
| `IkRefresh.cs` | EFT IK 解算器刷新（切角色／還原路徑） |
| `Character/Camera/FirstPersonSMRMask.cs` | **1D 唯一保留面：FP 頭部開關**——FP 頭部 ShadowsOnly、TP／死亡一律 On；由 `BodyReplacer` 直接驅動，零幾何寫入 |
| `Visibility/` | **Primary Module 2 (Visibility)**：`VanillaBodyHider`（原版素體隱藏）、`VisualShield`（0.25s 防閃白）、`VanillaSelection`、`EquipmentHide`（8 槽資料級隱藏）、`AccessoryFilter`（自帶配件過濾） |
| `MenuUI/` | **Primary Module 3 (MenuUI)**：`PreviewProxy`（主選單 3D 預覽，同樣走 ModelScaler；0.3.10 空手契約＝`WeaponType=-1`＋MenuPlayerPoser 停用＋`preview unarmed:` 行）、`CharacterSwitchUI`（角色切換列）、`TradingCardIcon`（角色卡圖示） |
| `Packager/` | PMX → `.gfc` 打包器與 `--verify` 矩陣相容閘門；`MeshSplitter.cs`（頭身網格分離） |
| `Tests/` | `GF2Verify` 離線驗證工具（net8.0，**15 支子命令**，無 Unity、無遊戲本體） |
| `ServerMod/` | 資訊性伺服器端 companion（net10.0，`GF2.Server.csproj`） |
| `tools/` | `check-log.sh`（日誌契約檢查）、`extract_bones.py`（資料提取）、`check_matrix.py`（相容矩陣驗證，恰 41 包）、`bake_sdf_face.py`（SDF 臉烘焙，重製後 staged pmx 上執行） |
| `analysis/*.json` | 凍結基準：`koleda_bones.json`、`eft_skeleton.json`、`fit_expectations.json`、`model_matrix.json`（41 包）、**`proportion_scale.json`（41 包 uniform k 表；armK 為歷史比例資料）** |

**已刪除模塊（2026-11-14）**：`SkeletonFit`（遊戲骨架 fit，全程退役）、`SpringBone`/`SpringMath`/`Dynamics/`（1C 次要動作）、`FirstPersonCamera`/`ArmsMesh`/`FpVisibility`/`FpBranch`/`FpRigDump`/`Camera/SightlineSolver`（1D 視角）、`PostureMath` 與 `Kinematics/RigAim` 之 IK 側（1B 姿態補償）、`WeaponAlignment`/`ArmIkObservation`/`Contract/FpSlots`（1E 握把真值與觀測）。

---

## 建置與驗證命令（Commands）

* **建置主模組**：
  ```bash
  dotnet build -c Release                       # 建置並自動部署至遊戲 plugins 目錄
  dotnet build -c Release -p:CopyOutputToBepInEx=false  # 純驗證建置，不拷貝 DLL
  ```
* **離線驗證 Harness（機器僅 .NET 10 SDK，需 Roll-Forward）**：
  ```bash
  DOTNET_ROLL_FORWARD=LatestMajor dotnet Tests/bin/Debug/net8.0/GF2Verify.dll [subcommand]
  ```
  * 子命令清單以 `Tests/Program.cs` 的 `Commands` 表為權威（13 核心 + 2 架構管線 = 15）。
  * `factors`、`proportions` 等需解析 `.gfc` 的子命令找不到角色包時，以 `GF2_PACKAGES_DIR` 環境變數指向 `.gfc` 所在資料夾。
  * 失敗 QA 旋鈕：`GF2_EXPECT_DELTA=1`（factors 釘點污染）、`GF2_SCALE_DELTA=1`（proportions 釘點污染）。
* **日誌契約檢查**：
  ```bash
  bash tools/check-log.sh tools/fixtures/good.log   # 預期 exit 0
  bash tools/check-log.sh tools/fixtures/bad.log    # 預期 exit 1
  bash tools/check-log.sh <遊戲目錄>/BepInEx/LogOutput.log
  ```
* **PMX 打包**：
  ```bash
  dotnet run --project Packager -- <pmx檔> [-o <輸出目錄>]   # 產出 .gfc；資料夾名＝包名
  ```
* **注意事項**：`Packager --verify <gfc目錄>` 會覆寫 `analysis/model_matrix.json`（重打包後必跑，再由 `python3 tools/check_matrix.py` 校驗**恰 41 包**與 `unitScale 0.078`）。上架流程＝stage 複製 → `bake_sdf_face.py`（原始 pmx 上）→ Packager；**warp 拉伸建模已退役，不得再執行**。

---

## 核心架構硬性不變量（Hard Invariants）

### 1. 骨架與零拉伸（Skeleton & Zero-Stretch，2026-11-14 改約）
* **結構零拉伸**：PMX 骨骼階層與樞紐位置在 `CharacterRig.Build` 時固定，每幀 `Drive` **僅寫 localRotation**——手腕對位（2026-11-14-7）已於 **2026-11-14-8 移除**（藏身處實測腕端位移 8–15cm 呈可見變形；骨 localPosition 幀寫歸零），手指/手堂零縮放零位移（原始尺寸），TP 手端位置由**臂鏈 IK（0.3.9）決定、死亡/退化回退 FK 複製**（模型臂短，握把為「自然持槍姿勢落點」非接觸保證）。
* **模型側全模型單一等比縮放（2026-11-14-6 改約）**：`ModelScaleMath.Solve` 只由腿鏈因子推**單一 uniform k**（k＝遊戲腿鏈長/模型腿鏈長，箝制帶 [0.70, 1.45]）；`ModelScaler.Apply` 在 Build 的樹建立後、一切驅動捕捉量（bindposes/配對方向/接地位）**之前**，於 **rig 根**一次性乘入 k（`localScale ×= k`）——**唯一**合法 localScale 寫入點、僅 Build 一次、逐位等比。全身（含手指）比例 100% 作者原始，僅腿跨距貼齊遊戲腿鏈錨定地面；臂鏈因子量測照常但不施加任何臂鏈縮放（臂閘退役，`proportion_scale.json` armK 保留為歷史資料）。
* **遊戲骨架全程 pristine**：`SkeletonFit` 已退役；任何模塊嚴禁寫入遊戲骨骼 localPosition/localScale；`GameBones.CapturePristineBaseline` 提供從未變形的量測基線。
* **骨盆 PureDelta 拷貝**（`BoneContract.PureDeltaPmxBones = { 下半身 }`）與**足踝足趾 PureRel 拷貝**（`PureRelPmxBones` 4 骨）契約不變。

### 2. 武器與動作回歸原生（Native Gameplay）
* 武器零縮放（嚴禁武器節點 localScale 寫入、嚴禁掉落物/FOV Z 軸干涉）；TP/FP/選單預覽單一幾何真值。
* 純 FK 重定向＋臂鏈 IK：模型骨骼追蹤 pristine 遊戲骨架旋轉；0.3.9 起上臂/前臂由兩骨 IK 求解伸展至遊戲手錨（握把對位回復）；手指與手堂維持作者原始尺寸（不重演 0.3.6 前的臂鏈等比放大）；**手腕對位已移除（2026-11-14-8）**，腕端無位置寫入（IK 亦為 rotation-only）。**0.3.10 狀態閘（0.3.12 判準改約）**：`ArmIkActive = alive ∧ armed`，armed 唯一判準＝**手上實體物品是否為槍械實體**（`HandsController?.Item is EFT.InventoryLogic.Weapon`；`Player.EmptyHandsController` 身分判定自 0.3.12 退役）——空手＝純空手垂手純鏡射（含殘留展示武器網格隱藏）＋臂面整面停用；持械＝兩骨 IK。綁定源頭由 `rig source:` 行隨 attach 驗證（TP 骨架子樹、非 FP Hands 骨架）。迎槍前傾／貼腮頭頸／肘內收／導軌滑移／距離連動觀測已全部隨 1B/1E 退役。

### 3. 第一人稱視角（FP，2026-11-14 縮約）
* 相機、FP 模型手臂、Sightline 共軸對焦已全部退役——FP 即遊戲原生視角（原生 viewmodel 完整保留）。
* **唯一保留**：`FirstPersonSMRMask` 頭部開關（配置鍵 `Hide head in first person`）——FP 頭部 `ShadowsOnly`（保留地面頭部影子），TP/死亡 `On`；由 `BodyReplacer` 以 POV 狀態變化驅動，零幾何寫入，狀態翻轉才寫渲染器並發 `fp head mask:` 行。

### 4. 生命週期、還原與快取（Lifecycle & Protection）
* **Pristine 基線原則**：`GameBones.CapturePristineBaseline` 快照原版骨架 localPosition；切換角色由此基線還原重算。
* **原版 (PMC) 還原路徑**：切回原版必須 `VanillaBodyHider.RestoreAllVanilla(player)` ＋ `IkRefresh.RefreshIK(player)`。
* **裝備隱藏**：8 個 Equipment 配置鍵資料級快照清空，預設全隱。
* **Harmony 容錯**：7 個 TryPatch 容器各自獨立容錯（FpRigDump 容器隨模塊退役）。
* **膠囊補償**：`CapsulePatches` 輸入取縮放後模型身高（`metrics.HeightM × k`，全模型 uniform 等比）。

### 6. Raid 建模停用（2026-11-14-2，Suspend Until Re-model）
* 0.3.3 起重製完成，2026-11-14-5 起建模套件移除（`GF2_Characters_removed/` 保存），`Replace player body` **預設 false**；復原＝回改名資料夾／重產／回退腳本；此前為預設回 true；off 時契約：**零玩家側寫入**——不建 rig、不隱原版、不寫膠囊、不刷新 IK、無頭部遮罩。全部 Harmony 掛鉤必須保持「off＝短路」 Gate（Patches.PreInitVisuals／PostInitVisuals／BodyReplacer.PostUpdatePlayerRenders／EquipmentHide.PreBodyInit 由 `Plugin.ReplaceBody` 首行短路；CapsulePatches 五個 postfix 由 scale 字典失效——**嚴禁在字典閘之前寫入任何遊戲狀態**）。
* 重新啟用條件＝套件以**貼合遊戲骨架比例的重新建模模型**打包；此條件由用戶 2026-11-14-6 決策改約後實質凍結——uniform 等比路線下 `scale:` 行的 raw k（腿錨定臂差）為正常比例資料，不再作為重製品質閘。
* 選單 3D 預覽（PreviewProxy／TradingCardIcon／CharacterSwitchUI）屬顯示面，不屬本停用範疇。

### 5. 日誌契約與雙編譯基建（Telemetry & Harness）
* **固定前綴（6 個）**：`contract:`、`rig: bones=`、`body replaced on '`、`preload:`、`cache: evicted`、`scale:`。
* **失敗標記（4 個）**：`contract skip:`、`implausible`、`missing`、`unresolved`。
* **行族登記（18 行族＋4 標記）**：任何新增 `[GF2]` 日誌行必須於 `Character/Contract/TelemetryRegistry.cs` 登記（`VerifyCompleteness` 斷言行族 ≥12、failure 恰 4、6 釘點逐字在列），並由 harness `telemetry` 子命令與 `check-log.sh` 同步（fixture 同步更新）。同步順序＝實作生產者 → 登記 → 補 harness 釘點／check-log fixture → README 疑難排解與本檔。
* **雙編譯純數學**：`ModelScaleMath`（`Character/ModelScaler.cs`）保持 System 純數學，以 `GF2_PMX_PARSER_ONLY` 雙編譯進 `Tests/` 與 `Packager/`；兩者以 `<Compile Include="../…">` Link 共享主模組 `.cs`；主 csproj 以資料夾排除＋萬用字元（刪檔免改主 csproj）。

---

## 單向執行管線速查（Single-Pass Pipeline）

```
Player.InitVisuals (Harmony Prefix/Postfix)
 ├─ Prefix:  VanillaBodyHider.PreHideVanilla（提早隱藏皮膚；VisualShield 0.25s 防閃白）
 └─ Postfix: BodyReplacer.Apply
      孤兒網格清理 → Root_Joint scale 歸一 → GameBones 契約自檢＋pristine 基線
      → PmxAssets 預載（冷/暖快取；AccessoryFilter 過濾自帶裝備）
      → ModelMetrics 量測（12 段因子）→ ModelScaleMath 求解腿錨 uniform k
      → CharacterRig.Build（零拉伸獨立 Rig 掛載＋rig 根一次性等比縮放＋臂鏈 IK capture＋`ik:` 行）
      → CapsulePatches（縮放後身高）＋ IkRefresh
      → VanillaBodyHider.SyncVanillaHidden（隱藏原版全身素體）

HideoutPlayerOwner.SetPointOfView (Harmony Postfix)     # 0.3.10 層 1-b
 └─ 被 GF2 替換的玩家：BodyAnimatorCommon.enabled 恢復 true
     （藏身處 FP 骨架持續動畫；未替換者零介入；單一生產者 `hideout body animator:` 行）

每幀驅動（BodyReplacer.LateUpdate 嚴格順序）：
  0. 狀態閘取值                  # povQuery(alive)＋IsArmed(HandsController 身分)；翻轉發 `arm gate:` 行
  1. _rig.Drive()                 # FK 骨骼重定向＋臂鏈兩骨 IK（上臂/前臂；ArmIkActive=alive∧armed，
                                  #   空手/死亡回退 FK 複製＝純鏡射；PureDelta 骨盆/PureRel 足踝）→ 付與 → 接地
  2. 頭部遮罩 POV 切換            # FirstPersonSMRMask（狀態變化才寫；死亡優先）
  3. _vanillaHider.EnforceHiddenEveryFrame()  # 原版身體與裝備渲染器持續強制隱藏
```

---

## 開發規範（Conventions）

* **文件與註解**：繁體中文為主，夾帶標準英文專業術語。
* **Git 提交風格**：Conventional Commits 規範（`feat(rig):`, `fix(fp):`, `chore:`, `docs:`）。
* **詳細技術文件**：[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)（架構論證；2026-11-14 重構後以本檔為準）、[`docs/DATA.md`](docs/DATA.md)（凍結基準來源；`analysis/proportion_scale.json` 重打包後由 `analysis/model_matrix.json` 因子經 ModelScaleMath 公式推得）。
