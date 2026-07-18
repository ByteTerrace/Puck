using Puck.Scene;

namespace Puck.World;

/// <summary>The engine-wide soft-shadow quality tier. <see cref="High"/> is the full reach and <see cref="Off"/> the
/// cheapest; <see cref="Low"/> and <see cref="Medium"/> shorten the shadow reach (both the gather cull cone and the
/// march ceiling — one shared length) between them.</summary>
internal enum ShadowTier {
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
internal static class ShadowTiers {
    public static float Scale(ShadowTier tier) => tier switch {
        ShadowTier.Off => 0f,
        ShadowTier.Low => 0.25f,
        ShadowTier.Medium => 0.5f,
        _ => 1f,
    };

    /// <summary>The nearest named <see cref="ShadowTier"/> to a continuous soft-shadow reach — the reverse of
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

        return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"{(reach * 100f):0.#}%");
    }
}

/// <summary>The soft-shadow candidate-mask policy. <see cref="Auto"/> keeps exact gathers for small sessions and uses
/// the camera-tile approximation at the declared fleet tiers; the other values are live profiling/authoring overrides.</summary>
internal enum ShadowMaskMode {
    Auto,
    ExactGather,
    CameraTile,
}

/// <summary>The soft-shadow march quality policy. Auto keeps the exact engine path for small sessions and selects the
/// bounded-cost approximation at fleet scale; the other values are live profiling/authoring overrides.</summary>
internal enum ShadowMarchMode {
    Auto,
    Exact,
    Fast,
}

/// <summary>The ambient-occlusion sampling policy. Auto keeps the quality ladder for small sessions and selects the
/// calibrated one-sample contact approximation at fleet scale; the other values are live profiling overrides.</summary>
internal enum AmbientOcclusionMode {
    Auto,
    Exact,
    Fast,
}

/// <summary>
/// The world's live render settings — the engine-wide levers (shadows, ambient occlusion, render scale) mutated by
/// console verbs in real time and read by <see cref="Client.WorldFrameSource"/> every captured frame. Session state, not
/// identity: per-player preferences belong on the profile.
/// </summary>
internal sealed class WorldRenderSettings {
    /// <summary>Initializes a new instance of the <see cref="WorldRenderSettings"/> class from the world definition's
    /// render-lever boot defaults (<see cref="WorldRenderDefaults"/>), copied into the live, mutable settings the
    /// console verbs move from here.</summary>
    /// <param name="defaults">The render-lever boot defaults to wake on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    public WorldRenderSettings(WorldRenderDefaults defaults) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        ShadowReach = ShadowTiers.Scale(tier: defaults.Shadows);
        ShadowCrowdRadius = defaults.ShadowCrowdRadius;
        ShadowMask = ShadowMaskMode.Auto;
        ShadowMarch = ShadowMarchMode.Auto;
        AmbientOcclusionQuality = AmbientOcclusionMode.Auto;
        AmbientOcclusion = defaults.AmbientOcclusion;
        RenderScale = WorldRenderScaleTiers.Scale(tier: defaults.RenderScale);
        UpscaleSharpness = defaults.UpscaleSharpness;
        FarBound = true;
        ShadowFarExit = true;
    }

    /// <summary>The engine-wide soft-shadow reach fraction from 0 (off) through 1 (full reach). Named tiers are facades
    /// over this continuous value. The <c>world.shadows</c> verb moves it live through the per-frame
    /// <see cref="Puck.SdfVm.SdfFrame.DisableSoftShadows"/> / <see cref="Puck.SdfVm.SdfFrame.ShadowDistanceScale"/> lanes,
    /// so no rebuild).</summary>
    public float ShadowReach { get; set; }

    /// <summary>The soft-shadow crowd radius (world units): an avatar within this distance of any joined local seat casts
    /// soft shadows; beyond it, it is suppressed from the soft-shadow march only (still rendered, still self-lit). Boots
    /// at the definition's default; the <c>world.shadows</c> verb's optional second arg moves it live (it rides the
    /// per-instance <see cref="Puck.SdfVm.DynamicTransform.CastsSoftShadow"/> lane <see cref="Client.WorldFrameSource"/> computes
    /// per frame, so no rebuild). 0 = only the local seats cast; a value ≥ the world's diameter = everyone casts. Bounding
    /// who casts is how the population scales, since soft shadows dominate the GPU cost.</summary>
    public float ShadowCrowdRadius { get; set; }

    /// <summary>The live shadow candidate-mask policy. Auto selects the camera-tile approximation at 16 or more
    /// simulated stand-ins; exact and camera-tile are explicit A/B overrides.</summary>
    public ShadowMaskMode ShadowMask { get; set; }

    /// <summary>The live soft-shadow march policy. Auto selects the bounded-cost path at 16 or more simulated stand-ins;
    /// exact and fast are explicit A/B overrides.</summary>
    public ShadowMarchMode ShadowMarch { get; set; }

    /// <summary>Whether ambient occlusion is on. Boots at the definition's default (<see langword="false"/> in the built-in
    /// world); the <c>world.ao</c> verb toggles it live (it rides the per-frame
    /// <see cref="Puck.SdfVm.SdfFrame.DisableAmbientOcclusion"/> lane, so no rebuild).</summary>
    public bool AmbientOcclusion { get; set; }

    /// <summary>The live ambient-occlusion sampling policy. Auto selects the one-sample contact approximation at 16 or
    /// more simulated stand-ins; exact and fast are explicit visual/performance A/B overrides.</summary>
    public AmbientOcclusionMode AmbientOcclusionQuality { get; set; }

    /// <summary>The engine-wide internal render-scale fraction, applied to every player view's
    /// <see cref="Puck.SdfVm.SdfViewSnapshot.RenderScale"/> each frame. Named tiers initialize it, while
    /// <c>world.render-scale</c> also accepts a live numeric fraction/percentage for performance sweeps. Native 1.0 is
    /// the bit-exact fast path; lower values use the compositor reconstruction selected by
    /// <see cref="UpscaleSharpness"/>.</summary>
    public float RenderScale { get; set; }
    /// <summary>The continuous reduced-resolution reconstruction blend: 0 is bilinear, 1 is clamped Catmull-Rom, and
    /// intermediate values blend between them. Native render scale ignores it.</summary>
    public float UpscaleSharpness { get; set; }

    /// <summary>Whether the F1 beam-published per-tile FAR BOUND is active (default <see langword="true"/> = the shipped
    /// behavior). Set <see langword="false"/> (via <c>world.far-field bound off</c>) to march far-field sky rays to
    /// MaxDistance exactly as pre-F1 — the "off" side of the owner's far-field A/B. Rides the per-frame
    /// <see cref="Puck.SdfVm.SdfFrame.DisableFarBound"/> lane <see cref="Client.WorldFrameSource"/> inverts each frame, so no
    /// rebuild. A pure performance isolator (output-identical when on), so it is session state, never durable config.</summary>
    public bool FarBound { get; set; }

    /// <summary>Whether the F2 soft-shadow light-side EARLY EXIT is active (default <see langword="true"/> = the shipped
    /// behavior). Set <see langword="false"/> (via <c>world.far-field shadow off</c>) to run the full shadow step
    /// budget/reach exactly as pre-F2 — the "off" side of the owner's shadow far-exit A/B. Rides the per-frame
    /// <see cref="Puck.SdfVm.SdfFrame.DisableShadowFarExit"/> lane <see cref="Client.WorldFrameSource"/> inverts each frame. A
    /// march-path change (not bit-identical), so it lives in session state, not durable config.</summary>
    public bool ShadowFarExit { get; set; }
}
