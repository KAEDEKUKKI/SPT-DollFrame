# DollFrame 變更紀錄（Changelog）

## 0.4.1（2026-11-18 補強：角色建模質感、微表面法線與光影校準）

* **材質與高光去塑料化（`PmxRuntime.cs`、`ShaderManager.cs`）**：
  * 校準 `ClassifyMaterial` 與 `ClassifyStylizedLook`：降低臉部（`0.15`）、皮膚（`0.18`）與霧面布料（`0.14`）之 `Smoothness` 與白色 `SheenIntensity`，改用暖色收斂的皮膚邊緣光（`0.26`／`4.0`），並提高 `_RimAmbientAdapt` 至 `0.90`。
  * 新增伴隨法線貼圖自動偵測（`_n`／`_normal`／`_bump`／`_nrm`，Linear 空間）與程序化微表面法線合成（`GenerateMicroNormalMap`，依布料編織／皮革顆粒／髮絲／金屬拉絲生成輕量 Tangent-Space `_BumpMap` 并納入 `PmxAssets.Textures` LRU 生命週期；臉部／眼睛／睫毛保持平滑）。
* **雙面網格法線與蒙皮／貼圖過濾（`PmxRuntime.cs`、`CharacterRig.cs`）**：
  * `BuildUnityMesh` 針對 `DoubleSided` 子網格拆出背面頂點並翻轉法線（`-normal`），修正頭髮、裙襬、緞帶正反面受光錯亂。
  * `LoadTexture`／`Downscale` 啟用 `FilterMode.Trilinear`、`anisoLevel = 8` 與漸進 2x 降採樣（`Apply(true, true)` 補齊 Mipmap）。
  * `CharacterRig` 強制 `Body_SMR` 與 `Head_SMR` 使用 `SkinQuality.Bone4`。
* **臉部 SDF Mipmap 與環境光耦合（`SdfFaceShading.cs`）**：
  * `BuildCloneTexture` 啟用 Mipmap、`Trilinear` 與 `anisoLevel = 4`；`SdfFaceDriver.Update` 加入光向 `Slerp` 平滑與環境亮度自適應陰影深度（`0.14–0.28`），減少暗處雙重壓黑與光向跳動。
* **驗證**：`dotnet build -c Release -p:CopyOutputToBepInEx=false` 0 警告 0 錯誤。

## 0.4.1（2026-11-16：全模組優化、裝備/武器隱藏修復、IK/語音優化與三語系文件）

* **全模組生命週期、記憶體洩漏與邊界防護**：
  * `Plugin.cs` 新增 `IdleCacheSize` / `ReplaceBody` 即時變動同步；`CapsulePatches` 新增已銷毀鍵清理（`PruneDestroyed`）與停用守衛；`IkRefresh` 補上 null 與啟用檢查；`BodyReplacer` 強化 `Reapply` 失敗還原與資源釋放；`VoiceReplacer` 修正切換家族時的 `AudioClip` Use-After-Free 與 WAV 緩衝安全；`PmxRuntime` / `SdfFaceShading` 強化 PMX 負數/截斷防護與 `finally` 暫存貼圖回收；`MenuUI` 優化 `CharacterSwitchUI` 版面重算、`PreviewProxy` VisualShield 隔離及 `TradingCardIcon` 快取鍵與資源釋放。
* **裝備與武器隱藏修復**：
  * `EquipmentSlotHider.CollectSlotRenderers` 擴大收集 `SlotView.Model`、`Renderers`、`MainDress` 與 `Dresses`（修復頭盔、耳機、面罩未隱藏問題）。
  * `BodyReplacer.LateUpdate` 偵測手中物品切換當幀即時呼叫 `Sync`，並於 `CollectSlotRenderers` 跳過 `Weapon_root*` 下方模型，修復切換武器時短暫隱藏問題。
* **IK 與空手姿態優化**：
  * 腕部握點收斂放寬至 `errLen < 0.35f`、下限 `0.00005f`（`< 0.05 cm` 精度），並為持槍手指加入漸進式吸附收攏（改善指尖微翹）。
  * 空手垂臂伸展比調整為 `0.86f`、增加 `7°` 腕部自然內屈與五指漸進微彎，改善生硬感。
* **語音音量優化**：
  * `VoiceReplacer.LoadWavBytes` 加入 RMS 響度標準化（目標 RMS `0.24`、上限 `6x`）與軟削波限幅（`0.80` knee / `0.98` 上限），解決語音偏小聲問題。
* **設定精簡與三語系 README**：
  * 移除 `Switch character key`、`Flip UV`、`Scale model to game proportions`、`Hide head in first person`、`IK arm retarget to game hands` 設定項並固定為預設行為；重寫英文／繁體中文／日文三語系 README。
* **版本**：client `0.4.0` → `0.4.1`；`ServerMod` 同步 `0.4.1`。

## 0.4.0（2026-11-16：藏身處穿模掉進虛空修復）

* **藏身處穿地板掉進虛空修復**：
  * `CapsulePatches.ApplyScale` / `ApplyDerived` 對 `HideoutPlayer` 直接返回，保留藏身處原版物理膠囊（視覺模型與腳底接地仍由 `CharacterRig` / `GroundOffsetM` 正常處理），避免藏身處傳送時序導致膠囊穿模掉進虛空。
  * 新增 `ScaledYWithSkin` 保留 `UnityEngine.CharacterController.skinWidth` 偏移，修正 `PlayerCharacterController` 路徑縮放後膠囊底面下沉問題。
  * 移除 `BodyReplacer.Reapply` 針對 `HideoutPlayer` 的 `+0.05f` 臨時上抬補償。
* **版本**：client `0.3.18` → `0.4.0`；`ServerMod` 同步 `0.4.0`。

## 角色包製作鏈移除（2026-11-15：回歸純執行期框架；版本維持 0.3.18）

