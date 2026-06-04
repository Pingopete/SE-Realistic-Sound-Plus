using System;
using VRage.Audio;

namespace RealisticSoundPlus.Patches
{
    internal static class EngineAudioClassifier
    {
        public static bool IsKnownEngineCue(MyCueId? cueId)
        {
            if (!cueId.HasValue)
                return false;

            return IsKnownEngineCue(cueId.Value.ToString());
        }

        public static bool IsKnownEngineCue(string cueName)
        {
            if (string.IsNullOrWhiteSpace(cueName))
                return false;

            if (cueName.StartsWith("ArcBlockHydrogenEngine", StringComparison.OrdinalIgnoreCase))
                return true;

            if (cueName.IndexOf("HydrogenEngine", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (cueName.IndexOf("JetHydrogen", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (cueName.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0 && cueName.IndexOf("Ship", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (cueName.IndexOf("Thruster", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (cueName.IndexOf("Thuster", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (cueName.Equals("ShipLargeEngine", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallEngine", StringComparison.OrdinalIgnoreCase))
                return true;

            return IsKnownShipMotionCue(cueName);
        }

        private static bool IsKnownShipMotionCue(string cueName)
        {
            return cueName.Equals("ShipLargeIdle", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeRunLoop", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeSpeedDown", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeSpeedUp", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunLoop", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunSlow", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunMedium", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunFast", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallSpeedDown", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallSpeedUp", StringComparison.OrdinalIgnoreCase);
        }
    }
}
