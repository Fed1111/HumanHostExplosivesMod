using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HumanHostExplosives.Registry
{
    internal enum ExplosiveKind
    {
        Grenade,
        Molotov,
        Nailbomb
    }

    /// <summary>
    /// Everything needed to register one explosive as a real inventory item: its invented
    /// Addressables identity, its on-disk mesh/texture, and its tooltip text. One instance
    /// per explosive type; built once in Plugin.Awake from BepInEx config.
    /// </summary>
    internal sealed class ExplosiveDef
    {
        internal ExplosiveKind Kind;
        internal string Tag;
        internal string IconGuid;
        internal string ModelGuid;
        internal string ObjFileName;
        internal string PngFileName;

        /// <summary>
        /// Optional dedicated 2D icon image, separate from the 3D model's diffuse texture (which
        /// is a flat UV map, not a picture of the object). Falls back to a crop of the diffuse
        /// texture if not set or missing.
        /// </summary>
        internal string IconPngFileName;

        internal int MaxStack;

        internal string TooltipName;
        internal string TooltipType;
        internal string TooltipInstruction;

        /// <summary>Craft_Mgr.WorkbenchType enum name this recipe should appear under, e.g. "HandMade".</summary>
        internal string WorkbenchTypeName;

        /// <summary>Which tab (index into Craft_Items._CraftItemsData) to inject into for that workbench.</summary>
        internal int TabIndex;

        internal float CraftSeconds = 30f;

        /// <summary>
        /// ABSOLUTE target for WeaponPosData.localPosOfHand (see HandOffsetFixer) - THIRD-PERSON
        /// hand-bone attachment only, not what the player sees for their own character while
        /// playing in first person. Not a delta added to the template weapon's own tuned value.
        /// </summary>
        internal Vector3 HandOffset = Vector3.zero;

        /// <summary>
        /// ABSOLUTE target for all six Tool_Interacter._1stCamMod entries (see
        /// HandOffsetFixer.ApplyFirstPerson) - the actual FIRST-PERSON viewmodel position system,
        /// separate from HandOffset above.
        /// </summary>
        internal Vector3 FirstPersonOffset = Vector3.zero;

        /// <summary>Material GUID -> count needed. Empty means "recipe not configured yet, skip injection".</summary>
        internal List<(string Guid, int Count)> Recipe = new List<(string Guid, int Count)>();

        /// <summary>
        /// Whether this explosive should be added to world loot tables. Off for anything without
        /// finished art - a lootable item the player cannot see in hand is worse than no item.
        /// </summary>
        internal bool SpawnsInLoot = true;

        internal Mesh RuntimeMesh;
        internal Material RuntimeMaterial;
        internal Texture2D RuntimeTexture;
        internal Sprite RuntimeIconSprite;
        internal Icon_Info RuntimeIconInfo;

        /// <summary>
        /// Loads the mesh/texture from Assets/&lt;...&gt; next to the plugin DLL, once.
        /// Returns false (without throwing) if the files aren't there, so a missing-art
        /// explosive (e.g. Molotov before its model exists) is skipped instead of crashing
        /// registration for every other explosive.
        /// </summary>
        internal bool TryLoadMeshAndMaterial()
        {
            if (RuntimeMesh != null && RuntimeMaterial != null)
            {
                return true;
            }

            string pluginDir = Path.GetDirectoryName(typeof(ExplosiveDef).Assembly.Location);
            string objPath = Path.Combine(pluginDir, ObjFileName);
            string pngPath = Path.Combine(pluginDir, PngFileName);
            if (!File.Exists(objPath) || !File.Exists(pngPath))
            {
                return false;
            }

            RuntimeMesh = ObjLoader.LoadMesh(objPath);
            RuntimeTexture = TextureLoader.LoadPng(pngPath);

            Shader shader = Shader.Find("HDRP/Lit");
            if (shader != null)
            {
                RuntimeMaterial = new Material(shader);
                RuntimeMaterial.SetTexture("_BaseColorMap", RuntimeTexture);
            }
            else
            {
                RuntimeMaterial = new Material(Shader.Find("Standard"));
                RuntimeMaterial.mainTexture = RuntimeTexture;
            }

            // A dedicated 2D icon image looks like an actual icon; the 3D model's diffuse
            // texture (used above for RuntimeMaterial) is a flat UV map, not a picture of the
            // object, and looks wrong used directly as one. (A first attempt at generating an
            // icon by rendering the model in-engine - IconRenderer, calling Camera.Render() from
            // here - hung the game on boot: this method runs from Plugin.Awake(), which fires
            // the moment BepInEx loads the plugin, long before the game's scene/camera/HDRP
            // render pipeline exist. Plain texture loading below has no such risk - it's just
            // data, no rendering involved.)
            Texture2D iconTexture = RuntimeTexture;
            if (!string.IsNullOrEmpty(IconPngFileName))
            {
                string iconPath = Path.Combine(pluginDir, IconPngFileName);
                if (File.Exists(iconPath))
                {
                    iconTexture = TextureLoader.LoadPng(iconPath);
                }
            }

            RuntimeIconSprite = Sprite.Create(
                iconTexture,
                new Rect(0f, 0f, iconTexture.width, iconTexture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            RuntimeIconSprite.name = Tag + "_Icon";

            return true;
        }
    }
}
