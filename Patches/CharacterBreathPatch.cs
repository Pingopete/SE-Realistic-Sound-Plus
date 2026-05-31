using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using VRage.Game.ModAPI.Interfaces;
using VRage.Utils;

namespace RealisticSoundPlus.Patches
{
    [HarmonyPatch(typeof(MyCharacterBreath), "Update")]
    internal static class CharacterBreathPatch
    {
        private static readonly FieldInfo CharacterField = AccessTools.Field(typeof(MyCharacterBreath), "m_character");

        private static bool _disabled;
        private static int _patchHits;

        private static void Postfix(MyCharacterBreath __instance)
        {
            if (_disabled)
                return;

            try
            {
                MyCharacter character = (MyCharacter)CharacterField.GetValue(__instance);
                if (character == null)
                    return;

                IMyControllableEntity controllable = character as IMyControllableEntity;
                if (controllable == null || controllable.EnabledHelmet)
                    return;

                if (__instance.CurrentState != MyCharacterBreath.State.NoBreath)
                    __instance.CurrentState = MyCharacterBreath.State.NoBreath;

                if (++_patchHits == 1)
                    MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Open-helmet breath suppression is active.");
            }
            catch (Exception ex)
            {
                _disabled = true;
                MyLog.Default.WriteLineAndConsole("[RealisticSoundPlus] Disabling character breath patch after error: " + ex);
            }
        }
    }
}