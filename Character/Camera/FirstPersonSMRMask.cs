using UnityEngine;
using UnityEngine.Rendering;

namespace GF2
{
    /// <summary>
    /// Point of view state for character rendering.
    /// </summary>
    public enum PointOfView
    {
        FirstPerson = 0,
        ThirdPerson = 1,
        FreeCamera = 2
    }

    /// <summary>
    /// 【Character 連動 1D：第一人稱 SMR 遮罩與全身陰影保留控制器 (FirstPersonSMRMask)】
    /// 配合 MeshSplitter 將 PMX 模型分割為 Body_SMR（軀幹、雙臂、手部、雙腿、雙腳）與 Head_SMR（頭、臉、頭髮、頸部），
    /// 在不同視角（POV）下動態控制渲染模式：
    /// 1. 第一人稱視角（FirstPerson）：
    ///    - Head_SMR 設為 ShadowsOnly（消除相機近裁剪面頭部穿透，100% 保留地面上頭部與長髮陰影）；
    ///    - Body_SMR 設為 ShadowCastingMode.On 且維持可見（保留第一人稱雙手持槍、俯視軀幹與雙腿視圖）；
    ///    - 徹底停用並取代動態頂點切割（ArmsMesh.cs），杜絕記憶體重複分配與肩縫破圖。
    /// 2. 第三人稱／自由視角（ThirdPerson / FreeCamera）：
    ///    - Body_SMR 與 Head_SMR 均設為 ShadowCastingMode.On（完整全身與頭部渲染，零無頭騎士現象）。
    /// 3. 死亡狀態（Dead）：
    ///    - 優先於視角，強制將所有 SMR 設為 On，確保布娃娃系統與擊殺重播中角色頭部完整。
    /// 4. 開鏡瞄準（Aiming / ADS）：
    ///    - 第一人稱開鏡瞄準時持續保持 Head_SMR ShadowsOnly 與 Body_SMR On，視線同軸無遮擋。
    /// </summary>
    public sealed class FirstPersonSMRMask
    {
        public bool IsAiming { get; private set; }
        public bool IsAlive { get; private set; } = true;

        /// <summary>
        /// 核心接口：依視角模式切換 Body SMR 與 Head SMR 的陰影模式與可見性。
        /// </summary>
        public void SetPointOfView(PointOfView pov, SkinnedMeshRenderer bodySMR, SkinnedMeshRenderer headSMR)
        {
            SetPointOfView(pov, bodySMR, headSMR, isAiming: false, isAlive: true);
        }

        /// <summary>
        /// 完整重載：包含開鏡與存活狀態判定。
        /// </summary>
        public void SetPointOfView(PointOfView pov, SkinnedMeshRenderer bodySMR, SkinnedMeshRenderer headSMR, bool isAiming, bool isAlive = true)
        {
            IsAiming = isAiming;
            IsAlive = isAlive;

            // 死亡狀態：布娃娃與觀戰優先，恢復頭部與身體完整可見
            if (!isAlive)
            {
                if (bodySMR != null)
                {
                    bodySMR.enabled = true;
                    bodySMR.shadowCastingMode = ShadowCastingMode.On;
                }
                if (headSMR != null)
                {
                    headSMR.enabled = true;
                    headSMR.shadowCastingMode = ShadowCastingMode.On;
                }
                return;
            }

            if (pov == PointOfView.FirstPerson)
            {
                if (headSMR != null)
                {
                    // 頭部 SMR：第一人稱相機剔除可見，但保留投射至地面的全身頭部與長髮陰影
                    headSMR.enabled = true;
                    headSMR.shadowCastingMode = ShadowCastingMode.ShadowsOnly;

                    // 身體 SMR：包含雙臂、雙手、軀幹與雙腿，相機可見並正常投射陰影
                    if (bodySMR != null)
                    {
                        bodySMR.enabled = true;
                        bodySMR.shadowCastingMode = ShadowCastingMode.On;
                    }
                }
                else
                {
                    // 若模型未拆分頭部（fallback）：身體包含頭部，為防鏡頭穿透將身體設為 ShadowsOnly
                    if (bodySMR != null)
                    {
                        bodySMR.enabled = true;
                        bodySMR.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    }
                }
            }
            else
            {
                // 第三人稱 / 自由視角：身體與頭部均完整渲染
                if (bodySMR != null)
                {
                    bodySMR.enabled = true;
                    bodySMR.shadowCastingMode = ShadowCastingMode.On;
                }
                if (headSMR != null)
                {
                    headSMR.enabled = true;
                    headSMR.shadowCastingMode = ShadowCastingMode.On;
                }
            }
        }

#if !GF2_PMX_PARSER_ONLY
        /// <summary>
        /// 從 EFT 原版 EPointOfView 轉換為內部 PointOfView。
        /// </summary>
        public static PointOfView FromEft(EFT.EPointOfView eftPov)
        {
            switch (eftPov)
            {
                case EFT.EPointOfView.FirstPerson:
                    return PointOfView.FirstPerson;
                case EFT.EPointOfView.FreeCamera:
                    return PointOfView.FreeCamera;
                default:
                    return PointOfView.ThirdPerson;
            }
        }

        public void SetPointOfView(EFT.EPointOfView eftPov, SkinnedMeshRenderer bodySMR, SkinnedMeshRenderer headSMR, bool isAiming, bool isAlive = true)
        {
            SetPointOfView(FromEft(eftPov), bodySMR, headSMR, isAiming, isAlive);
        }
#endif
    }
}
