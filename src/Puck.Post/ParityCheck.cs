using Puck.Capture;

namespace Puck.Post;

/// <summary>
/// Tolerance-aware difference metrics between two RGBA images of equal extent — the quantitative core the parity
/// stages share, metric shapes ported from the demo's <c>ParityMetrics</c> (the worked reference). Pure and GPU-free.
/// Per pixel the difference is <c>d = max(|ΔR|, |ΔG|, |ΔB|)</c> (alpha is ignored — every kernel writes opaque).
/// The "smart" signal is <see cref="IsolatedFraction"/>: benign cross-backend divergence is driver-level FP codegen —
/// a sprinkle of isolated ±1-LSB quantization flips — while a real bug spreads into contiguous regions, so its
/// differing pixels clump and the isolated fraction collapses.
/// </summary>
internal sealed record ParityMetrics {
    // A differing pixel is "isolated" (benign-like) when at most this many of its 8 neighbours also differ.
    private const int IsolationNeighbourLimit = 1;

    /// <summary>Gets the total number of compared pixels.</summary>
    public required int TotalPixels { get; init; }
    /// <summary>Gets the number of pixels whose max-channel delta is non-zero.</summary>
    public required int DifferingPixels { get; init; }
    /// <summary>Gets the differing pixels as a percentage of the total.</summary>
    public required double PercentDiffering { get; init; }
    /// <summary>Gets the largest single-channel absolute delta over all pixels.</summary>
    public required int MaxChannelDelta { get; init; }
    /// <summary>Gets the mean max-channel delta over <em>all</em> pixels (so ±1 noise on a fraction of pixels stays tiny).</summary>
    public required double MeanAbsError { get; init; }
    /// <summary>Gets the fraction of differing pixels that are spatially isolated — at most one of their eight
    /// neighbours also differs (1.0 when nothing differs). Benign ±1 noise is near-1; a clustered bug drops it.</summary>
    public required double IsolatedFraction { get; init; }
    /// <summary>Gets the fraction of differing pixels whose delta is exactly 1 (1.0 when nothing differs).</summary>
    public required double UnitDeltaFraction { get; init; }

    /// <summary>Computes the parity metrics between two tightly packed RGBA8 images of the same extent.</summary>
    /// <param name="reference">The reference image's RGBA pixels.</param>
    /// <param name="comparand">The comparand image's RGBA pixels.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The computed metrics.</returns>
    /// <exception cref="ArgumentException">A buffer is not exactly <c>width * height * 4</c> bytes.</exception>
    public static ParityMetrics Compute(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> comparand, int width, int height) {
        var expected = ((width * height) * 4);

        if (
            (reference.Length != expected) ||
            (comparand.Length != expected)
        ) {
            throw new ArgumentException(message: $"Expected two {width}x{height} RGBA buffers of {expected} bytes; got {reference.Length} and {comparand.Length}.");
        }

        var totalPixels = (width * height);
        var differs = new bool[totalPixels];
        var differingPixels = 0;
        var maxChannelDelta = 0;
        var unitDeltaPixels = 0;
        var deltaSum = 0L;

        for (var pixel = 0; (pixel < totalPixels); pixel++) {
            var offset = (pixel * 4);
            var delta = Math.Max(
                Math.Abs(value: (reference[offset] - comparand[offset])),
                Math.Max(
                    Math.Abs(value: (reference[offset + 1] - comparand[offset + 1])),
                    Math.Abs(value: (reference[offset + 2] - comparand[offset + 2]))
                )
            );

            deltaSum += delta;

            if (delta > 0) {
                differs[pixel] = true;
                differingPixels++;
                maxChannelDelta = Math.Max(maxChannelDelta, delta);

                if (delta == 1) {
                    unitDeltaPixels++;
                }
            }
        }

        return new ParityMetrics {
            DifferingPixels = differingPixels,
            IsolatedFraction = ((differingPixels == 0) ? 1.0 : ((double)CountIsolated(differs: differs, width: width, height: height) / differingPixels)),
            MaxChannelDelta = maxChannelDelta,
            MeanAbsError = ((double)deltaSum / totalPixels),
            PercentDiffering = ((totalPixels == 0) ? 0.0 : (((double)differingPixels / totalPixels) * 100.0)),
            TotalPixels = totalPixels,
            UnitDeltaFraction = ((differingPixels == 0) ? 1.0 : ((double)unitDeltaPixels / differingPixels)),
        };
    }

