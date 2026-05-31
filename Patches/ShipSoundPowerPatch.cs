using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using VRage.Utils;
using VRageMath;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyShipSoundComponent), "UpdateSpeedBasedShipSound")]
    internal static class ShipSoundPowerPatch
    {
        private const float PowerChangeSpeedUp = 0.006666667f;
        private const float PowerChangeSpeedDown = 0.01f;
        private const float MinimumAudibleForce = 10f;
        private const float AudioCurveExponent = 0.65f;
        private const float PerceptualGain = 1.25f;

        private static readonly FieldInfo ShipThrustersField = AccessTools.Field(typeof(MyShipSoundComponent), "m_shipThrusters");
        private static readonly FieldInfo CurrentPowerField = AccessTools.Field(typeof(MyShipSoundComponent), "m_shipCurrentPower");
        private static readonly FieldInfo CurrentPowerTargetField = AccessTools.Field(typeof(MyShipSoundComponent), "m_shipCurrentPowerTarget");
        private static readonly FieldInfo MaxPositiveThrustField = AccessTools.Field(typeof(MyEntityThrustComponent), "m_totalMaxPositiveThrust");
        private static readonly FieldInfo MaxNegativeThrustField = AccessTools.Field(typeof(MyEntityThrustComponent), "m_totalMaxNegativeThrust");

        private static bool _disabled;
        private static int _patchHits;

        private static void Postfix(MyShipSoundComponent __instance)
        {
            if (_disabled)
                return;

            try
            {
                var thrusters = (MyEntityThrustComponent)ShipThrustersField.GetValue(__instance);
                if (thrusters == null || !thrusters.HasThrust)
                    return;

                float targetPower = CalculateContinuousPower(thrusters);
                float currentPower = (float)CurrentPowerField.GetValue(__instance);
                float nextPower = MoveTowards(currentPower, targetPower, targetPower > currentPower ? PowerChangeSpeedUp : PowerChangeSpeedDown);

                CurrentPowerTargetField.SetValue(__instance, targetPower);
                CurrentPowerField.SetValue(__instance, nextPower);

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Continuous thruster audio power is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling ship sound power patch after error: " + ex);
            }
        }

        private static float CalculateContinuousPower(MyEntityThrustComponent thrusters)
        {
            Vector3 finalThrust = thrusters.FinalThrust;
            float activeForce = finalThrust.Length();
            if (activeForce < MinimumAudibleForce)
                return 0f;

            Vector3 maxPositive = (Vector3)MaxPositiveThrustField.GetValue(thrusters);
            Vector3 maxNegative = (Vector3)MaxNegativeThrustField.GetValue(thrusters);
            float maxForce = CombinedAxisMagnitude(maxPositive, maxNegative);
            if (maxForce <= MinimumAudibleForce)
                return 0f;

            float normalized = Clamp01(activeForce / maxForce * PerceptualGain);
            return Clamp01((float)Math.Pow(normalized, AudioCurveExponent));
        }

        private static float CombinedAxisMagnitude(Vector3 positive, Vector3 negative)
        {
            float x = Math.Max(Math.Abs(positive.X), Math.Abs(negative.X));
            float y = Math.Max(Math.Abs(positive.Y), Math.Abs(negative.Y));
            float z = Math.Max(Math.Abs(positive.Z), Math.Abs(negative.Z));
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }

        private static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Math.Abs(target - current) <= maxDelta)
                return target;

            return current + Math.Sign(target - current) * maxDelta;
        }

        private static float Clamp01(float value)
        {
            if (value <= 0f)
                return 0f;

            return value >= 1f ? 1f : value;
        }
    }
}
