using Content.Client.Clothing;
using Content.Client.Items.Systems;
using Content.Client.Toggleable;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.Clothing;
using Content.Shared.Hands;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Serialization.TypeSerializers.Implementations;
using Robust.Shared.Utility;

namespace Content.Client._CorvaxGoob.Gilbert;

/// <summary>
/// Draws <see cref="GilbertCrackleComponent"/>: a looping crackle layer on top of the item's sprite on the floor,
/// and on top of its in-hand / worn layers on whoever carries it.
/// </summary>
public sealed class GilbertCrackleVisualsSystem : EntitySystem
{
    [Dependency] private readonly IResourceCache _cache = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    public const string LayerKey = "gilbert-crackle";
    private const string Suffix = "-gilbert-crackle";
    private const string Shader = "unshaded";

    private readonly Dictionary<ResPath, RSI?> _rsis = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertCrackleComponent, GetInhandVisualsEvent>(OnGetHeldVisuals,
            after: [typeof(ItemSystem), typeof(ToggleableVisualsSystem)]);
        SubscribeLocalEvent<GilbertCrackleComponent, GetEquipmentVisualsEvent>(OnGetEquipmentVisuals,
            after: [typeof(ClientClothingSystem), typeof(ToggleableVisualsSystem)]);
    }

    private RSI? Rsi(GilbertCrackleComponent comp)
    {
        if (_rsis.TryGetValue(comp.Sprite, out var cached))
            return cached;

        RSI? rsi = null;
        try
        {
            rsi = _cache.GetResource<RSIResource>(SpriteSpecifierSerializer.TextureRoot / comp.Sprite).RSI;
        }
        catch (Exception e)
        {
            Log.Error($"Gilbert crackle RSI {comp.Sprite} failed to load: {e.Message}");
        }

        _rsis[comp.Sprite] = rsi;
        return rsi;
    }

    /// <summary>The crackle state for a sprite state of the item, if the crackle RSI has one.</summary>
    public string? CrackleState(GilbertCrackleComponent comp, string? state)
    {
        if (string.IsNullOrEmpty(state) || Rsi(comp) is not { } rsi)
            return null;

        var fx = comp.StatePrefix + state;
        return rsi.TryGetState(fx, out _) ? fx : null;
    }

    private void AddCrackle(GilbertCrackleComponent comp, List<(string, PrototypeLayerData)> layers)
    {
        // Topmost layer that has a crackle shape (e.g. the raised shield over the folded one).
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var (key, data) = layers[i];
            if (key.EndsWith(Suffix) || CrackleState(comp, data.State) is not { } fx)
                continue;

            layers.Add((key + Suffix, new PrototypeLayerData
            {
                RsiPath = comp.Sprite.ToString(),
                State = fx,
                Shader = Shader,
            }));
            return;
        }
    }

    private void OnGetHeldVisuals(EntityUid uid, GilbertCrackleComponent comp, GetInhandVisualsEvent args)
    {
        AddCrackle(comp, args.Layers);
    }

    private void OnGetEquipmentVisuals(EntityUid uid, GilbertCrackleComponent comp, GetEquipmentVisualsEvent args)
    {
        AddCrackle(comp, args.Layers);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var query = EntityQueryEnumerator<GilbertCrackleComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var comp, out var sprite))
        {
            UpdateSprite((uid, comp, sprite));
        }
    }

    /// <summary>Keeps the crackle layer on the item's own sprite matching whatever state it is drawn with.</summary>
    public void UpdateSprite(Entity<GilbertCrackleComponent, SpriteComponent> ent)
    {
        var (uid, comp, sprite) = ent;
        if (!_sprite.LayerMapTryGet((uid, sprite), LayerKey, out var own, false))
            own = -1;

        string? fx = null;
        var index = -1;
        foreach (var layer in sprite.AllLayers)
        {
            if (++index == own || !layer.Visible)
                continue;
            if (CrackleState(comp, layer.RsiState.Name) is { } s)
                fx = s;
        }

        if (fx == comp.CurrentState)
            return;
        comp.CurrentState = fx;

        if (fx == null)
        {
            if (_sprite.LayerMapTryGet((uid, sprite), LayerKey, out var hide, false))
                _sprite.LayerSetVisible((uid, sprite), hide, false);
            return;
        }

        var idx = _sprite.LayerMapReserve((uid, sprite), LayerKey);
        _sprite.LayerSetData((uid, sprite), idx, new PrototypeLayerData
        {
            RsiPath = comp.Sprite.ToString(),
            State = fx,
            Shader = Shader,
            Visible = true,
        });
    }
}