* **移除範圍**：`Packager/` 整個專案（`DollFramePackager` console：PMX→`.gfc` 打包器＋`Program.cs`＋`Stubs.cs`＋`--verify`）與 `tools/` 製作面腳本（`bake_sdf_face.py`、`check_matrix.py`、`extract_bones.py`、`remodel_pmx.py`（已退役）、`rollback_packages.sh`）。本 repo 回歸「執行期＋離線驗證」：41 包 `.gfc`／20 位 `.gf2v` 為完成品部署資產，重打包與語音歸檔產製皆不在本 repo 範疇（語音 `.gf2v` 製作鏈本就在 repo 外）。
* **雙編譯共享面重接（零行為變動）**：`Packager/MeshSplitter.cs` → `Character/MeshSplitter.cs`（`PmxRuntime` 載入期拆頭/身網格的執行期依賴；main csproj 原以顯式 Include 掛回，遷移後改由資料夾萬用字元納入）；`Packager/Stubs.cs` → `Tests/Stubs.cs`（harness `DOLLFRAME_PMX_PARSER_ONLY` 的最小 UnityEngine 替代面）；`DollFrame.csproj` 移除 `Packager/**` Remove 與 MeshSplitter Include 條目；`Tests/DollFrame.Verify.csproj` 改引 `../Character/MeshSplitter.cs` 與本目錄 `Stubs.cs`。
* **唯一執行期異動（僅文案，零行為）**：① `character package missing` 警示尾部由「run DollFramePackager to build .gfc files」改為「.gfc packages are prebuilt artifacts placed in this folder」——不再指引已移除的工具；`missing` 失敗標記語意與 `tools/fixtures/bad.log` fixture 相容不變。② `Replace player body` 的 BepInEx 配置描述重寫：移除 `tools/remodel_pmx.py` 引用與過時的「DEFAULT ON / 19 packages REMODELED」敘事，改述現行契約（default OFF、預覽不受影響、off 時零玩家寫入）。③ `SdfFaceShading.cs` 檔頭註解的 `tools/bake_sdf_face.py` 引用改述為「sdf_face.png 烘焙端（repo 外製作管線）」。
* **驗證面**：`dotnet build -c Release -p:CopyOutputToBepInEx=false` 0 警告 0 錯誤；harness `bindpose`／`split`／`telemetry` 綠（telemetry 確認 18 行族＋4 失敗標記＝22 entries、6 釘點前綴、15 子命令不變）；`check-log.sh` fixtures good=exit 0／bad=exit 1。

## DollFrame 品牌轉換（2026-11-15：GF2 → DollFrame；版本維持 0.3.18）

自 `kukki.GF2` 框架移植並轉換為 **DollFrame**（模組識別無 `kukki.` 前綴）：

* **模組識別**：Plugin GUID 與 Harmony id 均為 `DollFrame`；根命名空間 `GF2`→`DollFrame`（含 `DollFrame.Visibility`／`DollFrame.Tests`／`DollFrame.Server` 子命名空間）。
* **日誌契約**：行族前綴 `[GF2]`→`[DollFrame]`（18 行族＋4 失敗標記語意不變；`tools/check-log.sh` 與 fixtures 同步；harness `telemetry` 22 entries 驗證通過）。
* **資產命名**：rig/預覽/卡片等 GameObject 與網格前綴 `GF2_`→`DollFrame_`；素材資料夾 `GF2_Characters`→`DollFrame_Characters`、`GF2_Voices`→`DollFrame_Voices`；著色器套束檔 `gf2_shaders.bundle`→`dollframe_shaders.bundle`（套束**內部** Shader 資產名 `GF2CharacterPBR*`/`GF2/CharacterPBR*` 與 `_GF2Rim*` 全域 Uniform 為已烘焙套束的逐位真值，維持原名並以新名位優先、舊名 fallback 相容）。
* **部屬面**：`BepInEx/plugins/DollFrame/`（`DollFrame.dll`＋`dollframe_shaders.bundle`＋`DollFrame_Characters/` 41 包 `.gfc`＋`DollFrame_Voices/` 20 位 `.gf2v` 語音）；伺服端 companion 佈署 `user/mods/DollFrame/`（`DollFrame.Server.dll`）。舊 `kukki.GF2` 客戶端資料夾未動，可自行移除。
* **驗證面**：`Tests/DollFrame.Verify.csproj`（`DollFrameVerify`，15 子命令）全綠（41/41 包 factors/proportions/pairing/grants）；env 旋鈕改約 `DOLLFRAME_PACKAGES_DIR`／`DOLLFRAME_PACKAGES`／`DOLLFRAME_SCALE_DELTA`／`DOLLFRAME_EXPECT_DELTA`（雙編譯 define `DOLLFRAME_PMX_PARSER_ONLY`）。

## 0.3.13（2026-11-14-13：空手放鬆驅動＋onBeforeRender 時序對齊＋Ribcage 縮放補償）

* **完整架構根因定位（反編譯 `Assembly-CSharp.dll` ＋ `DuckCam.dll` ＋ 實機日誌交叉證明）**：
  1. **EFT `Player.VisualPass()` 每幀強制架槍與扣扳機**：`Player.LateUpdate()` 尾段呼叫 `VisualPass()`，不論是否空手（`EmptyHandsController` 其 prefab `empty_hands_container` 仍帶胸前 `weapon_L/R_IK_marker` 與握槍手掌），每幀必跑 `_limbs[0..1].solver.Update()`（`LimbIK` 將 TP 骨架雙掌拉至胸前）與 `HandPosers[0..1].ManualUpdate()`（`HandPoser.SimpleGrip()` 逐幀覆寫 TP 骨架全部 30 根指骨 `localRotation` 為「右手食指扣扳機＋左手托護木」），且 `Player_anim_controller` 第 0/1 層無空手垂手動畫。因此 0.3.10–0.3.12 於 `!armed` 回退純 FK 鏡射時，實際上是忠實複製了 `VisualPass()` 的架槍手臂與扣扳機十指。
  2. **為何原版 PMC 在 `DuckCam` 垂手擺臂而 DollFrame 凍結（`Application.onBeforeRender` 時序斷層）**：`DuckCam.RelaxedArms.Apply`（及 `PoseHold`）註冊於 `Application.onBeforeRender`，執行時機在所有 `LateUpdate()` **之後**、渲染之前；`BodyReplacer` 僅在 `LateUpdate()` 執行 `Drive()`，導致原版 PMC 渲染 `onBeforeRender` 垂手姿態，而 DollFrame 渲染 `LateUpdate` 的 `VisualPass()` 架槍姿態。
  3. **持械態第一人稱／藏身處 FreeCamera 伸距萎縮（`0.40m → 0.26m`）**：EFT 在 `PointOfView != ThirdPerson`（含 `FirstPerson` 與藏身處 `FreeCamera`）將 `Ribcage.localScale` 縮為 `0.65`（`_ribcageScaleCompensated`），導致世界座標量測的 `|gamePalm − gameUpper|` 被乘上 `0.65`。
  4. **`rig source:` 誤報與 `Weapon_root` 同名克隆骨防護**：`SkeletonHands`（`Weapon_root_anim`）掛在 `Ribcage`（`Root_Joint` 子樹）下，故 `handsRoot.IsChildOf(rootJoint)` 恆為 `True`。
