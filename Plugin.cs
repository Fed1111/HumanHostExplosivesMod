using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

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

        private void Awake()
        {
            Log = Logger;

            ExplosionRadius = Config.Bind(
                "Explosives",
                "ExplosionRadius",
                5f,
                "Radius in meters of the explosion effect.");

            Log.LogInfo($"{Name} v{Version} loaded. ExplosionRadius = {ExplosionRadius.Value}");
        }
    }
}
