using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// Lets the game's own item system (which always loads item prefabs through Addressables,
    /// by GUID) resolve GUIDs we invented ourselves, backed by GameObjects we built at runtime
    /// instead of anything in the game's packed asset catalog. Same technique the already-installed
    /// "MiningDrill" mod on this machine uses to add its own new placeable item; ported here for a
    /// hand-held consumable instead of a placeable.
    /// </summary>
    internal static class ExplosiveAddressablesInterceptor
    {
        private sealed class Locator : IResourceLocator
        {
            private readonly string _providerId;

            public string LocatorId => "HumanHostExplosivesLocator";

            public IEnumerable<object> Keys => Items.Keys;

            internal Locator(string providerId)
            {
                _providerId = providerId;
            }

            public bool Locate(object key, Type type, out IList<IResourceLocation> locations)
            {
                locations = null;
                string guid = key as string;
                if (guid == null && key is AssetReference assetRef)
                {
                    guid = assetRef.AssetGUID;
                }
                if (guid == null || !Items.ContainsKey(guid))
                {
                    return false;
                }
                locations = new List<IResourceLocation>
                {
                    new ResourceLocationBase(guid, guid, _providerId, typeof(GameObject))
                };
                return true;
            }
        }

        private sealed class Provider : ResourceProviderBase
        {
            public override void Provide(ProvideHandle handle)
            {
                try
                {
                    if (Items.TryGetValue(handle.Location.PrimaryKey, out GameObject value) && value != null)
                    {
                        handle.Complete(value, true, null);
                    }
                    else
                    {
                        handle.Complete<GameObject>(null, false,
                            new InvalidOperationException("HumanHostExplosives: no registered item for key " + handle.Location.PrimaryKey));
                    }
                }
                catch (Exception ex)
                {
                    handle.Complete<GameObject>(null, false, ex);
                }
            }

            public override Type GetDefaultType(IResourceLocation location) => typeof(GameObject);
        }

        internal static readonly Dictionary<string, GameObject> Items = new Dictionary<string, GameObject>();

        private static bool _installed;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            var provider = new Provider();
            Addressables.ResourceManager.ResourceProviders.Add(provider);
            Addressables.AddResourceLocator(new Locator(provider.ProviderId));
            _installed = true;
            Plugin.Log.LogInfo("[Registry] custom Addressables locator/provider installed");
        }
    }
}