* **架構修正（零遊戲骨骼寫入、純 `localRotation` 幀寫不變量 100% 維持）**：
  * **空手自然垂手與張開手掌（`TwoBoneIkMath.SolveRelaxed` ＋ `UnarmedRelaxActive`）**：於 `TwoBoneIkMath` 新增 `SolveRelaxed`（由 `ArmRestMath.TryCalibrate` 以 `rigRoot.TransformDirection` 標定胸腔局部空間垂手軸 `RelaxUpperLocalChest`／`RelaxForeLocalChest`，同時修正 `PoleLocalChest` 的 `rigRoot` 世界轉換）；當 `alive && !armed`（及主選單預覽）時，上臂/前臂隨本幀胸腔（`上半身2`）呼吸/擺動/轉向協變垂手並經 3 次胸腔 SDF 防穿模，肩（`左/右肩`）、手腕（`左/右手首`）與 30 根指骨回 PMX 作者原始放鬆姿態（`Quaternion.identity`），徹底隔離 `VisualPass` 的 `SetShoulders`、`LimbIK` 與 `HandPoser`。
  * **持械 `Ribcage` 縮放補償**：`CharacterRig.EvaluateBonePlan` 於 `ArmIkActive` 時以 `en.GameBone.lossyScale.x / _rootJoint.lossyScale.x` 還原未縮放肩掌距離，使 FP／FreeCamera 與 TP 伸距一致（`~0.40m`）。
  * **`Application.onBeforeRender` 末位同步**：`BodyReplacer.LateUpdate` 尾段重排 `Application.onBeforeRender` 委派並於 `OnBeforeRender` 再次 `Drive()`（`OnDisable`/`OnDestroy` 解綁），確保外部相機/姿勢插件於渲染前的骨架修改即時同步。
  * **綁定源頭與克隆骨隔離**：`EmitRigSourceLine` 改判 `rootJoint.IsChildOf(handsRoot)`；`GameBones.IsUnderWeaponSubtree` 排除 `Weapon_root*`／`WeaponPrefab` 子樹下的同名 `Base Human*` 骨。
* 版本 0.3.12 → 0.3.13（client；`ServerMod` 未動）。

## 0.3.12（2026-11-14-12：Armed 判準改約＋綁定源頭執行期驗證）

* **實測反證（用戶 FreeCam 截圖對比）**：原版 PMC 垂手擺臂對照 DollFrame 模型仍「右手食指扣扳機＋左手向前托護木」**連手指都未變**——指骨走純 FK delta 鏡射、源動則模型必動 ⇒ 鏡射源骨架的指骨同處持槍靜態（狀態閘未生效或綁錯骨架源頭，兩擇一）。
* **Armed 判準改約（用戶指令）**：退役 `HandsController is Player.EmptyHandsController` 身分判定，改唯一判準 `HandsController?.Item is EFT.InventoryLogic.Weapon`——反編譯核實 `Weapon : CompoundItem` 為 runtime 項類基類、`PistolItemClass : Weapon` 全槍械子類命中；Knife/grenade/道具→空手態純鏡射；異常時回 false（鏡射安全方向）。`arm gate:` 行加 `item=<ItemClass>` token。
* **綁定源頭驗證面**：`BodyReplacer.EmitRigSourceLine`（attach 一次性，行族 `rig source:`）——`root@id/parent`＋`underTPBody`（Root_Joint 是否在 `PlayerBody` GO 子樹）＋`fpHandsInside`（是否圈住 `PlayerBody.SkeletonHands` 第一人稱手部骨架子樹；反編譯核實 PlayerBody 兼持 `SkeletonRootJoint`(TP, :492) 與 `SkeletonHands`(FP, :495) 兩套骨架）。綁定錯源在日誌立判。
* **判定收斂規則**：日誌 `armed=False ik=False underTPBody=True fpHandsInside=False` 而模型仍凍結 ⇒ 凍結源在遊戲端姿勢產出層（IK 組件/動畫機），依 UnityExplorer 實測值精準治理；`armed=True` ⇒ 帶回 `item=` 型別名對症。
* **藏身處維持零 guess patch**（承接 0.3.11 決策）；行族 17→18（`rig source:`）。
* 版本 0.3.11 → 0.3.12（client；`ServerMod` 未動）。

## 0.3.11（2026-11-14-11：兩態收斂——展示武器網格隱藏；藏身處轉 UnityExplorer 實測）

* **0.3.10 實測回報**：選單手臂垂下成功；但**展示武器網格仍插在胸口**（`PlayerModelLoader` 父掛 `PBones.Ribcage.Original` 的 `WeaponPrefab` 為剛性 `MeshRenderer`，不蒙皮骨架，`HideVanillaSkins` 的 IsSkinnedTo 過濾天然漏掉）；藏身處 FreeCam 仍無變化。
* **展示武器網格隱藏**：`PreviewProxyDriver.HideShowcaseWeapon`——沿 `_skeleton` 子樹掃 `WeaponPrefab` 元件（契約錨：loader 產出物、僅選單預覽身體子樹內存在）把其下全部渲染器收進既有 `_hidden` 面——逐幀強制、vanilla 還原自動覆蓋，**零新狀態零新行族**；`preview unarmed:` 行加 `weaponMesh=<n>` token；0.5s 重掃描補 loader 刷新晚到武器。
* **架構簡化原則（用戶決策）**：统一收斂為「只認手上是否有武器實體」兩態——無武器＝純空手垂手（含殘留展示武器隱藏），有武器＝`TwoBoneIk`；不再遷就遊戲原版選單/藏身處特殊姿勢源差異。
* **藏身處停止盲猜**：本版**零新增**藏身處 patch；`PostHideoutSetPointOfView` postfix 維持現狀；待用戶 UnityExplorer 實測回報真正凍結手臂的組件名後精準修改。
* 版本 0.3.10 → 0.3.11（client；`ServerMod` 未動）。

## 0.3.10（2026-11-14-10：狀態感知手臂治理——空手凍結根治）

* **根因取證（反編譯 itinybad/SPT-Client-400@54e455a9）**：選單/藏身處「手臂卡死架槍」根因在**遊戲端姿勢源**，與臂鏈 IK 數學無關——①主選單 `PlayerModelLoader` 依「展示武器」寫 `WeaponType` 並由 `MenuPlayerPoser.LateUpdate` 每幀手動 LimbIK 指向武器 markers，DollFrame 藏走原版身體後構圖只剩「孤兒架槍姿勢」被 rig 忠實鏡射；②藏身處 FP 時 `HideoutPlayerOwner.SetPointOfView` 直接把 `BodyAnimatorCommon.enabled=false`（原版玩家 FP 看不到自己的 TP 身體故無人察覺），FreeCam 仍讀凍結骨架；③FP 手正常＝`EmptyHandsController` 自帶 prefab 動畫機（與 TP body animator 分離）。
* **層 1-a 選單空手契約**：`PreviewProxyDriver.ApplyUnarmedContract` 於 attach 寫 `WeaponType=-1`（原版無武器編碼；`EFT.PlayerAnimator.WEAPON_TYPE`／`WEAPON_TYPE_FLOAT`／`UI_WEAPON_TYPE` 三參數，偏離才寫、逐幀強制）＋**停用 MenuPlayerPoser**（其 LateUpdate 為手動 LimbIK/HandPoser 唯一生產點；本體動畫機由引擎推進不受影響；代價＝失去選單隨機查膛 idle beats）；掛載前原值存檔、`Teardown`/`OnDestroy` 反轉，保 `Vanilla (PMC)` 預覽原構圖；`PreviewProxyDriver` 補 `[DefaultExecutionOrder(32100)]`、每幀 `ArmIkActive=false`。
* **層 1-b 藏身處復活**：`Patches.PostHideoutSetPointOfView`（postfix `HideoutPlayerOwner.SetPointOfView`）——被替換玩家恢復 `BodyAnimatorCommon.enabled=true`，未替換者零介入。
* **層 3 狀態閘**：`BodyReplacer.IsArmed`——`player.HandsController is Player.EmptyHandsController`＝空手（Fika/SIT/SPT-VR 同款 idiom）；`ArmIkActive = alive ∧ armed`——空手臂鏈面整面停用、rig 純鏡射原版動畫機垂手/擺臂/待機，持械＝0.3.9 兩骨 IK 照舊；翻轉行 `arm gate: alive=… armed=… ik=… hands=…`（狀態變化才發）。
* **telemetry**：行族 14→17（`arm gate:`／`preview unarmed:`／`hideout body animator:`，皆單一生產者、零新配置鍵）；主 csproj 補引用 `UnityEngine.AnimationModule.dll`／`AnimationSystem.Types.dll`（`IAnimator` 所在）。
* 版本 0.3.9 → 0.3.10（client；`ServerMod` 版本隨自身 csproj，本版未動）。

