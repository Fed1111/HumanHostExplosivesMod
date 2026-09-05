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

            iconInfo._ToolTipText = clone;
        }
    }
}