    private static int CountIsolated(bool[] differs, int width, int height) {
        var isolated = 0;

        for (var pixel = 0; (pixel < differs.Length); pixel++) {
            if (!differs[pixel]) {
                continue;
            }

            var x = (pixel % width);
            var y = (pixel / width);
            var neighbours = 0;

            for (var dy = -1; (dy <= 1); dy++) {
                for (var dx = -1; (dx <= 1); dx++) {
                    if ((dx == 0) && (dy == 0)) {
                        continue;
                    }

                    var nx = (x + dx);
                    var ny = (y + dy);

                    if (
                        (nx >= 0) &&
                        (nx < width) &&
                        (ny >= 0) &&
                        (ny < height) &&
                        differs[((ny * width) + nx)]
                    ) {
                        neighbours++;
                    }
                }
            }

            if (neighbours <= IsolationNeighbourLimit) {
                isolated++;
            }
        }

        return isolated;
    }
}

/// <summary>
/// A conjunctive set of PASS thresholds for one parity comparison. Every active threshold must hold;
/// <see cref="Evaluate"/> returns the ones that tripped (empty = pass). A threshold is disabled by a sentinel:
/// <see cref="MaxChannelDelta"/> at 255 or <see cref="MinUnitDeltaFraction"/> at 0 never trips. Shape ported from
/// the demo's <c>ParityThresholdSet</c>.
/// </summary>
internal sealed record ParityThresholdSet {
    /// <summary>Gets the largest allowed single-channel delta (255 disables the check).</summary>
    public required int MaxChannelDelta { get; init; }
    /// <summary>Gets the largest allowed differing-pixel percentage.</summary>
    public required double MaxPercentDiffering { get; init; }
    /// <summary>Gets the smallest allowed fraction of differing pixels whose delta is exactly 1 (0 disables the check).</summary>
    public required double MinUnitDeltaFraction { get; init; }
    /// <summary>Gets the smallest allowed isolated fraction.</summary>
    public required double MinIsolatedFraction { get; init; }
    /// <summary>Gets the largest allowed mean max-channel delta over all pixels.</summary>
    public required double MaxMeanAbsError { get; init; }

    /// <summary>Evaluates the metrics against every active threshold and returns a description of each that
    /// tripped. An empty list means this comparison passed.</summary>
    /// <param name="metrics">The measured parity metrics.</param>
    /// <returns>The tripped thresholds, or an empty list on pass.</returns>
    public IReadOnlyList<string> Evaluate(ParityMetrics metrics) {
        ArgumentNullException.ThrowIfNull(metrics);

        var failures = new List<string>();

        if (metrics.MaxChannelDelta > MaxChannelDelta) {
            failures.Add(item: $"maxChannelDelta {metrics.MaxChannelDelta} > {MaxChannelDelta}");
        }

        if (metrics.PercentDiffering > MaxPercentDiffering) {
            failures.Add(item: $"percentDiffering {metrics.PercentDiffering:0.####}% > {MaxPercentDiffering}%");
        }

        if (metrics.UnitDeltaFraction < MinUnitDeltaFraction) {
            failures.Add(item: $"unitDeltaFraction {metrics.UnitDeltaFraction:0.####} < {MinUnitDeltaFraction}");
        }

        if (metrics.IsolatedFraction < MinIsolatedFraction) {
            failures.Add(item: $"isolatedFraction {metrics.IsolatedFraction:0.####} < {MinIsolatedFraction}");
        }

        if (metrics.MeanAbsError > MaxMeanAbsError) {
            failures.Add(item: $"meanAbsError {metrics.MeanAbsError:0.######} > {MaxMeanAbsError}");
        }

        return failures;
    }
}

