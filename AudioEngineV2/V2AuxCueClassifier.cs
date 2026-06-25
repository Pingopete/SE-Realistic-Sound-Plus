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

            if (IsSoundBlockCue(value))
                return true;

            if (Contains(value, "Block"))
                return true;

            return Contains(value, "GravityGen")
                || Contains(value, "AirVent")
                || Contains(value, "OxyGen")
                || Contains(value, "Oxygen")
                || Contains(value, "Medical")
                || Contains(value, "Assembler")
                || Contains(value, "Refinery")
                || Contains(value, "Rafinery")
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

        public static bool IsBlockMedicalCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return Contains(value, "BlockMedical");
        }

        public static bool IsKnownBlockCueButNeedsPhysicalSource(string cueName)
        {
            string value = cueName ?? string.Empty;
            return Contains(value, "WindTurbine");
        }

        public static bool IsSoundBlockCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (value.Length == 0)
                return false;

            return Contains(value, "SoundBlock")
                || Contains(value, "Jukebox")
                || Contains(value, "Speaker")
                || IsSoundBlockMusicCue(value);
        }

        public static bool IsSoundBlockMusicCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return value.StartsWith("Mus", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("Music", StringComparison.OrdinalIgnoreCase);
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

        public static bool IsTransientEnvironmentReverbCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return Contains(value, "Thunder")
                || Contains(value, "Lightning");
        }

        public static bool IsSustainedBlockReverbCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return IsSoundBlockMusicCue(value)
                || IsSustainedWeaponCue(value)
                || Contains(value, "Idle")
                || Contains(value, "Loop")
                || Contains(value, "Process")
                || Contains(value, "Assembler")
                || Contains(value, "Refinery")
                || Contains(value, "Rafinery")
                || Contains(value, "AirVent")
                || Contains(value, "OxyGen")
                || Contains(value, "Oxygen")
                || Contains(value, "Reactor")
                || Contains(value, "Battery")
                || Contains(value, "GravityGen")
                || Contains(value, "JumpDrive")
                || Contains(value, "SafeZone")
                || Contains(value, "Conveyor")
                || Contains(value, "Turbine")
                || Contains(value, "WindTurbine")
                || Contains(value, "HydrogenEngine")
                || Contains(value, "Generator")
                || Contains(value, "Beacon")
                || Contains(value, "Antenna");
        }

        public static bool IsImmersiveUiCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (value.Length == 0)
                return false;

            return Contains(value, "Hud")
                || Contains(value, "Gui")
                || Contains(value, "Terminal")
                || Contains(value, "Menu")
                || Contains(value, "Toolbar")
                || Contains(value, "Inventory")
                || Contains(value, "Clipboard")
                || Contains(value, "Button")
                || Contains(value, "Mouse")
                || Contains(value, "Click")
                || Contains(value, "Cursor")
                || Contains(value, "Unable")
                || Contains(value, "BuildPlanner")
                || Contains(value, "BuildMode")
                || Contains(value, "ColorPicker")
                || Contains(value, "Notification")
                || Contains(value, "Objective")
                || Contains(value, "Questlog")
                || Contains(value, "Locking")
                || Contains(value, "HudUse")
                || Contains(value, "HudItem")
                || Contains(value, "HudClick")
                || Contains(value, "HudMouse")
                || Contains(value, "HudPlaceBlock")
                || Contains(value, "HudDeleteBlock")
                || Contains(value, "HudRotateBlock")
                || Contains(value, "HudColorBlock")
                || Contains(value, "HudUnable")
                || Contains(value, "PlayTakeItem")
                || Contains(value, "PlayDropItem")
                || Contains(value, "PlaceBlock")
                || Contains(value, "DeleteBlock")
                || Contains(value, "RotateBlock")
                || Contains(value, "ColorBlock");
        }

        public static bool IsConstructionProgressCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return Contains(value, "PrgConstr")
                || Contains(value, "PrgDeconstr");
        }

        public static bool IsSustainedLocalReverbCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (IsConstructionProgressCue(value))
                return Contains(value, "Ph02") || Contains(value, "Proc");

            return IsPlayerToolCue(value) && (Contains(value, "Idle") || Contains(value, "Loop"));
        }

        public static bool IsToolActionCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return IsPlayerToolCue(value) || IsShipToolCue(value);
        }

        public static bool IsWeaponCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (value.Length == 0)
                return false;

            return StartsWith(value, "ArcWep")
                || StartsWith(value, "RealWep")
                || StartsWith(value, "ArcWeFirework")
                || Contains(value, "Weapon")
                || Contains(value, "Railgun")
                || Contains(value, "RailGun")
                || Contains(value, "Gatling")
                || Contains(value, "Autocannon")
                || Contains(value, "Missile")
                || Contains(value, "Rocket")
                || Contains(value, "Rifle")
                || Contains(value, "Pistol");
        }

        public static bool IsSustainedWeaponCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return IsWeaponCue(value)
                && (Contains(value, "Loop")
                    || Contains(value, "Rotation")
                    || Contains(value, "Rotate")
                    || Contains(value, "Charge")
                    || Contains(value, "Fly")
                    || Contains(value, "Flight"));
        }

        public static bool IsWorldImpactCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (value.Length == 0)
                return false;

            return StartsWith(value, "ArcImp")
                || StartsWith(value, "RealImp")
                || Contains(value, "ImpMetal")
                || Contains(value, "ImpRock")
                || Contains(value, "ImpShip")
                || Contains(value, "Explosion")
                || Contains(value, "Expl");
        }

        public static bool IsControllableActionCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (IsNonWorldCue(value) || IsEngineCue(value))
                return false;

            return IsToolActionCue(value)
                || IsConstructionProgressCue(value)
                || IsWeaponCue(value)
                || IsWorldImpactCue(value);
        }

        public static bool IsPlayerLocalCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (IsImmersiveUiCue(value))
                return true;

            if (IsNonWorldCue(value))
                return false;

            if (IsBlockMedicalCue(value))
                return false;

            if (IsMedicalServiceCue(value))
                return true;

            return IsFootstepCue(value)
                || IsPlayerMovementCue(value)
                || IsPlayerImpactCue(value)
                || IsPlayerToolCue(value)
                || IsConstructionProgressCue(value)
                || IsLikelyHandWeaponCue(value)
                || IsPlayerJetpackCue(value)
                || IsPlayerSuitCue(value);
        }

        public static bool IsPlayerLocalReverbCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            if (IsImmersiveUiCue(value))
                return true;

            if (IsNonWorldCue(value) || IsMedicalServiceCue(value))
                return false;

            return IsFootstepCue(value)
                || IsPlayerMovementCue(value)
                || IsPlayerImpactCue(value)
                || IsPlayerToolCue(value)
                || IsConstructionProgressCue(value);
        }

        public static bool IsPlayerToolCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            bool playerToolPrefix = StartsWith(value, "ArcToolPlay")
                || StartsWith(value, "RealToolPlay")
                || StartsWith(value, "ArcToolLrg")
                || StartsWith(value, "RealToolLrg");
            if (!playerToolPrefix)
                return false;

            return Contains(value, "Drill")
                || Contains(value, "Grind")
                || Contains(value, "Weld");
        }

        public static bool IsShipToolCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            bool shipToolPrefix = StartsWith(value, "ArcToolShip")
                || StartsWith(value, "RealToolShip");
            if (!shipToolPrefix)
                return false;

            return Contains(value, "Drill")
                || Contains(value, "Grind")
                || Contains(value, "Weld");
        }

        public static bool IsPlayerImpactCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return StartsWith(value, "ArcImpPlayer")
                || StartsWith(value, "RealImpPlayer")
                || StartsWith(value, "ArcImpFemPlayer")
                || StartsWith(value, "RealImpFemPlayer")
                || value.Equals("PlayChokeHit", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFootstepCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return Contains(value, "Foot")
                || Contains(value, "Step");
        }

        private static bool IsPlayerMovementCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return StartsWith(value, "ArcPlayJump")
                || StartsWith(value, "RealPlayJump")
                || StartsWith(value, "ArcPlayFall")
                || StartsWith(value, "RealPlayFall")
                || StartsWith(value, "ArcPlayCrouch")
                || StartsWith(value, "RealPlayCrouch")
                || StartsWith(value, "ArcPlayMagBoots")
                || StartsWith(value, "RealPlayMagBoots");
        }

        private static bool IsPlayerJetpackCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return StartsWith(value, "ArcPlayJet")
                || StartsWith(value, "RealPlayJet");
        }

        private static bool IsPlayerSuitCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return StartsWith(value, "ArcPlayHelmet")
                || StartsWith(value, "RealPlayHelmet")
                || StartsWith(value, "PlayHelmet")
                || StartsWith(value, "PlayVoc")
                || StartsWith(value, "FemPlayVoc")
                || StartsWith(value, "RealPlayVocBreath")
                || StartsWith(value, "RealFemPlayVocBreath")
                || StartsWith(value, "PlayChoke")
                || StartsWith(value, "FemPlayChoke")
                || StartsWith(value, "ArcPlayEat")
                || StartsWith(value, "ArcPlayDrink")
                || StartsWith(value, "ArcPlayUse");
        }

        private static bool IsLikelyHandWeaponCue(string cueName)
        {
            string value = cueName ?? string.Empty;
            return IsWeaponCue(value)
                && !Contains(value, "Ship")
                && !Contains(value, "Turret")
                && !Contains(value, "Missile")
                && !Contains(value, "Autocannon")
                && !Contains(value, "Calibre")
                && !Contains(value, "Railgun")
                && !Contains(value, "RailGun");
        }

        private static bool IsMedicalServiceCue(string cueName)
        {
            if (IsBlockMedicalCue(cueName))
                return false;

            return Contains(cueName, "MedicalProgress")
                || Contains(cueName, "MedicalProcess")
                || Contains(cueName, "MedicalRefill")
                || Contains(cueName, "MedicalHeal");
        }

        private static bool IsFunctionalBlockEngineCue(string cueName)
        {
            return EngineAudioClassifier.IsFunctionalBlockHydrogenEngineCue(cueName);
        }

        private static bool Contains(string value, string fragment)
        {
            return value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool StartsWith(string value, string prefix)
        {
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
