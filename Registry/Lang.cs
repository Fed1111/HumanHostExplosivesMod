using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// Player-facing text, translatable by anyone (Workshop request 2026-10-03).
    ///
    /// The English text lives in code (Ui below, and each ExplosiveDef's Tooltip* fields). At startup
    /// the mod writes it all to Lang/en.json next to the DLL. A translator copies that file to their
    /// language code (Lang/ru.json, de.json, ...), translates the values and restarts; the mod picks
    /// the file matching the game's language and falls back to English for anything missing. Codes are
    /// the ones HHMM uses for the settings sidecar, indexed by the game's LanguageType.
    ///
    /// Item tooltips carry one entry per game language (TooltipBuilder), so item names and
    /// descriptions follow a language switch in the game's menu by themselves; on-screen messages look
    /// the language up each time they are shown.
    /// </summary>
    internal static class Lang
    {
        /// <summary>By LanguageType: Chinese_S, Chinese_T, English, Russian, Japanese, Korean, French,
        /// German, Polish, Spanish, Italian, Portuguese, Turkish, Thai.</summary>
        internal static readonly string[] Codes =
            { "zh-CN", "zh-TW", "en", "ru", "ja", "ko", "fr", "de", "pl", "es", "it", "pt-PT", "tr", "th" };

        /// <summary>On-screen messages. Keys are stable - translations are keyed by them.</summary>
        private static readonly string[,] Ui =
        {
            { "toast.thrown", "{0} thrown" },
            { "toast.noExplosive", "No explosive in inventory" },
            { "toast.throwCancelled", "Throw cancelled" },
            { "toast.onFire", "You're on fire!" },
            { "toast.mineLive", "It's live - get away from it!" },
            { "toast.noRoomInventory", "No room in your inventory" },
            { "toast.pickedUp", "Picked up {0}" },
            { "toast.tooManyMines", "Too many mines placed ({0}/{1})" },
            { "toast.noRoomForMine", "No room to place a mine here" },
            { "toast.noArmedCharges", "No armed charges in range" },
            { "toast.noCharges", "No charges placed" },
            { "toast.detonateKeyClash", "Remote detonate key {0} clashes with the game's {1} key - change it in the config" },
            { "hint.mineLive", "{0} - LIVE" },
            { "hint.pickUp", "[{0}] Pick up {1}" },
            { "widget.title", "REMOTE CHARGES  x{0}" },
            { "widget.ready", "READY  [{0}] detonate" },
            { "widget.arming", "ARMING..." },
            { "widget.outOfRange", "OUT OF RANGE" },
        };

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>();
        private static readonly Dictionary<string, Dictionary<string, string>> Loaded = new Dictionary<string, Dictionary<string, string>>();
        private static string _dir;

        /// <summary>Builds the English table from code, writes Lang/en.json, and loads every translation present.</summary>
        internal static void Init(string pluginDir, IEnumerable<ExplosiveDef> defs)
        {
            _dir = null;   // AddItem writes en.json only once Init is done
            English.Clear();
            for (int i = 0; i < Ui.GetLength(0); i++)
            {
                English[Ui[i, 0]] = Ui[i, 1];
            }
            foreach (ExplosiveDef def in defs)
            {
                AddItem(def);
            }
            _dir = Path.Combine(pluginDir, "Lang");
            WriteEnglish();

            Loaded.Clear();
            foreach (string code in Codes)
            {
                if (code == "en")
                {
                    continue;
                }
                string path = Path.Combine(_dir, code + ".json");
                if (!File.Exists(path))
                {
                    continue;
                }
                try
                {
                    var table = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                    if (table != null)
                    {
                        Loaded[code] = table;
                        Plugin.Log.LogInfo($"[Lang] loaded {code}.json ({table.Count} entries).");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Lang] {code}.json could not be read (English used instead): {ex.Message}");
                }
            }
        }

        /// <summary>Adds an item registered later than Init (the any-ammo material). English only; translations
        /// are looked up on use, so a translation file already loaded covers it too.</summary>
        internal static void AddItem(ExplosiveDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Tag))
            {
                return;
            }
            English[def.Tag + ".name"] = def.TooltipName ?? "";
            English[def.Tag + ".type"] = def.TooltipType ?? "";
            English[def.Tag + ".description"] = def.TooltipInstruction ?? "";
            if (_dir != null)
            {
                WriteEnglish();
            }
        }

        /// <summary>Text for <paramref name="key"/> in the game's current language.</summary>
        internal static string T(string key, params object[] args)
        {
            return In(CurrentCode(), key, args);
        }

        /// <summary>Text for <paramref name="key"/> in the language with this code, falling back to English.</summary>
        internal static string In(string code, string key, params object[] args)
        {
            string text = null;
            if (code != null && Loaded.TryGetValue(code, out Dictionary<string, string> table))
            {
                table.TryGetValue(key, out text);
            }
            if (string.IsNullOrEmpty(text) && !English.TryGetValue(key, out text))
            {
                text = key;
            }
            if (args == null || args.Length == 0)
            {
                return text;
            }
            try
            {
                return string.Format(text, args);
            }
            catch (FormatException)
            {
                // A translation with a broken {0}: show the English rather than nothing.
                return English.TryGetValue(key, out string en) ? string.Format(en, args) : text;
            }
        }

        internal static string ItemName(ExplosiveDef def) => T(def.Tag + ".name");

        internal static string CurrentCode()
        {
            int index = Language_Mgr.ins != null ? Language_Mgr.ins._LanguageIndex : 2;
            return index >= 0 && index < Codes.Length ? Codes[index] : "en";
        }

        private static void WriteEnglish()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string path = Path.Combine(_dir, "en.json");
                string json = JsonConvert.SerializeObject(English, Formatting.Indented);
                if (!File.Exists(path) || File.ReadAllText(path) != json)
                {
                    File.WriteAllText(path, json);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Lang] could not write Lang/en.json: " + ex.Message);
            }
        }
    }
}