/// <summary>
/// The calibrated PASS thresholds the parity stages apply — VALUES kept in sync with the demo's
/// <c>ParityThresholds.cs</c> (calibrated against the measured RTX 4070 baseline): the demo gate is the POST's
/// standing cross-check, so the same comparison must pass or fail identically in both.
/// <para><b>POSTURE (user decision 2026-07-03): RELAXED by default.</b> Pixel-perfect cross-backend agreement is a
/// LONG-TERM ideal, not a day-to-day gate: the fine-grained signatures (±1-LSB exactness, unit-delta mass,
/// isolation) re-roll with every shader-codegen change and kept blocking short-term work. The default posture keeps
/// only the guards a REAL divergence cannot dodge — a missing/relocated/recolored region explodes the mean, a wrong
/// layout explodes the spread — and shrugs at every known FP-noise class. <c>PUCK_PARITY_STRICT=1</c> opts back into
/// the strict calibrations below (each set's doc records its calibration evidence) for dedicated parity-hunting
/// sessions chasing the ideal.</para>
/// </summary>
internal static class ParityThresholds {
    // Declaration order matters: the posture fields must initialize before the public sets below read them.
    private static readonly bool s_strict = string.Equals(Environment.GetEnvironmentVariable(variable: "PUCK_PARITY_STRICT"), "1", StringComparison.Ordinal);

    // The one relaxed envelope (KEEP IN SYNC with the demo's copy): mean is the load-bearing guard — worst measured
    // benign noise is ~0.06 (all-±1 at ~6% spread), a real region divergence lands in the multiple-of-1.0 range.
    private static readonly ParityThresholdSet s_relaxed = new() {
        MaxChannelDelta = 255, // disabled: boundary flips and amplified tails are legitimately large.
        MaxMeanAbsError = 0.35, // ~5x over the worst measured benign mean; a real region bug blows far past it.
        MaxPercentDiffering = 20.0, // generous: FP noise redistributes freely; a wrong LAYOUT still trips this.
        MinIsolatedFraction = 0.0, // disabled: benign noise clusters along gradients.
        MinUnitDeltaFraction = 0.0, // disabled: the delta-mass signature is a strict-posture concern.
    };

    static ParityThresholds() {
        if (s_strict) {
            Console.Error.WriteLine(value: "[parity] STRICT posture (PUCK_PARITY_STRICT=1): the long-term pixel-perfect calibrations are enforced.");
        }
    }

    /// <summary>STRICT: continuous-shading views, where the only cross-backend (or dynamic-vs-baked) residual is
    /// ±1-LSB quantization noise.</summary>
    public static readonly ParityThresholdSet Continuous = (s_strict ? new ParityThresholdSet {
        MaxChannelDelta = 1, // ±1-LSB noise; above 1 is a real divergence.
        MaxMeanAbsError = 0.05, // ±1 on a fraction of pixels keeps this far below 0.05.
        MaxPercentDiffering = 0.5, // ~4x over the measured 0.13%.
        MinIsolatedFraction = 0.90, // benign noise is ~99% isolated; a bug clumps.
        MinUnitDeltaFraction = 0.99, // benign noise is entirely ±1.
    } : s_relaxed);

