using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using HumanHostExplosives.Registry;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;

namespace HumanHostExplosives
{
    /// <summary>
    /// Placed mines survive save/load, can be picked back up, and go off when shot or hit.
    ///
    /// SAVING. The game writes every save into Save/Auto_Save, and on load copies the chosen slot
    /// folder INTO Auto_Save (Save_Player_Data._Awake); a manual save or quit copies Auto_Save back
    /// to the slot (SaveDataManager.BeginSaveData, SaveData:516). So one file in Auto_Save rides
    /// along with the game's own saves for free. The copy (UI_Control.SyncDirectoryCore) only ADDS
    /// files and never deletes, so a stale file from another slot can sit in Auto_Save - the header
    /// carries the save ID and world seed and a mismatched file is ignored. The file is written on
    /// every save, even with no mines, so an old one never survives its mines.
    ///
    /// Format (tab-separated, invariant culture; no JsonUtility - MOD_CONVENTIONS §26):
    ///   V  1  saveID  worldSeed
    ///   M  itemTag  px py pz  qx qy qz qw
    /// </summary>
    internal static class MinePersistence
    {
        private const string FileName = "HHX_Mines.txt";
        private const float PickupDistance = 2.5f;

        private static bool _loadDone;
        private static float _playerSeenAt = -1f;
        private static PlacedMine _lookedAt;
        private static float _nextLookCheck;

        internal static PlacedMine LookedAt => _lookedAt;

        private static string AutoSaveFile => Path.Combine(Application.dataPath, "Save", "Auto_Save", FileName);

        internal static void Init()
        {
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                _loadDone = false;
                _playerSeenAt = -1f;
                _lookedAt = null;
            };
        }

        // ---------------------------------------------------------------- load

        /// <summary>Called from Plugin.Update: restores the file's mines a few seconds into a loaded world.</summary>
        internal static void TickLoad()
        {
            if (_loadDone)
            {
                return;
            }
            if (Player_Input.ins == null || !ExplosiveItemRegistry.Built)
            {
                _playerSeenAt = -1f;
                return;
            }
            if (_playerSeenAt < 0f)
            {
                _playerSeenAt = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _playerSeenAt < 5f)
            {
                return;
            }
            _loadDone = true;
            try
            {
                Load();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[MineSave] load failed: " + ex);
            }
        }

        private static void Load()
        {
            string path = AutoSaveFile;
            if (!File.Exists(path))
            {
                return;
            }
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                return;
            }
            string[] header = lines[0].Split('\t');
            if (header.Length < 4 || header[0] != "V")
            {
                Plugin.Log.LogWarning("[MineSave] unreadable header - ignoring the mine file.");
                return;
            }
            string id = header[2];
            string seed = header[3];
            bool idOk = id == G_Save.ID || id == "Auto_Save" || G_Save.ID == "Auto_Save";
            if (!idOk || seed != G_Save._config._TerraWorldSeed.ToString(CultureInfo.InvariantCulture))
            {
                Plugin.Log.LogInfo($"[MineSave] mine file belongs to another save ({id}, seed {seed}) - not loading it.");
                return;
            }

