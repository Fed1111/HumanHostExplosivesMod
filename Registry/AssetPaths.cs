using System;
using System.Collections.Generic;
using System.IO;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// Finds shipped art no matter which copy of the DLL BepInEx happened to load. Real reports
    /// (Workshop comments, 2026-09-18..21, see MOD_CONVENTIONS.md #40): players who installed the item
    /// through both the in-game mod browser and the Human Host Mod Manager, or who copied the DLL by
    /// hand, ended up with a second HumanHostExplosives.dll at the plugins root. BepInEx loads only one
    /// copy, and when that is the loose one, "beside the DLL" holds no Grenade/ or Nailbomb/ folder.
    /// So: look beside the DLL first, then in every HumanHostExplosives* folder under the plugins
    /// root, one and two levels deep. Same shape as SuppressorDef.AssetRoots in the Suppressor mod.
    /// </summary>
    internal static class AssetPaths
    {
        private static string[] _roots;

        internal static string[] Roots
        {
            get
            {
                if (_roots != null) return _roots;
                string pluginDir = Path.GetDirectoryName(typeof(AssetPaths).Assembly.Location);
                var roots = new List<string> { pluginDir };
                try
                {
                    foreach (string dir in Directory.GetDirectories(BepInEx.Paths.PluginPath))
                    {
                        AddIfOurs(roots, dir, pluginDir);
                        foreach (string sub in Directory.GetDirectories(dir))
                        {
                            AddIfOurs(roots, sub, pluginDir);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Registry] asset folder scan failed: {ex.Message}");
                }
                _roots = roots.ToArray();
                if (_roots.Length > 1)
                {
                    Plugin.Log.LogInfo($"[Registry] asset roots: {string.Join(" | ", _roots)}");
                }
                return _roots;
            }
        }

        private static void AddIfOurs(List<string> roots, string dir, string pluginDir)
        {
            string name = Path.GetFileName(dir);
            if (name.StartsWith("HumanHostExplosives", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), Path.GetFullPath(pluginDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && !roots.Contains(dir))
            {
                roots.Add(dir);
            }
        }

        /// <summary>First root where the relative file exists, else the plugin-folder path (so
        /// "not found" messages still name a sensible location).</summary>
        internal static string Resolve(string relative)
        {
            foreach (string root in Roots)
            {
                string candidate = Path.Combine(root, relative);
                if (File.Exists(candidate)) return candidate;
            }
            return Path.Combine(Roots[0], relative);
        }
    }
}
