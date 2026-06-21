namespace Puck.Scene;

/// <summary>
/// The ONE renderability envelope shared by the scene validator and the differential fuzzer (<c>FuzzSdfProgram</c>),
/// so a hand-authored scene and a fuzz-generated one share one definition of "renders rather than driving the unbounded
/// march into a TDR". The fuzzer reads ALL fields to GENERATE within them; the validator reuses the shape-parameter
/// ranges (sphere/box/torus/round-cone/smooth) to gate authoring. The screen-slab ranges are validator-only (the fuzzer
/// never emits a screen slab), and <see cref="Translation"/>/<see cref="Scale"/> are fuzzer-GENERATION ranges only — the
/// validator does not bound object placement/scale (a content-driven renderer positions objects freely). The round-cone
/// precondition (in <c>SceneObject</c>) is a separate geometric invariant. <see cref="Default"/> is the v1 envelope; a
/// document's <c>fuzzing.bounds</c> overrides it for both consumers.
/// </summary>
/// <remarks>The defaults mirror the historic constants in <c>src/Puck.Demo/FuzzSdfProgram.cs</c>, which now consumes this type.</remarks>
public sealed record ShapeBounds {
    /// <summary>The default envelope (the fuzzer's historic generation bounds).</summary>
    public static ShapeBounds Default { get; } = new();

    /// <summary>The most materials a scene may declare / the fuzzer may generate.</summary>
    public int MaxMaterials { get; init; } = 4;
    /// <summary>The most non-plane primitives a scene may place / the fuzzer may generate.</summary>
    public int MaxPrimitives { get; init; } = 6;
    /// <summary>The fuzzer's per-axis object TRANSLATION range (generation only; the validator does not bound placement).</summary>
    public FloatRange Translation { get; init; } = new(Maximum: 1.8f, Minimum: -1.8f);
    /// <summary>The fuzzer's per-axis object SCALE range (generation only; the validator does not bound scale).</summary>
    public FloatRange Scale { get; init; } = new(Maximum: 1.6f, Minimum: 0.5f);
    /// <summary>The allowed smooth-blend radius range.</summary>
    public FloatRange Smooth { get; init; } = new(Maximum: 0.4f, Minimum: 0f);
    /// <summary>The allowed sphere radius range.</summary>
    public FloatRange SphereRadius { get; init; } = new(Maximum: 1.0f, Minimum: 0.2f);
    /// <summary>The allowed per-axis box half-extent range.</summary>
    public FloatRange BoxHalfExtent { get; init; } = new(Maximum: 0.9f, Minimum: 0.2f);
    /// <summary>The allowed box rounding-radius range.</summary>
    public FloatRange BoxRound { get; init; } = new(Maximum: 0.2f, Minimum: 0f);
    /// <summary>The allowed torus major-radius range.</summary>
    public FloatRange TorusMajorRadius { get; init; } = new(Maximum: 1.0f, Minimum: 0.4f);
    /// <summary>The allowed torus minor-radius range.</summary>
    public FloatRange TorusMinorRadius { get; init; } = new(Maximum: 0.4f, Minimum: 0.1f);
    /// <summary>The allowed round-cone lower-radius range.</summary>
    public FloatRange RoundConeLowerRadius { get; init; } = new(Maximum: 0.6f, Minimum: 0.2f);
    /// <summary>The allowed round-cone upper-radius range.</summary>
    public FloatRange RoundConeUpperRadius { get; init; } = new(Maximum: 0.4f, Minimum: 0.1f);
    /// <summary>The allowed round-cone height range.</summary>
    public FloatRange RoundConeHeight { get; init; } = new(Maximum: 1.2f, Minimum: 0.4f);
    /// <summary>The allowed per-axis screen-slab half-extent range.</summary>
    public FloatRange ScreenSlabHalfExtent { get; init; } = new(Maximum: 1.0f, Minimum: 0.01f);
    /// <summary>The allowed screen-slab rounding-radius range.</summary>
    public FloatRange ScreenSlabRound { get; init; } = new(Maximum: 0.5f, Minimum: 0f);
}
