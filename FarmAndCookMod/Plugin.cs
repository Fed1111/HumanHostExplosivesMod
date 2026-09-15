using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HumanHostFarmAndCook
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.nathanfeddema.humanhostfarmandcook";
        public const string Name = "Human Host Farm and Cook";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<float> GrowthRateMultiplier;
        internal static ConfigEntry<float> CookTimeMultiplier;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            GrowthRateMultiplier = Config.Bind(
                "Farming",
                "GrowthRateMultiplier",
                1f,
                "Multiplier applied to crop growth speed. 2 = crops grow twice as fast.");

            CookTimeMultiplier = Config.Bind(
                "Cooking",
                "CookTimeMultiplier",
                1f,
                "Multiplier applied to cooking duration. 0.5 = food cooks in half the time.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();

            Log.LogInfo(
                $"{Name} v{Version} loaded. GrowthRateMultiplier = {GrowthRateMultiplier.Value}, " +
                $"CookTimeMultiplier = {CookTimeMultiplier.Value}");
        }
    }
}
