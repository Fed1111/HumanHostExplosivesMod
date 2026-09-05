using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// Builds each ExplosiveDef into a real inventory item by cloning a simple one-handed
    /// tool/melee weapon (configured via Plugin.TemplateIconGuid / TemplateModelGuid) and
    /// swapping its identity, icon, tooltip and 3D model. The clone keeps the template's
    /// Tool_Interacter/Weapon_Melee/animation wiring, since that's what makes the game
    /// hand-equip and animate it correctly without a custom weapon rig - a tool/weapon
    /// template is required here, not a consumable, because consumables (food/water/bandages)
    /// have no working hand-model/animation content in this game yet. The clone still ends up
    /// looking and behaving like a stackable consumable at the UI/inventory level (forced
    /// _Can_Stack/_Tag below) - Weapon_Melee's own attack/durability logic on the clone never
    /// runs, because ExplosiveUseHook suppresses LMB-attack for our tag before it can fire.
    /// </summary>
    internal static class ExplosiveItemRegistry
    {
        private static List<ExplosiveDef> _defs;
        private static bool _built;
        private static bool _buildFailed;
        private static bool _retrying;
        private static GameObject _root;

        internal static void Initialize(List<ExplosiveDef> defs)
        {
            _defs = defs;
        }

        internal static ExplosiveDef FindByTag(string tag)
        {
            if (_defs == null || string.IsNullOrEmpty(tag))
            {
                return null;
            }
            foreach (ExplosiveDef def in _defs)
            {
                if (def.Tag == tag)
                {
                    return def;
                }
            }
            return null;
        }

        private static Transform PrefabRoot
        {
            get
            {
                if (_root == null)
                {
                    _root = new GameObject("HHX_PrefabRoot");
                    UnityEngine.Object.DontDestroyOnLoad(_root);
                    _root.hideFlags = HideFlags.HideAndDontSave;
                    _root.SetActive(false);
                }
                return _root.transform;
            }
        }

        // Item_Slot_Mgr._Awake runs well before any save data is loaded (Char_Item_Icons.Start,
        // which resolves saved items by GUID, comes later), so registering here guarantees our
        // GUIDs exist before anything can ask Addressables to resolve them.
        [HarmonyPatch(typeof(Item_Slot_Mgr), "_Awake")]
        private static class AwakePatch
        {
            private static void Postfix()
            {
                try
                {
                    if (!TryBuildAll() && !_buildFailed && !_retrying && Plugin.Instance != null)
                    {
                        _retrying = true;
                        Plugin.Instance.StartCoroutine(RetryBuild());
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[Registry] initialization failed: " + ex);
                }
            }
        }

        // The catalog may not be ready the instant Item_Slot_Mgr wakes; retry for up to 30s.
        private static IEnumerator RetryBuild()
        {
            for (int i = 0; i < 60 && !_built && !_buildFailed; i++)
            {
                yield return new WaitForSeconds(0.5f);
                try
                {
                    TryBuildAll();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[Registry] retry build failed: " + ex);
                    break;
                }
            }
            _retrying = false;
            if (_built)
            {
                Plugin.Log.LogInfo("[Registry] retry build succeeded");
            }
            else if (!_buildFailed)
            {
                Plugin.Log.LogError("[Registry] template items still unavailable after 30 seconds");
            }
        }

        internal static bool TryBuildAll()
        {
            if (_built)
            {
                return true;
            }
            if (_buildFailed)
            {
                return false;
            }
            if (_defs == null || _defs.Count == 0)
            {
                return false;
            }

            if (string.IsNullOrEmpty(Plugin.TemplateIconGuid.Value) || string.IsNullOrEmpty(Plugin.TemplateModelGuid.Value))
            {
                Plugin.Log.LogWarning(
                    "[Registry] TemplateIconGuid/TemplateModelGuid are not set. Run the game once with " +
                    "Diagnostics.EnableDiagnostics on, pick up a simple one-handed tool or melee weapon " +
                    "(a knife/hatchet/hammer - not a consumable, not anything two-handed), and copy the " +
                    "logged assetRef_Key/ModelRef GUID into the config file. " +
                    "Explosive item registration skipped for this session.");
                _buildFailed = true;
                return false;
            }

            GameObject iconTemplate = LoadByGuid(Plugin.TemplateIconGuid.Value, "template icon");
            GameObject modelTemplate = LoadByGuid(Plugin.TemplateModelGuid.Value, "template model");
            if (iconTemplate == null || modelTemplate == null)
            {
                return false;
            }

            ExplosiveAddressablesInterceptor.Install();

            var registered = new Dictionary<string, GameObject>();
            var staged = new List<GameObject>();
            try
            {
                foreach (ExplosiveDef def in _defs)
                {
                    if (!def.TryLoadMeshAndMaterial())
                    {
                        Plugin.Log.LogWarning($"[Registry] {def.Tag}: mesh/texture not found on disk, skipping this item.");
                        continue;
                    }

                    GameObject modelClone = BuildModel(modelTemplate, def);
                    registered.Add(def.ModelGuid, modelClone);
                    staged.Add(modelClone);

                    GameObject iconClone = BuildIcon(iconTemplate, def);
                    registered.Add(def.IconGuid, iconClone);
                    staged.Add(iconClone);

                    Plugin.Log.LogInfo($"[Registry] built '{def.Tag}' (icon={def.IconGuid}, model={def.ModelGuid})");
                }

                foreach (KeyValuePair<string, GameObject> kv in registered)
                {
                    ExplosiveAddressablesInterceptor.Items.Add(kv.Key, kv.Value);
                }

                _built = true;
                Plugin.Log.LogInfo($"[Registry] {registered.Count / 2} explosive item(s) registered.");
                return true;
            }
            catch (Exception ex)
            {
                foreach (GameObject go in staged)
                {
                    if (go != null)
                    {
                        UnityEngine.Object.DestroyImmediate(go);
                    }
                }
                _buildFailed = true;
                Plugin.Log.LogError("[Registry] build aborted and rolled back: " + ex);
                return false;
            }
        }

        private static GameObject LoadByGuid(string guid, string label)
        {
            try
            {
                GameObject result = Addressables.LoadAssetAsync<GameObject>(guid).WaitForCompletion();
                if (result == null)
                {
                    Plugin.Log.LogWarning($"[Registry] {label} ('{guid}') not resolved yet.");
                }
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Registry] failed to load {label} ('{guid}'): {ex.Message}");
                return null;
            }
        }

        private static GameObject BuildModel(GameObject template, ExplosiveDef def)
        {
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(template, PrefabRoot);
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.name = def.Tag + "_Model";
                clone.SetActive(true);

                Item_Info itemInfo = clone.GetComponent<Item_Info>();
                if (itemInfo == null)
                {
                    throw new InvalidOperationException(def.Tag + ": template model has no Item_Info");
                }
                itemInfo.assetRef_Key = def.ModelGuid;

                MeshFilter filter = clone.GetComponentInChildren<MeshFilter>(true);
                MeshRenderer renderer = clone.GetComponentInChildren<MeshRenderer>(true);
                if (filter == null || renderer == null)
                {
                    throw new InvalidOperationException(def.Tag + ": template model has no MeshFilter/MeshRenderer to swap");
                }
                filter.sharedMesh = def.RuntimeMesh;
                renderer.sharedMaterial = def.RuntimeMaterial;

                return clone;
            }
            catch
            {
                if (clone != null)
                {
                    UnityEngine.Object.DestroyImmediate(clone);
                }
                throw;
            }
        }

        private static GameObject BuildIcon(GameObject template, ExplosiveDef def)
        {
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(template, PrefabRoot);
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.name = def.Tag + "_Icon";
                clone.SetActive(true);

                Icon_Info iconInfo = clone.GetComponent<Icon_Info>();
                if (iconInfo == null)
                {
                    throw new InvalidOperationException(def.Tag + ": template icon has no Icon_Info");
                }

                iconInfo.Icon = def.RuntimeIconSprite;
                iconInfo.ModelRef = new AssetReference(def.ModelGuid);
                iconInfo._SlotType = Icon_Info.Slot_Type.Hand_R;
                iconInfo._Can_Stack = true;
                iconInfo.MaxStack = def.MaxStack;
                iconInfo._Can_Repair = false;
                iconInfo._DisResources = null;
                iconInfo._Tag = def.Tag;
                if (iconInfo._BaseMaxDurability <= 0f)
                {
                    iconInfo._BaseMaxDurability = 100f;
                }

                TooltipBuilder.Rewrite(iconInfo, def);
                def.RuntimeIconInfo = iconInfo;

                return clone;
            }
            catch
            {
                if (clone != null)
                {
                    UnityEngine.Object.DestroyImmediate(clone);
                }
                throw;
            }
        }
    }
}
