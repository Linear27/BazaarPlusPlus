#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Cards.Skill;
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.Game.CollectionPanel.Data;
using BazaarPlusPlus.GameInterop.CardPreview;
using BazaarPlusPlus.Infrastructure;
using TheBazaar.AppFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BazaarPlusPlus.Game.CollectionPanel.Grid;

// Resolves a CollectionCardVm into a native UI card using the game's current AssetLoader
// path. The collection grid is virtualized, so bindings may be returned before their async
// InstantiateUICardAsync call completes; the binding owns that race and destroys late cards.
internal sealed class CollectionCardFactory
{
    private readonly Transform _parent;
    private readonly int _layer;
    private readonly GameObject _stagingRoot;
    private readonly List<GameObject> _created = new();
    private readonly HashSet<CollectionCardBinding> _bindings = new();
    private bool _disposed;
    private int _instanceCounter;

    public CollectionCardFactory(Transform parent, int layer)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _layer = layer;

        _stagingRoot = new GameObject(
            "CollectionPanelCardStaging",
            typeof(RectTransform),
            typeof(CanvasGroup)
        );
        _stagingRoot.transform.SetParent(parent, worldPositionStays: false);
        NativeCardPreviewReflection.ApplyLayerRecursive(_stagingRoot, layer);

        var canvasGroup = _stagingRoot.GetComponent<CanvasGroup>();
        if (canvasGroup != null)
        {
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }
    }

    public bool ReflectionReady => NativeCardPreviewReflection.CardPreviewBaseType != null;

    public CollectionCardBinding? TryBind(CollectionCardVm vm)
    {
        if (vm == null)
            return null;

        if (_disposed)
            return null;

        if (!Services.TryGet<AssetLoader>(out var assetLoader) || assetLoader == null)
            return null;

        var kind =
            vm.Type == ECardType.Skill
                ? NativeCardPreviewKind.ForSkill()
                : NativeCardPreviewKind.ForItem(vm.Size);

        var instance = BuildSyntheticInstance(vm);
        var binding = new CollectionCardBinding(kind);
        _bindings.Add(binding);
        _ = CreateCardAsync(binding, assetLoader, instance, kind, _instanceCounter);
        return binding;
    }

    public void Return(CollectionCardBinding? binding)
    {
        if (binding == null)
            return;

        binding.MarkReleased();
        _bindings.Remove(binding);
        DestroyBoundCard(binding);
    }

    public void DestroyAll()
    {
        _disposed = true;
        foreach (var binding in _bindings)
            binding.MarkReleased();
        _bindings.Clear();

        foreach (var go in _created)
        {
            if (go != null)
                Object.Destroy(go);
        }
        _created.Clear();

        if (_stagingRoot != null)
            Object.Destroy(_stagingRoot);
    }

    private async Task CreateCardAsync(
        CollectionCardBinding binding,
        AssetLoader assetLoader,
        TCardInstance instance,
        NativeCardPreviewKind kind,
        int instanceIndex
    )
    {
        GameObject? go = null;
        try
        {
            if (_disposed || _stagingRoot == null)
            {
                binding.MarkReady();
                return;
            }

            go = await assetLoader.InstantiateUICardAsync(
                instance,
                _stagingRoot.transform,
                CancellationToken.None
            );
            if (go == null)
            {
                binding.MarkReady();
                return;
            }

            if (_disposed || binding.IsReleased)
            {
                Object.Destroy(go);
                binding.MarkReady();
                return;
            }

            go.name = $"CollectionPanelCard_{kind}_{Mathf.Max(0, instanceIndex)}";
            NativeCardPreviewReflection.ApplyLayerRecursive(go, _layer);

            var card = go.GetComponent(NativeCardPreviewReflection.CardPreviewBaseType!);
            var rect = go.transform as RectTransform ?? go.GetComponent<RectTransform>();
            if (card == null || rect == null)
            {
                BppLog.Warn(
                    "CollectionCardFactory",
                    $"Instantiated collection card without CardPreviewBase/RectTransform template={instance.TemplateId}."
                );
                Object.Destroy(go);
                binding.MarkReady();
                return;
            }

            if (go.GetComponent<CollectionPanelOwnedMarker>() == null)
                go.AddComponent<CollectionPanelOwnedMarker>();

            var canvasGroup = go.GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = go.AddComponent<CanvasGroup>();
            canvasGroup.alpha = 0f;

            go.transform.SetParent(_parent, worldPositionStays: false);
            go.SetActive(true);
            NativeCardPreviewRuntime.Resize(card, "CollectionCardFactory");
            go.SetActive(false);

            _created.Add(go);
            binding.Bind(card, rect);
            binding.MarkReady();
        }
        catch (Exception ex)
        {
            if (go != null)
                Object.Destroy(go);
            BppLog.Warn(
                "CollectionCardFactory",
                $"InstantiateUICardAsync failed for collection template={instance.TemplateId}: {ex.Message}"
            );
            binding.MarkFailed(ex);
        }
    }

    private void DestroyBoundCard(CollectionCardBinding binding)
    {
        var card = binding.Card;
        if (card == null)
            return;

        var go = card.gameObject;
        if (go != null)
        {
            _created.Remove(go);
            Object.Destroy(go);
        }
    }

    private TCardInstance BuildSyntheticInstance(CollectionCardVm vm)
    {
        var attributes = new Dictionary<ECardAttributeType, int>();
        var id = $"bpp-collection-{++_instanceCounter}";

        if (vm.Type == ECardType.Skill)
        {
            return new TCardInstanceSkill
            {
                TemplateId = vm.Id,
                TemplateVersion = string.Empty,
                InstanceId = id,
                Tier = vm.StartingTier,
                Attributes = attributes,
            };
        }

        return new TCardInstanceItem
        {
            TemplateId = vm.Id,
            TemplateVersion = string.Empty,
            InstanceId = id,
            Tier = vm.StartingTier,
            Attributes = attributes,
        };
    }
}

// One realized collection card request. Card/Rect are populated only after the game's
// AssetLoader has created and bound the native UI card.
internal sealed class CollectionCardBinding
{
    private readonly TaskCompletionSource<object?> _ready = new();

    public CollectionCardBinding(NativeCardPreviewKind kind)
    {
        Kind = kind;
        SetUpTask = _ready.Task;
    }

    public Component? Card { get; private set; }
    public RectTransform? Rect { get; private set; }
    public NativeCardPreviewKind Kind { get; }
    public Task SetUpTask { get; }
    public bool IsReleased { get; private set; }

    public void Bind(Component card, RectTransform rect)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
        Rect = rect ?? throw new ArgumentNullException(nameof(rect));
    }

    public void MarkReady() => _ready.TrySetResult(null);

    public void MarkFailed(Exception ex) => _ready.TrySetException(ex);

    public void MarkReleased() => IsReleased = true;
}