    /// <summary>The thresholds for the full compute SDF world composite: the same continuous-shading flavour as
    /// <see cref="Continuous"/>, but the richer scene puts measurably more pixels in the 1/255 transition bands, so
    /// only the spread cap is relaxed; the max-delta, isolation, unit-delta, and mean guards stay strict.</summary>
    public static readonly ParityThresholdSet WorldComposite = (s_strict ? new ParityThresholdSet {
        MaxChannelDelta = 1, // ±1-LSB noise; above 1 is a real divergence.
        MaxMeanAbsError = 0.05, // ±1 on <1% of pixels keeps this far below 0.05.
        MaxPercentDiffering = 2.0, // ~3.5x over the measured 0.57% split baseline; benign noise scales with scene richness.
        MinIsolatedFraction = 0.85, // measured 93-96% isolated; a clustered bug collapses well below this.
        MinUnitDeltaFraction = 0.99, // benign noise is entirely ±1.
    } : s_relaxed);

    /// <summary>The thresholds for continuous world views whose benign residual is EXACTLY ±1 LSB but whose spread
    /// legitimately moves with shader codegen: every time the VM's interpreter grows an opcode, DXC's SPIR-V and DXIL
    /// codegen re-make different contraction/scheduling choices, and the ±1 rounding noise REDISTRIBUTES (measured on
    /// the rt stage when the wallpaper fold landed 2026-07-03: 0.01% → 6.2% of pixels differing, still every delta
    /// exactly ±1, visible as gradient-band dither). Chasing the spread per codegen roll is whack-a-mole; the
    /// signature that cannot be a real bug is "EVERY delta is exactly ±1" — so the magnitude guards stay absolute
    /// (max delta 1, unit-delta 0.99) and the structure guards widen to what all-±1 noise can occupy. The mean guard
    /// scales with spread for ±1 noise (mean ≈ spread × 1/255-ish per channel), hence its looser cap.</summary>
    public static readonly ParityThresholdSet WorldLsbExact = (s_strict ? new ParityThresholdSet {
        MaxChannelDelta = 1, // absolute: any ≥2 delta is a real divergence.
        MaxMeanAbsError = 0.12, // all-±1 noise at ~10% spread lands here; a real bug (multi-LSB) blows past it.
        MaxPercentDiffering = 10.0, // codegen rolls redistribute the ±1 noise; magnitude, not spread, is the guard.
        MinIsolatedFraction = 0.0, // disabled: ±1 dither legitimately follows gradient bands.
        MinUnitDeltaFraction = 0.99, // absolute: benign residual is entirely ±1.
    } : s_relaxed);

    /// <summary>The thresholds for continuous world views over HIGH-CONTRAST palettes (emissive/specular materials),
    /// where a benign ±1-ULP field difference at a material boundary can flip the WINNING MATERIAL of an isolated
    /// pixel — a legitimately multi-LSB delta (measured on the menagerie when the wallpaper fold's codegen roll
    /// landed 2026-07-03: a handful of isolated Δ126 pixels where the glowing cream pair meets its neighbors; the
    /// same phenomenon the Discrete set has always absorbed for the material-id debug view). The max-delta guard is
    /// disabled for exactly that signature; everything else stays showcase-strict — a real bug clumps (isolation),
    /// spreads (percent), shifts the mass off ±1 (unit-delta), or lifts the mean.</summary>
    public static readonly ParityThresholdSet WorldHighContrast = (s_strict ? new ParityThresholdSet {
        MaxChannelDelta = 255, // disabled: an isolated boundary-winner flip is a legitimately large delta.
        MaxMeanAbsError = 0.05, // a real bug spreads, lifting the mean well past this.
        MaxPercentDiffering = 2.0, // showcase-strict spread.
        MinIsolatedFraction = 0.85, // flips are isolated; a clustered bug collapses this.
        MinUnitDeltaFraction = 0.99, // the delta MASS stays exactly ±1; flips are the <1% tail.
    } : s_relaxed);