## 0.3.9（2026-11-14-9：臂鏈 IK 重定向——混合 FK＋臂鏈 IK，握把對位回復）

* **TwoBoneIKConstraintJob 對齊重寫（0.3.9 內部迭代，版本不動）**：臂鏈兩骨 IK 依 **4 模塊分解**重版——**M1 `Kinematics/ArmRest.cs`**（rest 校準：`ArmRestChain` 收編 TwoBoneIkChain＋具名掌錨＋chest 節點＋收緊半徑）、**M2 動態目標**（raid/預覽統一具名掌錨解析：prefer 精確名 `Base HumanLPalm/RPalm` 於活體骨架錨下，缺漏回退契約錨並記 source=pb*）、**M3 `Kinematics/TwoBoneIk.cs`**（`TwoBoneIkMath` 取代退役 `ArmIk.cs`/`ArmIkMath`；pole 改 **chest 局部空間**——胸腔彎/傾時彎曲平面跟隨胸腔；胸腔 SDF 半徑收緊＝clamp(0.6×|chest→上臂 attach 距|＋0.02, 0.09, 0.18)，缺量測回退 0.13）、**M4 手腕/十指 delta 映射**（腕 cross-axis 特殊通道退役、併回單一 baked/live delta 映射面）；求解公式（目標伸出/reach 箝制/扭轉重分配/pole fallback/SDF 擠出）與 8 支 harness 情境逐項保持原 0.3.9 已驗數學逐位等效。
* **零位置寫入不變量不變**：IK 只產 world 旋轉（`Drive` 經 `LocalFromWorld` 寫 localRotation，零 localPosition/localScale 幀寫）；手腕/十指維持 palmL/R 原複製，其它骨照常 FK/live。**掌扭量 50% 落前臂**（`TwoBoneIkMath.TwistAngle` 量遊戲掌相對 attach 掌繞臂軸扭量、w&lt;0 先翻轉、(−π,π] 纏回；ForeWorld 以扭量繞臂軸 roll，腕端由 LocalFromWorld 逐幀補償父層 roll，手部姿態不變、前臂網格承載共享扭量）。胸腔 SDF：圓球心＝上半身2 rig 節點，半徑＝clamp(0.6×|chest→上臂 attach 距|＋0.02, 0.09, 0.18)（缺量測 0.13；重寫收緊，肘/掌擠出帶變窄、軀幹外緣更貼）；肘/掌入球沿球面擠出（≤3 迭代，|E−S|＝L1、|H−E|＝L2 恆等）。肘 pole＝**上半身2 chest 局部空間**（Build 自 rig 根空間規約「下 −Y×1.0／外 ×0.6／後 −Z×0.4」經 chest attach 世界旋轉反變換捕捉；每幀由 `TwoBoneIkMath.Solve` 經 chest 世界旋轉升回世界——胸腔彎曲/前傾時彎曲平面跟隨胸腔）；pole 與臂軸夾角 sin<0.12（近平行）時 `RigAim.Perpendicular` 決定性 fallback——防近退化 1/|cross| 方向放大造成彎曲平面逐幀翻面（直立靜止旋轉逐位恆定，harness 60 幀逐位釘＋rest 軸偏移 identity 釘）。**父鏈錨定（旋轉協變）**：swing/rest 軸與扭量基準錨定本幀父鏈世界像（`parentWorld ∘ localChildAxis`、扭量經 `upperWorld ∘ InitRotUpper⁻¹ ∘ ChainGamePalm0` 共軛式）——轉動人物（父鏈轉 R）整臂剛性跟轉、相對軀幹零自旋（harness 旋轉協變釘 57° yaw 對照）。
* **生存關與配置**：`BodyReplacer.LateUpdate` 逐幀寫 `ArmIkActive`＝alive（死亡/布娃娃回退 FK 複製；預覽路徑恆 true）；cfg `Visual/IK arm retarget to game hands`（24→25 鍵，預設 true，Build 時讀取、變更需重新掛載）off 時面不掛。IK 面骨不入 parity 採樣與 drive fallback 計數（目標非 FK 配對真值，註解標明）。
* **telemetry/harness**：`ik:` 行族雙形（行族維持 14；attach 一次性行 `CharacterRig.SetupArmRest`：`cfg=on L=ok palm=BaseHumanLPalm ratio=0.87 len=0.48m R=ok palm=BaseHumanRPalm …`／`skip:<reason>`／`cfg=off`；**新增節流行** `CharacterRig.EmitIkTargetLine`：`[DollFrame] ik target: L dir=(0.05,-0.25,0.97) reach=0.31m …`——≥0.25s（unscaled）節流、單一生產者、未驅動側省略 token；fixture 同步）。harness 維持 15 子命令——`ik` 對齊重寫（8 場景：rest-identity／dynamic-track／pole chest-follow／rotation-covariance 57°＋60 幀站立恆定／近平行 pole 箝制／退化回退／finger-delta（FrameMap 語義十指映射）／zero-stretch SDF 恆等／GameArmChainM 對 `analysis/eft_skeleton.json` 凍結 dump 釘點）。
* **三項用戶驗收準則**（本版設計依此釘點）：① 手端對位——TP 手沿遊戲肩→掌方向自然伸展至持槍位置（握把對位可視驗收）；② 手指/手堂零縮放零位移——100% 作者原始，不重演 0.3.6 前臂鏈放大與 0.3.7 腕端位移（rotation-only 不變量，harness `ik`/`zerostretch` 釘死）；③ 腕/手姿態逐幀複製不變——掌扭量僅落前臂網格（50% 重分配），手局部旋轉維持 palmL/R 原複製。
* 版本 0.3.8 → 0.3.9（client；`ServerMod` 版本隨自身 csproj，本版未動）。

## 0.3.8（2026-11-14-8：手腕對位移除——藏身處實測變形收斂）

