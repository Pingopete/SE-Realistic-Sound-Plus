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

            if (cueName.IndexOf("Thruster", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (cueName.IndexOf("Thuster", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Confirmed in-game: ShipLargeRunLoop is the H2/thruster hiss/spool layer, not the coasting rattle bed.
            return cueName.Equals("ShipLargeRunLoop", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunLoop", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsKnownAmbientCue(MyCueId? cueId)
        {
            if (!cueId.HasValue)
                return false;

            return IsKnownAmbientCue(cueId.Value.ToString());
        }

        public static bool IsKnownAmbientCue(string cueName)
        {
            if (string.IsNullOrWhiteSpace(cueName))
                return false;

            return cueName.Equals("ArcBlockMedical", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ArcBlockAirVentIdle", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("BlockOxyGenIdle", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ArcBlockGravityGen", StringComparison.OrdinalIgnoreCase)
                || cueName.IndexOf("Medical", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("AirVent", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("OxyGen", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("OxygenGenerator", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("GravityGen", StringComparison.OrdinalIgnoreCase) >= 0;
        }


        public static bool IsKnownExteriorWeaponCue(MyCueId? cueId)
        {
            if (!cueId.HasValue)
                return false;

            return IsKnownExteriorWeaponCue(cueId.Value.ToString());
        }

        public static bool IsKnownExteriorWeaponCue(string cueName)
        {
            if (string.IsNullOrWhiteSpace(cueName))
                return false;

            if (cueName.IndexOf("NoAmmo", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Reload", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return cueName.StartsWith("ArcWep", StringComparison.OrdinalIgnoreCase)
                || cueName.StartsWith("RealWep", StringComparison.OrdinalIgnoreCase)
                || cueName.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Gatling", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Autocannon", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Railgun", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Calibre", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Warhead", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Explosion", StringComparison.OrdinalIgnoreCase) >= 0
                || cueName.IndexOf("Expl", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        public static bool IsKnownSpeedAmbientCue(MyCueId? cueId)
        {
            if (!cueId.HasValue)
                return false;

            return IsKnownSpeedAmbientCue(cueId.Value.ToString());
        }

        public static bool IsKnownSpeedAmbientCue(string cueName)
        {
            return cueName.Equals("ArcShipWindSpeed", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeEngine", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallEngine", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeIdle", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeSpeedDown", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipLargeSpeedUp", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunSlow", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunMedium", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallRunFast", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallSpeedDown", StringComparison.OrdinalIgnoreCase)
                || cueName.Equals("ShipSmallSpeedUp", StringComparison.OrdinalIgnoreCase);
        }
    }
}
