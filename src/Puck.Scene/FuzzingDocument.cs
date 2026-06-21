using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The fuzzing section: cross-backend DIFFERENTIAL fuzzing of the SDF VM. A single <see cref="Seed"/> runs ONE
/// iteration in-process (a fuzz-generated scene rendered on both Vulkan and Direct3D 12 at a fixed frame and diffed —
/// the same gate the <c>world</c> validation uses, with a generated scene); a <see cref="SeedRange"/> is driven by the
/// process-isolated <c>tools fuzz</c> child loop (a malformed program can TDR the GPU, so each seed runs in its own
/// process). <see cref="Bounds"/> overrides the shared <see cref="ShapeBounds"/> envelope — the SAME one the scene
/// validator gates authoring against — so the generated and the authored space widen or narrow together.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuzzingDocument {
    /// <summary>The most seeds a <see cref="SeedRange"/> may span — one isolated child process is spawned per seed, so an
    /// unbounded range would launch a runaway number of GPU processes. The <c>tools fuzz -Run</c> loop mirrors this cap.</summary>
    public const int MaxSeedSpan = 100_000;

    /// <summary>A single fuzz seed: run one differential-fuzzing iteration in-process. Mutually exclusive with <see cref="SeedRange"/>.</summary>
    public int? Seed { get; init; }
    /// <summary>An inclusive <c>[start, end]</c> seed range: drive the process-isolated <c>tools fuzz</c> child loop. Mutually exclusive with <see cref="Seed"/>.</summary>
    public IReadOnlyList<int>? SeedRange { get; init; }
    /// <summary>The per-seed child wall-clock timeout in seconds for a <see cref="SeedRange"/> run (a hung/TDR'd child is killed).</summary>
    public int? TimeoutSeconds { get; init; }
    /// <summary>Optional overrides for the shared generation/validation envelope.</summary>
    public BoundsDocument? Bounds { get; init; }

    /// <summary>The effective envelope: the document overrides overlaid on <see cref="ShapeBounds.Default"/>.</summary>
    /// <returns>The resolved bounds.</returns>
    public ShapeBounds ResolveBounds() {
        return (Bounds?.ToShapeBounds() ?? ShapeBounds.Default);
    }

    internal void Validate(string path, ValidationErrors errors) {
        var hasSeed = (Seed is not null);
        var hasRange = (SeedRange is not null);

        if (hasSeed == hasRange) {
            errors.Add(path: path, message: "a fuzzing section requires exactly one of 'seed' (in-process) or 'seedRange' (the tools fuzz loop)");
        }

        if (Seed is < 0) {
            errors.Add(path: $"{path}.seed", message: $"seed must be >= 0 (was {Seed})");
        }

        if (SeedRange is { } range) {
            if (range.Count != 2) {
                errors.Add(path: $"{path}.seedRange", message: $"seedRange must be [start, end] (2 entries), found {range.Count}");
            } else if ((range[0] < 0) || (range[1] < range[0])) {
                errors.Add(path: $"{path}.seedRange", message: $"seedRange [{range[0]}, {range[1]}] must satisfy 0 <= start <= end");
            } else if ((((long)range[1] - range[0]) + 1) > MaxSeedSpan) {
                errors.Add(path: $"{path}.seedRange", message: $"seedRange [{range[0]}, {range[1]}] spans {((long)range[1] - range[0]) + 1} seeds; the limit is {MaxSeedSpan} (one isolated child process is spawned per seed)");
            }
        }

        if (TimeoutSeconds is < 1) {
            errors.Add(path: $"{path}.timeoutSeconds", message: $"timeoutSeconds must be >= 1 (was {TimeoutSeconds})");
        }

        // timeoutSeconds governs the per-seed child of a range run; it is meaningless for a single in-process seed.
        if ((TimeoutSeconds is not null) && hasSeed) {
            errors.Add(path: $"{path}.timeoutSeconds", message: "timeoutSeconds applies only to a 'seedRange' run");
        }

        Bounds?.Validate(errors: errors, path: $"{path}.bounds");
    }
}