* **診斷**：藏身處/預覽實測腕端位移 8–15cm 呈可見「手掌變形位移」——0.3.7 的「≈3cm 正常帶」宣稱低估（程式註解自證「正常貼合差 ≈0.08m」，`CharacterRig` 註解與文檔差近 3 倍）；且臂 idle 不跟屬**遊戲端源頭靜態**（預覽/藏身處 idle 臂骨近乎不動，模型忠實鏡射；驅動鏈完整：旋轉逐幀複製＋掌錨位置逐幀貼合雙通道，`contract: 21/21 anchors, digits 30` 全解析）。
* **手腕對位移除**：依用戶決策移除 hand-align——`DriveEntry.HandAlign` 旗標、`HandAlignMaxM=0.15m` 常數與 `Drive` 幀內 localPosition 寫入區塊全數退役（`CharacterRig.cs` 四處），零拉伸回復「每幀僅寫 localRotation」純本地旋轉契約；腕旋轉仍逐幀複製 palmL/R。
* **取捨回歸**：TP 手端位置由 FK 臂旋轉鏈唯一決定，握把對位失效（回 0.3.6 後期狀態——模型臂短，TP 手搆不到握把）；臂 idle 靜態屬遊戲源頭，模型端自主微擺/morph 動源為未決策項（未實作）。
* 版本 0.3.7 → 0.3.8（client；`ServerMod` 版本隨自身 csproj，本版未動）。

## 0.3.7（2026-11-14-7：手腕對位——握把對位恢復）

* **診斷**：uniform 改約後用戶實測「手會集中在胸前」；pose-parity 遙測實證旋轉鏈 a0 b0 全對位（collar/upperarm/forearm/palm/十指零誤差），缺的是**臂鏈長度**——原比例臂 0.603m 對遊戲臂 0.681m（差 7.8cm），純旋轉驅動物理上無法閉合。
* **手腕對位**：`CharacterRig.Drive` 每幀於旋轉寫入後對 `左手首`/`右手首` 節點寫 `localPosition` 貼上遊戲掌錨（`BoneContract` 左手首/右手首→palmL/R 本就指 PMC 掌錨＝持槍接觸點），位移 `HandAlignMaxM=0.15m` 箝制（防布娃娃撕裂）——手端落在 PMC 手持槍的真實位置，握把對位恢復；手指/手堂維持作者原始尺寸（零縮放零位移），僅前臂段視覺拉伸承載臂長差（≈3cm 正常帶）。
* **不變量改約**：零拉伸條款新增唯一核准的骨 localPosition 幀寫點（手腕對位）；`zerostretch` sim 為自建 3 骨模擬不受影響，harness 六面全綠（proportions 41/41、telemetry、constants 56、factors 12/12、profile、zerostretch）。
* **版本字面值同步修正**：`Plugin.cs` 的 `BepInPlugin` 版本屬性漏同步停在 0.3.5（BepInEx 控制台/日誌/cfg 標頭的顯示來源，字面值常數不可由 csproj 推導）；`ServerMod` 的 `IModMetadata.Version` 字面值停滯 0.3.4——改為由 assembly 版本（csproj `<Version>`）`ToString(3)` 推導，伺服器 console 版本自此單一真值隨 csproj。
* 版本 0.3.6 → 0.3.7（client＋server）。

## 0.3.6（2026-11-14-6：全模型單一等比改約——手指/手臂拉長根治）

* **根因**：原始比例包的執行期四鏈根貼合對**左肩/右肩 pivot 寫臂鏈 k（live log 實測 1.2888）**——整個手臂子樹含手掌手指被相對軀幹放大約 23%，即「手指與手臂被拉長」之根因。
* **改約（用戶決策：按照原比例正常縮放不變形）**：`ModelScaleMath.Solve` 只由腿鏈因子推**單一 uniform k**（腿錨定，k＝遊戲腿鏈長/模型腿鏈長，箝制帶 [0.70, 1.45]）；`ModelScaler.Apply` 改寫 **rig 根** localScale（`unitScale×k`，一次、逐位等比、唯一合法寫入點）——腿跨距貼齊遊戲腿鏈（接地/腳軌跡/膠囊錨定零回歸），全身（含手指）100% 作者原始比例。
* **臂鏈機制整組退役**：`ModelArmSpanM`/臂鏈遊戲常數/KArm 帶/`RawArm` 閘告警（BodyReplacer）與 `proportions` 的 armK 閘斷言刪除；armK 保留於 `proportion_scale.json` 作歷史比例資料（41 條值不動，note 改約）。
* **膠囊補償公式**：`scaledHeightM` 由 `HeightM + 腿跨×(k_leg−1)` 改為精確 `HeightM × k`（全模型 uniform）。
* **契約同步**：`scale:` 行面改 `k=… root=1`（`check-log.sh` 前綴釘點不變，fixture 同步）；`TelemetryRegistry` producer 描述、`Plugin.cs` 配置描述、harness `proportions` 斷言重列（legK 逐包重現＋raw 带＋克萊妲原皮釘點）。
* **已知取捨**：模型手臂自然短於遊戲手臂（armK/legK ≈ 0.83–0.95），TP 握把對位不再由縮放保證（FP 原生 viewmodel 不受影響）。
* 版本 0.3.5 → 0.3.6（client＋server）。

## 0.3.5（2026-11-13：SDF 臉部陰影＋寫實渲染＋41 包上架）

* **SDF 臉部陰影**：`tools/bake_sdf_face.py` 對 Face 材質區域烘焙角度域 SDF（逐紋素終止線方位角 θ* 場，R=(ψ*+π)/2π×255 編碼＋coverage 通道，1024 解析）；`sdf_face.png` 以額外紋理條目入包（GFCP 讀側 count-driven，格式版本不動）。執行期 `Character/SdfFaceShading.cs`：臉材質 per-rig 實例＋漫貼副本（≤1024、無 mip），`BodyReplacer.LateUpdate`／`PreviewProxy` 以頭骨基座×光向量量 ψ，角差逾 2°（最低 1/12s）才對 coverage bbox 重合成（LUT）。共享材質零污染，隨 rig 拆除銷毀。
* **寫實風格主體**：材質建立改中性 tint（RGB 歸一、保留 authored alpha）＋類別化 smoothness（skin 0.10／cloth 0.12／hair 0.20／default 0.08）＋金屬零；動畫風格僅保留於 SDF 臉面。
* **等比回歸＋41 包重上架（原始幾何/UV 零觸碰）**：拉伸建模（0.3.2 `warp` 靜態網格形變／手堂徑向縮放）依用戶決策**全面退役**——拉伸幾何會拉花原始 UV 貼圖。41 檔（19 包家族＋休息室/衣裝變體＋海倫×2 新上架＋萊娜＋黛煙）以**原始 PMX 逐位元組**重新打包（抽樣 MD5：包內 PMX＝來源檔），比例貼合回執行期 `ModelScaler` 四鏈根等比縮放；`model_matrix.json`/`proportion_scale.json` 重凍（41 條原始比例 k，arm k 1.015–1.412 全數入閘），`fit_expectations.json` 重凍回原始克萊妲 12 群因子，`check_matrix.py` 閘 19→41。
* **remodel_pmx.py 維護性修復（退役前留痕）**：死碼 warp 重複定義刪除、pivot 字典初始化補上、DBG 殘留清除——identity-test 逐位元組恢復（該工具不再是上架管線成員）。
* **維持淘汰**：伊格蕾塔／佩莉／索米／芙洛倫／芙鈴／維爾德（原始 arm k 1.52–1.66）；「重新分析」確認海倫（kArm 1.236–1.254）過關、維爾德（1.562）未過、閃電（比例過關惟新版 dump 骨旗標 0x0200 解析器未支援）與玉玲瓏（缺 `左/右足首先` 契約骨，つま先命名）兩檔暫緩。
* 版本 0.3.4 → 0.3.5（client＋server）。

