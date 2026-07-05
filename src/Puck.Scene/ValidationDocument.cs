using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The validation section: instead of presenting a live render, the run installs one cross-backend validation GATE and
/// propagates its exit code (0 pass, 1 gate-fail, 2 infra-fail). The <c>world</c> gate is data-driven — it renders THIS
/// document's scene + viewports on both Vulkan and Direct3D 12 at a fixed delta-zero frame and diffs them tolerance-aware
/// (overridable via <see cref="Thresholds"/>). The other gates (the <c>parity</c> SDF debug-mode sweep and the
/// <c>overworld</c>/<c>determinism</c> CPU self-checks) are self-contained smoke tests selected by name. When a
/// validation section is present the composition <c>graph</c> is ignored (the gate IS the root node). To
/// differential-fuzz a GENERATED scene instead, use the <c>fuzzing</c> section.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ValidationDocument {
    /// <summary>The validation gate to run: <c>"parity"</c>, <c>"world"</c>, <c>"overworld"</c>, or <c>"determinism"</c>.</summary>
    public string Gate { get; init; } = "";
    /// <summary>Optional PASS-threshold overrides (the <c>world</c> gate only).</summary>
    public ParityThresholdsDocument? Thresholds { get; init; }
    /// <summary>Optional directory the gate writes its artifacts (captures, diff heatmap, report) to; defaults to <c>artifacts/</c>.</summary>
    public string? ArtifactDir { get; init; }
    /// <summary>Whether the comparison frame is fixed at delta-zero (always true today; preserves cross-backend determinism).</summary>
    public bool? FixedFrame { get; init; }
    /// <summary>Whether the bottom-right viewport hosts an animated child surface instead of an SDF camera (the <c>world</c>
    /// gate only; requires a four-viewport split layout). Diffs the per-viewport child-surface seam across backends.</summary>
    public bool Child { get; init; }

    /// <summary>The recognized validation gate names — exactly the set the demo's gate map can build. (The retired
    /// engine gates whose coverage moved to Puck.Post — export/compute/reverse/indirect/resample/viewports/pixelate/
    /// capture — were removed from this list when their nodes were.)</summary>
    public static IReadOnlyList<string> Gates { get; } = ["parity", "world", "overworld", "determinism"];

    internal void Validate(string path, int viewportCount, ValidationErrors errors) {
        var isWorld = string.Equals(Gate, "world", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(Gate) || !Gates.Contains(Gate.ToLowerInvariant())) {
            errors.Add(path: $"{path}.gate", message: $"'{Gate}' is not a valid validation gate; expected one of: {string.Join(", ", Gates)}");
        }

        Thresholds?.Validate(errors: errors, path: $"{path}.thresholds");

        // fixedFrame is always true today (the gate compares at the delta-zero frame); reject an explicit false rather
        // than silently ignoring it.
        if (FixedFrame is false) {
            errors.Add(path: $"{path}.fixedFrame", message: "only fixedFrame:true is supported today (the gate always compares at the delta-zero frame)");
        }

        // artifactDir, when present, must be a usable path — a blank or illegal value would otherwise crash the gate's
        // directory creation at host-start instead of failing cleanly here.
        if ((ArtifactDir is not null) && (string.IsNullOrWhiteSpace(ArtifactDir) || (ArtifactDir.IndexOfAny(anyOf: Path.GetInvalidPathChars()) >= 0))) {
            errors.Add(path: $"{path}.artifactDir", message: "artifactDir must be a non-empty path with no invalid path characters");
        }

        // thresholds are honored only by the 'world' gate; reject them elsewhere rather than silently ignoring.
        if (!isWorld && (Thresholds is not null)) {
            errors.Add(path: $"{path}.thresholds", message: "thresholds apply only to the 'world' gate");
        }

        // child is a 'world'-gate-only, four-viewport feature (the bottom-right slot becomes a hosted child surface).
        if (Child && !isWorld) {
            errors.Add(path: $"{path}.child", message: "child applies only to the 'world' gate");
        }

        if (Child && (viewportCount != 4)) {
            errors.Add(path: $"{path}.child", message: $"a child viewport requires a four-viewport split layout, but the document declares {viewportCount} viewport(s)");
        }
    }
}
