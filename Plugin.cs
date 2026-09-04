using BepInEx;
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

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{Name} v{Version} loaded.");
        }
    }
}