## 0.3.3（2026-11-14-4：選單恢復）

* **修復**：0.3.1 的 `Replace player body=false` 預設把 MenuUI 三件（Overall 選單列／主選單 3D 預覽／角色卡）整組閘死（選單未刪、被閘）——0.3.2 重製完成後條款已滿足，**預設翻回 true**＋現場 cfg 同步更新；off 語義保留（完全原生 raid、零干擾）。
* 版本 0.3.2 → 0.3.3（client＋server）。

## 0.3.2（2026-11-14-3：RBF 靜態網格重製——建模問題根治）

* **重製管線上線**：`tools/remodel_pmx.py`（本地開源，無 AI 重拓撲）——地標 RBF＋線性多項式位移場作用於「頂點＋骨樞紐」；權重/材質/UV/物理/表情零觸碰（位元組精準修補，恆等場輸出＝輸入逐位元組相同）。手堂徑向縮放以「頂點權重中手部子樹佔比」混合解析施加（袖口零捲入）；畸形加權（Σw>1）附掛網格排除。
* **重製規格實現**：臂鏈 0.6808m（肘 63.1%）、腿鏈 1.0710m、趾 0.1545m、手堂 ×1.68–1.79（authored 最遠指尖距基準）；±0.5cm 規格內（實測逐段 ≤2mm）。
* **重製結果（19/19 包）**：model_matrix 全段因子 uarm/farm/thigh/shank/foot/clavicle ＝ 1.0000（worst |k−1| = 0），proportion_scale.json 重凍；factors/proportions/profile 釘點以重製後值重凍。
* **守門員**：RBF 插值自檢、頂點過衝守門員、hand scale 帶 1.2–3.2、無效 bone 索引歸零。
* 版本 0.3.1 → 0.3.2（client＋server）。

## 0.3.1（2026-11-14-2：raid 建模停用）

* **raid 側建模停用（用戶實測回饋）**：MMD 原始比例模型在 pristine FBAK 骨架上手臂/手拉長變形，且與原生 PMC 手並存時握把對不上——`Replace player body` 預設改 **false**；off 時模組對玩家**零寫入**（掛鉤審計：Patches/BodyReplacer/EquipmentHide 首行短路＋CapsulePatches 五 postfix 字典失效），動作與視角完全還給遊戲原生。現場 `DollFrame.cfg` 移除重生成以套用新預設。
* **重新啟用條件**：模型重新建模至比例貼合遊戲骨架（臂鏈 k ≈ 1.0）後再開啟；此前 raid 即純原生 PMC。
* 選單 3D 預覽與角色卡維持可用；版本 0.3.0 → 0.3.1（client＋server 同步）。


## 0.3.0（2026-11-14 架構反轉：模型側等比縮放）

* **架構反轉**：`SkeletonFit`（遊戲骨架擬合）全程退役；遊戲骨架全程 pristine、嚴禁寫入遊戲骨骼 localPosition/localScale，Pristine 基線收編 `GameBones`。
* **模型側等比縮放**：新增 `ModelScaler`（`ModelScaleMath` 純數學雙編譯權威）——Build 時對左足/右足/左肩/右肩四鏈根 pivot 一次性 `localScale`（腿鏈 k ∈ [0.70, 1.45]、臂鏈 k ∈ [0.90, 1.45]），鏈內比例不變（非拉伸），每幀仍僅 localRotation；膠囊補償改取縮放後身高。
* **次要動作全退役**：刪除 `SpringBone`/`SpringMath`/`Dynamics/{UniSpringBoneSolver,SpringCapsule,CapsuleColliderBuilder}`（1C）、`PostureMath`/`Kinematics/RigAim` IK 側（1B）、`WeaponAlignment`/`ArmIkObservation`/`Contract/FpSlots`（1E）；動作回歸遊戲原生，武器零縮放。
* **視角模塊縮約**：刪除 `FirstPersonCamera`/`ArmsMesh`/`FpVisibility`/`FpBranch`/`FpRigDump`/`Camera/SightlineSolver`；唯一保留面＝`FirstPersonSMRMask` 頭部開關（`Hide head in first person`，ShadowsOnly 防穿透＋保地面影子），死亡優先還原，僅狀態翻轉寫渲染器。
* **包集重建（22→19）**：臂鏈 k ≤ 1.45 用戶拍板門檻（`analysis/proportion_scale.json` 凍結，`proportions` 斷言）；淘汰 伊格蕾塔/佩莉/索米/芙洛倫/芙鈴 系（k 1.52–1.66），新增 黛煙原皮、萊娜-秘夜獵影。
* **契約重列**：配置鍵 27→24；日誌釘點 8→6（`scale:` 入列）、失敗標記 5→4（`diverges` 隨 armik-frame 退役除名）、行族 20→13（新增 `scale:`/`fp head mask:`）；harness 23→14 子命令（退役 spring/unispring/smrmask 之外 10 支、新增 `proportions`）；`check-log.sh`/fixtures/`check_matrix.py`（恰 19 包）同步。
* **版本**：0.2.0 → 0.3.0。

本文檔記錄 DollFrame 專案之核心架構演進、功能發布與關鍵缺陷修復歷史。

---

## 2026-11-13：武器縮放退役與手到握把距離連動（Arm Reach Chain）

### 1. 武器等比縮放退役（`WeaponScale`/`WeaponScaleMath`）
* **問題**：縮放路線需同時維持 FP 容器根/TP 根縮放、`grip probe:` 歸屬探測與手臂鏈同帶擬合，且縮小武器偏離原廠瞄具光軸與握把跨距；用戶裁定回歸「槍不動、人適應槍」之全尺寸武器。
* **修復**：刪除 `WeaponScale.cs`/`WeaponScaleMath.cs` 與 `Tests/WeaponScaleTest.cs`（含 `BodyReplacer` 縮放管線 6 處、`Plugin.OnDestroy` 清表、csproj 雙編譯連結）；登記表 `weapon-scale:`／`grip probe:` 行族除名；harness `weaponscale` 子命令退役（24→23 支）。武器恆為 PMC 原廠全尺寸、零 scaling 寫入。

