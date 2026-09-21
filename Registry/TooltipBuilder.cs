using System;
using System.Collections.Generic;
using UnityEngine;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// Rewrites a cloned Icon_Info's tooltip in place. This is not cosmetic: both
    /// Item_Slot_Mgr.PickItemIn_Bag_Belt and Craft_Items.Load_TopButtonTag_Icons index
    /// straight into Tooltip_Text._Infos[Language_Mgr.ins._LanguageIndex] with no bounds
    /// check beyond the list's own length, so every entry must be overwritten (not just
    /// entry 0) or picking up / crafting the item throws for anyone not on the first
    /// language in the list.
    /// </summary>
    internal static class TooltipBuilder
    {
        internal static void Rewrite(Icon_Info iconInfo, ExplosiveDef def)
        {
            Tooltip_Text template = iconInfo._ToolTipText;
            if (template == null)
            {
                throw new InvalidOperationException(def.Tag + ": template icon has no Tooltip_Text");
            }

            var clone = UnityEngine.Object.Instantiate(template);
            clone.hideFlags = HideFlags.HideAndDontSave;
            clone.name = def.Tag + "_Tooltip";

            if (clone._Infos == null || clone._Infos.Count == 0)
            {
                throw new InvalidOperationException(def.Tag + ": template Tooltip_Text has no language entries");
            }

            List<Tooltip_Text.ToolTipInfo> infos = clone._Infos;
            for (int i = 0; i < infos.Count; i++)
            {
                Tooltip_Text.ToolTipInfo info = infos[i];
                info._ItemName = def.TooltipName;
                info._ItemType = def.TooltipType;
                info._ItemInstruction = def.TooltipInstruction;
                info._ItemProperty = string.Empty;
                infos[i] = info;
            }

            // Pad out to cover every LanguageType value (14 as of this game version: Chinese_S/T,
            // English, Russian, Japanese, Korean, French, German, Polish, Spanish, Italian,
            // Portuguese, Turkish, Thai), not just however many entries the template happened to
            // ship with. Language_Mgr.ins._LanguageIndex can be as high as (LanguageType count - 1),
            // and both PickItemIn_Bag_Belt and Load_TopButtonTag_Icons index straight into this list
            // with no bounds check - if the Iron Pickaxe template was only ever fully localized for
            // a handful of languages, a client on a language past the template's own list length
            // would throw the moment the game tries to render OUR item's tooltip, deep inside
            // menu-population code. That surfaces as the recipe silently missing from the crafting
            // list, not as a visible error - confirmed live by a tester on German or Polish (unclear
            // which) whose grenade recipe never appeared at all. Every entry above already gets the
            // same non-localized text regardless of language, so padding with more copies of the
            // last entry costs nothing and closes the gap for every LanguageType value regardless of
            // the template's own coverage.
            int languageCount = Enum.GetValues(typeof(LanguageType)).Length;
            int originalCount = infos.Count;
            if (infos.Count > 0 && infos.Count < languageCount)
            {
                Tooltip_Text.ToolTipInfo padTemplate = infos[infos.Count - 1];
                while (infos.Count < languageCount)
                {
                    infos.Add(padTemplate);
                }
            }

            if (Plugin.EnableDiagnostics.Value)
            {
                int currentIndex = Language_Mgr.ins != null ? Language_Mgr.ins._LanguageIndex : -1;
                Plugin.Log.LogInfo(
                    $"[Tooltip] '{def.Tag}': template had {originalCount}/{languageCount} language entries " +
                    $"(padded to {infos.Count}); this client's Language_Mgr._LanguageIndex={currentIndex}.");
            }

            iconInfo._ToolTipText = clone;
        }
    }
}