    /// <summary>The thresholds for cross-backend DIFFERENTIAL FUZZING of the SDF world. Fuzz-generated scenes span
    /// the whole input space, so the benign ±1-LSB codegen residual is legitimately MORE clustered and widespread
    /// than the hand-tuned showcase: it follows the large smooth ground-plane gradients and the cone-march banding
    /// (thin contiguous ±1 bands), which collapses the isolated-fraction and lifts the spread far below what those
    /// showcase-calibrated guards expect — so the isolation guard is disabled and the spread cap widened.
    /// <para>The 7-shape generator (capsule/cylinder/ellipsoid joined 2026-07-03) additionally produces LARGE CURVED
    /// GRAZING surfaces (e.g. seed 42: a capsule under a non-uniform Scale filling a third of the frame edge-on),
    /// where the march itself amplifies sub-ULP field differences: a step-termination flip near the surface shifts
    /// the traveled distance, and the 6-tap normal's catastrophic cancellation turns that into an ISOLATED few-LSB
    /// shading delta (measured: maxΔ5 on 0.02% of pixels, 100% isolated, 98.85% still exactly ±1). "Every delta
    /// exactly ±1" was therefore an empirical property of the old 4-shape scene distribution, not of the backends.
    /// The recalibrated signature: the mass of deltas stays ±1 (<see cref="ParityThresholdSet.MinUnitDeltaFraction"/>
    /// = 0.95 — the load-bearing subtle-bug guard: a shape whose field diverges by even 2–3 LSB across its screen
    /// area collapses it), and a real region-level divergence still trips the mean/spread guards regardless of
    /// magnitude. The max-delta guard is DISABLED outright: once the subtraction-like blends joined the generator
    /// (2026-07-03), the carve boundaries produce the same benign ISOLATED material-winner flips the high-contrast
    /// showcase scenes show (a ±1-ULP field difference at a discrete ownership decision; measured seed 1: maxΔ165 on
    /// isolated pixels, 94% isolated, delta mass still ±1) — magnitude no longer separates benign from real; the
    /// MASS does. KEEP IN SYNC with the demo's <c>ParityThresholds.WorldFuzz</c> (the same seed must pass or fail
    /// identically in both gates).</para></summary>
    public static readonly ParityThresholdSet WorldFuzz = (s_strict ? new ParityThresholdSet {
        MaxChannelDelta = 255, // disabled: isolated boundary-winner flips are legitimately large; the unit-delta mass is the guard.
        MaxMeanAbsError = 0.05, // few-LSB deltas on a tiny minority of pixels stay far below this; a real bug lifts the mean.
        MaxPercentDiffering = 8.0, // wide: gradient/banding-heavy fuzz scenes put many pixels in ±1 transition bands.
        MinIsolatedFraction = 0.0, // disabled: benign ±1 noise follows gradient bands and is legitimately clustered.
        MinUnitDeltaFraction = 0.95, // THE guard: benign deltas are OVERWHELMINGLY ±1; a real divergence shifts the mass off it.
    } : s_relaxed);
}

/// <summary>Shared parity plumbing for the cross-backend stages: the amplified diff heatmap and a one-line
/// metrics digest for stage details.</summary>
internal static class ParityCheck {
    /// <summary>Writes a grayscale max-channel-delta heatmap, amplified so divergences glow without a 1-LSB image
    /// looking alarming: <c>value = min(255, d * 64)</c>. Shape ported from the demo's diff writer.</summary>
    /// <param name="path">The output PNG path.</param>
    /// <param name="reference">The reference image's RGBA pixels.</param>
    /// <param name="comparand">The comparand image's RGBA pixels.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    public static void WriteDiffImage(string path, ReadOnlySpan<byte> reference, ReadOnlySpan<byte> comparand, int width, int height) {
        var pixelCount = (width * height);
        var diff = new byte[(pixelCount * 4)];

        for (var pixel = 0; (pixel < pixelCount); pixel++) {
            var offset = (pixel * 4);
            var delta = Math.Max(
                Math.Abs(value: (reference[offset] - comparand[offset])),
                Math.Max(
                    Math.Abs(value: (reference[offset + 1] - comparand[offset + 1])),
                    Math.Abs(value: (reference[offset + 2] - comparand[offset + 2]))
                )
            );
            var value = (byte)Math.Min(255, (delta * 64));

            diff[offset] = value;
            diff[offset + 1] = value;
            diff[offset + 2] = value;
            diff[offset + 3] = byte.MaxValue;
        }