### 2. 遊戲骨架手臂鏈退出 fit（`SkeletonFit`）
* **問題**：手臂鏈 0.70–1.00 同帶箝制與武器縮放互為前提；武器回到全尺寸後縮短的遊戲手臂令 FBAK 掌錨（FP 模型手直接釘在其上）搆不到握把——正是「雙手停在胸前」的遊戲側根因。
* **修復**：移除六條手臂鏈與 `[0.70, 1.00]` 箝制帶；遊戲骨架手臂恢復原版長度，FBAK、武器掛架與瞄準管線純度重建。

### 3. 手到握把距離連動（`CharacterRig`，模型側閉合）
* **量測**：驅動迴圈前按左右各量一次 $d=\lvert$模型掌位$-\,$腕目標$\rvert$（腕目標＝握把真值沿肩→握把回拉 $0.15\times$ 前臂長；`SolveWristGripPosition`）；遲滯啟閉（$d>2\text{cm}$ 進入、$<1\text{cm}$ 退出）。
* **閉合鏈**：鎖骨生理前伸（`ComputeClavicleExtension`，胸廓座標系 ≤15°）→ 兩骨解析 IK（`RigAim.SolveArmIk`；子軸改寫以新增 `RigAim.AimChild` 在本幀 FK 目標上 premultiply，保留 FK roll）→ 前臂重瞄腕目標 → 腕骨保持 FK（拇指↔小指 roll 通道保留）；胸（脊椎）層由既有迎槍前傾承擔；全程僅寫 localRotation（零拉伸不變量）。
* **右手真值重持**：`WeaponAlignment.SolveGripTarget` 恢復右手分流——`_ikTargets[1]` 優先、null 回退武器容器根 `weapon_R_IK_marker`（武器真值；不回退遊戲 RightPalm），換彈／檢視動作態右手交還原版動畫；marker 解析以武器根 InstanceID 快取（失敗 60 幀重試）。
* **逐幀距離觀測重持**：`armik-frame:` 行族再登記（`CharacterRig.EmitArmikObservation`；行面單一組裝經 `ArmIkObservation.FrameLine`）——狀態變化 0.5s 去抖、落差 >4cm 落 `diverges`（30s 心跳）、真值缺席落 `unresolved`；發射限 own-player rig。

### 4. 契約同步
* `TelemetryRegistry` 行族 21→20＋failure 標記 5（除名 `weapon-scale:`/`grip probe:`、再登記 `armik-frame:`）；harness 子命令 24→23；`armik` 16→17 checks（新增 `AimChild` 合成精確性、等冪性與退化 no-op 釘點）；README/AGENTS/docs/ARCHITECTURE 全量同步至「武器零縮放＋距離連動」敘事。

---

## 2026-11-12（續）：空抓根因修正（武器容器根縮放）與上半身關節逐段擬合

### 1. FP 武器縮放根上移至武器容器根（`WeaponScale`）
* **問題（fp_rig 實機判決＋反編譯證據）**：`PlayerBones.Weapon_Root_Anim` 是武器模型內部的 `Weapon_root` 節點（`Weapon_root ← weapon_*.model.generated(Clone) ← weapon_*_container(Clone)`），縮放它只收縮武器網格子樹；FBAK 手位錨點 `_ikTargets` 不在其子樹內、維持原尺寸——「槍體縮、手位不縮」＝雙手視覺空抓，抬槍 pitch 使槍位收縮偏移顯形。
* **修復**：FP 等比縮放目標改為武器容器根 `*_container(Clone)`（武器網格與 `_ikTargets` 同子樹一起等比閉合，FBAK 手位自動跟上）；`Weapon_Root_Anim` 本身停寫 scale（避免雙重縮放）；找不得到容器祖先的 rig 變體退回舊制（fail-open）。TP 維持 `Weapon_Root_Third`。新增 `grip probe:` 登記行族（每縮放根實例一次，落 `_ikTargets` 歸屬分類 fp/third/body/outside/null，同時可見 G28 等 `_ikTargets[1]`=null 武器）。

### 2. 脊椎逐段擬合（`SkeletonFit`，上半身關節連接）
* **問題**：脊椎以單一均一 k 只閉合 pelvis→neck 端點；模型內段比例（spineRoot 0.12~0.16 / torso 0.32~0.34 / chestNeck 1.0~1.07）與遊戲均一分段不同源，遊戲胸骨（Spine3/Ribcage——武器與 FP 掛架父鏈）落在模型胸口上方最高約 18cm＝上半身關節連接錯位，槍位／掛架／迎槍量測全部失準。
* **修復**：脊椎改逐段——seg1 pelvis→spine1（原始 k 低於合理帶，箝入 [0.25, 2.5]，殘差 ≤6mm）、seg2 spine1→spine3（torso 因子）、seg3 spine3→neck（chestNeck 因子）、Ribcage 掛架 offset 隨 seg3。端點（neck/head 高度＝FP 相機錨）位移 ≤5mm，相機/ADS 零回歸。

### 3. 契約同步
* `TelemetryRegistry` 行族 19→21（登記 `fit:`、`grip probe:`）；harness `weaponscale` 10/10→12/12（新增 `DecideScaleTarget` 容器根判決釘點）。

---

## 2026-11-12：握把、眼位閉合與專案架構全量重修

### 1. 右手主握把空抓防護（`WeaponAlignment`）
* **問題**：反編譯 `Player.IkProcess` 發現，G28、RFB 等無獨立右手 GripPose 的武器，其 `_ikTargets[1]` 恆為 null。先前邏輯回退至男模動畫手掌錨點（`pb.RightPalm`），使較短的 DollFrame 女模手臂產生 0.9~2.7cm 缺口形成右手空抓。
* **修復**：`_ikTargets[1]` 為 null 時，優先回退遊戲武器階層自身的原生 `weapon_R_IK_marker`（武器真值），雙手緊密貼合武器。

### 2. 第一人稱相機眼位動態閉合與姿態感知閘門（`FirstPersonCamera`）
* **眼位高度閉合**：消費 mount-local 校準量 `dy` 到 `HandsContainer.CameraOffset.y`，閉合因 SkeletonFit 縮短脊椎導致相機停在雙眼下方的 7.5cm 殘差。
* **姿態感知閘門**：引入姿態穩定閘（$\text{mount.up} \cdot \text{Vector3.up} \ge 0.85$ 俯仰延後校準），防止轉場幀將垂直 delta 剪切至水平通道；配合橫向 $|x| \le 0.15\text{m}$、縱向 $z \le +0.25\text{m}$ 的感應窗與跨通道一致性閘（差值 $\le 0.08\text{m}$），確保全角色平視精準鎖定模型雙眼。

### 3. 外觀還原日誌強化與專案結構精簡
* 原版素體渲染器還原失敗時輸出警告日誌（消除無聲 catch）。
* 清理 `.omo/`、`bug/`（30MB 歷史截圖）、`Proof/` 等冗餘目錄與舊檔案，優化目錄結構。
* 全面重構文檔體系：重寫 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) 技術架構說明書，精簡重組 [`README.md`](README.md)、[`AGENTS.md`](AGENTS.md) 與本檔。

---

