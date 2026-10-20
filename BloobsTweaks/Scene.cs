using System;
using System.Collections.Generic;
using UnityEngine;

namespace BloobsTweaks
{
    // Finds the game's singleton-ish components even while they're hidden. Object.FindObjectOfType skips
    // inactive objects, and much of the game's UI (InventoryUI included, whenever the inventory panel is
    // closed) sits on inactive objects - so it would report "no save loaded" depending on which panel is open.
    internal static class Scene
    {
        private static readonly Dictionary<Type, Component> Cache = new Dictionary<Type, Component>();

        public static T Find<T>() where T : Component
        {
            if (Cache.TryGetValue(typeof(T), out var cached) && cached != null)
                return (T)cached;
            foreach (var c in Resources.FindObjectsOfTypeAll<T>())
            {
                if (c != null && c.gameObject.scene.IsValid())
                {
                    Cache[typeof(T)] = c;
                    return c;
                }
            }
            return null;
        }

        public static LevelUpMessage Messages => Find<InventoryUI>()?.levelUpMessage;
    }
}
