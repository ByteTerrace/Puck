using System.Numerics;

namespace Puck.SdfVm.Debug;

/// <summary>The curated exhibits of the SDF torture museum (<c>sdf.gallery</c>) — one hand-authored known-nasty scene
/// each, sourced from the settled record (Post stages, docs/sdf-bench-notes.md, the negative-results ledger, and the
/// live-defects section of docs/sdf-backlog.md). The enum VALUE is the exhibit index the <c>sdf.gallery &lt;index&gt;</c>
/// jump uses; the emission for each lives in <see cref="SdfDebugRenderer.EmitGallery"/> (reusing the shared shape/carve
/// emitters), the camera pose + stdout plaque in <see cref="SdfGalleryScene"/>.</summary>
public enum SdfGalleryExhibit {
    LiarSpiral,
    DrosteTunnel,
    CellJitterCreases,
    NotchHorizon,
    SmoothChain,
    WallpaperP4G,
    CarveCeiling,
    LogSphereRunDoc,
    DriftMonolith,
}

/// <summary>
/// The SDF torture museum's state — a cycling curated tour INSIDE the fullscreen SDF-debug mode. <c>sdf.gallery</c>
/// enters (the first exhibit) then advances; <c>sdf.gallery &lt;name|index&gt;</c> jumps; <c>sdf.gallery off</c> exits
/// back to the plain debug subject. Each exhibit is a small hand-authored scene the mode renders in place of the debug
/// subject (<see cref="SdfDebugRenderer.EmitGallery"/>), framed by a fixed per-exhibit SNAP pose (simpler than an eased
/// orbit, and every exhibit's defect wants a specific framing anyway), with a 2-4 line stdout PLAQUE naming what to look
/// for, what is settled, and which gate/doc owns it. Rebuilds ride the same <see cref="Revision"/>/Bump path the debug
/// scene uses. Presentation only — the deterministic simulation never learns it exists. Deterministic and parameterized:
/// no wall clock, no RNG, so each exhibit's breakdown is reliable run to run.
/// </summary>
public sealed class SdfGalleryScene {
    // The exhibit table: kind + jump name + plaque title + the fixed snap pose (target, yaw, pitch, distance) + the
    // multi-line plaque. Order IS the index the sdf.gallery <index> jump addresses (and the enum value). Poses are
    // starting framings the lead tunes by eye; the DEFECTS they expose are the point, not the exact camera.
    private static readonly ExhibitEntry[] Exhibits = [
        new ExhibitEntry(
            Kind: SdfGalleryExhibit.LiarSpiral,
            Name: "liar-spiral",
            Title: "The liar's spiral",
            Target: Vector3.Zero, Yaw: 0.7f, Pitch: 0.5f, Distance: 5.0f,
            Plaque: [
                "A twist-rate-3 thin blade — the field over-estimates distance where the hard twist shears space, so",
                "it is NOT 1-Lipschitz along a ray. This is exactly WHY the per-program Lipschitz clamp (SdfProgram",
                "stepScale) exists: an unclamped march tunnels straight through the blade.",
                "PAIR WITH debug.view.overshoot — it lights hot right here where the clamp is load-bearing.",
            ]
        ),
        new ExhibitEntry(
            Kind: SdfGalleryExhibit.DrosteTunnel,
            Name: "droste",
            Title: "The Droste tunnel",
            Target: Vector3.Zero, Yaw: 0.6f, Pitch: 0.5f, Distance: 6.0f,
            Plaque: [
                "A LogSphere shellRatio-2 fold. The log-polar",
                "map makes the field DISCONTINUOUS at each shell boundary, so a ±1-LSB UV delta lands in a different",
                "shell on each backend. The camera stays outside the fold, matching the world-log-sphere Post scene.",
            ]
        ),
        new ExhibitEntry(
            Kind: SdfGalleryExhibit.CellJitterCreases,
            Name: "celljitter",
            Title: "CellJitter neighbour-cell creases",
            Target: Vector3.Zero, Yaw: 0.6f, Pitch: 0.45f, Distance: 5.0f,
            Plaque: [
                "A contained CellJitter prototype that STILL seams at the cell walls — the round fold picks each point's",
                "OWN cell, not the nearest copy, so containment ≠ nearest-copy and the field overestimates across a",
                "boundary. Look along the cell walls; conservative jitter reduces this nearest-copy error.",
            ]
        ),
        new ExhibitEntry(
            Kind: SdfGalleryExhibit.NotchHorizon,
            Name: "notch",
            Title: "The ground-plane horizon notch",
            Target: new Vector3(x: 0f, y: -1.0f, z: -6f), Yaw: 0.0f, Pitch: 0.08f, Distance: 4.0f,
            Plaque: [
                "The far ground silhouette against the sky steps in EXACT one-tile (16 px) increments near MaxDistance",
                "instead of a smooth perspective curve. Per-tile marchStart / beam-cull GRANULARITY leaking into the",
                "grazing horizon. MaxSteps exhaustion and occluder framing do not cause this artifact. Drive with a low",
                "sdf.cam pitch to inspect the beam-cull boundary.",
            ]
        ),
        new ExhibitEntry(
            Kind: SdfGalleryExhibit.SmoothChain,
            Name: "smooth-chain",
            Title: "A deep smooth-chamfer chain",
            Target: new Vector3(x: 0.4f, y: 0f, z: 0f), Yaw: 0.7f, Pitch: 0.5f, Distance: 6.5f,
            Plaque: [
                "Eight spheres blended in a long alternating SmoothUnion/ChamferUnion chain — each blend accumulates a",
                "least-significant-bit of rounding, so a deep chain drifts. The scoped field accumulator bounds where",
                "this can reach (docs/sdf-vm-evolution-roadmap). Inspect the seams under debug.view.normals.",
            ]
        ),
        new ExhibitEntry(
            Kind: SdfGalleryExhibit.WallpaperP4G,
            Name: "wallpaper-p4g",
            Title: "P4G glide mirrors",
            Target: new Vector3(x: 0f, y: -1.0f, z: 0f), Yaw: 0.5f, Pitch: 0.9f, Distance: 7.0f,
            Plaque: [
                "P4G folds an asymmetric motif through quarter-turn and glide-reflection classes while preserving a",
                "one-cell translation period. The asymmetric tile makes the glide mirrors visible and keeps the",
                "periodicity proof from passing through accidental motif symmetry.",
            ]
        ),
        new ExhibitEntry(
            Kind: SdfGalleryExhibit.CarveCeiling,
            Name: "carve-ceiling",
            Title: "Clustered carves at the views ceiling",
            Target: Vector3.Zero, Yaw: 0.6f, Pitch: 0.5f, Distance: 5.5f,
            Plaque: [
                "~256 hard carves packed onto one subject, densely overlapping the same screen tiles — the honest",
                "destruction budget made visible (every overlapping carve is evaluated per covered tile: the views-cost",
                "worst case). Measured ~1024 in-frame scattered carves at 60 fps; dense per-tile stacking is the real",
                "ceiling (docs/sdf-bench-notes.md). Watch it with debug.view.mask — the tiles under the cluster run red.",
            ]
        ),
        new ExhibitEntry(
            Kind: SdfGalleryExhibit.LogSphereRunDoc,
            Name: "logsphere-rundoc",
            Title: "Validator-legal ≠ marcher-safe",
            Target: new Vector3(x: 0f, y: -0.8f, z: 0f), Yaw: 0.4f, Pitch: 0.18f, Distance: 2.6f,
            Plaque: [
                "An aggressive LogSphere (shellRatio ~2.8, twist) with the camera DOWN INSIDE the fold near a floor.",
                "The folded field can overestimate distance near shell boundaries, so marchers step on the minimum of",
                "the field value and the published boundary gap while terminating on the field value (sdfMapStepBound).",
                "The world-droste-solidity stage protects this contract. The accepted residual is pixel-level containment-crease",
                "speckle + MaxSteps filaments at extreme grazes. PAIR WITH debug.view.overshoot AND debug.view.termination",
                "— a regression re-opens tile-size holes, which that solidity gate now catches.",
            ]
        ),
        new ExhibitEntry(
            Kind: SdfGalleryExhibit.DriftMonolith,
            Name: "drift-monolith",
            Title: "The drift monolith",
            Target: new Vector3(x: 0f, y: 1f, z: 0f), Yaw: 0f, Pitch: 0.26f, Distance: 10f,
            Plaque: [
                "A stacked-amplifier parity stress: LogSphere Droste + P6M wallpaper fold + a near-tie emissive",
                "material seam + a deep smooth/chamfer chain + a far grazing wall, all in one frame. Shared verbatim",
                "with Puck.Post's world-drift-monolith stage, so the exhibit and proof exercise identical geometry.",
            ]
        ),
    ];
    private int m_index = -1;
    private int m_revision;

