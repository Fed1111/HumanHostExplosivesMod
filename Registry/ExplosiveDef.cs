using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HumanHostExplosives.Registry
{
    internal enum ExplosiveKind
    {
        Grenade,
        Molotov
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
        internal int MaxStack;

        internal string TooltipName;
        internal string TooltipType;
        internal string TooltipInstruction;

        /// <summary>Craft_Mgr.WorkbenchType enum name this recipe should appear under, e.g. "HandMade".</summary>
        internal string WorkbenchTypeName;

        /// <summary>Which tab (index into Craft_Items._CraftItemsData) to inject into for that workbench.</summary>
        internal int TabIndex;

        internal float CraftSeconds = 30f;

        /// <summary>Material GUID -> count needed. Empty means "recipe not configured yet, skip injection".</summary>
        internal List<(string Guid, int Count)> Recipe = new List<(string Guid, int Count)>();

        internal Mesh RuntimeMesh;
        internal Material RuntimeMaterial;
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
            Texture2D texture = TextureLoader.LoadPng(pngPath);

            Shader shader = Shader.Find("HDRP/Lit");
            if (shader != null)
            {
                RuntimeMaterial = new Material(shader);
                RuntimeMaterial.SetTexture("_BaseColorMap", texture);
            }
            else
            {
                RuntimeMaterial = new Material(Shader.Find("Standard"));
                RuntimeMaterial.mainTexture = texture;
            }

            RuntimeIconSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            RuntimeIconSprite.name = Tag + "_Icon";

            return true;
        }
    }
}
