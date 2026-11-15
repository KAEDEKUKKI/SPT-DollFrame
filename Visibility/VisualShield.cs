namespace DollFrame
{
    public static class VisualShieldConfig
    {
        /// <summary>
        /// Safety timeout in seconds: if the model/skeleton is delayed or fails to attach,
        /// disarm the visual shield after this time so the UI never stays permanently blank.
        /// </summary>
        public const float ShieldTimeoutSeconds = 0.25f;
    }

    /// <summary>
    /// Pure state tracker for the visual shield mechanism.
    /// </summary>
    public sealed class VisualShieldState
    {
        public bool IsArmed { get; private set; }
        public float ArmedAt { get; private set; }

        public void Arm(float now)
        {
            IsArmed = true;
            ArmedAt = now;
        }

        public void Disarm()
        {
            IsArmed = false;
        }

        public bool ShouldAutoDisarm(float now)
        {
            return IsArmed && (now - ArmedAt >= VisualShieldConfig.ShieldTimeoutSeconds);
        }
    }
}
