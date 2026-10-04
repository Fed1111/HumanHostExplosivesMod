using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// Sets Tool_Interacter's own WeaponPosData.localPosOfHand directly instead of touching any
    /// transform. Two earlier attempts (editing the mesh's local position directly, then
    /// inserting a static offset node above it) both had zero visible effect - not "wrong
    /// amount", genuinely unchanged - because Tool_Interacter.Equip_This_Tool() does a hard
    /// assignment on every equip:
    ///
    ///   base.transform.SetParent(_CharBase._EquipBones.rightHand);
    ///   base.transform.localPosition = _weaponPosData.localPosOfHand;
    ///
    /// `base.transform` is the same GameObject Tool_Interacter lives on - i.e. the model root -
    /// so any one-time position edit gets overwritten, and any wrapper parented above it gets
    /// severed when that transform is re-parented straight to the hand bone. The actual
    /// authority is _weaponPosData.localPosOfHand.
    ///
    /// A third attempt tried nudging that value with small per-axis deltas (guessing which axis
    /// was "left/right" etc) - three different single-axis guesses each moved the model in some
    /// OTHER direction (distance, then vertical) without ever correcting the actual left/right
    /// problem, which means localPosOfHand's axes don't align with character-perspective
    /// directions in any simple way (it's evaluated in the hand bone's own, presumably rotated,
    /// local space). Guessing further deltas wasn't converging, so this now REPLACES the value
    /// outright with an absolute target (ExplosiveDef.HandOffset) instead of adding to whatever
    /// the template weapon's own tuned value was - starting from the hand bone's own local
    /// origin (0,0,0) is a much safer baseline than compounding onto an unknown pickaxe-shaped
    /// starting point. The original value is logged before being overwritten, in case the exact
    /// number is useful for further tuning.
    ///
    /// WeaponPosDatas/WeaponPosData are internal classes nested inside the abstract Tool_Interacter
    /// (Hand_Tools.dll), invisible to us at compile time, so this goes through reflection.
    /// </summary>
    internal static class HandOffsetFixer
    {
        private static readonly Type WeaponPosDatasType = AccessTools.Inner(typeof(Tool_Interacter), "WeaponPosDatas");
        private static readonly Type WeaponPosDataType = WeaponPosDatasType != null ? AccessTools.Inner(WeaponPosDatasType, "WeaponPosData") : null;
        private static readonly FieldInfo WeaponPosDatasArrayField = AccessTools.Field(typeof(Tool_Interacter), "_WeaponPosDatas");
        private static readonly FieldInfo DataField = WeaponPosDatasType != null ? AccessTools.Field(WeaponPosDatasType, "data") : null;
        private static readonly FieldInfo LocalPosOfHandField = WeaponPosDataType != null ? AccessTools.Field(WeaponPosDataType, "localPosOfHand") : null;
        private static readonly FieldInfo LocalScaleOfHandField = WeaponPosDataType != null ? AccessTools.Field(WeaponPosDataType, "localScaleOfHand") : null;

        private static bool ReflectionReady =>
            WeaponPosDatasArrayField != null && DataField != null && LocalPosOfHandField != null;

        internal static void Apply(GameObject clone, Vector3 targetLocalPos, string tag)
        {
            if (!ReflectionReady)
            {
                Plugin.Log.LogWarning($"[Registry] '{tag}': Tool_Interacter.WeaponPosData reflection layout changed; hand position not applied.");
                return;
            }

            Tool_Interacter toolInteracter = clone.GetComponentInChildren<Tool_Interacter>(true);
            if (toolInteracter == null)
            {
                Plugin.Log.LogWarning($"[Registry] '{tag}': no Tool_Interacter found on cloned model; hand position not applied.");
                return;
            }

            var weaponPosDatas = (Array)WeaponPosDatasArrayField.GetValue(toolInteracter);
            if (weaponPosDatas == null || weaponPosDatas.Length == 0)
            {
                Plugin.Log.LogWarning($"[Registry] '{tag}': Tool_Interacter._WeaponPosDatas is empty; hand position not applied.");
                return;
            }

            int patched = 0;
            foreach (object entry in weaponPosDatas)
            {
                object data = DataField.GetValue(entry);
                if (data == null)
                {
                    continue;
                }
                var original = (Vector3)LocalPosOfHandField.GetValue(data);
                LocalPosOfHandField.SetValue(data, targetLocalPos);
                patched++;

                // Untouched until now - still whatever scale the pickaxe's own tuning used. The
                // grenade rendering completely absent (not just mispositioned) after the position
                // fix raises the possibility that scale, not position, is the real remaining
                // problem: a very small tuned scale could render the mesh imperceptibly tiny.
                // Logged either way; only forced to a sane default (1,1,1) if it looks degenerate
                // (near-zero on any axis), so a legitimate non-uniform scale isn't clobbered blind.
                string scaleNote = "left as-is";
                if (LocalScaleOfHandField != null)
                {
                    var originalScale = (Vector3)LocalScaleOfHandField.GetValue(data);
                    if (Mathf.Abs(originalScale.x) < 0.05f || Mathf.Abs(originalScale.y) < 0.05f || Mathf.Abs(originalScale.z) < 0.05f)
                    {
                        LocalScaleOfHandField.SetValue(data, Vector3.one);
                        scaleNote = $"original {originalScale} looked near-zero, forced to (1,1,1)";
                    }
                    else
                    {
                        scaleNote = $"original {originalScale}, left as-is";
                    }
                }

                Plugin.Log.LogInfo($"[Registry] '{tag}': WeaponPosData entry {patched} original localPosOfHand={original}, replaced with {targetLocalPos}. Scale: {scaleNote}.");
            }
        }

        /// <summary>
        /// WeaponPosData (above) governs THIRD-PERSON hand-bone attachment - not what the player
        /// sees for their own character while playing in first person, which is why every fix
        /// based on it had zero visible effect no matter what value was tried. The actual
        /// first-person viewmodel position comes from a completely separate system:
        /// Weapon_Melee copies Tool_Interacter._1stCamMod's six Vector3s directly into
        /// CamController.ins._LookUpCamLoPos/_LookMidCamLoPos/_LookDownCamLoPos (+ crouch _C
        /// variants) on equip, and CamController.Modify_1st_Person_Cam_LoPos() interpolates
        /// between them every frame to position the held-item camera transform. _1stCamMod and
        /// its FirstPersonCamMod struct are both public (no reflection needed here, unlike
        /// WeaponPosData) - this replaces all six entries with one absolute target, same
        /// replace-not-add approach, for the same reason: the pickaxe's own tuned values aren't a
        /// meaningful starting point for a differently-shaped held object.
        /// </summary>
        internal static void ApplyFirstPerson(GameObject clone, Vector3 target, string tag)
        {
            Tool_Interacter toolInteracter = clone.GetComponentInChildren<Tool_Interacter>(true);
            if (toolInteracter == null)
            {
                Plugin.Log.LogWarning($"[Registry] '{tag}': no Tool_Interacter found on cloned model; first-person position not applied.");
                return;
            }

            Tool_Interacter.FirstPersonCamMod original = toolInteracter._1stCamMod;
            toolInteracter._1stCamMod = new Tool_Interacter.FirstPersonCamMod
            {
                _1stLookUpPosMod = target,
                _1stLookMidPosMod = target,
                _1stLookDownPosMod = target,
                _1stLookUpPosMod_C = target,
                _1stLookMidPosMod_C = target,
                _1stLookDownPosMod_C = target,
            };

            Plugin.Log.LogInfo(
                $"[Registry] '{tag}': first-person cam mod original (up={original._1stLookUpPosMod}, mid={original._1stLookMidPosMod}, " +
                $"down={original._1stLookDownPosMod}), all 6 entries replaced with {target}.");
        }
    }
}
