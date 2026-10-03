using Puck.SignedDistance;

namespace Puck.World;

/// <summary>What a document's sky layer is to the engine: its kind, class, blend, visibility, tier and the label its
/// counted rows carry, each absent field resolved to its kind's default. The validator and the presentation read the one
/// mapping, so a stack is judged as it draws.</summary>
public static class WorldSkyLayers {
    /// <summary>Returns a layer's engine kind.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The kind, or <see langword="null"/> for a layer of no kind the engine draws.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layer"/> is <see langword="null"/>.</exception>
    public static SdfSkyLayerKind? KindOf(WorldRenderSkyLayer layer) {
        ArgumentNullException.ThrowIfNull(argument: layer);

        return layer switch {
            WorldRenderSkyLayer.Gradient => SdfSkyGradient.Kind,
            WorldRenderSkyLayer.SunDisc => SdfSkyDisc.Kind,
            WorldRenderSkyLayer.Stars => SdfSkyStars.Kind,
            WorldRenderSkyLayer.Clouds => SdfSkyClouds.Kind,
            WorldRenderSkyLayer.Aurora => SdfSkyAurora.Kind,
            WorldRenderSkyLayer.Noise => SdfSkyNoise.Kind,
            WorldRenderSkyLayer.Pattern => SdfSkyPattern.Kind,
            WorldRenderSkyLayer.Panorama => SdfSkyPanorama.Kind,
            _ => null,
        };
    }
    /// <summary>Returns a layer's class, its kind's.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The class.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layer"/> is <see langword="null"/>.</exception>
    public static SdfSkyLayerClass ClassOf(WorldRenderSkyLayer layer) => KindOf(layer: layer) switch {
        SdfSkyLayerKind.Stars => SdfSkyStars.Class,
        SdfSkyLayerKind.Disc => SdfSkyDisc.Class,
        _ => SdfSkyLayerClass.Field,
    };
    /// <summary>Returns how a layer composes: its authored blend, or its kind's, <see cref="SdfSkyBlend.Add"/> for stars, a
    /// sun disc and an aurora and <see cref="SdfSkyBlend.Over"/> for every other kind.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The blend.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layer"/> is <see langword="null"/>.</exception>
    public static SdfSkyBlend BlendOf(WorldRenderSkyLayer layer) {
        ArgumentNullException.ThrowIfNull(argument: layer);

        return layer.Blend switch {
            WorldSkyBlend.Over => SdfSkyBlend.Over,
            WorldSkyBlend.Add => SdfSkyBlend.Add,
            WorldSkyBlend.Multiply => SdfSkyBlend.Multiply,
            WorldSkyBlend.Screen => SdfSkyBlend.Screen,
            _ => ((layer is WorldRenderSkyLayer.Stars or WorldRenderSkyLayer.SunDisc or WorldRenderSkyLayer.Aurora) ? SdfSkyBlend.Add : SdfSkyBlend.Over),
        };
    }
    /// <summary>Returns who sees a layer: its authored visibility, or its kind's, the camera and the lighting for a
    /// gradient and the camera alone for every other kind.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The visibility.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layer"/> is <see langword="null"/>.</exception>
    public static SdfSkyVisibility VisibilityOf(WorldRenderSkyLayer layer) {
        ArgumentNullException.ThrowIfNull(argument: layer);

        return layer.Visibility switch {
            WorldSkyVisibility.Camera => SdfSkyVisibility.Camera,
            WorldSkyVisibility.Lighting => SdfSkyVisibility.Lighting,
            WorldSkyVisibility.Both => SdfSkyVisibility.Both,
            _ => ((layer is WorldRenderSkyLayer.Gradient) ? SdfSkyVisibility.Both : SdfSkyVisibility.Camera),
        };
    }
    /// <summary>Returns the lowest quality tier a layer draws at: its authored tier, or <see cref="SdfSkyTier.Low"/>.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The tier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layer"/> is <see langword="null"/>.</exception>
    public static SdfSkyTier TierOf(WorldRenderSkyLayer layer) {
        ArgumentNullException.ThrowIfNull(argument: layer);

        return TierOf(tier: (layer.Tier ?? WorldSkyTier.Low));
    }
    /// <summary>Returns the engine's tier for a document tier.</summary>
    /// <param name="tier">The document tier.</param>
    /// <returns>The engine tier.</returns>
    public static SdfSkyTier TierOf(WorldSkyTier tier) => tier switch {
        WorldSkyTier.Medium => SdfSkyTier.Medium,
        WorldSkyTier.High => SdfSkyTier.High,
        _ => SdfSkyTier.Low,
    };
    /// <summary>Returns the kind name a layer's rows are labelled with when it has no name: <c>gradient</c>, <c>disc</c>,
    /// <c>stars</c>, <c>clouds</c>, <c>aurora</c>, <c>noise</c>, <c>pattern</c> or <c>panorama</c>.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The name, or <see langword="null"/> for a layer of no kind the engine draws.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layer"/> is <see langword="null"/>.</exception>
    public static string? KindNameOf(WorldRenderSkyLayer layer) => KindOf(layer: layer) switch {
        SdfSkyLayerKind.Gradient => SdfSkyGradient.Name,
        SdfSkyLayerKind.Disc => SdfSkyDisc.Name,
        SdfSkyLayerKind.Stars => SdfSkyStars.Name,
        SdfSkyLayerKind.Clouds => SdfSkyClouds.Name,
        SdfSkyLayerKind.Aurora => SdfSkyAurora.Name,
        SdfSkyLayerKind.Noise => SdfSkyNoise.Name,
        SdfSkyLayerKind.Pattern => SdfSkyPattern.Name,
        SdfSkyLayerKind.Panorama => SdfSkyPanorama.Name,
        _ => null,
    };
    /// <summary>Returns the labels each layer of a stack counts its work under: its name, or its kind's name, with
    /// <c>#2</c>, <c>#3</c> and so on after the second and later unnamed layers of one kind.</summary>
    /// <param name="layers">The stack, lowest first.</param>
    /// <returns>One label a layer, <see langword="null"/> for a missing layer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers"/> is <see langword="null"/>.</exception>
    public static string?[] LabelsOf(IReadOnlyList<WorldRenderSkyLayer?> layers) {
        ArgumentNullException.ThrowIfNull(argument: layers);

        var labels = new string?[layers.Count];
        var seen = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < layers.Count); index++) {
            if ((layers[index] is not { } layer) || (KindNameOf(layer: layer) is not { } kind)) {
                continue;
            }
            if (layer.LayerName is { } name) {
                labels[index] = name;

                continue;
            }

            var count = (seen.GetValueOrDefault(key: kind) + 1);

            seen[kind] = count;
            labels[index] = ((count == 1) ? kind : $"{kind}#{count}");
        }

        return labels;
    }
}
