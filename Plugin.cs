using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.nathanfeddema.humanhostexplosives";
        public const string Name = "Human Host Explosives";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<float> ExplosionRadius;
        internal static ConfigEntry<float> ExplosionDamage;

        private Harmony _harmony;

        private Mesh _grenadeMesh;
        private Material _grenadeMaterial;

        private void Awake()
        {
            Log = Logger;

            ExplosionRadius = Config.Bind(
                "Explosives",
                "ExplosionRadius",
                5f,
                "Radius in meters of the explosion effect.");

            ExplosionDamage = Config.Bind(
                "Explosives",
                "ExplosionDamage",
                120f,
                "Damage applied at the center of the explosion, falling off linearly to zero at the edge of ExplosionRadius.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();

            LoadGrenadeAssets();

            Log.LogInfo($"{Name} v{Version} loaded. ExplosionRadius = {ExplosionRadius.Value}, ExplosionDamage = {ExplosionDamage.Value}");
        }

        private void LoadGrenadeAssets()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string objPath = Path.Combine(pluginDir, "Grenade", "grenade.obj");
            string pngPath = Path.Combine(pluginDir, "Grenade", "grenade.png");

            if (!File.Exists(objPath) || !File.Exists(pngPath))
            {
                Log.LogWarning($"Grenade assets not found at '{objPath}'. Debug spawn (press G) disabled.");
                return;
            }

            _grenadeMesh = ObjLoader.LoadMesh(objPath);
            Texture2D texture = TextureLoader.LoadPng(pngPath);

            Shader shader = Shader.Find("HDRP/Lit");
            if (shader != null)
            {
                _grenadeMaterial = new Material(shader);
                _grenadeMaterial.SetTexture("_BaseColorMap", texture);
            }
            else
            {
                _grenadeMaterial = new Material(Shader.Find("Standard"));
                _grenadeMaterial.mainTexture = texture;
            }

            Log.LogInfo($"Grenade mesh loaded: {_grenadeMesh.vertexCount} verts, shader={_grenadeMaterial.shader.name}");
        }

        private void Update()
        {
            if (_grenadeMesh != null && Input.GetKeyDown(KeyCode.G))
            {
                SpawnTestGrenade();
            }
        }

        private void SpawnTestGrenade()
        {
            Transform camTrans = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (camTrans == null)
            {
                Log.LogWarning("No camera found; cannot spawn test grenade.");
                return;
            }

            var go = new GameObject("HumanHostExplosives_Grenade");
            go.transform.position = camTrans.position + camTrans.forward * 0.5f;
            go.transform.rotation = Quaternion.identity;

            var filter = go.AddComponent<MeshFilter>();
            filter.mesh = _grenadeMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _grenadeMaterial;

            var collider = go.AddComponent<SphereCollider>();
            collider.radius = 0.045f;

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.4f;
            rb.velocity = camTrans.forward * 8f + Vector3.up * 2f;

            var grenade = go.AddComponent<GrenadeProjectile>();
            grenade.MaxDamage = ExplosionDamage.Value;
            grenade.Thrower = Player_Input.ins;

            Log.LogInfo("[Grenade] Test grenade thrown.");
        }
    }
}
