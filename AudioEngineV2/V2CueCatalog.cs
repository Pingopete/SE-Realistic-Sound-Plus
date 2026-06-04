using System;
using System.Reflection;
using Sandbox.Game.Entities;

namespace RealisticSoundPlus.AudioEngineV2
{
    internal static class V2CueCatalog
    {
        private const float VanillaFullSpeed = 96f;

        public static string SelectDetailCue(MyThrust thruster)
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
            if (!IsSmallGrid(grid))
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

        private static bool IsSmallGrid(MyCubeGrid grid)
        {
            return grid != null && string.Equals(grid.GridSizeEnum.ToString(), "Small", StringComparison.OrdinalIgnoreCase);
        }
    }
}
