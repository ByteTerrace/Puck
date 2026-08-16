using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>The engine-wide soft-shadow quality tier. <see cref="High"/> is the full reach and <see cref="Off"/> the
/// cheapest; <see cref="Low"/> and <see cref="Medium"/> shorten the shadow reach (both the gather cull cone and the
/// march ceiling — one shared length) between them.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShadowTier>))]
public enum ShadowTier {
    /// <summary>No soft shadows (the single most expensive shading term skipped).</summary>
    Off,

    /// <summary>Soft shadows at QUARTER reach (<c>ShadowDistanceScale</c> 0.25) — only near contact shadows survive.</summary>
    Low,

    /// <summary>Soft shadows at HALF reach (<c>ShadowDistanceScale</c> 0.5) — far shadows fade, mid shadows stay.</summary>
    Medium,

    /// <summary>The full 1.0 shadow reach (<c>ShadowDistanceScale</c> 0, the engine default).</summary>
    High,
}
/// <summary>Named facades over the continuous soft-shadow reach used at runtime.</summary>
public static class ShadowTiers {
    /// <summary>Names a continuous soft-shadow reach — <c>"off"</c>, <c>"low"</c>, <c>"medium"</c>, or <c>"high"</c>
    /// for a reach matching one of the four named tiers, or a formatted percentage otherwise.</summary>
    /// <param name="reach">The continuous shadow reach.</param>
    /// <returns>The tier name, or a formatted percentage when <paramref name="reach"/> matches none.</returns>
    public static string Name(float reach) {
        if (MathF.Abs(x: reach) <= 0.0001f) {
            return "off";
        }

        if (MathF.Abs(x: (reach - 0.25f)) <= 0.0001f) {
            return "low";
        }

        if (MathF.Abs(x: (reach - 0.5f)) <= 0.0001f) {
            return "medium";
        }

        if (MathF.Abs(x: (reach - 1f)) <= 0.0001f) {
            return "high";
        }

        return string.Create(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"{(reach * 100f):0.#}%"
        );
    }
    /// <summary>Returns the continuous soft-shadow reach scale for a named tier — the reverse of <see cref="Tier"/>.</summary>
    /// <param name="tier">The named shadow tier.</param>
    /// <returns>The shadow reach scale: 0 for <see cref="ShadowTier.Off"/>, 0.25 for <see cref="ShadowTier.Low"/>, 0.5 for <see cref="ShadowTier.Medium"/>, and 1 for <see cref="ShadowTier.High"/>.</returns>
    public static float Scale(ShadowTier tier) => tier switch {
        ShadowTier.Off => 0f,
        ShadowTier.Low => 0.25f,
        ShadowTier.Medium => 0.5f,
        _ => 1f,
    };
    /// <summary>Returns the nearest named <see cref="ShadowTier"/> to a continuous soft-shadow reach — the reverse of
    /// <see cref="Scale"/>, used when <c>world.save</c> folds the live reach back into the document's tiered
    /// <see cref="WorldRenderDefaults.Shadows"/> boot default. Round-trips exactly for the four tier scales (0/.25/.5/1);
    /// a continuous authoring override quantizes to its closest tier (the document holds only tiers).</summary>
    public static ShadowTier Tier(float reach) {
        var best = ShadowTier.High;
        var bestDelta = float.MaxValue;

        foreach (var tier in Enum.GetValues<ShadowTier>()) {
            var delta = MathF.Abs(x: (reach - Scale(tier: tier)));

            if (delta < bestDelta) {
                best = tier;
                bestDelta = delta;
            }
        }

        return best;
    }
}
