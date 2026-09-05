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
            go.transform.position = camTrans.position + camTrans.forward * 0.5f;
            go.transform.rotation = Quaternion.identity;

            var filter = go.AddComponent<MeshFilter>();
            filter.mesh = def.RuntimeMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = def.RuntimeMaterial;

            var collider = go.AddComponent<SphereCollider>();
            collider.radius = 0.045f;

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.4f;
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
    }
}