            int restored = 0, rejected = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string[] p = lines[i].Split('\t');
                if (p.Length < 9 || p[0] != "M")
                {
                    continue;
                }
                ExplosiveDef def = ExplosiveItemRegistry.FindByTag(p[1]);
                if (def == null || !def.Placeable || def.RuntimeMesh == null)
                {
                    rejected++;
                    continue;
                }
                try
                {
                    var pos = new Vector3(F(p[2]), F(p[3]), F(p[4]));
                    var rot = new Quaternion(F(p[5]), F(p[6]), F(p[7]), F(p[8]));
                    PlacedMine.Create(def, pos, rot, Player_Input.ins, silent: true);
                    restored++;
                }
                catch (Exception ex)
                {
                    rejected++;
                    Plugin.Log.LogWarning($"[MineSave] bad mine line {i}: {ex.Message}");
                }
            }
            Plugin.Log.LogInfo($"[MineSave] restored {restored} mine(s) for {G_Save.ID}" + (rejected > 0 ? $", {rejected} skipped" : "") + ".");
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- save

        /// <summary>
        /// Runs when the game starts a save - before it copies Auto_Save into the slot, so the file
        /// goes with it. A prefix on the coroutine method runs at the call, ahead of the copy.
        /// </summary>
        [HarmonyPatch(typeof(SaveDataManager), "BeginSaveData")]
        private static class SavePatch
        {
            private static void Prefix()
            {
                try
                {
                    Save();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[MineSave] save failed: " + ex);
                }
            }
        }

        private static void Save()
        {
            // Never write before this world's mines were read back, or an early autosave would
            // replace the file with an empty one and lose them.
            if (!_loadDone)
            {
                return;
            }
            var sb = new StringBuilder();
            sb.Append("V\t1\t").Append(G_Save.ID).Append('\t')
              .Append(G_Save._config._TerraWorldSeed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            int n = 0;
            foreach (PlacedMine m in MineManager.All())
            {
                if (m == null || m.Phase == PlacedMine.State.Done || m.Phase == PlacedMine.State.Triggered)
                {
                    continue;
                }
                Vector3 p = m.transform.position;
                Quaternion q = m.transform.rotation;
                sb.Append("M\t").Append(m.Def.Tag);
                foreach (float f in new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w })
                {
                    sb.Append('\t').Append(f.ToString("R", CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
                n++;
            }
            string path = AutoSaveFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".write";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmp, path);
            Plugin.Log.LogInfo($"[MineSave] saved {n} mine(s) for {G_Save.ID}.");
        }

        // ---------------------------------------------------------------- pickup

        /// <summary>Called from Plugin.Update: tracks the mine under the crosshair and handles the pickup key.</summary>
        internal static void TickPickup()
        {
            if (MineManager.Count == 0 || Player_Input.ins == null)
            {
                _lookedAt = null;
                return;
            }
            if (Time.unscaledTime >= _nextLookCheck)
            {
                _nextLookCheck = Time.unscaledTime + 0.1f;
                _lookedAt = FindLookedAt();
            }
            if (_lookedAt == null || !UiFree())
            {
                return;
            }
            KeyCode key = Plugin.MinePickupKey.Value;
            if (key == KeyCode.None && Player_HotKeys.ins != null && Player_HotKeys.ins.UseF != null)
            {
                key = Player_HotKeys.ins.UseF.keyCode;
            }
            if (key != KeyCode.None && Input.GetKeyDown(key))
            {
                PickUp(_lookedAt);
            }
        }

        internal static string PickupKeyName()
        {
            KeyCode key = Plugin.MinePickupKey.Value;
            if (key == KeyCode.None && Player_HotKeys.ins != null && Player_HotKeys.ins.UseF != null)
            {
                key = Player_HotKeys.ins.UseF.keyCode;
            }
            return key.ToString();
        }

        private static bool UiFree()
        {
            UI_Control ui = UI_Control.ins;
            return ui == null || (ui.OnShowingUIs.Count == 0 && (ui.Esc_GameMenu == null || !ui.Esc_GameMenu.activeSelf));
        }

        private static PlacedMine FindLookedAt()
        {
            Transform cam = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (cam == null)
            {
                return null;
            }
            Player_Input player = Player_Input.ins;
            float camToPlayer = Vector3.Distance(cam.position, player.transform.position);
            RaycastHit[] hits = Physics.RaycastAll(cam.position, cam.forward, camToPlayer + PickupDistance + 1f,
                                                   ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            foreach (RaycastHit hit in hits)
            {
                // Third person: the ray from behind the shoulder can clip your own body or held tool first.
                if (hit.collider.GetComponentInParent<C_Controller_Base>() == player)
                {
                    continue;
                }
                Tool_Interacter tool = hit.collider.GetComponentInParent<Tool_Interacter>();
                if (tool != null && tool._CharBase == player)
                {
                    continue;
                }
                PlacedMine m = hit.collider.GetComponent<PlacedMine>();
                if (m == null || m.Phase == PlacedMine.State.Done)
                {
                    return null;
                }
                return Vector3.Distance(player.transform.position, m.transform.position) <= PickupDistance ? m : null;
            }
            return null;
        }

        private static void PickUp(PlacedMine m)
        {
            if (m.Phase == PlacedMine.State.Triggered)
            {
                Plugin.Toast("It's live - get away from it!");
                return;
            }
            Item_Slot_Mgr mgr = Item_Slot_Mgr.ins;
            if (mgr == null)
            {
                return;
            }
            int added = mgr.Add_PurchasedItem_To_Player(new AssetReference(m.Def.IconGuid), 1);
            if (added <= 0)
            {
                Plugin.Toast("No room in your inventory");
                return;
            }
            m.Phase = PlacedMine.State.Done;
            SmallSounds.PlayClick(m.transform.position, 0.4f);
            Plugin.Toast($"Picked up {m.Def.TooltipName}");
            Plugin.Log.LogInfo($"[Mine] '{m.Def.Tag}' picked up.");
            UnityEngine.Object.Destroy(m.gameObject);
            _lookedAt = null;
        }

        // ---------------------------------------------------------------- shot / hit

        /// <summary>
        /// Bullets are physical spheres; on contact Bullet_Impact hands the collider to
        /// Tool_Interacter.Directly_Interact (Hand_Tools:1545), the same entry melee hits use. A hit
        /// on a mine's collider sets it off, and skips vanilla's handling, which would treat the
        /// mine as terrain.
        /// </summary>
        [HarmonyPatch(typeof(Tool_Interacter), nameof(Tool_Interacter.Directly_Interact))]
        private static class HitPatch
        {
            private static bool Prefix(Collider hitCol, ref bool __result)
            {
                if (hitCol == null)
                {
                    return true;
                }
                PlacedMine m = hitCol.GetComponent<PlacedMine>();
                if (m == null)
                {
                    return true;
                }
                try
                {
                    MineManager.SetOffByHit(m);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[Mine] hit handling threw: " + ex);
                }
                __result = true;
                return false;
            }
        }
    }
}
