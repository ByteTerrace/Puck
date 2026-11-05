using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World.Client;

// Palette lowering shared by creation and lattice emitters.
internal static class WorldSurfaceMaterials {
    // Shared palette lowering for creations and lattice surfaces. Callers resolve parented paint frames.
    internal static SdfMaterial Resolve(PaletteEntryDocument? entry, Vector3 albedo, WorldBakedColors colors, float scale = 1f, SdfPaint[]? paints = null) => new(
                Albedo: albedo,
                Emissive: (entry?.Emissive ?? 0f),
                Roughness: (entry?.Roughness ?? SdfMaterial.DefaultRoughness),
                Sheen: (entry?.Sheen ?? 0f),
                Specular: (entry?.Specular ?? 0f),
                Metal: (entry?.Metal ?? 0f),
                Coat: (entry?.Coat ?? 0f),
                Weathering: entry?.Weathering?.ToWeathering(resolve: colors.LayerColor, scale: scale),
                Wrap: (entry?.Wrap ?? 0f),
                Soften: (entry?.Soften ?? 0f),
                Fill: colors.Resolve(
                    fallback: Vector3.Zero,
                    value: entry?.Fill
                ),
                Inset: entry?.Inset?.ToInset(resolve: colors.LayerColor, scale: scale),
                Bleed: colors.Resolve(fallback: Vector3.One, value: entry?.Bleed),
                Receive: (entry?.Receive ?? 1f),
                Paints: paints,
                EmissiveLane: entry?.EmissiveLane,
                Glass: entry?.Glass?.ToGlass(scale: scale),
                Toon: (((entry?.ToonSelect is { } select) ? select.CaseOf(value: colors.ResolveWhole(token: select.State)) : null) ?? entry?.Toon)?.ToToon(),
                Flow: entry?.Flow?.ToFlow(resolve: colors.LayerColor, scale: scale)
            );

}