    /// <summary>The active exhibit index (0-based), or -1 when the gallery is OFF (the plain debug subject renders).</summary>
    public int Index => m_index;

    /// <summary>Whether the gallery is showing an exhibit (the mode renders it in place of the debug subject).</summary>
    public bool Active => (m_index >= 0);

    /// <summary>The active exhibit kind — valid only while <see cref="Active"/> (the emitter dispatches on it).</summary>
    public SdfGalleryExhibit Exhibit => Exhibits[m_index].Kind;

    /// <summary>The active exhibit's title, or empty when the gallery is off — the overlay plaque card's headline
    /// (the diegetic museum tour's stdout plaque names the same title).</summary>
    public string CurrentTitle => (Active ? Exhibits[m_index].Title : "");

    /// <summary>The active exhibit's jump name, or empty when the gallery is off (the plaque card's metadata line).</summary>
    public string CurrentName => (Active ? Exhibits[m_index].Name : "");

    /// <summary>The exhibit count (the tour length).</summary>
    public static int Count => Exhibits.Length;

    /// <summary>Bumped on every enter/advance/jump/off — the mode folds it into its revision so the frame source
    /// rebuilds the program to the active exhibit (mirrors <see cref="SdfDebugScene.Revision"/>).</summary>
    public int Revision => m_revision;

