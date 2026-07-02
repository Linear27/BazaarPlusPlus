#nullable enable
#pragma warning disable CS0436
using BazaarPlusPlus.Game.CollectionPanel.Grid;
using HarmonyLib;
using TheBazaar.UI;

namespace BazaarPlusPlus.Patches.CollectionPanel;

// CardPreviewBase.OnDestroy ends with `if (_cardMaterial) Object.Destroy(_cardMaterial)`.
// When the collection LoadArt patch has assigned a shared material from
// CollectionCardMaterialCache, the cache owns that material and releases it here before the
// original destroy branch can touch it. Cards created entirely by AssetLoader without a
// tracked cache material fall through to the game's normal material lifecycle.
//
// Also Release the L2 art-cache refcount so the LRU eviction can reclaim entries that no
// longer back any live card.
[HarmonyPatch(typeof(CardPreviewBase), "OnDestroy")]
internal static class CollectionCardPreviewDestroyPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardPreviewBase __instance)
    {
        var marker = __instance.GetComponent<CollectionPanelOwnedMarker>();
        if (marker == null)
            return;

        var artCache = CollectionCardCacheHost.ArtCache;
        var materialCache = CollectionCardCacheHost.MaterialCache;
        var hasTrackedArtKey = !string.IsNullOrEmpty(marker.CurrentArtKey);
        if (artCache != null && hasTrackedArtKey)
        {
            artCache.Release(marker.CurrentArtKey!);
            materialCache?.Release(marker.CurrentArtKey!);
            marker.CurrentArtKey = null;
        }

        if (hasTrackedArtKey && materialCache?.Contains(__instance._cardMaterial) == true)
            __instance._cardMaterial = null!;
    }
}
