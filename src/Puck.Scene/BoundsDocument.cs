using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// Overrides for the shared renderability envelope (<see cref="ShapeBounds"/>). Every field is optional and replaces
/// the corresponding default; range fields are authored as <c>[min, max]</c> arrays. The resulting envelope is applied
/// to BOTH the differential fuzzer (which generates within it) AND the scene validator (which gates authoring against
/// the shape-parameter ranges) — one definition for both. The screen-slab ranges are not exposed here (the fuzzer never
/// emits a screen slab; the validator keeps its defaults).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BoundsDocument {
    /// <summary>The most materials the fuzzer may generate / a scene may declare.</summary>
    public int? MaxMaterials { get; init; }
    /// <summary>The most non-plane primitives the fuzzer may generate / a scene may place.</summary>
    public int? MaxPrimitives { get; init; }
    /// <summary>The fuzzer's per-axis translation range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? Translation { get; init; }
    /// <summary>The fuzzer's per-axis scale range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? Scale { get; init; }
    /// <summary>The smooth-blend radius range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? Smooth { get; init; }
    /// <summary>The sphere radius range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? SphereRadius { get; init; }
    /// <summary>The per-axis box half-extent range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? BoxHalfExtent { get; init; }
    /// <summary>The box rounding-radius range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? BoxRound { get; init; }
    /// <summary>The torus major-radius range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? TorusMajorRadius { get; init; }
    /// <summary>The torus minor-radius range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? TorusMinorRadius { get; init; }
    /// <summary>The round-cone lower-radius range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? RoundConeLowerRadius { get; init; }
    /// <summary>The round-cone upper-radius range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? RoundConeUpperRadius { get; init; }
    /// <summary>The round-cone height range, as <c>[min, max]</c>.</summary>
    public IReadOnlyList<float>? RoundConeHeight { get; init; }

    /// <summary>Overlays the provided overrides onto <see cref="ShapeBounds.Default"/>.</summary>
    /// <returns>The effective envelope.</returns>
    public ShapeBounds ToShapeBounds() {
        var defaults = ShapeBounds.Default;

        return defaults with {
            BoxHalfExtent = ToRange(fallback: defaults.BoxHalfExtent, value: BoxHalfExtent),
            BoxRound = ToRange(fallback: defaults.BoxRound, value: BoxRound),
            MaxMaterials = (MaxMaterials ?? defaults.MaxMaterials),
            MaxPrimitives = (MaxPrimitives ?? defaults.MaxPrimitives),
            RoundConeHeight = ToRange(fallback: defaults.RoundConeHeight, value: RoundConeHeight),
            RoundConeLowerRadius = ToRange(fallback: defaults.RoundConeLowerRadius, value: RoundConeLowerRadius),
            RoundConeUpperRadius = ToRange(fallback: defaults.RoundConeUpperRadius, value: RoundConeUpperRadius),
            Scale = ToRange(fallback: defaults.Scale, value: Scale),
            Smooth = ToRange(fallback: defaults.Smooth, value: Smooth),
            SphereRadius = ToRange(fallback: defaults.SphereRadius, value: SphereRadius),
            TorusMajorRadius = ToRange(fallback: defaults.TorusMajorRadius, value: TorusMajorRadius),
            TorusMinorRadius = ToRange(fallback: defaults.TorusMinorRadius, value: TorusMinorRadius),
            Translation = ToRange(fallback: defaults.Translation, value: Translation),
        };
    }

    // The fuzzer packs each count into a program word and allocates one slot per material/primitive; an unbounded
    // override would overflow `count + 1` in FuzzSdfProgram.Generate (an unhandled crash during host start) and yield an
    // absurd program. Cap both at a sane ceiling far above the defaults (4 materials / 6 primitives).
    private const int MaxCount = 64;

    internal void Validate(string path, ValidationErrors errors) {
        if (MaxMaterials is < 1 or > MaxCount) {
            errors.Add(path: $"{path}.maxMaterials", message: $"maxMaterials must be in [1, {MaxCount}] (was {MaxMaterials})");
        }

        if (MaxPrimitives is < 1 or > MaxCount) {
            errors.Add(path: $"{path}.maxPrimitives", message: $"maxPrimitives must be in [1, {MaxCount}] (was {MaxPrimitives})");
        }

        RequireRange(errors: errors, name: "translation", path: $"{path}.translation", value: Translation);
        RequireRange(errors: errors, name: "scale", path: $"{path}.scale", value: Scale);

        // A non-positive scale is geometrically degenerate for an SDF — the shader clamps abs(scale) to 1e-4, so a <= 0
        // scale is meaningless and trips a spurious cross-backend divergence. Require a positive minimum. (translation
        // legitimately spans negatives, so it is exempt.)
        if ((Scale is { Count: 2 } scale) && (scale[0] <= 0f)) {
            errors.Add(path: $"{path}.scale", message: "scale min must be > 0 (a non-positive scale is geometrically degenerate)");
        }

        RequireRange(errors: errors, name: "smooth", path: $"{path}.smooth", value: Smooth);
        RequireRange(errors: errors, name: "sphereRadius", path: $"{path}.sphereRadius", value: SphereRadius);
        RequireRange(errors: errors, name: "boxHalfExtent", path: $"{path}.boxHalfExtent", value: BoxHalfExtent);
        RequireRange(errors: errors, name: "boxRound", path: $"{path}.boxRound", value: BoxRound);
        RequireRange(errors: errors, name: "torusMajorRadius", path: $"{path}.torusMajorRadius", value: TorusMajorRadius);
        RequireRange(errors: errors, name: "torusMinorRadius", path: $"{path}.torusMinorRadius", value: TorusMinorRadius);
        RequireRange(errors: errors, name: "roundConeLowerRadius", path: $"{path}.roundConeLowerRadius", value: RoundConeLowerRadius);
        RequireRange(errors: errors, name: "roundConeUpperRadius", path: $"{path}.roundConeUpperRadius", value: RoundConeUpperRadius);
        RequireRange(errors: errors, name: "roundConeHeight", path: $"{path}.roundConeHeight", value: RoundConeHeight);
    }

    private static FloatRange ToRange(IReadOnlyList<float>? value, FloatRange fallback) {
        return ((value is { Count: 2 }) ? new FloatRange(Maximum: value[1], Minimum: value[0]) : fallback);
    }
    private static void RequireRange(string path, string name, IReadOnlyList<float>? value, ValidationErrors errors) {
        if (value is null) {
            return;
        }

        if ((value.Count != 2) || !float.IsFinite(f: value[0]) || !float.IsFinite(f: value[1]) || (value[0] > value[1])) {
            errors.Add(path: path, message: $"{name} must be a finite [min, max] array with min <= max");
        }
    }
}