        PngEncoder.Write(height: height, path: path, rgba: diff, width: width);
    }

    /// <summary>Formats the one-line metrics digest the parity stages put in their outcome detail.</summary>
    /// <param name="metrics">The measured parity metrics.</param>
    /// <returns>The digest, e.g. <c>diff 0.37% (2130px) | maxΔ1 | isolated 95% | unitΔ 1.00</c>.</returns>
    public static string Describe(ParityMetrics metrics) {
        ArgumentNullException.ThrowIfNull(metrics);

        return $"diff {metrics.PercentDiffering:0.##}% ({metrics.DifferingPixels}px) | maxΔ{metrics.MaxChannelDelta} | isolated {(metrics.IsolatedFraction * 100.0):0}% | unitΔ {metrics.UnitDeltaFraction:0.##}";
    }

    /// <summary>Evaluates the flat ≡ instanced contract the instancing stages assert PER BACKEND: Vulkan
    /// BIT-IDENTICAL (the mask gate is an exact culling decision over a fixed SPIR-V compile, so any divergence at
    /// all is a real masking bug), Direct3D 12 within <see cref="ParityThresholds.WorldLsbExact"/> (the documented
    /// benign DXIL codegen redistribution — see the stages' own docs for the measured signature). On failure it
    /// writes the Vulkan flat-vs-instanced diff heatmap — the diagnosis of WHERE the masked walk diverged from the
    /// flat walk, not just the bool — beside the stage's main diff artifact and returns the failure outcome.</summary>
    /// <param name="stageName">The stage name, prefixing the on-fail Vulkan diff artifact.</param>
    /// <param name="artifactsDirectory">The battery artifacts directory.</param>
    /// <param name="diffPath">The stage's main diff artifact path (the failure outcome's artifact).</param>
    /// <param name="vulkanFlatPixels">The Vulkan flat render.</param>
    /// <param name="vulkanInstancedPixels">The Vulkan instanced render.</param>
    /// <param name="directXFlatPixels">The Direct3D 12 flat render.</param>
    /// <param name="directXInstancedPixels">The Direct3D 12 instanced render.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The failure outcome, or <see langword="null"/> when the contract holds.</returns>
    public static PostStageOutcome? EvaluateFlatInstancedContract(string stageName, string artifactsDirectory, string diffPath, byte[] vulkanFlatPixels, byte[] vulkanInstancedPixels, byte[] directXFlatPixels, byte[] directXInstancedPixels, int width, int height) {
        var vulkanIdentical = vulkanFlatPixels.AsSpan().SequenceEqual(other: vulkanInstancedPixels);
        var directXMetrics = ParityMetrics.Compute(reference: directXFlatPixels, comparand: directXInstancedPixels, width: width, height: height);
        var directXFailures = ParityThresholds.WorldLsbExact.Evaluate(metrics: directXMetrics);

        if (vulkanIdentical && (directXFailures.Count == 0)) {
            return null;
        }

        var vulkanMetrics = ParityMetrics.Compute(reference: vulkanFlatPixels, comparand: vulkanInstancedPixels, width: width, height: height);

        WriteDiffImage(comparand: vulkanInstancedPixels, height: height, path: Path.Combine(artifactsDirectory, $"{stageName}-vulkan-flat-diff.png"), reference: vulkanFlatPixels, width: width);

        return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"instanced != flat: Vulkan bit-identical={vulkanIdentical} ({Describe(metrics: vulkanMetrics)}); Direct3D 12 {Describe(metrics: directXMetrics)}{(directXFailures.Count == 0 ? "" : $" — {string.Join(separator: "; ", values: directXFailures)}")}");
    }
}
