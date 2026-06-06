using System;
using System.Globalization;
using System.Reflection;
using Sandbox.Game.Entities;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2CueCatalog
    {
        private const float VanillaFullSpeed = 96f;
        private const float LargeShipMinWeight = 500000f;

        public static string SelectDetailActiveCue(MyThrust thruster)
        {
            MyCubeGrid grid = thruster?.CubeGrid;
            V2ShipSoundGroup group = SelectShipSoundGroup(grid);
            V2ThrusterKind kind = DetectThrusterKind(thruster);

            if (kind == V2ThrusterKind.Prototech)
                return "ShipThrusterPrototech";

            if (kind == V2ThrusterKind.Hydrogen)
                return group == V2ShipSoundGroup.Large ? "ShipLargeThrusterHydrogen" : "ShipSmallThrusterHydrogen";

            if (kind == V2ThrusterKind.Atmospheric)
                return SelectAtmosphericCue(group, grid);

            if (kind == V2ThrusterKind.Ion || kind == V2ThrusterKind.Unknown)
                return group == V2ShipSoundGroup.Large ? "ShipLargeThrusterIon" : "ShipSmallThrusterIon";

            return SelectPrimarySoundFallback(thruster);
        }

        public static string SelectDetailIdleCue(MyThrust thruster)
        {
            MyCubeGrid grid = thruster?.CubeGrid;
            V2ShipSoundGroup group = SelectShipSoundGroup(grid);
            V2ThrusterKind kind = DetectThrusterKind(thruster);

            if (kind == V2ThrusterKind.Prototech)
                return "ShipLargeThrusterIonIdle";

            if (kind == V2ThrusterKind.Hydrogen)
                return group == V2ShipSoundGroup.Large ? "ShipLargeThrusterHydrogenIdle" : "ShipSmallThrusterHydrogenIdle";

            if (kind == V2ThrusterKind.Atmospheric)
                return group == V2ShipSoundGroup.Large ? "ShipLargeThrusterAtmosphericIdle" : "ShipSmallThrusterAtmosphericIdle";

            if (kind == V2ThrusterKind.Ion || kind == V2ThrusterKind.Unknown)
                return group == V2ShipSoundGroup.Large ? "ShipLargeThrusterIonIdle" : "ShipSmallThrusterIonIdle";

            return SelectPrimarySoundFallback(thruster);
        }

        public static bool HasDetailLocalVariant(string cueName)
        {
            switch ((cueName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "shiplargethrusterhydrogen":
                case "shipsmallthrusterhydrogen":
                case "shiplargethrusterhydrogenidle":
                case "shiplargethrusterion":
                case "shipsmallthrusterion":
                case "shiplargethrusterionidle":
                case "shipsmallthrusterionidle":
                case "shiplargethrusteratmosphericslow":
                case "shiplargethrusteratmosphericfast":
                case "shiplargethrusteratmosphericidle":
                case "shipsmallthrusteratmosphericslow":
                case "shipsmallthrusteratmosphericfast":
                case "shipsmallthrusteratmosphericidle":
                case "shipthrusterprototech":
                    return true;
                default:
                    return false;
            }
        }

        public static string SelectPrimarySoundFallback(MyThrust thruster)
        {
            string primarySound = TryReadPrimarySound(thruster);
            if (!string.IsNullOrWhiteSpace(primarySound))
                return primarySound;

            string subtype = thruster?.BlockDefinition?.Id.SubtypeName ?? string.Empty;
            bool smallGrid = IsSmallGrid(thruster?.CubeGrid);
            bool largeNozzle = subtype.IndexOf("Large", StringComparison.OrdinalIgnoreCase) >= 0;

            if (subtype.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (smallGrid)
                    return largeNozzle ? "SmShipLrgJetHydrogen" : "SmShipSmJetHydrogen";
                return largeNozzle ? "LrgShipLrgJetHydrogen" : "LrgShipSmJetHydrogen";
            }

            if (subtype.IndexOf("Atmospheric", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (smallGrid)
                    return largeNozzle ? "SmShipLrgJetAtmo" : "SmShipSmJetAtmo";
                return "LrgShipSmJetAtmo";
            }

            if (smallGrid)
                return largeNozzle ? "SmShipLrgJet" : "SmShipSmJet";

            return largeNozzle ? "LrgShipLrgJet" : "LrgShipSmJet";
        }

        public static string SelectStateLoopCue(MyCubeGrid grid)
        {
            if (SelectShipSoundGroup(grid) == V2ShipSoundGroup.Large)
                return "ShipLargeRunLoop";

            float speed = 0f;
            try
            {
                speed = grid?.Physics == null ? 0f : (float)grid.Physics.LinearVelocity.Length();
            }
            catch
            {
                speed = 0f;
            }

            float normalized = Math.Max(0f, Math.Min(1f, speed / VanillaFullSpeed));
            if (normalized < 0.33f)
                return "ShipSmallRunSlow";

            return normalized < 0.66f ? "ShipSmallRunMedium" : "ShipSmallRunFast";
        }

        public static V2ShipSoundGroup SelectShipSoundGroup(MyCubeGrid grid)
        {
            float mass = TryGetGridMass(grid);
            if (mass >= LargeShipMinWeight)
                return V2ShipSoundGroup.Large;

            if (mass > 0f)
                return V2ShipSoundGroup.Small;

            return IsSmallGrid(grid) ? V2ShipSoundGroup.Small : V2ShipSoundGroup.Large;
        }

        private static string SelectAtmosphericCue(V2ShipSoundGroup group, MyCubeGrid grid)
        {
            float normalizedSpeed = CalculateNormalizedSpeed(grid);
            if (group == V2ShipSoundGroup.Large)
                return normalizedSpeed >= 0.66f ? "ShipLargeThrusterAtmosphericFast" : "ShipLargeThrusterAtmosphericSlow";

            return normalizedSpeed >= 0.66f ? "ShipSmallThrusterAtmosphericFast" : "ShipSmallThrusterAtmosphericSlow";
        }

        private static V2ThrusterKind DetectThrusterKind(MyThrust thruster)
        {
            string type = TryReadMember(thruster?.BlockDefinition, "ThrusterType")?.ToString() ?? string.Empty;
            string subtype = thruster?.BlockDefinition?.Id.SubtypeName ?? string.Empty;
            string primary = TryReadPrimarySound(thruster) ?? string.Empty;
            string text = type + " " + subtype + " " + primary;

            if (Contains(text, "Prototech"))
                return V2ThrusterKind.Prototech;
            if (Contains(text, "Hydrogen") || Contains(text, "Hydro"))
                return V2ThrusterKind.Hydrogen;
            if (Contains(text, "Atmospheric") || Contains(text, "Atmo"))
                return V2ThrusterKind.Atmospheric;
            if (Contains(text, "Ion"))
                return V2ThrusterKind.Ion;

            return V2ThrusterKind.Unknown;
        }

        private static bool Contains(string value, string pattern)
        {
            return value != null && value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string TryReadPrimarySound(MyThrust thruster)
        {
            if (thruster?.BlockDefinition == null)
                return null;

            try
            {
                PropertyInfo property = thruster.BlockDefinition.GetType().GetProperty("PrimarySound", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object value = property?.GetValue(thruster.BlockDefinition, null);
                string text = value?.ToString();
                if (string.IsNullOrWhiteSpace(text) || text == "NullOrEmpty")
                    return null;

                return text;
            }
            catch
            {
                return null;
            }
        }

        private static float TryGetGridMass(MyCubeGrid grid)
        {
            object physics = grid?.Physics;
            if (physics == null)
                return 0f;

            object value = TryReadMember(physics, "Mass")
                ?? TryReadMember(TryReadMember(physics, "MassProperties"), "Mass");

            if (value == null)
                return 0f;

            try
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0f;
            }
        }

        private static float CalculateNormalizedSpeed(MyCubeGrid grid)
        {
            try
            {
                if (grid?.Physics == null)
                    return 0f;

                return Math.Max(0f, Math.Min(1f, (float)grid.Physics.LinearVelocity.Length() / VanillaFullSpeed));
            }
            catch
            {
                return 0f;
            }
        }

        private static object TryReadMember(object instance, string name)
        {
            if (instance == null)
                return null;

            try
            {
                PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                    return property.GetValue(instance, null);

                FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSmallGrid(MyCubeGrid grid)
        {
            return grid != null && string.Equals(grid.GridSizeEnum.ToString(), "Small", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal enum V2ShipSoundGroup
    {
        Small,
        Large
    }

    internal enum V2ThrusterKind
    {
        Unknown,
        Ion,
        Hydrogen,
        Atmospheric,
        Prototech
    }
}
