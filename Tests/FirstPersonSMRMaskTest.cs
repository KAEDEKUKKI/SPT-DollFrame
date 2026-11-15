using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GF2.Tests
{
    /// <summary>
    /// Verification test for Task 8: First-Person SMR Masking & Full-Body Shadow Preservation.
    /// Validates:
    /// 1. FirstPerson mode with head split: headSMR.shadowCastingMode == ShadowsOnly, bodySMR.shadowCastingMode == On, both enabled.
    /// 2. ThirdPerson mode: both headSMR and bodySMR have shadowCastingMode == On and are enabled.
    /// 3. FreeCamera mode: both headSMR and bodySMR have shadowCastingMode == On and are enabled.
    /// 4. Dead state: dead overrides POV, both headSMR and bodySMR have shadowCastingMode == On (no headless ragdoll).
    /// 5. Aiming (ADS) state: headSMR == ShadowsOnly, bodySMR == On.
    /// 6. Fallback when headSMR == null: bodySMR == ShadowsOnly in FP, bodySMR == On in TP.
    /// 7. Null safety: null body or head does not throw.
    /// </summary>
    public static class FirstPersonSMRMaskTest
    {
        public static int Run()
        {
            Console.WriteLine("=== FirstPersonSMRMaskTest (smrmask) ===");

            if (!TestFirstPersonMaskWithHeadSplit()) return 1;
            if (!TestThirdPersonAndFreeCamera()) return 1;
            if (!TestDeadStateOverride()) return 1;
            if (!TestAimingState()) return 1;
            if (!TestFallbackWithoutHeadSplit()) return 1;
            if (!TestNullSafety()) return 1;

            Console.WriteLine("smrmask: ALL CHECKS PASSED (6/6)");
            return 0;
        }

        private static bool TestFirstPersonMaskWithHeadSplit()
        {
            Console.WriteLine("  1. Testing FirstPerson mode with head split...");
            var mask = new FirstPersonSMRMask();
            var bodySMR = new SkinnedMeshRenderer();
            var headSMR = new SkinnedMeshRenderer();

            mask.SetPointOfView(PointOfView.FirstPerson, bodySMR, headSMR);

            if (headSMR.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
            {
                Console.Error.WriteLine($"FAIL: headSMR shadowCastingMode expected ShadowsOnly, got {headSMR.shadowCastingMode}");
                return false;
            }
            if (bodySMR.shadowCastingMode != ShadowCastingMode.On)
            {
                Console.Error.WriteLine($"FAIL: bodySMR shadowCastingMode expected On, got {bodySMR.shadowCastingMode}");
                return false;
            }
            if (!headSMR.enabled || !bodySMR.enabled)
            {
                Console.Error.WriteLine("FAIL: headSMR and bodySMR must both remain enabled");
                return false;
            }
            return true;
        }

        private static bool TestThirdPersonAndFreeCamera()
        {
            Console.WriteLine("  2. Testing ThirdPerson and FreeCamera modes...");
            var mask = new FirstPersonSMRMask();
            var bodySMR = new SkinnedMeshRenderer();
            var headSMR = new SkinnedMeshRenderer();

            // Set to FP first
            mask.SetPointOfView(PointOfView.FirstPerson, bodySMR, headSMR);

            // Switch to TP
            mask.SetPointOfView(PointOfView.ThirdPerson, bodySMR, headSMR);
            if (headSMR.shadowCastingMode != ShadowCastingMode.On || bodySMR.shadowCastingMode != ShadowCastingMode.On)
            {
                Console.Error.WriteLine("FAIL: ThirdPerson must restore both headSMR and bodySMR to ShadowCastingMode.On");
                return false;
            }
            if (!headSMR.enabled || !bodySMR.enabled)
            {
                Console.Error.WriteLine("FAIL: ThirdPerson must keep both SMRs enabled");
                return false;
            }

            // Switch to FreeCamera
            mask.SetPointOfView(PointOfView.FreeCamera, bodySMR, headSMR);
            if (headSMR.shadowCastingMode != ShadowCastingMode.On || bodySMR.shadowCastingMode != ShadowCastingMode.On)
            {
                Console.Error.WriteLine("FAIL: FreeCamera must have both SMRs at ShadowCastingMode.On");
                return false;
            }
            return true;
        }

        private static bool TestDeadStateOverride()
        {
            Console.WriteLine("  3. Testing Dead state override (no headless ragdoll)...");
            var mask = new FirstPersonSMRMask();
            var bodySMR = new SkinnedMeshRenderer();
            var headSMR = new SkinnedMeshRenderer();

            // When player is dead, even if POV is FirstPerson, head must be visible (ShadowCastingMode.On)
            mask.SetPointOfView(PointOfView.FirstPerson, bodySMR, headSMR, isAiming: false, isAlive: false);
            if (headSMR.shadowCastingMode != ShadowCastingMode.On || bodySMR.shadowCastingMode != ShadowCastingMode.On)
            {
                Console.Error.WriteLine("FAIL: Dead state must render head and body with ShadowCastingMode.On");
                return false;
            }
            return true;
        }

        private static bool TestAimingState()
        {
            Console.WriteLine("  4. Testing Aiming (ADS) state...");
            var mask = new FirstPersonSMRMask();
            var bodySMR = new SkinnedMeshRenderer();
            var headSMR = new SkinnedMeshRenderer();

            mask.SetPointOfView(PointOfView.FirstPerson, bodySMR, headSMR, isAiming: true, isAlive: true);
            if (headSMR.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
            {
                Console.Error.WriteLine($"FAIL: ADS headSMR expected ShadowsOnly, got {headSMR.shadowCastingMode}");
                return false;
            }
            if (bodySMR.shadowCastingMode != ShadowCastingMode.On)
            {
                Console.Error.WriteLine($"FAIL: ADS bodySMR expected On, got {bodySMR.shadowCastingMode}");
                return false;
            }
            if (!mask.IsAiming)
            {
                Console.Error.WriteLine("FAIL: mask.IsAiming should be true");
                return false;
            }
            return true;
        }

        private static bool TestFallbackWithoutHeadSplit()
        {
            Console.WriteLine("  5. Testing Fallback when headSMR is null...");
            var mask = new FirstPersonSMRMask();
            var bodySMR = new SkinnedMeshRenderer();

            // When headSMR is null, bodySMR contains the full mesh including head.
            // In FP, bodySMR should be ShadowsOnly to avoid head clipping into camera.
            mask.SetPointOfView(PointOfView.FirstPerson, bodySMR, null);
            if (bodySMR.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
            {
                Console.Error.WriteLine($"FAIL: Fallback FP bodySMR expected ShadowsOnly, got {bodySMR.shadowCastingMode}");
                return false;
            }

            // In TP, bodySMR should be On.
            mask.SetPointOfView(PointOfView.ThirdPerson, bodySMR, null);
            if (bodySMR.shadowCastingMode != ShadowCastingMode.On)
            {
                Console.Error.WriteLine($"FAIL: Fallback TP bodySMR expected On, got {bodySMR.shadowCastingMode}");
                return false;
            }
            return true;
        }

        private static bool TestNullSafety()
        {
            Console.WriteLine("  6. Testing Null safety...");
            var mask = new FirstPersonSMRMask();
            try
            {
                mask.SetPointOfView(PointOfView.FirstPerson, null, null);
                mask.SetPointOfView(PointOfView.ThirdPerson, null, null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL: Null safety threw exception: {ex.Message}");
                return false;
            }
            return true;
        }
    }
}
