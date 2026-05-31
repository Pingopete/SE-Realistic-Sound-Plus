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
        private const float AudioCurveExponent = 0.72f;
        private const float ControlInfluence = 0.3f;
        private const float ThrustInfluence = 0.7f;
        private const float MinimumShipPresence = 0.35f;
        private const float QuietShipForceLog10 = 4.0f;
        private const float LoudShipForceLog10 = 7.0f;
        private const float NormalizedVectorMagnitude = 1.7320508f;

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

            float thrustLoad = Clamp01(activeForce / maxForce);
            float controlLoad = GetControlLoad(thrusters, maxForce);
            float requestedLoad = Clamp01(thrustLoad * ThrustInfluence + Math.Max(thrustLoad, controlLoad) * ControlInfluence);
            float shapedLoad = Clamp01((float)Math.Pow(requestedLoad, AudioCurveExponent));
            float shipPresence = CalculateShipPresence(maxForce);

            return Clamp01(shapedLoad * shipPresence);
        }

        private static float GetControlLoad(MyEntityThrustComponent thrusters, float maxForce)
        {
            float control = NormalizeCommandVector(thrusters.ControlThrust, maxForce);
            float autopilot = thrusters.AutopilotEnabled ? NormalizeCommandVector(thrusters.AutoPilotControlThrust, maxForce) : 0f;
            return Math.Max(control, autopilot);
        }

        private static float NormalizeCommandVector(Vector3 command, float maxForce)
        {
            float magnitude = command.Length();
            if (magnitude <= 0f)
                return 0f;

            if (magnitude > NormalizedVectorMagnitude * 2f)
                return Clamp01(magnitude / maxForce);

            return Clamp01(magnitude / NormalizedVectorMagnitude);
        }

        private static float CalculateShipPresence(float maxForce)
        {
            float forceLog = (float)Math.Log10(Math.Max(maxForce, 1f));
            float normalized = Clamp01((forceLog - QuietShipForceLog10) / (LoudShipForceLog10 - QuietShipForceLog10));
            return MinimumShipPresence + (1f - MinimumShipPresence) * normalized;
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
