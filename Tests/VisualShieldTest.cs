using System;

namespace GF2.Tests
{
    public static class VisualShieldTest
    {
        public static int Run()
        {
            // Verify shield state machine and safety timeout constant (0.25s)
            float timeout = VisualShieldConfig.ShieldTimeoutSeconds;
            if (timeout < 0.1f || timeout > 0.5f)
            {
                Console.Error.WriteLine($"visual shield: ShieldTimeoutSeconds {timeout} out of safe range (0.1..0.5)");
                return 1;
            }

            // Test shield logic simulation
            var state = new VisualShieldState();
            state.Arm(10.0f);
            if (!state.IsArmed)
            {
                Console.Error.WriteLine("visual shield: expected state to be armed");
                return 1;
            }
            if (state.ShouldAutoDisarm(10.1f))
            {
                Console.Error.WriteLine("visual shield: disarmed too early");
                return 1;
            }
            if (!state.ShouldAutoDisarm(10.3f))
            {
                Console.Error.WriteLine("visual shield: failed to auto-disarm after timeout");
                return 1;
            }
            state.Disarm();
            if (state.IsArmed)
            {
                Console.Error.WriteLine("visual shield: expected state to be disarmed");
                return 1;
            }

            Console.WriteLine("visual shield: OK");
            return 0;
        }
    }
}