    /// <summary>The active exhibit's fixed SNAP pose (target/yaw/pitch/distance), or null when the gallery is off (the
    /// pad orbit resumes). Snapped — applied verbatim so each exhibit holds its authored framing.</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? CameraFrame {
        get {
            if (!Active) {
                return null;
            }

            var exhibit = Exhibits[m_index];

            return (exhibit.Target, exhibit.Yaw, exhibit.Pitch, exhibit.Distance, false);
        }
    }

    /// <summary>Enters the tour at the FIRST exhibit when off, else advances to the next (wrapping). Prints the exhibit's
    /// plaque and bumps the revision.</summary>
    /// <returns>A one-line status.</returns>
    public string EnterOrAdvance() {
        m_index = (Active ? ((m_index + 1) % Exhibits.Length) : 0);
        m_revision++;

        return Announce();
    }

    /// <summary>Jumps to an exhibit by index (clamped into range). Prints the plaque and bumps the revision.</summary>
    /// <returns>A one-line status.</returns>
    public string Jump(int index) {
        m_index = Math.Clamp(value: index, min: 0, max: (Exhibits.Length - 1));
        m_revision++;

        return Announce();
    }

    /// <summary>Jumps to an exhibit by jump name (case-insensitive), or returns usage when the name is unknown (no state
    /// change on a miss). Prints the plaque and bumps the revision on a hit.</summary>
    /// <returns>A one-line status, or usage on an unknown name.</returns>
    public string JumpByName(string name) {
        for (var index = 0; (index < Exhibits.Length); index++) {
            if (string.Equals(a: Exhibits[index].Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                m_index = index;
                m_revision++;

                return Announce();
            }
        }

        return $"[sdf.gallery: unknown exhibit '{name}' — {Names()}]";
    }

    /// <summary>Exits the tour back to the plain debug subject (no-op when already off). Bumps the revision on change.</summary>
    /// <returns>A one-line status.</returns>
    public string Off() {
        if (!Active) {
            return "[sdf.gallery: already off]";
        }

        m_index = -1;
        m_revision++;

        return "[sdf.gallery off — back to the plain debug subject]";
    }

    /// <summary>Lists every exhibit (index + jump name + title) for the console.</summary>
    public string List() {
        var lines = new System.Text.StringBuilder(value: "[sdf.gallery — the torture museum]");

        for (var index = 0; (index < Exhibits.Length); index++) {
            _ = lines.Append(value: '\n').Append(value: "  ").Append(value: index).Append(value: ". ")
                .Append(value: Exhibits[index].Name).Append(value: " — ").Append(value: Exhibits[index].Title);
        }

        return lines.ToString();
    }

    // Prints the active exhibit's plaque to stdout (the museum placard — the scriptable stdout channel the console
    // control-plane echoes) and returns a one-line console status. The caller appends the run-`sdf`-to-view nudge when
    // the mode is down (only it knows the mode state).
    private string Announce() {
        var exhibit = Exhibits[m_index];
        var placard = new System.Text.StringBuilder();

        _ = placard.Append(value: "[sdf.gallery ").Append(value: m_index).Append(value: '/').Append(value: (Exhibits.Length - 1))
            .Append(value: " — ").Append(value: exhibit.Title).Append(value: " (").Append(value: exhibit.Name).Append(value: ")]");

        foreach (var line in exhibit.Plaque) {
            _ = placard.Append(value: '\n').Append(value: "    ").Append(value: line);
        }

        Console.Out.WriteLine(value: placard.ToString());

        return $"[sdf.gallery {m_index}/{(Exhibits.Length - 1)} {exhibit.Name} — {exhibit.Title}]";
    }
    private static string Names() {
        var names = new string[Exhibits.Length];

        for (var index = 0; (index < Exhibits.Length); index++) {
            names[index] = Exhibits[index].Name;
        }

        return string.Join(separator: " | ", values: names);
    }

    private readonly record struct ExhibitEntry(SdfGalleryExhibit Kind, string Name, string Title, Vector3 Target, float Yaw, float Pitch, float Distance, string[] Plaque);
}
