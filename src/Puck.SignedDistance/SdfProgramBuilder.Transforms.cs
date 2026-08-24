using System.Numerics;

using Puck.Maths;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    /// <summary>Bends space about the local X axis: the XY plane rotates by <paramref name="rate"/> · x radians.</summary>
    /// <param name="rate">Radians of rotation per unit of local X.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> is not finite.</exception>
    public SdfProgramBuilder BendX(float rate) {
        return FiniteScalarTransform(
            op: SdfOp.BendX,
            value: rate,
            paramName: nameof(rate),
            subject: "A bend rate"
        );
    }
    /// <summary>Bends the XY plane by <paramref name="rate"/> · y radians.</summary>
    /// <param name="rate">Radians of rotation per unit of local Y.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> is not finite.</exception>
    public SdfProgramBuilder BendY(float rate) {
        return FiniteScalarTransform(
            op: SdfOp.BendY,
            value: rate,
            paramName: nameof(rate),
            subject: "A bend rate"
        );
    }
    /// <summary>Rotates the YZ plane by <paramref name="rate"/> · y radians. The three bends are distinct ops, not a
    /// symmetric family: <see cref="BendX"/> keys on x and rotates XY, <see cref="BendY"/> keys on y and rotates XY, and
    /// this one keys on y and rotates YZ. Each keys on a coordinate inside the plane it rotates, which is what gives the
    /// bends their <c>1 + rate·ρ</c> Lipschitz factor (see <c>SdfProgram.BendOperatorNorm</c>) rather than
    /// <see cref="TwistY"/>'s smaller one.</summary>
    /// <param name="rate">Radians of rotation per unit of local Y.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> is not finite.</exception>
    public SdfProgramBuilder BendZ(float rate) {
        return FiniteScalarTransform(
            op: SdfOp.BendZ,
            value: rate,
            paramName: nameof(rate),
            subject: "A bend rate"
        );
    }
    /// <summary>Stochastic domain-repeat fold: tiles space into cells of <paramref name="spacing"/> like
    /// <see cref="Repeat"/>, then per cell displaces the point by a hashed offset, optionally tumbles (a hashed
    /// rotation), and optionally recolors by a hashed material variant — scattering the prototype that follows into a
    /// jittered field from a single instruction. Both the displacement and the tumble are isometries, so the field stays
    /// distance-preserving (only the jitter half-amplitude joins <c>AnalyzeLipschitz</c>). The per-cell hash is
    /// integer-only (canonical PCG3D keyed on the two's-complement cell index xored with <paramref name="seed"/>), so
    /// cell decisions are bit-identical across both GPU backends. jitter/tumble/materialVariants each default to an exact
    /// identity, so an unused op leaves the point byte-identical. Like <see cref="Repeat"/>, keep the prototype clear of
    /// the cell boundary: the caller must ensure jitter/2 + prototype radius ≤ min(spacing)/2 (this builder validates
    /// only the half it can see — that the displacement alone cannot cross a boundary; the prototype is emitted later,
    /// so its radius is unknown here). Containment is not sufficient: even with
    /// the in-cell rule satisfied, the single-cell <c>round()</c> fold can pick the wrong copy near a cell wall — a
    /// copy jittered toward the boundary is nearer to the adjacent cell's query points than that cell's own copy — so
    /// the field overestimates at cell boundaries (visible seams, grazing-angle hole risk). The in-cell rule keeps the
    /// surface watertight inside each cell; the boundary field stays merely conservative-looking-but-overestimating, so
    /// keep jitter conservative relative to spacing. KEEP IN SYNC with SDF_OP_CELL_JITTER in
    /// Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="spacing">The per-axis cell spacing in world units (clamped to ≥ 0.001 per axis).</param>
    /// <param name="jitter">The peak-to-peak per-cell position displacement in world units (0 = no displacement).</param>
    /// <param name="seed">The hash seed — different seeds give independent jitter/tumble/variant fields.</param>
    /// <param name="tumble">The per-cell rotation amount in [0,1]: 0 = no rotation, 1 = up to ±π about a random axis
    /// (clamped to [0,1]).</param>
    /// <param name="materialVariants">The number of hashed material rows (0 = geometric only): a hit in a cell adds a
    /// hashed 0..variants-1 to its shape's material id.</param>
    /// <param name="flavor">How the per-cell position offset is distributed (the SDF_NOISE_* Blend lane, header.z):
    /// <see cref="SdfNoiseFlavor.White"/> (default, byte-identical to pre-flavor programs), <see cref="SdfNoiseFlavor.Blue"/>,
    /// or <see cref="SdfNoiseFlavor.Gaussian"/>. Reshapes only the displacement — tumble and material variant are
    /// unaffected, and every flavor shares White's <c>±jitter/2</c> offset bound (no Lipschitz change). KEEP IN SYNC with
    /// SDF_NOISE_* and the SDF_OP_CELL_JITTER flavor branch in Assets/Shaders/Sdf/sdf-vm.hlsli.</param>
    /// <exception cref="ArgumentException"><paramref name="materialVariants"/> is negative, or half of
    /// <paramref name="jitter"/> is not strictly less than half the smallest <paramref name="spacing"/> component (the
    /// displaced content would cross a cell boundary and hole the march).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flavor"/> is not a defined
    /// <see cref="SdfNoiseFlavor"/>.</exception>
    public SdfProgramBuilder CellJitter(Vector3 spacing, float jitter, uint seed = 0u, float tumble = 0f, int materialVariants = 0, SdfNoiseFlavor flavor = SdfNoiseFlavor.White) {
        // BEFORE the in-cell rule below, because that rule cannot see a NaN: (NaN * 0.5f) >= x is false, so a NaN
        // jitter would pass the containment check and pack straight through. Signs are absorbed downstream (the
        // spacing clamp, MathF.Abs on jitter, the tumble clamp), so only finiteness is refused.
        RequireFinite(
            value: spacing,
            paramName: nameof(spacing),
            subject: "A cell-jitter spacing"
        );
        RequireFinite(
            value: jitter,
            paramName: nameof(jitter),
            subject: "A cell-jitter jitter"
        );
        RequireFinite(
            value: tumble,
            paramName: nameof(tumble),
            subject: "A cell-jitter tumble"
        );
        RequireDefined(
            value: flavor,
            paramName: nameof(flavor)
        );

        // The degenerate-spacing clamp and the reciprocal are HOST-BAKED (Data1.xyz), mirroring Repeat().
        var clamped = ClampSpacing(spacing: spacing);

        if (materialVariants < 0) {
            throw new ArgumentException(
                message: "CellJitter materialVariants must be >= 0 (0 = geometric only).",
                paramName: nameof(materialVariants)
            );
        }

        // The half the builder CAN see: the displacement alone must not push content across the round() cell boundary.
        // (The caller must also keep jitter/2 + prototype radius <= min(spacing)/2 — the prototype radius is unknown here.)
        var minSpacing = MathF.Min(
            x: clamped.X,
            y: MathF.Min(
                x: clamped.Y,
                y: clamped.Z
            )
        );

        if ((MathF.Abs(x: jitter) * 0.5f) >= (0.5f * minSpacing)) {
            throw new ArgumentException(
                message: "CellJitter jitter/2 must be < min(spacing)/2, or jittered content crosses the cell boundary and holes the march. The full in-cell rule (jitter/2 + prototype reach <= min(spacing)/2) is refused later, at Build, where the prototype is visible — and even then the single-cell round() fold overestimates near cell walls (containment does not guarantee the nearest copy; boundary seams and grazing-angle hole risk persist), so keep jitter conservative.",
                paramName: nameof(jitter)
            );
        }

        var clampedTumble = Math.Clamp(
            max: 1f,
            min: 0f,
            value: tumble
        );

        m_instructions.Add(item: new SdfInstruction(
            Blend: ((uint)flavor),
            Data0: new Vector4(
                value: clamped,
                w: jitter
            ),
            Data1: new Vector4(
                value: (Vector3.One / clamped),
                w: clampedTumble
            ),
            Material: ((uint)materialVariants),
            Op: SdfOp.CellJitter,
            Shape: seed
        ));

        // Mirrors the shader's `if (instructionHeader.w != 0u) parityMaterialDelta = h0.z % variants` — a hashed row in
        // 0..variants-1, so ONE unit of the raw lane reaches at most variants-1 (MaxRecolorDelta subtracts that 1).
        // Unlike the two folds this records no m_positionalFold entry: a hashed variant count is not a stride, so the
        // scope clamp has nothing to narrow — the Build()-time refusal is the whole guard for this route.
        if (materialVariants != 0) {
            m_materialRecolor = ((m_instructions.Count - 1), 1, SdfOp.CellJitter);
        }

        return this;
    }
    /// <summary>Warps the sample point by a bounded, cross-coupled sinusoidal field before the shapes evaluate — organic
    /// bulging / wobble / terrain. A point op (like the fold ops): order it before the shapes it should warp. Each axis
    /// is driven by the next axis's coordinate, so the warp is non-separable; the basis is deterministic across both
    /// backends. Not an isometry — the metric stretches by up to <c>1 + amplitude·‖frequency‖</c>, so
    /// <c>AnalyzeLipschitz</c> bakes a conservative step clamp (and folds the point's max travel into a downstream
    /// twist/bend's reach); keep <c>amplitude·‖frequency‖</c> moderate. KEEP IN SYNC with SDF_OP_DOMAIN_WARP in
    /// Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="frequency">Per-axis angular frequency of the warp (radians per world unit).</param>
    /// <param name="amplitude">Peak point displacement (world units; 0 = an exact identity).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frequency"/> or <paramref name="amplitude"/> is
    /// not finite.</exception>
    public SdfProgramBuilder DomainWarp(Vector3 frequency, float amplitude) {
        // Signed for the same reason as Displace, and the reach/Lipschitz folds take MathF.Abs of the amplitude.
        RequireFinite(
            value: frequency,
            paramName: nameof(frequency),
            subject: "A domain-warp frequency"
        );
        RequireFinite(
            value: amplitude,
            paramName: nameof(amplitude),
            subject: "A domain-warp amplitude"
        );

        return Transform(
            data0: new Vector4(
                value: frequency,
                w: amplitude
            ),
            op: SdfOp.DomainWarp
        );
    }
    /// <summary>Elongates the shape that follows: the point clamps into a box of the given extents, sweeping the
    /// shape's cross-section over ±extents (the classic capsule-from-sphere operator).</summary>
    /// <param name="extents">The per-axis elongation half-extents (0 on an axis = no stretch there).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="extents"/> is not finite and non-negative.</exception>
    public SdfProgramBuilder Elongate(Vector3 extents) {
        // The decoder is `p -= clamp(p, -extents, extents)` (SDF_OP_ELONGATE, and ClampComponents in
        // SdfFieldEvaluator): a negative component inverts the clamp bounds, which is undefined in HLSL and
        // backend-dependent, so the half-extents must be non-negative.
        RequireNonNegative(
            value: extents,
            paramName: nameof(extents),
            subject: "An elongation half-extent"
        );

        return Transform(
            data0: new Vector4(
                value: extents,
                w: 0f
            ),
            op: SdfOp.Elongate
        );
    }
    /// <summary>Log-spherical domain warp: tiles space into infinite self-similar "Droste" shells. A translation along
    /// <c>log(radius)</c> becomes a uniform scaling in Cartesian space, so the prototype shape(s) that follow repeat
    /// outward and inward as scaled copies from a handful of instructions. Folds only the radial coordinate (no polar
    /// pinching); an optional per-shell Z-spin gives the Droste spiral at no cost. Not an isometry — the r/density
    /// correction rides the runtime <c>distanceScale</c> and <c>AnalyzeLipschitz</c> bakes a conservative step clamp, so
    /// the over-relaxed march stays hole-free. Like <see cref="Repeat"/>, the prototype content should stay within one
    /// shell cell (radii within a factor of <paramref name="shellRatio"/>) so no shell boundary overshoots.</summary>
    /// <param name="shellRatio">The Cartesian scale factor between consecutive shells (e.g. 2 = each shell twice the
    /// previous). Clamped to at least 1.0001 (a ratio of 1 means no shells and a divide-by-zero on the baked 1/w).</param>
    /// <param name="twist">Radians of Z-spin added per shell (the Droste spiral). 0 = concentric, un-spun shells.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shellRatio"/> or <paramref name="twist"/> is not
    /// finite.</exception>
    public SdfProgramBuilder LogSphere(float shellRatio, float twist = 0f) {
        // The 1.0001 floor below absorbs a too-small (or negative) ratio, but not NaN — MathF.Max(NaN, x) is NaN, and
        // the log and its reciprocal would then pack NaN into three lanes. A signed twist is the spiral handedness.
        RequireFinite(
            value: shellRatio,
            paramName: nameof(shellRatio),
            subject: "A log-sphere shell ratio"
        );
        RequireFinite(
            value: twist,
            paramName: nameof(twist),
            subject: "A log-sphere twist"
        );

        // w = ln(ratio) and its reciprocal are HOST-BAKED (the shader avoids a per-eval log-of-constant and a divide,
        // matching Repeat's baked-reciprocal pattern; KEEP IN SYNC with SDF_OP_LOG_SPHERE in sdf-vm.hlsli).
        var ratio = MathF.Max(
            x: shellRatio,
            y: 1.0001f
        );
        var w = MathF.Log(x: ratio);

        return Transform(
            data0: new Vector4(
                w: 0f,
                x: w,
                y: twist,
                z: (1f / w)
            ),
            op: SdfOp.LogSphere
        );
    }
    /// <summary>Infinite domain-repeat fold: tiles space into cells of <paramref name="spacing"/> with a single-cell
    /// <c>round()</c> fold, so the prototype that follows repeats on the lattice. The returned distance is the current
    /// cell's copy only, so the fold is exact only for
    /// an on-center prototype contained within half-<paramref name="spacing"/> per axis. An off-center or oversized
    /// prototype creases the field at the cell walls with an overestimate (the nearest surface lives in a neighbouring
    /// cell the fold never consults) — an overestimate can hole the march, and neither the Lipschitz step clamp nor the
    /// over-relaxation step-back catches it (they bound the field's rate, not a boundary discontinuity). The builder
    /// cannot validate this (the prototype is emitted later and its post-fold translation matters as much as its
    /// radius) — the caller owns the rule, exactly like <see cref="CellJitter"/>'s in-cell rule. A 3^k neighbour-cell
    /// check would remove the constraint but is judged not worth the interpreter cost at current usage. KEEP IN SYNC
    /// with SDF_OP_REPEAT in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="spacing">The per-axis cell spacing in world units (clamped to ≥ 0.001 per axis).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="spacing"/> is not finite.</exception>
    public SdfProgramBuilder Repeat(Vector3 spacing) {
        // Sign is absorbed by the clamp below and that absorption is a settled contract (see RepeatLimited's remark
        // that a negative spacing must keep behaving as it did), so only finiteness is refused.
        RequireFinite(
            value: spacing,
            paramName: nameof(spacing),
            subject: "A repeat spacing"
        );

        // The degenerate-spacing clamp and the reciprocal are HOST-BAKED (Data1.xyz): shapes evaluate millions of
        // times per frame, programs build once (KEEP IN SYNC with SDF_OP_REPEAT in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var clamped = ClampSpacing(spacing: spacing);

        return Transform(
            data0: new Vector4(
                value: clamped,
                w: 0f
            ),
            data1: new Vector4(
                value: (Vector3.One / clamped),
                w: 0f
            ),
            op: SdfOp.Repeat
        );
    }
    /// <summary>Bounded domain-repeat fold: <see cref="Repeat"/> with the cell index clamped to ±<paramref name="limit"/>
    /// per axis. Carries <see cref="Repeat"/>'s exactness contract unchanged: exact only for an on-center prototype
    /// within half-<paramref name="spacing"/> per axis; off-center/oversized prototypes crease the field at interior
    /// cell walls with a march-holing OVERestimate (see <see cref="Repeat"/> — the caller owns the rule; the builder
    /// cannot see the prototype).</summary>
    /// <param name="spacing">The per-axis cell spacing in world units (clamped to ≥ 0.001 per axis).</param>
    /// <param name="limit">The per-axis repeat-cell limit (the lattice spans cell indices −limit..+limit).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="spacing"/> is not finite, or
    /// <paramref name="limit"/> is not finite and non-negative.</exception>
    public SdfProgramBuilder RepeatLimited(Vector3 spacing, Vector3 limit) {
        // The limit rides `clamp(round(p / spacing), -limit, limit)`: a negative component inverts the clamp bounds
        // (undefined in HLSL), so it must be non-negative. Zero is legal and pins that axis to the single centre cell —
        // the shipped world's placement stamper does exactly that.
        RequireFinite(
            value: spacing,
            paramName: nameof(spacing),
            subject: "A repeat spacing"
        );
        RequireNonNegative(
            value: limit,
            paramName: nameof(limit),
            subject: "A repeat-cell limit"
        );

        // The degenerate-spacing clamp is HOST-BAKED, exactly as <see cref="Repeat"/> bakes it (KEEP IN SYNC with
        // SDF_OP_REPEAT_LIMITED in Assets/Shaders/Sdf/sdf-vm.hlsli). Clamped WITHOUT Abs, matching the shader's old
        // max(data0.xyz, 0.001) — a negative spacing must keep behaving as it did. Unlike Repeat there is no free lane
        // for the reciprocal (Data1.xyz carries the limit), so the shader keeps its divide.
        var clamped = ClampSpacing(spacing: spacing);

        return Transform(
            data0: new Vector4(
                value: clamped,
                w: 0f
            ),
            data1: new Vector4(
                value: limit,
                w: 0f
            ),
            op: SdfOp.RepeatLimited
        );
    }
    /// <summary>Angular domain-repeat fold: folds the plane perpendicular to <paramref name="axis"/> into
    /// <paramref name="count"/> equal sectors, so the prototype that follows repeats rotationally around the axis —
    /// gears, wheels, columns of a rotunda, clock ticks, flower petals — from a single instruction (the rotational
    /// sibling of the linear <see cref="Repeat"/> and the lattice <see cref="WallpaperFold"/>). The fold rotates the
    /// point into the base sector and, when <paramref name="mirror"/> is set, reflects each sector across its bisector
    /// for kaleidoscope symmetry: both are isometries, so the field stays 1-Lipschitz (factor 1, no step clamp — like
    /// <see cref="Repeat"/>) and no cull bound changes. Like <see cref="Repeat"/>, keep the prototype clear of the
    /// sector walls (the two radial half-planes through the axis) — content that overspills a wall is clipped by the
    /// neighbouring sector. The sector angle and its reciprocals are host-baked. KEEP IN SYNC with SDF_OP_REPEAT_POLAR
    /// in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="count">The number of sectors around the axis (clamped to ≥ 1; 1 = a single full-circle no-op).</param>
    /// <param name="axis">The rotation axis — the fold acts in the plane perpendicular to it (default
    /// <see cref="SdfPolarAxis.Y"/>, the XZ ground plane).</param>
    /// <param name="mirror">When <see langword="true"/>, reflects each sector across its bisector so adjacent sectors
    /// mirror — the kaleidoscope fold (still an isometry).</param>
    /// <param name="materialStride">The per-sector palette stride: the sector index (0..count-1) times this strides the
    /// material id of a later shape win, so each sector can select its own palette row. 0 (the default) keeps the fold
    /// purely geometric.</param>
    /// <exception cref="ArgumentException"><paramref name="materialStride"/> is negative, or <paramref name="count"/>
    /// (after clamping to ≥ 1) exceeds <see cref="MaxExactFloatSectorCount"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis"/> is not a defined <see cref="SdfPolarAxis"/>.</exception>
    public SdfProgramBuilder RepeatPolar(int count, SdfPolarAxis axis = SdfPolarAxis.Y, bool mirror = false, int materialStride = 0) {
        if (materialStride < 0) {
            throw new ArgumentException(
                message: "RepeatPolar materialStride must be >= 0 (0 = geometric only).",
                paramName: nameof(materialStride)
            );
        }

        RequireDefined(
            value: axis,
            paramName: nameof(axis)
        );

        // count and the sector angle's reciprocals are HOST-BAKED (Data0.yzw): shapes evaluate millions of times per
        // frame, programs build once (KEEP IN SYNC with SDF_OP_REPEAT_POLAR in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var sectors = Math.Max(
            val1: 1,
            val2: count
        );

        // THE FLOAT-EXACT SECTOR CEILING (see MaxExactFloatSectorCount). Past 2^24, (float)sectors is no longer the
        // exact count — the shader observes a ROUNDED count, so the recolor window's claimed max (sectors - 1, an
        // exact host integer) is no longer honestly the shader's max. Refuse rather than let the Build()-time gate
        // judge against a maximum the shader does not actually enforce.
        if (sectors > MaxExactFloatSectorCount) {
            throw new ArgumentException(
                message: $"RepeatPolar count must be <= {MaxExactFloatSectorCount} (2^24, the largest integer a 32-bit float represents exactly) — the shader reads the packed sector count back as a float, and past this bound the host's exact sector-1 maximum can diverge from what the shader's float wrap arithmetic actually produces.",
                paramName: nameof(count)
            );
        }

        var angle = ((2f * MathF.PI) / sectors);

        m_instructions.Add(item: new SdfInstruction(
            Blend: (mirror
            ? 1u
            : 0u),
            Data0: new Vector4(
                w: (1f / sectors),   // 1/count — the per-sector material wrap
                x: angle,            // 2π/count — the sector angle
                y: (1f / angle),     // count/(2π) — 1/angle, for the sector floor-division
                z: sectors           // count — the sector-index wrap
            ),
            Data1: default,
            Material: ((uint)materialStride),
            Op: SdfOp.RepeatPolar,
            Shape: ((uint)axis)
        ));

        // Mirrors the shader's `if (instructionHeader.w != 0u) parityMaterialDelta = wrapped * stride` (wrapped ranges
        // 0..sectors-1) — see WallpaperFold's identical tracking above for why this is sound to compute HERE, at
        // RepeatPolar's own call site.
        if (materialStride != 0) {
            m_positionalFold = ((m_instructions.Count - 1), (sectors - 1), materialStride);
            m_materialRecolor = ((m_instructions.Count - 1), (sectors - 1), SdfOp.RepeatPolar);
        }

        return this;
    }
    /// <summary>Resets the local evaluation point for the next instruction chain without clearing the accumulated field.</summary>
    /// <returns>This builder.</returns>
    public SdfProgramBuilder ResetPoint() {
        // Mirrors the shader's SDF_OP_RESET clearing parityMaterialDelta — see m_positionalFold's remarks. Both mirrors
        // of that slot clear together; they differ in what they track, never in when the GPU forgets it.
        m_positionalFold = null;
        m_materialRecolor = null;

        return Transform(op: SdfOp.ResetPoint);
    }
    /// <summary>Rotates subsequent point evaluation by a normalized copy of <paramref name="rotation"/>.</summary>
    /// <param name="rotation">The local-space rotation.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rotation"/> is not finite, or has zero
    /// length.</exception>
    public SdfProgramBuilder Rotate(Quaternion rotation) {
        RequireRotation(
            value: rotation,
            paramName: nameof(rotation),
            subject: "A rotation"
        );

        // Normalized HOST-SIDE (defensive: JSON-authored quaternions arrive here raw) — the shader's inverse-rotate
        // assumes a unit quaternion, and a drifted one would shear space rather than rotate it.
        var unit = Quaternion.Normalize(value: rotation);

        return Transform(
            data0: new Vector4(
                w: unit.W,
                x: unit.X,
                y: unit.Y,
                z: unit.Z
            ),
            op: SdfOp.Rotate
        );
    }
    /// <summary>Rotates subsequent point evaluation by a quaternion normalized in the deterministic fixed-point
    /// domain.</summary>
    /// <param name="rotation">The local-space rotation.</param>
    /// <returns>This builder.</returns>
    /// <remarks>This overload is the simulation-safe encoding boundary. It performs every derived operation,
    /// including normalization, before converting the four finished components to the program's single-precision
    /// storage format. The conversion itself is exactly rounded and does not feed a platform math routine.</remarks>
    public SdfProgramBuilder Rotate(FixedQuaternion rotation) {
        var unit = rotation.Normalize().ToQuaternion();

        return Transform(
            data0: new Vector4(
                w: unit.W,
                x: unit.X,
                y: unit.Y,
                z: unit.Z
            ),
            op: SdfOp.Rotate
        );
    }
    /// <summary>Scales subsequent point evaluation and applies the conservative minimum-axis distance correction.</summary>
    /// <param name="scale">The local-space scale. Components are converted to positive nonzero magnitudes.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not finite.</exception>
    public SdfProgramBuilder Scale(Vector3 scale) {
        // The sign is DELIBERATELY unconstrained: the clamp below takes the absolute value, which is this method's
        // documented contract. Only NaN/infinity, which no clamp absorbs, are refused.
        RequireFinite(
            value: scale,
            paramName: nameof(scale),
            subject: "A scale"
        );

        // The degenerate-scale clamp AND the resulting distance rescale are HOST-BAKED (Data0.xyz = |scale| clamped,
        // Data0.w = its min axis): shapes evaluate millions of times per frame while programs build once, and the
        // shader's per-evaluation abs/max/min collapse to one lane read. The min-axis factor is the conservative
        // correction for a non-uniform scale — f(S⁻¹p)·min(s) is 1-Lipschitz, so it can only underestimate true
        // distance, never overstep. HLSL's abs/max/min agree with MathF's bit-for-bit on every non-NaN input, and
        // 0.0001f is the shader's clamp value (KEEP IN SYNC with SDF_OP_SCALE in
        // Assets/Shaders/Sdf/sdf-vm.hlsli).
        var clamped = Vector3.Max(
            value1: Vector3.Abs(value: scale),
            value2: new Vector3(value: 0.0001f)
        );

        return Transform(
            data0: new Vector4(
                value: clamped,
                w: MathF.Min(
                    x: clamped.X,
                    y: MathF.Min(
                        x: clamped.Y,
                        y: clamped.Z
                    )
                )
            ),
            op: SdfOp.Scale
        );
    }
    /// <summary>Reflection fold across an arbitrary plane — the general-normal superset of <see cref="SymmetryX"/>/
    /// <see cref="SymmetryY"/>/<see cref="SymmetryZ"/>: everything on the plane's negative side (<c>dot(p, normal) +
    /// offset &lt; 0</c>) is mirrored onto its positive side, so one authored half repeats mirror-imaged (a kaleidoscope
    /// leaf, a bilateral body, the reflect atom of a KIFS fold). A reflection is an isometry, so the field stays
    /// 1-Lipschitz (factor 1, no step clamp) and no cull bound changes. Like the axis symmetries, keep authored content
    /// on the plane's positive (kept) side. The normal is normalized host-side. KEEP IN SYNC with SDF_OP_SYMMETRY_PLANE
    /// in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="normal">The plane normal (normalized here; the positive side, toward the normal, is the kept half).</param>
    /// <param name="offset">The plane's constant term: the mirror plane is <c>dot(p, normal) + offset = 0</c>, so it
    /// sits at signed distance <c>-offset</c> along the normal. A positive offset therefore moves the plane against the
    /// normal. 0 puts it through the local origin.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="normal"/> is not finite or has zero length, or
    /// <paramref name="offset"/> is not finite.</exception>
    public SdfProgramBuilder SymmetryPlane(Vector3 normal, float offset = 0f) {
        // The offset is signed by construction (it slides the plane along the normal in either direction).
        RequireDirection(
            value: normal,
            paramName: nameof(normal),
            subject: "A symmetry-plane normal"
        );
        RequireFinite(
            value: offset,
            paramName: nameof(offset),
            subject: "A symmetry-plane offset"
        );

        // Normalized HOST-SIDE (the shader's reflect assumes a unit normal; a drifted one would scale the mirrored half).
        return Transform(
            data0: new Vector4(
                value: Vector3.Normalize(value: normal),
                w: offset
            ),
            op: SdfOp.SymmetryPlane
        );
    }
    /// <summary>Mirrors the point across the local X = 0 plane (<c>abs(p.x)</c>) — convenience sugar for
    /// <see cref="SymmetryPlane"/> with the X-axis normal (the axis <c>SymmetryX</c> op it replaced).</summary>
    public SdfProgramBuilder SymmetryX() {
        return SymmetryPlane(normal: Vector3.UnitX);
    }
    /// <summary>Mirrors the point across the local Y = 0 plane — sugar for <see cref="SymmetryPlane"/> (Y-axis normal).</summary>
    public SdfProgramBuilder SymmetryY() {
        return SymmetryPlane(normal: Vector3.UnitY);
    }
    /// <summary>Mirrors the point across the local Z = 0 plane — sugar for <see cref="SymmetryPlane"/> (Z-axis normal).</summary>
    public SdfProgramBuilder SymmetryZ() {
        return SymmetryPlane(normal: Vector3.UnitZ);
    }
    /// <summary>Applies a rigid transform (translation + orientation) sourced at evaluation time from per-frame dynamic
    /// transform <paramref name="slot"/> — element <c>2*slot</c> is the position, <c>2*slot+1</c> the orientation
    /// quaternion in the renderer's dynamic-transform buffer. The shape that follows is repositioned each frame by
    /// updating that buffer, leaving this program (uploaded once) untouched. Honored only by the world render path
    /// (shaders compiled with <c>SDF_DYNAMIC_TRANSFORMS</c>).</summary>
    /// <param name="slot">The dynamic-transform slot index (0-based).</param>
    public SdfProgramBuilder TransformDynamic(int slot) {
        if (
            (slot < 0) ||
            (slot > SdfProgram.MaxDynamicTransformSlot)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(slot),
                message: $"Dynamic transform slots must be in [0, {SdfProgram.MaxDynamicTransformSlot}]."
            );
        }

        return Transform(
            data0: new Vector4(
                w: 0f,
                x: slot,
                y: 0f,
                z: 0f
            ),
            op: SdfOp.TransformDynamic
        );
    }
    /// <summary>Translates subsequent point evaluation by <paramref name="offset"/>.</summary>
    /// <param name="offset">The translation in local units.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is not finite.</exception>
    public SdfProgramBuilder Translate(Vector3 offset) {
        // A translation is signed in every component by construction, so only finiteness is refused.
        RequireFinite(
            value: offset,
            paramName: nameof(offset),
            subject: "A translation"
        );

        return Transform(
            data0: new Vector4(
                value: offset,
                w: 0f
            ),
            op: SdfOp.Translate
        );
    }
    /// <summary>Twists space about the local Y axis: the XZ plane rotates by <paramref name="rate"/> · y radians.
    /// Not an isometry — keep rates moderate so the march stays stable.</summary>
    /// <param name="rate">Radians of rotation per unit of local Y.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> is not finite.</exception>
    public SdfProgramBuilder TwistY(float rate) {
        // A signed rate is the whole point (the twist handedness), so only finiteness is refused.
        return FiniteScalarTransform(
            op: SdfOp.TwistY,
            value: rate,
            paramName: nameof(rate),
            subject: "A twist rate"
        );
    }
    /// <summary>Folds the point's in-plane coordinates onto the fundamental cell of a wallpaper symmetry group — the
    /// shapes that follow repeat under the group's mirrors/rotations across the lattice. Every fold branch is an
    /// isometry, so distances are preserved; like <see cref="Repeat"/>, content must stay clear of cell boundaries
    /// (and of the rotation seams of P2/CMM/P4*) unless a mirror of the group protects that edge.</summary>
    /// <param name="group">The wallpaper group. P4/P4M/P4G and the hex groups (P3 and up) require a square cell —
    /// quarter-turns and the equilateral hex lattice are only isometries there (hex pitch = <paramref name="cell"/>.X).</param>
    /// <param name="cell">The lattice cell extents in the fold plane.</param>
    /// <param name="limit">The repeat-cell limit per plane axis (RepeatLimited semantics; axial indices for hex).</param>
    /// <param name="plane">The plane the fold acts on (the third axis is untouched).</param>
    /// <param name="materialStride">The parity-material stride: the cell key (checker parity for square lattices,
    /// the 3-coloring for hex) times this strides the material id of later shape wins in the chain, so each lattice
    /// cell selects its own row of the palette. 0 (the default) keeps the fold purely geometric.</param>
    /// <param name="lodDistance">The symmetry-LOD distance threshold: past it the lattice keeps its copy positions
    /// but skips the in-cell folds (upright copies, cheaper and shimmer-free at range). 0 (the default) = off.</param>
    /// <exception cref="ArgumentException"><paramref name="materialStride"/> is negative.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A <paramref name="cell"/> extent the group reads is not finite and
    /// positive, <paramref name="limit"/> is not finite and non-negative, <paramref name="lodDistance"/> is not
    /// finite and non-negative, <paramref name="group"/> is not a defined <see cref="SdfWallpaperGroup"/>, or
    /// <paramref name="plane"/> is not a defined <see cref="SdfWallpaperPlane"/>.</exception>
    public SdfProgramBuilder WallpaperFold(SdfWallpaperGroup group, Vector2 cell, Vector2 limit, SdfWallpaperPlane plane = SdfWallpaperPlane.XZ, int materialStride = 0, float lodDistance = 0f) {
        // Mirrors RepeatPolar's stride check — the same Material lane, the same uint cast, and the shader reads it back
        // as `(int)instructionHeader.w`, so a negative stride would recolor shapes DOWNWARD out of the palette.
        if (materialStride < 0) {
            throw new ArgumentException(
                message: "WallpaperFold materialStride must be >= 0 (0 = geometric only).",
                paramName: nameof(materialStride)
            );
        }

        // Checked before isHex reads group's ordinal — an out-of-range group would otherwise silently misclassify.
        RequireDefined(
            value: group,
            paramName: nameof(group)
        );
        RequireDefined(
            value: plane,
            paramName: nameof(plane)
        );

        // The reciprocal cell extents are HOST-BAKED (Data0.zw): square lattices read them as 1/cell for the lattice
        // round; hex lattices (pitch = cell.x) read z = 1/pitch and w = 2/(√3·pitch) — the two divides in the axial
        // decompose (KEEP IN SYNC with the fold functions in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var isHex = (group >= SdfWallpaperGroup.P3);

        // cell.x is the lattice pitch for EVERY group, so it must be positive. cell.y is the second lattice extent for
        // a square group only — sdfWallpaperFoldCell hands the hex path cell.x alone (sdfWallpaperFoldHexCell takes a
        // scalar pitch), so a hex caller may leave cell.y at zero and it is checked for finiteness only.
        RequirePositive(
            value: cell.X,
            paramName: nameof(cell),
            subject: "A wallpaper cell extent"
        );

        if (isHex) {
            RequireFinite(
                value: cell.Y,
                paramName: nameof(cell),
                subject: "A wallpaper cell extent"
            );
        } else {
            RequirePositive(
                value: cell.Y,
                paramName: nameof(cell),
                subject: "A wallpaper cell extent"
            );
        }

        // The limit rides the same clamp(round(...), -limit, limit) shape RepeatLimited uses, and lodDistance is
        // compared as `data1.z > 0.0` with 0 meaning off — a negative threshold has no spelling.
        RequireNonNegative(
            value: limit,
            paramName: nameof(limit),
            subject: "A wallpaper repeat-cell limit"
        );
        RequireNonNegative(
            value: lodDistance,
            paramName: nameof(lodDistance),
            subject: "A wallpaper symmetry-LOD distance"
        );

        var inverseX = (1f / MathF.Max(
            x: cell.X,
            y: 0.0001f
        ));
        var inverseY = (isHex
            ? ((2f / 1.7320508f) * inverseX)
            : (1f / MathF.Max(
                x: cell.Y,
                y: 0.0001f
            ))
        );

        m_instructions.Add(item: new SdfInstruction(
            Blend: ((uint)plane),
            Data0: new Vector4(
                w: inverseY,
                x: cell.X,
                y: cell.Y,
                z: inverseX
            ),
            Data1: new Vector4(
                w: 0f,
                x: limit.X,
                y: limit.Y,
                z: lodDistance
            ),
            Material: ((uint)materialStride),
            Op: SdfOp.WallpaperFold,
            Shape: ((uint)group)
        ));

        // Mirrors the shader's `if (instructionHeader.w != 0u) parityMaterialDelta = cellKey * stride` (a zero stride
        // leaves parityMaterialDelta — and this mirror — untouched, exactly like the shader's own guard). cellKey's
        // range is sdfWallpaperCellKey's: 0..2 (3-coloring) for a hex group (P3 and up), 0..1 (parity) otherwise — see
        // the sdf-world skill's C#↔HLSL contract table — so the largest per-unit-stride reach is known HERE, at
        // WallpaperFold's own call site, without waiting for the shape that eventually uses this fold's material.
        if (materialStride != 0) {
            var maxCellKey = ((group >= SdfWallpaperGroup.P3)
                ? 2
                : 1
            );

            m_positionalFold = ((m_instructions.Count - 1), maxCellKey, materialStride);
            m_materialRecolor = ((m_instructions.Count - 1), maxCellKey, SdfOp.WallpaperFold);
        }

        return this;
    }
}