## 2026-11-11：架構清殘波（arch-replan）與文件同步

* **休眠模組與死代碼全數退場**：
  * 刪除過度工程的全身肌肉重定向（`MuscleRetargeter.cs`）與扭轉校正（`TwistCorrection.cs`）及其測試。
  * 刪除 `RigAim.cs` 的 `ArmChain` struct 與未調用之戰術臂 IK，相關測試重收斂為 `railslide`。
  * 刪除 `PostureMath.cs` 中未使用的 Deferred 區域 7 支死函式，淨刪除近 2,000 行代碼。
  * `FpVisibility.Outputs` 收斂為 armsOn-only；刪除未調用之 EFT `SetPointOfView` 委派。
* **命名正名與離線 Harness 收斂**：
  * 將 `PostureMath.SolveRightWristGripPosition` 正名為 `SolveWristGripPosition`（符合左手唯一呼叫語義）。
  * 離線驗證工具子命令由 26 支收斂至 **24 支**（`retarget` 與 `twist2` 退役，`tacticalik` 改名為 `railslide`）。
* **文檔同步對齊**：修正配置預設值、補齊子模塊目錄樹與單向 LateUpdate 管線執行順序。

---

## 2026-11-10：相機姿態修復鏈與開源模組化重設計

* **開源模組化管線整合**：
  * **人體量測描述檔**：導入 `ModelProfile.cs`，宣告式量測模型身高、臂長、肩寬與動態鏈設定。
  * **頭身網格分離**：`MeshSplitter.cs` 依骨骼權重純演算法拆分頭部與身體網格，邊界頂點法線與 UV 100% 完整保留。
  * **Verlet 次級動態物理**：導入 `UniSpringBoneSolver.cs` 質點積分與防爆阻尼；配合 `CapsuleColliderBuilder.cs` 建立雙腿與人體膠囊碰撞體，杜絕穿腿穿模。
  * **第一人稱全身陰影保留**：導入 `FirstPersonSMRMask.cs`，頭部切換為 `ShadowsOnly`，身體保持可見，解決無頭騎士問題。
  * **開鏡光軸共軸投影**：導入 `SightlineSolver.cs`，ADS 開鏡時相機對齊瞄具紅點光軸，提供 Near-Clip 近裁剪面保護。
* **回退止血與穩態重構（023fa97 / c7da90f）**：
  * 當天引入的動態相機反算與全身肌肉重定向在實測中造成視角異位與手臂空抓，迅速回退至前版穩態基準，隨後於穩態之上重構脊椎人體工學前傾（Spine-Torso Adaptation），徹底消除視角晃動。

---

## 2026-11-09：生物力學握持反射（方案 C）與 FP 視野平面重構

* **「槍不動，人適應槍」架構確立**：
  * 徹底廢除寫入 `Weapon_Root_Anim` 的槍械抬升邏輯（該邏輯為先前左手脫離 5~22cm 的根因）。
  * 廢除驅動層的 `isFp` / `PointOfView` 視角分支，TP / FP / 選單預覽走同一條純幾何路徑。
* **武器錨定接觸三層模型**：
  * **第 1 層（導軌軸向滑移 Rail Slide）**：以握把至前握把連線定義槍管軸，左手超出舒適臂長時沿導軌向後滑移，手掌始終落在護木上。
  * **第 2 層（手肘極向量權威）**：以模型 authored 胸廓下後方為基準極向量，遊戲手肘需點積 $\ge 0.3$ 且無退化才接手。
  * **第 3 層（腕掌偏距與左肩前伸）**：手腕目標沿臂向回縮 $0.15 \times \text{前臂長度}$（`PalmWristRatio`）；超出 85% 臂長時左肩生理旋轉前伸；上臂原點允許前伸 $\le 8\text{cm}$。
* **換彈與手部動作 Anim-Follow**：
  * 偵測到換彈（`BaseReloadOperation`）或檢視動作時，左手握持目標自動切換為遊戲手掌錨點 `pb.LeftPalm`，動作結束自然歸位。
* **第一人稱視野平面重構**：
  * 反編譯確認 `HandsContainer.CameraOffset` 為 PWA 靜態讀取輸入，改採每幀自 pristine 基準絕對疊加校準量，修復相機與武器下沉殘差。
* **日誌行族登記制（`TelemetryRegistry`）**：
  * 集中登記 17 個行族與 5 個失敗標記，新增 `[DollFrame] armik-frame:` 逐幀握持觀測行，使 `diverges` 具備首個 runtime 生產者。

---

## 2026-11-07~08：三大主模塊化重構、外觀裝備隱藏與骨架擬合

* **模組解耦重構**：
  * 全面劃分為三大主模塊：Primary Module 1（Character 骨架與建模）、Primary Module 2（Visibility 外觀與裝備隱藏）、Primary Module 3（MenuUI 選單預覽與切換介面）。
  * 模組間嚴格維持單向依賴，MenuUI 與 Visibility 僅能唯讀複用 Character，Character 絕不反向引用上層。
* **原生裝備資料級隱藏（`EquipmentHide`）**：
  * 承接已移除之 `EFT.Transmog-cust`，快照清空 8 個裝備槽位（護甲、背心、背包、頭盔、面罩、耳機、臂章、眼鏡，預設全隱），保留原生防彈屬性。
* **骨盆 PureDelta 與足踝 PureRel 拷貝落地**：
  * 骨盆（`下半身`）採純旋轉增量疊加，解決 GFL2 腿部 46° authored 外張導致骨盆歪斜的頑疾。
  * 足首與腳趾採父關節相對旋轉增量疊加，消除 MMD 趾骨 36° 下傾導致軸向瞄準翹趾問題。
* **SkeletonFit 邊界收斂**：
  * 手臂鏈停止縮放擬合，僅縮放腿部、骨盆與脊椎，維持原版 FBAK 與瞄準管線純度。
* **選單介面與即時切換**：
  * 實作全般畫面頂部 `＜ DollFrame 角色 ＞` 切換列（`CharacterSwitchUI`）與主選單 `Page Up` 快捷鍵；主選單 3D 預覽（`PreviewProxy`）100% 複用戰局骨架驅動。

---

## 2026-11-06：專案核心樞紐（Pivot）

* **架構樞紐裁定**：
  * 徹底廢棄「原版骨架均一縮放」與「原版骨架 PMX 蒙皮換皮」方案。
  * **全面確立「每角色獨立 PMX Rig 實例」架構**：在玩家 `Root_Joint` 下建立完整的 PMX Transform 階層與獨立 SkinnedMeshRenderer，以純旋轉驅動，構造保證結構零拉伸。
* **基礎設施建立**：
  * 建立 PMX 轉 `.gfc` 二進位封裝格式與 `Packager` 工具。
  * 建立 net8.0 離線驗證工具（`Tests/DollFrame.Verify`），脫離 Unity 引擎自動化斷言骨骼綁定矩陣、比例因子與映射契約。
  * 建立 `tools/check-log.sh` 日誌契約與 8 個 pinned 前綴守護網。
