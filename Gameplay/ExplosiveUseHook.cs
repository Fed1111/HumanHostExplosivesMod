using HarmonyLib;
using HumanHostExplosives.Registry;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// The whole throw mechanic. The game already has a first-class "equip a stackable
    /// consumable to the hand, LMB uses it, consume 1 from the stack" pipeline (used by
    /// food/bandages/etc) - a grenade fits that shape exactly, so there's no need to build a
    /// custom weapon rig. Two Harmony hooks plus a per-frame poll for the hold-to-charge part:
    ///
    ///  - Is_Icon_Usable: makes the game treat our tag as "usable" at all (this gates whether
    ///    the "LMB" prompt appears when the item is equipped, and whether Trigger_Use_For_Belt_Slot
    ///    even gets called from UI_Control.Update()).
    ///  - Trigger_Use_For_Belt_Slot: only fires on the initial mouse-down (the vanilla dispatch
    ///    in UI_Control.Update() is itself gated on GetMouseButtonDown, not GetMouseButton), so it
    ///    starts a charge instead of throwing immediately.
    ///  - PollCharge: called every frame from Plugin.Update() (the game has no "held" concept for
    ///    belt items, so nothing else will drive this) - tracks how long LMB has been held and
    ///    throws with a charge-scaled speed on release.
    /// </summary>
    internal static class ExplosiveUseHook
    {
        private static Slot_Info _chargingSlot;
        private static ExplosiveDef _chargingDef;
        private static float _chargeStartTime;

        [HarmonyPatch(typeof(Item_Slot_Mgr), nameof(Item_Slot_Mgr.Is_Icon_Usable))]
        private static class IsIconUsablePatch
        {
            private static void Postfix(Icon_Info iconPrefab, ref bool __result)
            {
                if (!__result && iconPrefab != null && ExplosiveItemRegistry.FindByTag(iconPrefab._Tag) != null)
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(Item_Slot_Mgr), nameof(Item_Slot_Mgr.Trigger_Use_For_Belt_Slot))]
        private static class TriggerUsePatch
        {
            private static void Postfix(Slot_Info slot)
            {
                if (slot == null || slot._iconInfoPrefab == null)
                {
                    return;
                }

                ExplosiveDef def = ExplosiveItemRegistry.FindByTag(slot._iconInfoPrefab._Tag);
                if (def == null)
                {
                    return;
                }

                _chargingSlot = slot;
                _chargingDef = def;
                _chargeStartTime = Time.time;

                if (Plugin.EnableDiagnostics.Value)
                {
                    Plugin.Log.LogInfo($"[ChargeDiag] charge started for '{def.Tag}' at t={_chargeStartTime:F2}");
                }
            }
        }

        /// <summary>Call once per frame. No-op unless a charge is in progress.</summary>
        internal static void PollCharge()
        {
            if (_chargingSlot == null)
            {
                return;
            }

            // Cancel if the slot's contents changed out from under us (consumed/swapped/dropped
            // elsewhere, or the player equipped something else into this same belt slot) or the
            // player switched to a different belt slot entirely while still holding LMB down.
            bool stillOurs =
                !_chargingSlot._IsEmptySlot &&
                _chargingSlot._iconInfoPrefab != null &&
                ExplosiveItemRegistry.FindByTag(_chargingSlot._iconInfoPrefab._Tag) == _chargingDef &&
                (UI_Control.ins == null || UI_Control.ins.selected_Belt_Slot == _chargingSlot);

            if (!stillOurs)
            {
                if (Plugin.EnableDiagnostics.Value)
                {
                    Plugin.Log.LogInfo(
                        $"[ChargeDiag] charge cancelled (slot no longer ours) after {Time.time - _chargeStartTime:F2}s - " +
                        $"emptySlot={_chargingSlot._IsEmptySlot}, hasIcon={_chargingSlot._iconInfoPrefab != null}, " +
                        $"selectedMatches={UI_Control.ins == null || UI_Control.ins.selected_Belt_Slot == _chargingSlot}");
                }
                CancelCharge();
                return;
            }

            if (Input.GetMouseButtonUp(0))
            {
                ThrowAndConsume();
                return;
            }

            if (!Input.GetMouseButton(0))
            {
                // Button released without an Up event this frame catching it (alt-tab, focus
                // loss, etc) - cancel safely rather than throw on a stale charge next time we see it.
                if (Plugin.EnableDiagnostics.Value)
                {
                    Plugin.Log.LogInfo($"[ChargeDiag] charge cancelled (button not held, no Up event seen) after {Time.time - _chargeStartTime:F2}s");
                }
                CancelCharge();
            }
        }

        private static void ThrowAndConsume()
        {
            Slot_Info slot = _chargingSlot;
            ExplosiveDef def = _chargingDef;
            float heldSeconds = Time.time - _chargeStartTime;
            float chargeFraction = Mathf.Clamp01(heldSeconds / Plugin.MaxChargeSeconds.Value);
            float throwSpeed = Mathf.Lerp(Plugin.MinThrowSpeed.Value, Plugin.MaxThrowSpeed.Value, chargeFraction);

            if (Plugin.EnableDiagnostics.Value)
            {
                Plugin.Log.LogInfo($"[ChargeDiag] throwing '{def.Tag}': held={heldSeconds:F2}s, chargeFraction={chargeFraction:F2}, throwSpeed={throwSpeed:F1}");
            }

            CancelCharge();

            Transform camTrans = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (camTrans == null)
            {
                return;
            }

            // Play the throw animation and hold the projectile back until the hand actually opens.
            // Play() returns 0 when there is no animation (bundle missing, feature disabled, wrong
            // Unity version), in which case we spawn immediately exactly as before.
            float animDuration = ThrowAnimation.Play(Plugin.ThrowAnimationSpeed.Value);
            if (animDuration > 0f && Plugin.Instance != null)
            {
                float releaseDelay = animDuration * Mathf.Clamp01(Plugin.ReleaseNormalized.Value);
                Plugin.Instance.StartCoroutine(SpawnAtRelease(releaseDelay, def, throwSpeed));
            }
            else
            {
                ExplosiveSpawner.Throw(def, camTrans, Player_Input.ins, throwSpeed);
            }

            Item_Slot_Mgr itemSlotMgr = Item_Slot_Mgr.Ins;
            itemSlotMgr.Item_Stack_Minus_1(slot);

            // Item_Stack_Minus_1 -> Clear_Slot_Info never removes the game object welded to the
            // player's hand (_playerInput._On_Hand_Item) - only DropItem/swap paths do that
            // normally. Without this, throwing your last grenade leaves a ghost grenade in hand.
            //
            // But Del_OnHand_Slot_Item ends with _UpperBodyLayer.StartFade(0f, 0.25f) and sends
            // Enable_BareHand - i.e. it tears down the exact layer the throw animation is playing
            // on, and zeroes the arm IK. Calling it here would kill the animation mid-swing on the
            // LAST grenade of a stack (and only then, since it is stack-empty gated), which is why
            // the last throw used to snap back and launch from the chest. Defer it until the
            // animation has played out; the fade then lands where it would naturally.
            if (string.IsNullOrEmpty(slot._StackText))
            {
                if (animDuration > 0f && Plugin.Instance != null)
                {
                    Plugin.Instance.StartCoroutine(PutAwayAfter(animDuration, slot));
                }
                else
                {
                    itemSlotMgr.Del_OnHand_Slot_Item(slot);
                    itemSlotMgr.Hide_UseClickIcon();
                }
            }
        }

        /// <summary>
        /// Runs the empty-stack hand cleanup once the throw animation has finished, so it does not
        /// fade out the upper-body layer while the throw is still playing.
        /// </summary>
        private static System.Collections.IEnumerator PutAwayAfter(float delay, Slot_Info slot)
        {
            yield return new WaitForSeconds(delay);

            Item_Slot_Mgr mgr = Item_Slot_Mgr.Ins;
            if (mgr == null || slot == null)
            {
                yield break;
            }

            // Re-check: the player may have picked more up, or swapped slots, during the throw.
            if (!string.IsNullOrEmpty(slot._StackText))
            {
                yield break;
            }

            mgr.Del_OnHand_Slot_Item(slot);
            mgr.Hide_UseClickIcon();
        }

        /// <summary>
        /// Spawns the projectile partway through the throw animation, at the frame the hand opens.
        /// The camera transform is re-read at spawn time rather than captured at release, so the
        /// player can still steer the throw during the windup.
        /// </summary>
        private static System.Collections.IEnumerator SpawnAtRelease(
            float delay, ExplosiveDef def, float throwSpeed)
        {
            yield return new WaitForSeconds(delay);

            Transform camTrans = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (camTrans == null || Player_Input.ins == null)
            {
                yield break;
            }

            ExplosiveSpawner.Throw(def, camTrans, Player_Input.ins, throwSpeed);
        }

        private static void CancelCharge()
        {
            _chargingSlot = null;
            _chargingDef = null;
        }
    }
}
