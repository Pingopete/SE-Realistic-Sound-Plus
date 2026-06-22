using System;
using System.Reflection;
using HarmonyLib;
using RealisticSoundPlus.AudioEngineV2;
using Sandbox.Game.Entities;
using VRage.Audio;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    // Disabled while the vanilla distance gate is tested through the narrower IsCloseEnough hook below.
    // Leaving the code here makes it easy to re-enable after we isolate the safe insertion point.
    internal static class BlockRangeScalePlaySoundPatch
    {
        private static void Prefix(MyEntity3DSoundEmitter __instance, MyCueId soundId)
        {
            V2BlockRangeScaler.TryPrimeEmitter(__instance, soundId.ToString(), "play");
        }
    }

    [HarmonyPatch]
    internal static class BlockRangeScaleSourceGatePatch
    {
        private static MethodBase _targetMethod;
        private static bool _loggedMissingTarget;

        private static bool Prepare()
        {
            _targetMethod = ResolveTargetMethod();
            if (_targetMethod != null)
                return true;

            if (!_loggedMissingTarget)
            {
                _loggedMissingTarget = true;
                MyLog.Default.WriteLine("[RealisticSoundPlus] Block range source-gate patch skipped: VRage.Audio.MyXAudio2.SourceIsCloseEnoughToPlaySound not found.");
            }

            return false;
        }

        private static MethodBase TargetMethod()
        {
            return _targetMethod ?? ResolveTargetMethod();
        }

        private static void Prefix(MyCueId cueId, ref float? customMaxDistance)
        {
            V2BlockRangeScaler.TryPrimeDistanceGate(cueId.ToString(), ref customMaxDistance, "source-gate");
        }

        private static MethodBase ResolveTargetMethod()
        {
            Type type = AccessTools.TypeByName("VRage.Audio.MyXAudio2");
            if (type == null)
            {
                try
                {
                    type = Assembly.Load("VRage.Audio").GetType("VRage.Audio.MyXAudio2", false);
                }
                catch
                {
                    type = null;
                }
            }

            return type == null ? null : AccessTools.Method(type, "SourceIsCloseEnoughToPlaySound");
        }
    }

    [HarmonyPatch(typeof(MyEntity3DSoundEmitter), "IsCloseEnough")]
    internal static class BlockRangeScaleIsCloseEnoughPatch
    {
        private static void Prefix(MyEntity3DSoundEmitter __instance)
        {
            if (__instance == null)
                return;

            V2BlockRangeScaler.TryPrimeEmitter(__instance, __instance.SoundId.ToString(), "gate");
        }
    }
}
