using System.Collections.Generic;
using System.Reflection;
using HumanHostExplosives.Registry;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Remote charges (demolition block and directional anti-personnel charge). They are placed,
    /// saved, picked up and shot exactly like mines (they ARE PlacedMines, with Def.RemoteDetonated),
    /// but never trigger on their own. There is no detonator item: while any are placed, a small HUD
    /// widget shows the count and state, and one key sets off every armed charge in range - a
    /// beep-beep, then the blasts chained a fraction of a second apart.
    /// </summary>
    internal static class RemoteCharges
    {
        private static bool _conflictChecked;
        private static GUIStyle _title, _state;
        private static Texture _icon;

        internal static void TickInput()
        {
            if (MineManager.Count == 0 || Player_Input.ins == null)
            {
                return;
            }
            KeyCode key = Plugin.DetonateKey.Value;
            if (key == KeyCode.None || !Input.GetKeyDown(key))
            {
                return;
            }
            UI_Control ui = UI_Control.ins;
            if (ui != null && (ui.OnShowingUIs.Count > 0 || (ui.Esc_GameMenu != null && ui.Esc_GameMenu.activeSelf)))
            {
                return;
            }
            Detonate();
        }

        private static void Detonate()
        {
            Player_Input player = Player_Input.ins;
            var ready = new List<PlacedMine>();
            foreach (PlacedMine m in MineManager.All())
            {
                if (IsReady(m, player))
                {
                    ready.Add(m);
                }
            }
            if (ready.Count == 0)
            {
                Plugin.Toast(CountPlaced() > 0 ? "No armed charges in range" : "No charges placed");
                return;
            }
            Vector3 p = player.transform.position;
            ready.Sort((a, b) => (a.transform.position - p).sqrMagnitude.CompareTo((b.transform.position - p).sqrMagnitude));
            SmallSounds.PlayBeep(p, 0.6f);
            for (int i = 0; i < ready.Count; i++)
            {
                MineManager.RemoteFire(ready[i], 0.35f + i * 0.12f);
            }
            Plugin.Log.LogInfo($"[Remote] detonating {ready.Count} charge(s).");
        }

        private static bool IsRemote(PlacedMine m) => m != null && m.Def != null && m.Def.RemoteDetonated;

        private static bool IsReady(PlacedMine m, Player_Input player)
        {
            return IsRemote(m) && m.Phase == PlacedMine.State.Armed &&
                   (m.Owner == null || m.Owner == player) &&
                   (m.transform.position - player.transform.position).sqrMagnitude <= Plugin.DetonateRange.Value * Plugin.DetonateRange.Value;
        }

        private static int CountPlaced()
        {
            int n = 0;
            foreach (PlacedMine m in MineManager.All())
            {
                if (IsRemote(m) && m.Phase != PlacedMine.State.Done)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>Called from Plugin.OnGUI. The widget only exists while you have charges out.</summary>
        internal static void DrawHud()
        {
            if (!Plugin.ShowRemoteHud.Value || MineManager.Count == 0 || Player_Input.ins == null)
            {
                return;
            }
            Player_Input player = Player_Input.ins;
            int placed = 0, ready = 0, arming = 0;
            ExplosiveDef iconDef = null;
            foreach (PlacedMine m in MineManager.All())
            {
                if (!IsRemote(m) || m.Phase == PlacedMine.State.Done)
                {
                    continue;
                }
                placed++;
                iconDef = iconDef ?? m.Def;
                if (IsReady(m, player))
                {
                    ready++;
                }
                else if (m.Phase == PlacedMine.State.Arming)
                {
                    arming++;
                }
            }
            if (placed == 0)
            {
                return;
            }
            CheckKeyConflict();

            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
                _state = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            }
            if (_icon == null && iconDef != null && iconDef.RuntimeIconSprite != null)
            {
                _icon = iconDef.RuntimeIconSprite.texture;
            }

            // Middle of the left edge - clear of the hotbar and the game's own bottom-left HUD.
            float x = 22f, y = Screen.height * 0.5f - 38f;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(x - 6f, y - 6f, 300f, 76f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            if (_icon != null)
            {
                GUI.DrawTexture(new Rect(x, y, 64f, 64f), _icon, ScaleMode.ScaleToFit);
            }

            string state;
            Color col;
            if (ready > 0)
            {
                state = $"READY  [{Plugin.DetonateKey.Value}] detonate" + (ready < placed ? $" ({ready})" : "");
                col = new Color(0.45f, 1f, 0.45f);
            }
            else if (arming > 0)
            {
                state = "ARMING...";
                col = new Color(1f, 0.8f, 0.3f);
            }
            else
            {
                state = "OUT OF RANGE";
                col = new Color(0.7f, 0.7f, 0.7f);
            }
            Shadowed(new Rect(x + 72f, y + 6f, 230f, 22f), $"REMOTE CHARGES  x{placed}", _title, new Color(0.95f, 0.92f, 0.85f));
            Shadowed(new Rect(x + 72f, y + 30f, 230f, 26f), state, _state, col);
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, Color color)
        {
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);
            style.normal.textColor = color;
            GUI.Label(r, text, style);
        }

        /// <summary>Warns once if the detonate key is also one of the game's own bindings.</summary>
        private static void CheckKeyConflict()
        {
            if (_conflictChecked || Player_HotKeys.ins == null)
            {
                return;
            }
            _conflictChecked = true;
            try
            {
                KeyCode key = Plugin.DetonateKey.Value;
                foreach (FieldInfo f in typeof(Player_HotKeys).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType == typeof(Hotkey_Sets) && f.GetValue(Player_HotKeys.ins) is Hotkey_Sets hk && hk != null && hk.keyCode == key)
                    {
                        Plugin.Log.LogWarning($"[Remote] detonate key {key} is also the game's '{f.Name}' key - change RemoteCharge.DetonateKey.");
                        Plugin.Toast($"Remote detonate key {key} clashes with the game's {f.Name} key - change it in the config", 6f);
                        return;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Remote] key check failed: " + ex.Message);
            }
        }
    }
}
