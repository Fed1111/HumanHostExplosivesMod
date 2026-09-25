using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HumanHostExplosives.Registry
{
    internal enum ExplosiveKind
    {
        Grenade,
        Molotov,
        Nailbomb,
        // 0.3.0. Appended, never inserted: DebugThrowKind persists these by name, but keep the
        // numeric values stable anyway.
        ContactGrenade,
        Mine,
        ImprovisedMine
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

        /// <summary>
        /// Placed on the ground with a single LMB tap (MinePlacer) instead of charged and thrown.
        /// The quick-throw key skips these - a mine is never lobbed.
        /// </summary>
        internal bool Placeable;

        /// <summary>How many items one craft yields (PerIconData.craftNum - the UI shows "x N").</summary>
        internal int CraftNum = 1;

        /// <summary>
        /// Read the OBJ's own vertex normals and weld corners (smooth shading) - see ObjLoader.
        /// On for the Blender-made 0.3.0 models, off for the published grenade/nailbomb look.
        /// </summary>
        internal bool UseObjNormals;

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
        /// <summary>
        /// All three shipped files are on disk. Checked BEFORE a def is added to Plugin.Defs, so an
        /// item with missing art never reaches the registry, a recipe or a loot table - an invented
        /// GUID that never resolves makes the engine log 'Invalid path in AssetBundleProvider', which
        /// the game treats as a corrupted install.
        /// </summary>
        internal bool ArtFilesPresent()
        {
            return File.Exists(AssetPaths.Resolve(ObjFileName))
                && File.Exists(AssetPaths.Resolve(PngFileName))
                && (string.IsNullOrEmpty(IconPngFileName) || File.Exists(AssetPaths.Resolve(IconPngFileName)));
        }

        /// <summary>For icon-only items (the "any ammunition" recipe material): just the 2D icon.</summary>
        internal bool TryLoadIconOnly()
        {
            if (RuntimeIconSprite != null)
            {
                return true;
            }
            string iconPath = AssetPaths.Resolve(IconPngFileName);
            if (!File.Exists(iconPath))
            {
                return false;
            }
            Texture2D tex = TextureLoader.LoadPng(iconPath);
            RuntimeIconSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            RuntimeIconSprite.name = Tag + "_Icon";
            return true;
        }

        internal bool TryLoadMeshAndMaterial()
        {
            if (RuntimeMesh != null && RuntimeMaterial != null)
            {
                return true;
            }

            string objPath = AssetPaths.Resolve(ObjFileName);
            string pngPath = AssetPaths.Resolve(PngFileName);
            if (!File.Exists(objPath) || !File.Exists(pngPath))
            {
                return false;
            }

            RuntimeMesh = ObjLoader.LoadMesh(objPath, UseObjNormals);
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
            // Owner prefix so a global material sweep (the texture pack) skips it - MOD_CONVENTIONS §18.
            RuntimeMaterial.name = "HHE_" + Tag + "_Mat";

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
                string iconPath = AssetPaths.Resolve(IconPngFileName);
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
