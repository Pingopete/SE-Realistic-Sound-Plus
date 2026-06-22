using System;
using RealisticSoundPlus.Patches;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2AuxCueClassifier
    {
        public static bool IsEngineCue(string cueName)
        {
            if (IsFunctionalBlockEngineCue(cueName))
                return false;

            return EngineAudioClassifier.IsKnownEngineCue(cueName);
        }

        public static bool IsNonWorldCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (value.Length == 0)
                return false;

            return Contains(value, "Hud")
                || Contains(value, "Gui")
                || Contains(value, "Toolbar")
                || Contains(value, "Inventory")
                || Contains(value, "Clipboard")
                || Contains(value, "Button")
                || Contains(value, "Click")
                || Contains(value, "Cursor")
                || Contains(value, "Unable")
                || Contains(value, "PlaceBlock")
                || Contains(value, "RotateBlock")
                || Contains(value, "BuildPlanner")
                || Contains(value, "BuildMode")
                || Contains(value, "ColorPicker");
        }

        public static bool IsKnownBlockCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (value.Length == 0 || IsNonWorldCue(value) || IsEngineCue(value) || IsPlayerLocalCue(value))
                return false;

            if (IsKnownBlockCueButNeedsPhysicalSource(value))
                return false;

            if (Contains(value, "Block"))
                return true;

            return Contains(value, "GravityGen")
                || Contains(value, "AirVent")
                || Contains(value, "OxyGen")
                || Contains(value, "Oxygen")
                || Contains(value, "Medical")
                || Contains(value, "Assembler")
                || Contains(value, "Refinery")
                || Contains(value, "Reactor")
                || Contains(value, "Battery")
                || Contains(value, "JumpDrive")
                || Contains(value, "Beacon")
                || Contains(value, "Antenna")
                || Contains(value, "Timer")
                || Contains(value, "Programmable")
                || Contains(value, "SafeZone")
                || Contains(value, "Door")
                || Contains(value, "Rotor")
                || Contains(value, "Piston")
                || Contains(value, "Conveyor")
                || Contains(value, "Drill")
                || Contains(value, "Welder")
                || Contains(value, "Grinder")
                || IsFunctionalBlockEngineCue(value);
        }

        public static bool IsKnownBlockCueButNeedsPhysicalSource(string cueName)
        {
            string value = cueName ?? string.Empty;
            return Contains(value, "WindTurbine");
        }

        public static bool IsEnvironmentCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (value.Length == 0 || IsNonWorldCue(value) || IsKnownBlockCue(value) || IsKnownBlockCueButNeedsPhysicalSource(value))
                return false;

            return value.Equals("ArcShipWindSpeed", StringComparison.OrdinalIgnoreCase)
                || value.Equals("ShipWindSpeed", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("WM_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Amb", StringComparison.OrdinalIgnoreCase)
                || Contains(value, "Wind")
                || Contains(value, "Rain")
                || Contains(value, "Snow")
                || Contains(value, "Fog")
                || Contains(value, "Hail")
                || Contains(value, "Thunder")
                || Contains(value, "Lightning")
                || Contains(value, "Weather")
                || Contains(value, "Storm")
                || Contains(value, "AmbWind");
        }

        public static bool IsPlayerLocalCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (IsNonWorldCue(value))
                return false;

            if (IsMedicalServiceCue(value))
                return true;

            return Contains(value, "Foot")
                || Contains(value, "Step")
                || Contains(value, "Breath")
                || Contains(value, "Helmet")
                || Contains(value, "Character")
                || Contains(value, "Player")
                || Contains(value, "Body")
                || Contains(value, "Jump")
                || Contains(value, "Land")
                || Contains(value, "Fall")
                || Contains(value, "Jet")
                || Contains(value, "MagBoot")
                || Contains(value, "Paint")
                || Contains(value, "Spray")
                || Contains(value, "Tool");
        }

        private static bool IsMedicalServiceCue(string cueName)
        {
            return Contains(cueName, "MedicalProgress")
                || Contains(cueName, "MedicalProcess")
                || Contains(cueName, "MedicalRefill")
                || Contains(cueName, "MedicalHeal");
        }

        private static bool IsFunctionalBlockEngineCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return Contains(value, "BlockHydrogenEngine");
        }

        private static bool Contains(string value, string fragment)
        {
            return value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
