using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// A recipe material that accepts ANY loose ammunition, of any caliber or tier.
    ///
    /// Vanilla recipes can't express that: every place Craft_Items counts, locks, moves or consumes
    /// materials compares the inventory item's GUID with the recipe's material GUID by plain string
    /// equality (icon_GUID == assetGUID / _IconRef.AssetGUID != assetGUID - UI:1135-1165, 1274,
    /// 1363, 1415, 1566, 1618, 1801-1833, and the craft loop in Update). So this transpiles every
    /// Craft_Items method that does such a compare, swapping string ==/!= for Matches(): identical to
    /// ordinary equality unless one side is the AnyAmmo sentinel GUID, in which case any ammo GUID
    /// satisfies it. Counting already SUMS across slots, so a mix of 9mm and shotgun shells adds up.
    ///
    /// "Ammunition" = every Icon_Info with _SlotType == Ammo except arrows, read from the bundles
    /// (2026-09-25): the seven lootable per-caliber items (Weapons/Ammo_Boxes/*_AmmoBox_Icon -
    /// despite the name these are the loose rounds guns reload from, stack 500) and the 35 crafted
    /// BHC_* rounds. Modded guns' ammo can be added with Loot.ExtraAmmoGuids.
    /// </summary>
    internal static class AnyAmmoMaterial
    {
        /// <summary>Invented; absent from the game catalog. Registered as an icon-only item.</summary>
        internal const string Guid = "3c9e1a7b5d2f4e6a8b0c1d2e3f4a5b6c";

        private static readonly HashSet<string> AmmoGuids = new HashSet<string>(StringComparer.Ordinal)
        {
            // Weapons/Ammo_Boxes/<caliber>/<caliber>_AmmoBox_Icon - the looted rounds.
            "70fab75f24a179149be14e3209fac74f", // 12x70mm
            "fcf7b18b8db6a6e44b19e76a8d5e9f26", // .45 ACP
            "2fdb99cf996c38a479485aa1e3a49c7d", // 5.56x45
            "4c1160db2de124844bf26c5170d95eb3", // 7.62x39
            "479fbed075aa7364d86429bdc4940de1", // 7.62x51
            "5a0c86bdbf40792439766fa9b56d8a24", // 7.62x54
            "484808ba17234584a9253a3cd1122263", // 9x19
            // Recipes/Ammo/BHC_<caliber>_<tier> - Gun Workbench crafted rounds.
            "94a9b8dfc4198de4cbbc9de0ce15bae0", "7d5030d1303e73942a8cb67ad7b84992", "6d86b0c4cf42464409611a0547677146",
            "ca28efb09c1651b428d3b3b3f6a1d9db", "956f51578bba3d941a9a73f8094baf36", "1f4d5eb8d95a82348ad8900447a3f35f",
            "3ecc184f8b6f24a4dab8d1475b7ef447", "ab36d4e6cb136034b825688bd7258f4d", "e5c0908f3512280499a79343b97bb9a9",
            "03a58593e2688a148b0ee9d89e60b2a8", "1c65f41e22bc3e24d976308c91d4ff43", "a37f116c6c4383e429b6359c19c1f3aa",
            "45f24c3c66a67b145a2765f1e07a634c", "313557ff55b9e2142889c2b63f91623e", "6f0aabda59153204684db3f69bd7dfc0",
            "5af5f4c2fbac67f4385dcf7042c14103", "586df6e5c3d330e49a5e0cc5c58c4d77", "6b1f16226f6446e449efcc1654002f45",
            "669e08b2385d65b499a2fa68098752de", "bb0d6ca54d8646c41bbafdde3377f7bc", "7a10b0c1b4a4915498f7eff658f92be2",
            "cb8f55fa332200a47878f4405e629611", "42be6f04b6edf364ca11246394c5c5ea", "0090016023b9ba841814f391fd462a0c",
            "6c8891da4fcf5f345bad698e018036c1", "6155f5a6e8fdf414db285abdd6bda318", "df4cd6aa02700a7409b3013d886889f9",
            "feb04e99abf629b4c885d43ea0ec0673", "b6f8dd7699852024b91222e50ba9d2e3", "cfb446f77978ccb41ba0b6a2c317d69e",
            "559a26257bff2de4385cb15c0537ed3d", "67f40585dd68add40a8b72ce7925c0fa", "7a6c03a947f58944d8505a5d29b175ae",
            "bc81e13bc4d111841b823d560cc0a470", "fae19624da40b884badb156dd3c0dbdd",
        };

        /// <summary>True once the sentinel's icon item is registered - recipes may only use it then.</summary>
        internal static bool Registered;

        internal static void AddExtraGuids(string commaList)
        {
            if (string.IsNullOrEmpty(commaList))
            {
                return;
            }
            foreach (string raw in commaList.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AmmoGuids.Add(raw.Trim().ToLowerInvariant());
            }
        }

        /// <summary>Drop-in for string ==. Symmetric, so argument order at each call site doesn't matter.</summary>
        public static bool Matches(string a, string b)
        {
            if (string.Equals(a, b))
            {
                return true;
            }
            if (a == null || b == null)
            {
                return false;
            }
            if (b.Length == Guid.Length && string.Equals(b, Guid, StringComparison.Ordinal))
            {
                return AmmoGuids.Contains(a);
            }
            if (a.Length == Guid.Length && string.Equals(a, Guid, StringComparison.Ordinal))
            {
                return AmmoGuids.Contains(b);
            }
            return false;
        }

        /// <summary>Drop-in for string !=.</summary>
        public static bool DoesNotMatch(string a, string b) => !Matches(a, b);

        private static readonly MethodInfo StrEq = AccessTools.Method(typeof(string), "op_Equality", new[] { typeof(string), typeof(string) });
        private static readonly MethodInfo StrNe = AccessTools.Method(typeof(string), "op_Inequality", new[] { typeof(string), typeof(string) });
        private static readonly MethodInfo MatchesMI = AccessTools.Method(typeof(AnyAmmoMaterial), nameof(Matches));
        private static readonly MethodInfo DoesNotMatchMI = AccessTools.Method(typeof(AnyAmmoMaterial), nameof(DoesNotMatch));

        /// <summary>
        /// Every Craft_Items method (and compiler-generated coroutine/lambda body under it) whose IL
        /// both compares strings and reads an item GUID. Found by scanning IL rather than listed by
        /// name, so a game update that moves the compare into a new helper is still covered.
        /// </summary>
        /// <summary>
        /// Applied by hand from Plugin.Awake, not PatchAll: Harmony throws on a patch class whose
        /// TargetMethods is empty, and inside PatchAll that would abort every other patch too.
        /// </summary>
        internal static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo transpiler = AccessTools.Method(typeof(CraftCompareTranspiler), "Transpiler");
                int n = 0;
                foreach (MethodBase target in CraftCompareTranspiler.TargetMethods())
                {
                    harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
                    n++;
                }
                Plugin.Log.LogInfo($"[AnyAmmo] patched {n} Craft_Items material compare(s).");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[AnyAmmo] could not patch the craft material checks - recipes using 'any ammunition' will be dropped: " + ex);
                Registered = false;
                Failed = true;
            }
        }

        internal static bool Failed;

        private static class CraftCompareTranspiler
        {
            internal static IEnumerable<MethodBase> TargetMethods()
            {
                var found = new List<MethodBase>();
                var types = new List<Type> { typeof(Craft_Items) };
                types.AddRange(typeof(Craft_Items).GetNestedTypes(AccessTools.all));
                foreach (Type t in types)
                {
                    foreach (MethodInfo m in t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null)
                        {
                            continue;
                        }
                        if (ComparesItemGuids(m))
                        {
                            found.Add(m);
                        }
                    }
                }
                return found;
            }

            private static bool ComparesItemGuids(MethodInfo m)
            {
                try
                {
                    bool compares = false, readsGuid = false;
                    foreach (KeyValuePair<OpCode, object> ins in PatchProcessor.ReadMethodBody(m))
                    {
                        if (ins.Value is MethodInfo mi)
                        {
                            if (mi == StrEq || mi == StrNe) compares = true;
                            else if (mi.Name == "get_AssetGUID") readsGuid = true;
                        }
                        else if (ins.Value is FieldInfo fi && fi.Name == "icon_GUID")
                        {
                            readsGuid = true;
                        }
                    }
                    return compares && readsGuid;
                }
                catch
                {
                    return false;
                }
            }

            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (CodeInstruction ins in instructions)
                {
                    if (ins.Calls(StrEq))
                    {
                        ins.operand = MatchesMI;
                    }
                    else if (ins.Calls(StrNe))
                    {
                        ins.operand = DoesNotMatchMI;
                    }
                    yield return ins;
                }
            }
        }
    }
}
