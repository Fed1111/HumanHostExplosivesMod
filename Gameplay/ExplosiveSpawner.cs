using HumanHostExplosives.Registry;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Spawns a physical thrown explosive prop from the camera position. Shared by the debug
    /// keybind (Plugin.cs) and the real equip-and-throw path (ExplosiveUseHook.cs), so both
    /// throw the exact same way.
    /// </summary>
    internal static class ExplosiveSpawner
    {
        internal static GameObject Throw(ExplosiveDef def, Transform camTrans, C_Controller_Base thrower, float throwSpeed = 8f, float upwardArc = 2f)
        {
            if (def == null || camTrans == null)
            {
                return null;
            }
            if (def.RuntimeMesh == null || def.RuntimeMaterial == null)
            {
                Plugin.Log.LogWarning($"[Explosive] '{def?.Tag}' has no loaded mesh/material; cannot throw.");
                return null;
            }

            var go = new GameObject("HumanHostExplosives_" + def.Tag);
            go.transform.position = GetOrigin(camTrans, thrower);
            go.transform.rotation = Quaternion.identity;

            var filter = go.AddComponent<MeshFilter>();
            filter.mesh = def.RuntimeMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = def.RuntimeMaterial;

            var collider = go.AddComponent<SphereCollider>();
            collider.radius = 0.045f;

            // The grenade now spawns at the hand bone, which is INSIDE the player's capsule and
            // ragdoll colliders - without this it immediately collides with the thrower and either
            // drops at their feet or gets flung sideways. Vanilla does the same for arrows
            // (Weapon_Range: Physics.IgnoreCollision(_BowSphereCol, collider, true)).
            // This only suppresses physics contact; the explosion's own overlap check is separate,
            // so self-damage still works exactly as configured.
            IgnoreThrowerCollisions(collider, thrower);

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.4f;
            // Default discrete collision detection can tunnel a small, fast collider straight
            // through thin ground/terrain geometry between fixed-update steps - at 28 m/s (full
            // charge) that's ~0.5m covered per step, easily enough to skip past a thin block
            // without ever registering the hit. Continuous dynamic detection sweep-tests instead.
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.velocity = camTrans.forward * throwSpeed + Vector3.up * upwardArc;

            switch (def.Kind)
            {
                case ExplosiveKind.Grenade:
                    var grenade = go.AddComponent<GrenadeProjectile>();
                    grenade.MaxDamage = Plugin.ExplosionDamage.Value;
                    grenade.Thrower = thrower;
                    break;
                case ExplosiveKind.Molotov:
                    var molotov = go.AddComponent<MolotovProjectile>();
                    molotov.Thrower = thrower;
                    break;
            }

            Plugin.Log.LogInfo($"[Explosive] '{def.Tag}' thrown.");
            return go;
        }

        /// <summary>
        /// Suppresses physics contact between the thrown projectile and the thrower's own body -
        /// capsule, push capsules, hand colliders and every ragdoll bone collider.
        /// </summary>
        private static void IgnoreThrowerCollisions(Collider projectile, C_Controller_Base thrower)
        {
            if (projectile == null || thrower == null)
            {
                return;
            }

            Collider[] own = thrower.GetComponentsInChildren<Collider>(includeInactive: true);
            int ignored = 0;
            for (int i = 0; i < own.Length; i++)
            {
                Collider c = own[i];
                // Triggers never generate contacts, so skipping them keeps the pair count down -
                // and the game leans on trigger volumes heavily (pickup, push, melee anti-wall).
                if (c == null || c.isTrigger)
                {
                    continue;
                }
                Physics.IgnoreCollision(projectile, c, ignore: true);
                ignored++;
            }

            if (Plugin.EnableDiagnostics.Value)
            {
                Plugin.Log.LogInfo($"[Explosive] ignoring collision against {ignored} thrower colliders.");
            }
        }

        /// <summary>
        /// Where the projectile leaves from. Preferably the right hand bone - the same transform
        /// the game parents held weapons to (Tool_Interacter does
        /// transform.SetParent(_CharBase._EquipBones.rightHand)) - so the grenade leaves the hand
        /// that just threw it.
        ///
        /// Falls back to a camera-space offset if the rig is unavailable. That fallback is only
        /// ever right in first person: in third person the camera sits behind the player, so a
        /// camera-relative origin starts the grenade well behind the character.
        ///
        /// The offsets are applied in the hand's own space, so they track the throw animation
        /// rather than staying fixed relative to the view.
        /// </summary>
        private static Vector3 GetOrigin(Transform camTrans, C_Controller_Base thrower)
        {
            // EquipBones is a struct, so there is nothing to null-check on it - only the Transform
            // inside it, which is unassigned on rigs that never equip anything.
            Transform hand = thrower != null ? thrower._EquipBones.rightHand : null;

            if (hand != null)
            {
                // Anchor at the hand, but offset in the PLAYER's space, not the hand's. A hand
                // bone's local axes are rotated unintuitively (the same trap as
                // WeaponPosData.localPosOfHand, where +Y moves the model down), and they also spin
                // through the throw, so a hand-space offset would not hold still. Player space
                // means right/up/forward are the character's own directions and stay stable.
                Transform root = thrower.transform;
                return hand.position
                    + root.right * Plugin.ThrowOriginRight.Value
                    + root.up * Plugin.ThrowOriginUp.Value
                    + root.forward * Plugin.ThrowOriginForward.Value;
            }

            return camTrans.position
                + camTrans.forward * Plugin.ThrowOriginForward.Value
                + camTrans.right * Plugin.ThrowOriginRight.Value
                + camTrans.up * Plugin.ThrowOriginUp.Value;
        }
    }
}
