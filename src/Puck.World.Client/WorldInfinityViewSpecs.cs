using Puck.Assets.Documents;
using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.SdfVm.Views;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>
/// The infinity views a world's sky authors, as the neutral records the host renders (<see cref="InfinityViewSpec"/>):
/// each <see cref="WorldRenderSkyLayer.View"/> and <see cref="WorldRenderSkyLayer.Far"/> layer lowered to its record, in
/// the order the stack authors them. A layer's cone mask, written in the sky frame, becomes the cone about the same
/// direction in the viewer's world frame (the frame's axes carry it), and a layer with no mask covers the whole frustum.
/// </summary>
public static class WorldInfinityViewSpecs {
    /// <summary>The render scale a view takes when its layer states none.</summary>
    public const float DefaultScale = 0.5f;
    /// <summary>The refresh divisor a view takes when its layer states none.</summary>
    public const int DefaultRefresh = 2;
    /// <summary>The far distance a view takes when its layer states none.</summary>
    public const float DefaultFarDistance = 1000f;

    /// <summary>Returns the views a sky authors.</summary>
    /// <param name="sky">The sky, or <see langword="null"/> for none.</param>
    /// <param name="fallback">Resolves a layer's fallback colour (it may bind a state row), given the layer's authored
    /// colour; <see langword="null"/> reads a literal colour or black.</param>
    /// <returns>One record per view or far layer, in stack order; empty for none.</returns>
    public static IReadOnlyList<InfinityViewSpec> Of(WorldRenderSky? sky, Func<BindableColor?, Vector3>? fallback = null) {
        if (sky?.Layers is not { } layers) {
            return [];
        }

        var frame = SdfSky.FrameOf(up: ((sky.Frame?.Up is { } up) ? ((Vector3)up) : Vector3.UnitY));
        var specs = new List<InfinityViewSpec>();

        foreach (var layer in layers) {
            switch (layer) {
                case WorldRenderSkyLayer.View view:
                    specs.Add(item: Lower(
                        anchor: view.Anchor,
                        ambientOcclusion: view.AmbientOcclusion,
                        farDistance: view.FarDistance,
                        fallback: view.Fallback,
                        fallbackOf: fallback,
                        frame: frame,
                        kind: InfinityViewKind.World,
                        layer: view,
                        name: view.Name,
                        refresh: view.Refresh,
                        scale: view.Scale,
                        shadows: view.Shadows,
                        turn: view.Turn
                    ));

                    break;
                case WorldRenderSkyLayer.Far far:
                    specs.Add(item: Lower(
                        anchor: far.Anchor,
                        ambientOcclusion: far.AmbientOcclusion,
                        farDistance: far.FarDistance,
                        fallback: far.Fallback,
                        fallbackOf: fallback,
                        frame: frame,
                        kind: InfinityViewKind.Far,
                        layer: far,
                        name: far.Name,
                        refresh: far.Refresh,
                        scale: far.Scale,
                        shadows: far.Shadows,
                        turn: far.Turn
                    ));

                    break;
            }
        }

        return specs;
    }

    private static InfinityViewSpec Lower(WorldRenderSkyLayer layer, InfinityViewKind kind, string? name, DocumentVector3? anchor, float? turn, float? scale, int? refresh, float? farDistance, bool? shadows, bool? ambientOcclusion, BindableColor? fallback, Func<BindableColor?, Vector3>? fallbackOf, (Vector3 Right, Vector3 Up, Vector3 Forward) frame) {
        InfinityViewMask? mask = null;

        if (layer.Mask?.Cone is { } cone) {
            var toward = ((Vector3)cone.Toward);

            mask = new InfinityViewMask(
                Axis: (((toward.X * frame.Right) + (toward.Y * frame.Up)) + (toward.Z * frame.Forward)),
                HalfAngle: ((float)cone.Spread)
            );
        }

        return new InfinityViewSpec(
            Anchor: ((anchor is { } point) ? point.Value : Vector3.Zero),
            Fallback: ((fallbackOf is not null) ? fallbackOf(arg: fallback) : Vector3.Zero),
            FarDistance: (farDistance ?? DefaultFarDistance),
            Kind: kind,
            Levers: ((shadows == true) ? InfinityViewLevers.Shadows : InfinityViewLevers.None) | ((ambientOcclusion == true) ? InfinityViewLevers.AmbientOcclusion : InfinityViewLevers.None),
            Mask: mask,
            MinimumTier: (WorldSkyLayers.TierOf(layer: layer) switch {
                SdfSkyTier.High => QualityTier.High,
                SdfSkyTier.Medium => QualityTier.Medium,
                _ => QualityTier.Low,
            }),
            Name: (name ?? string.Empty),
            Orientation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (((turn ?? 0f) * MathF.PI) / 180f)),
            Refresh: (refresh ?? DefaultRefresh),
            Scale: (scale ?? DefaultScale)
        );
    }
}
