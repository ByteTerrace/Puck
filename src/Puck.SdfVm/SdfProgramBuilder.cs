using System.Numerics;

namespace Puck.SdfVm;

public sealed class SdfProgramBuilder {
    // KEEP IN SYNC with SDF_SCREEN_MATERIAL in Assets/Shaders/Sdf/sdf-vm.hlsli.
    public const int ScreenMaterialId = 65535;
    /// <summary>The instance CEILING — the most instances one program may declare. The world renderer's per-tile
    /// mask is a DERIVED ceil(instanceCount/32) uints (<see cref="SdfProgram.InstanceMaskWordCount"/>), so this caps
    /// it at 32 words per tile. KEEP IN SYNC with SDF_MAX_INSTANCES in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    public const int MaxInstances = 1024;
    /// <summary>The most screen surfaces one program may declare (matches <see cref="SdfWorldEngine.MaxScreenSurfaces"/>
    /// — the kernels' <c>screenSurfaces[]</c>/<c>screenSources[]</c> array length; a contract SEPARATE from the
    /// viewport capacity <see cref="SdfWorldEngine.MaxViewports"/>).</summary>
    public const int MaxScreenSurfaces = 8;

    private readonly List<SdfInstanceRange> m_instances = [];
    private readonly List<SdfInstruction> m_instructions = [];
    private readonly List<SdfMaterial> m_materials = [];
    private readonly List<SdfScreenSurface> m_screenSurfaces = [];
    private int m_openInstanceFirst = -1;
    private bool m_openInstanceIsDynamic;
    private bool m_openInstanceActive;
    private Vector3 m_openInstanceCenter;
    private float m_openInstanceRadius;
    private int m_openInstanceSlot;

    public int AddMaterial(SdfMaterial material) {
        m_materials.Add(item: material);

        return (m_materials.Count - 1);
    }
    /// <summary>Opens a STATIC per-object instance: every instruction until the matching <see cref="EndInstance"/>
    /// belongs to it, and the world renderer's tile-cull beam prepass tests <paramref name="boundCenter"/>/
    /// <paramref name="boundRadius"/> (a world-space bounding sphere) per tile, evaluating the instance's instruction
    /// slice only for tiles the sphere may cover. Instructions declared OUTSIDE any instance are the WORLD set:
    /// always evaluated, unmasked (floors/walls/unbounded shapes).</summary>
    /// <param name="boundCenter">The instance's world-space bounding-sphere center.</param>
    /// <param name="boundRadius">The instance's world-space bounding-sphere radius.</param>
    /// <exception cref="InvalidOperationException">An instance is already open.</exception>
    public SdfProgramBuilder BeginInstance(Vector3 boundCenter, float boundRadius) {
        BeginInstanceCore(isDynamic: false, center: boundCenter, radius: boundRadius, slot: 0);

        return this;
    }
    /// <summary>Declares a STATIC per-object instance with balanced begin/end handling around <paramref name="emit"/>.
    /// If <paramref name="emit"/> throws, the builder is left with the instance open and partial state — discard it
    /// (no builder path rolls back on a throw).</summary>
    /// <param name="boundCenter">The instance's world-space bounding-sphere center.</param>
    /// <param name="boundRadius">The instance's world-space bounding-sphere radius.</param>
    /// <param name="emit">The instructions that belong to the instance.</param>
    /// <returns>This builder.</returns>
    public SdfProgramBuilder Instance(Vector3 boundCenter, float boundRadius, Action<SdfProgramBuilder> emit) {
        ScopedInstance(isDynamic: false, center: boundCenter, radius: boundRadius, slot: 0, emit: emit);

        return this;
    }
    /// <summary>Opens a DYNAMIC per-object instance: like <see cref="BeginInstance"/>, but the bound center resolves
    /// on the GPU each frame as (dynamic-transform <paramref name="slot"/>'s position + <paramref name="boundOffset"/>)
    /// — no quaternion rotate, the entity's orientation is folded into the host-baked <paramref name="boundRadius"/>
    /// (as the per-shape/segment bounds gate already does). Pairs with a <see cref="SdfOp.TransformDynamic"/> the
    /// instance's own instructions apply.</summary>
    /// <param name="slot">The dynamic-transform slot index (0-based) this instance's bound tracks.</param>
    /// <param name="boundOffset">The bound's pre-dynamic offset (added to the slot's per-frame position).</param>
    /// <param name="boundRadius">The instance's bounding-sphere radius (post-dynamic geometry folded in).</param>
    /// <param name="active">Whether the instance participates in the tile-cull scan. Pass <see langword="false"/> to PARK a
    /// reserved-pool slot that carries no live content this rebuild (the classic "hidden below the floor" placeholder):
    /// the slot still exists (so the pool's live emission always fits the once-sized buffers), but the beam prepass skips
    /// its per-tile sphere test with a single branch (<see cref="SdfInstanceRange.Active"/>), so a parked slot costs
    /// almost nothing. Its mask bit is always 0 — Stage 1 never marches it.</param>
    /// <exception cref="InvalidOperationException">An instance is already open.</exception>
    public SdfProgramBuilder BeginInstanceDynamic(int slot, Vector3 boundOffset, float boundRadius, bool active = true) {
        BeginInstanceCore(isDynamic: true, center: boundOffset, radius: boundRadius, slot: slot, active: active);

        return this;
    }
    /// <summary>Declares a DYNAMIC per-object instance with balanced begin/end handling around <paramref name="emit"/>.
    /// If <paramref name="emit"/> throws, the builder is left with the instance open and partial state — discard it
    /// (no builder path rolls back on a throw).</summary>
    /// <param name="slot">The dynamic-transform slot index (0-based) this instance's bound tracks.</param>
    /// <param name="boundOffset">The bound's pre-dynamic offset (added to the slot's per-frame position).</param>
    /// <param name="boundRadius">The instance's bounding-sphere radius (post-dynamic geometry folded in).</param>
    /// <param name="emit">The instructions that belong to the instance.</param>
    /// <returns>This builder.</returns>
    public SdfProgramBuilder DynamicInstance(int slot, Vector3 boundOffset, float boundRadius, Action<SdfProgramBuilder> emit) {
        ScopedInstance(isDynamic: true, center: boundOffset, radius: boundRadius, slot: slot, emit: emit);

        return this;
    }
    /// <summary>Closes the currently open instance (see <see cref="BeginInstance"/>/<see cref="BeginInstanceDynamic"/>).</summary>
    /// <exception cref="InvalidOperationException">No instance is open.</exception>
    public SdfProgramBuilder EndInstance() {
        if (m_openInstanceFirst < 0) {
            throw new InvalidOperationException(message: "EndInstance was called with no open instance (unbalanced Begin/EndInstance).");
        }

        if (m_instances.Count >= MaxInstances) {
            throw new InvalidOperationException(message: $"A program may declare at most {MaxInstances} instances.");
        }

        m_instances.Add(item: new SdfInstanceRange(
            First: m_openInstanceFirst,
            End: m_instructions.Count,
            IsDynamic: m_openInstanceIsDynamic,
            Center: m_openInstanceCenter,
            Radius: m_openInstanceRadius,
            Slot: m_openInstanceSlot,
            Active: m_openInstanceActive
        ));

        m_openInstanceFirst = -1;

        return this;
    }
    private void BeginInstanceCore(bool isDynamic, Vector3 center, float radius, int slot, bool active = true) {
        if (isDynamic && ((slot < 0) || (slot > SdfProgram.MaxDynamicTransformSlot))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot), message: $"Dynamic instance slots must be in [0, {SdfProgram.MaxDynamicTransformSlot}].");
        }

        if (m_openInstanceFirst >= 0) {
            throw new InvalidOperationException(message: "BeginInstance/BeginInstanceDynamic was called with an instance already open (nesting is not supported).");
        }

        m_openInstanceFirst = m_instructions.Count;
        m_openInstanceIsDynamic = isDynamic;
        m_openInstanceActive = active;
        m_openInstanceCenter = center;
        m_openInstanceRadius = radius;
        m_openInstanceSlot = slot;
    }
    private void ScopedInstance(bool isDynamic, Vector3 center, float radius, int slot, Action<SdfProgramBuilder> emit) {
        ArgumentNullException.ThrowIfNull(emit);

        var instanceCount = m_instances.Count;

        BeginInstanceCore(isDynamic: isDynamic, center: center, radius: radius, slot: slot);
        emit(this);

        // EndInstance always appends exactly one range, so a changed count directly detects an emitter that closed
        // the instance itself — including an End-then-Begin pair that would restore the open-index sentinel.
        if (m_instances.Count != instanceCount) {
            throw new InvalidOperationException(message: "A scoped instance emitter must leave its instance open; do not call BeginInstance/EndInstance inside the emitter.");
        }

        EndInstance();
    }
    public SdfProgramBuilder ResetPoint() {
        return Transform(op: SdfOp.ResetPoint);
    }
    public SdfProgramBuilder Translate(Vector3 offset) {
        return Transform(
            data0: new Vector4(
                value: offset,
                w: 0f
            ),
            op: SdfOp.Translate
        );
    }
    public SdfProgramBuilder Rotate(Quaternion rotation) {
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
    public SdfProgramBuilder Scale(Vector3 scale) {
        // The degenerate-scale clamp AND the resulting distance rescale are HOST-BAKED (Data0.xyz = |scale| clamped,
        // Data0.w = its min axis): shapes evaluate millions of times per frame while programs build once, and the
        // shader's per-evaluation abs/max/min collapse to one lane read. The min-axis factor is the conservative
        // correction for a non-uniform scale — f(S⁻¹p)·min(s) is 1-Lipschitz, so it can only underestimate true
        // distance, never overstep. HLSL's abs/max/min agree with MathF's bit-for-bit on every non-NaN input, and
        // 0.0001f is the same float the shader used to clamp against (KEEP IN SYNC with SDF_OP_SCALE in
        // Assets/Shaders/Sdf/sdf-vm.hlsli).
        var clamped = Vector3.Max(Vector3.Abs(scale), new Vector3(0.0001f));

        return Transform(
            data0: new Vector4(
                value: clamped,
                w: MathF.Min(clamped.X, MathF.Min(clamped.Y, clamped.Z))
            ),
            op: SdfOp.Scale
        );
    }
    /// <summary>Applies a rigid transform (translation + orientation) sourced at evaluation time from per-frame dynamic
    /// transform <paramref name="slot"/> — element <c>2*slot</c> is the position, <c>2*slot+1</c> the orientation
    /// quaternion in the renderer's dynamic-transform buffer. The shape that follows is repositioned each frame by
    /// updating that buffer, leaving this program (uploaded once) untouched. Honored only by the world render path
    /// (shaders compiled with <c>SDF_DYNAMIC_TRANSFORMS</c>).</summary>
    /// <param name="slot">The dynamic-transform slot index (0-based).</param>
    public SdfProgramBuilder TransformDynamic(int slot) {
        if ((slot < 0) || (slot > SdfProgram.MaxDynamicTransformSlot)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot), message: $"Dynamic transform slots must be in [0, {SdfProgram.MaxDynamicTransformSlot}].");
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
    /// <summary>Twists space about the local Y axis: the XZ plane rotates by <paramref name="rate"/> · y radians.
    /// NOT an isometry — keep rates moderate so the march stays stable.</summary>
    /// <param name="rate">Radians of rotation per unit of local Y.</param>
    public SdfProgramBuilder TwistY(float rate) {
        return Transform(
            data0: new Vector4(
                w: 0f,
                x: rate,
                y: 0f,
                z: 0f
            ),
            op: SdfOp.TwistY
        );
    }
    /// <summary>Log-spherical domain warp: tiles space into infinite self-similar "Droste" shells. A translation along
    /// <c>log(radius)</c> becomes a uniform SCALING in Cartesian space, so the prototype shape(s) that follow repeat
    /// outward and inward as scaled copies from a handful of instructions. Folds ONLY the radial coordinate (no polar
    /// pinching); an optional per-shell Z-spin gives the Droste spiral at no cost. NOT an isometry — the r/density
    /// correction rides the runtime <c>distanceScale</c> and <c>AnalyzeLipschitz</c> bakes a conservative step clamp, so
    /// the over-relaxed march stays hole-free. Like <see cref="Repeat"/>, the prototype content should stay within one
    /// shell cell (radii within a factor of <paramref name="shellRatio"/>) so no shell boundary overshoots.</summary>
    /// <param name="shellRatio">The Cartesian scale factor between consecutive shells (e.g. 2 = each shell twice the
    /// previous). Clamped to at least 1.0001 (a ratio of 1 means no shells and a divide-by-zero on the baked 1/w).</param>
    /// <param name="twist">Radians of Z-spin added per shell (the Droste spiral). 0 = concentric, un-spun shells.</param>
    public SdfProgramBuilder LogSphere(float shellRatio, float twist = 0f) {
        // w = ln(ratio) and its reciprocal are HOST-BAKED (the shader avoids a per-eval log-of-constant and a divide,
        // matching Repeat's baked-reciprocal pattern; KEEP IN SYNC with SDF_OP_LOG_SPHERE in sdf-vm.hlsli).
        var ratio = MathF.Max(shellRatio, 1.0001f);
        var w = MathF.Log(ratio);

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
    /// <summary>Stochastic domain-repeat fold: tiles space into cells of <paramref name="spacing"/> like
    /// <see cref="Repeat"/>, then per cell displaces the point by a hashed offset, optionally tumbles (a hashed
    /// rotation), and optionally recolors by a hashed material variant — scattering the prototype that follows into a
    /// jittered field from a single instruction. Both the displacement and the tumble are ISOMETRIES, so the field stays
    /// distance-preserving (only the jitter half-amplitude joins <c>AnalyzeLipschitz</c>). The per-cell hash is
    /// INTEGER-ONLY (canonical PCG3D keyed on the two's-complement cell index xored with <paramref name="seed"/>), so
    /// cell decisions are bit-identical across both GPU backends. jitter/tumble/materialVariants each default to an EXACT
    /// identity, so an unused op leaves the point byte-identical. Like <see cref="Repeat"/>, keep the prototype clear of
    /// the cell boundary: the caller must ensure jitter/2 + prototype radius ≤ min(spacing)/2 (this builder validates
    /// only the half it can see — that the displacement alone cannot cross a boundary). KEEP IN SYNC with
    /// SDF_OP_CELL_JITTER in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="spacing">The per-axis cell spacing in world units (clamped to ≥ 0.001 per axis).</param>
    /// <param name="jitter">The peak-to-peak per-cell position displacement in world units (0 = no displacement).</param>
    /// <param name="seed">The hash seed — different seeds give independent jitter/tumble/variant fields.</param>
    /// <param name="tumble">The per-cell rotation amount in [0,1]: 0 = no rotation, 1 = up to ±π about a random axis
    /// (clamped to [0,1]).</param>
    /// <param name="materialVariants">The number of hashed material rows (0 = geometric only): a hit in a cell adds a
    /// hashed 0..variants-1 to its shape's material id.</param>
    /// <param name="flavor">How the per-cell POSITION offset is distributed (the SDF_NOISE_* Blend lane, header.z):
    /// <see cref="SdfNoiseFlavor.White"/> (default, byte-identical to pre-flavor programs), <see cref="SdfNoiseFlavor.Blue"/>,
    /// or <see cref="SdfNoiseFlavor.Gaussian"/>. Reshapes ONLY the displacement — tumble and material variant are
    /// unaffected, and every flavor shares White's <c>±jitter/2</c> offset bound (no Lipschitz change). KEEP IN SYNC with
    /// SDF_NOISE_* and the SDF_OP_CELL_JITTER flavor branch in Assets/Shaders/Sdf/sdf-vm.hlsli.</param>
    /// <exception cref="ArgumentException"><paramref name="materialVariants"/> is negative, or half of
    /// <paramref name="jitter"/> is not strictly less than half the smallest <paramref name="spacing"/> component (the
    /// displaced content would cross a cell boundary and hole the march).</exception>
    public SdfProgramBuilder CellJitter(Vector3 spacing, float jitter, uint seed = 0u, float tumble = 0f, int materialVariants = 0, SdfNoiseFlavor flavor = SdfNoiseFlavor.White) {
        // The degenerate-spacing clamp and the reciprocal are HOST-BAKED (Data1.xyz), mirroring Repeat().
        var clamped = Vector3.Max(spacing, new Vector3(0.001f));

        if (materialVariants < 0) {
            throw new ArgumentException(message: "CellJitter materialVariants must be >= 0 (0 = geometric only).", paramName: nameof(materialVariants));
        }

        // The half the builder CAN see: the displacement alone must not push content across the round() cell boundary.
        // (The caller must also keep jitter/2 + prototype radius <= min(spacing)/2 — the prototype radius is unknown here.)
        var minSpacing = MathF.Min(clamped.X, MathF.Min(clamped.Y, clamped.Z));

        if ((MathF.Abs(jitter) * 0.5f) >= (0.5f * minSpacing)) {
            throw new ArgumentException(message: "CellJitter jitter/2 must be < min(spacing)/2, or jittered content crosses the cell boundary and holes the march (the caller must also keep jitter/2 + prototype radius <= min(spacing)/2).", paramName: nameof(jitter));
        }

        var clampedTumble = Math.Clamp(tumble, 0f, 1f);

        m_instructions.Add(item: new SdfInstruction(
            Blend: (uint)flavor,
            Data0: new Vector4(
                value: clamped,
                w: jitter
            ),
            Data1: new Vector4(
                value: (Vector3.One / clamped),
                w: clampedTumble
            ),
            Material: (uint)materialVariants,
            Op: SdfOp.CellJitter,
            Shape: seed
        ));

        return this;
    }
    /// <summary>Angular DOMAIN-REPEAT fold: folds the plane perpendicular to <paramref name="axis"/> into
    /// <paramref name="count"/> equal sectors, so the prototype that follows repeats ROTATIONALLY around the axis —
    /// gears, wheels, columns of a rotunda, clock ticks, flower petals — from a single instruction (the rotational
    /// sibling of the linear <see cref="Repeat"/> and the lattice <see cref="WallpaperFold"/>). The fold rotates the
    /// point into the base sector and, when <paramref name="mirror"/> is set, reflects each sector across its bisector
    /// for kaleidoscope symmetry: BOTH are ISOMETRIES, so the field stays 1-Lipschitz (factor 1, NO step clamp — like
    /// <see cref="Repeat"/>) and no cull bound changes. Like <see cref="Repeat"/>, keep the prototype clear of the
    /// sector walls (the two radial half-planes through the axis) — content that overspills a wall is clipped by the
    /// neighbouring sector. The sector angle and its reciprocals are HOST-BAKED. KEEP IN SYNC with SDF_OP_REPEAT_POLAR
    /// in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="count">The number of sectors around the axis (clamped to ≥ 1; 1 = a single full-circle no-op).</param>
    /// <param name="axis">The rotation axis — the fold acts in the plane perpendicular to it (default
    /// <see cref="SdfPolarAxis.Y"/>, the XZ ground plane).</param>
    /// <param name="mirror">When <see langword="true"/>, reflects each sector across its bisector so adjacent sectors
    /// mirror — the kaleidoscope fold (still an isometry).</param>
    /// <param name="materialStride">The per-sector palette stride: the sector index (0..count-1) times this strides the
    /// material id of a later shape win, so each sector can select its own palette row. 0 (the default) keeps the fold
    /// purely geometric.</param>
    /// <exception cref="ArgumentException"><paramref name="materialStride"/> is negative.</exception>
    public SdfProgramBuilder RepeatPolar(int count, SdfPolarAxis axis = SdfPolarAxis.Y, bool mirror = false, int materialStride = 0) {
        if (materialStride < 0) {
            throw new ArgumentException(message: "RepeatPolar materialStride must be >= 0 (0 = geometric only).", paramName: nameof(materialStride));
        }

        // count and the sector angle's reciprocals are HOST-BAKED (Data0.yzw): shapes evaluate millions of times per
        // frame, programs build once (KEEP IN SYNC with SDF_OP_REPEAT_POLAR in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var sectors = Math.Max(1, count);
        var angle = ((2f * MathF.PI) / sectors);

        m_instructions.Add(item: new SdfInstruction(
            Blend: (mirror ? 1u : 0u),
            Data0: new Vector4(
                w: (1f / sectors),   // 1/count — the per-sector material wrap
                x: angle,            // 2π/count — the sector angle
                y: (1f / angle),     // count/(2π) — 1/angle, for the sector floor-division
                z: sectors           // count — the sector-index wrap
            ),
            Data1: default,
            Material: (uint)materialStride,
            Op: SdfOp.RepeatPolar,
            Shape: (uint)axis
        ));

        return this;
    }
    /// <summary>Bends space about the local X axis: the XY plane rotates by <paramref name="rate"/> · x radians.</summary>
    /// <param name="rate">Radians of rotation per unit of local X.</param>
    public SdfProgramBuilder BendX(float rate) {
        return Transform(
            data0: new Vector4(
                w: 0f,
                x: rate,
                y: 0f,
                z: 0f
            ),
            op: SdfOp.BendX
        );
    }
    /// <summary>Bends the XY plane by <paramref name="rate"/> · y radians.</summary>
    /// <param name="rate">Radians of rotation per unit of local Y.</param>
    public SdfProgramBuilder BendY(float rate) {
        return Transform(
            data0: new Vector4(
                w: 0f,
                x: rate,
                y: 0f,
                z: 0f
            ),
            op: SdfOp.BendY
        );
    }
    /// <summary>Rotates the YZ plane by <paramref name="rate"/> · y radians. The three bends are DISTINCT ops, not a
    /// symmetric family: <see cref="BendX"/> keys on x and rotates XY, <see cref="BendY"/> keys on y and rotates XY, and
    /// this one keys on y and rotates YZ. Each keys on a coordinate INSIDE the plane it rotates, which is what gives the
    /// bends their <c>1 + rate·ρ</c> Lipschitz factor (see <c>SdfProgram.BendOperatorNorm</c>) rather than
    /// <see cref="TwistY"/>'s smaller one.</summary>
    /// <param name="rate">Radians of rotation per unit of local Y.</param>
    public SdfProgramBuilder BendZ(float rate) {
        return Transform(
            data0: new Vector4(
                w: 0f,
                x: rate,
                y: 0f,
                z: 0f
            ),
            op: SdfOp.BendZ
        );
    }
    /// <summary>Elongates the shape that follows: the point clamps into a box of the given extents, sweeping the
    /// shape's cross-section over ±extents (the classic capsule-from-sphere operator).</summary>
    /// <param name="extents">The per-axis elongation half-extents (0 on an axis = no stretch there).</param>
    public SdfProgramBuilder Elongate(Vector3 extents) {
        return Transform(
            data0: new Vector4(
                value: extents,
                w: 0f
            ),
            op: SdfOp.Elongate
        );
    }
    /// <summary>Shells the ENTIRE field accumulated so far into a hollow skin of the given thickness — a FIELD op:
    /// order it after everything it should shell.</summary>
    /// <param name="thickness">The shell half-thickness.</param>
    public SdfProgramBuilder Onion(float thickness) {
        return Transform(
            data0: new Vector4(
                w: 0f,
                x: thickness,
                y: 0f,
                z: 0f
            ),
            op: SdfOp.Onion
        );
    }
    /// <summary>Adds a bounded sinusoidal DISPLACEMENT to the field accumulated so far — surface relief (bumps,
    /// corrugation, a rippled skin) evaluated at the current point: the SDF-native answer to height/parallax mapping,
    /// where the relief is REAL geometry (it shadows and self-occludes). A FIELD op (like <see cref="Onion"/>/
    /// <see cref="Dilate"/>): order it after the shapes it should displace. The separable <c>sin·sin·sin</c> basis is
    /// deterministic across both backends. NOT 1-Lipschitz — the relief's gradient reaches <c>amplitude·‖frequency‖</c>,
    /// so the field can overestimate true distance by up to <c>1 + amplitude·‖frequency‖</c> and <c>AnalyzeLipschitz</c>
    /// bakes that as a conservative step clamp; keep <c>amplitude·‖frequency‖</c> moderate (a large product clamps the
    /// march to tiny steps). KEEP IN SYNC with SDF_OP_DISPLACE in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="frequency">Per-axis angular frequency of the sinusoid (radians per world unit).</param>
    /// <param name="amplitude">Peak displacement added to the field (world units; 0 = an exact identity).</param>
    public SdfProgramBuilder Displace(Vector3 frequency, float amplitude) {
        return Transform(
            data0: new Vector4(
                value: frequency,
                w: amplitude
            ),
            op: SdfOp.Displace
        );
    }
    /// <summary>Warps the sample point by a bounded, cross-coupled sinusoidal field BEFORE the shapes evaluate — organic
    /// bulging / wobble / terrain. A POINT op (like the fold ops): order it before the shapes it should warp. Each axis
    /// is driven by the NEXT axis's coordinate, so the warp is non-separable; the basis is deterministic across both
    /// backends. NOT an isometry — the metric stretches by up to <c>1 + amplitude·‖frequency‖</c>, so
    /// <c>AnalyzeLipschitz</c> bakes a conservative step clamp (and folds the point's max travel into a downstream
    /// twist/bend's reach); keep <c>amplitude·‖frequency‖</c> moderate. KEEP IN SYNC with SDF_OP_DOMAIN_WARP in
    /// Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="frequency">Per-axis angular frequency of the warp (radians per world unit).</param>
    /// <param name="amplitude">Peak point displacement (world units; 0 = an exact identity).</param>
    public SdfProgramBuilder DomainWarp(Vector3 frequency, float amplitude) {
        return Transform(
            data0: new Vector4(
                value: frequency,
                w: amplitude
            ),
            op: SdfOp.DomainWarp
        );
    }
    /// <summary>Inflates the ENTIRE field accumulated so far by a radius (rounds and fattens everything before it) —
    /// a FIELD op: order it after everything it should inflate.</summary>
    /// <param name="radius">The inflation radius.</param>
    public SdfProgramBuilder Dilate(float radius) {
        return Transform(
            data0: new Vector4(
                w: 0f,
                x: radius,
                y: 0f,
                z: 0f
            ),
            op: SdfOp.Dilate
        );
    }
    public SdfProgramBuilder Repeat(Vector3 spacing) {
        // The degenerate-spacing clamp and the reciprocal are HOST-BAKED (Data1.xyz): shapes evaluate millions of
        // times per frame, programs build once (KEEP IN SYNC with SDF_OP_REPEAT in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var clamped = Vector3.Max(spacing, new Vector3(0.001f));

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
    public SdfProgramBuilder RepeatLimited(Vector3 spacing, Vector3 limit) {
        // The degenerate-spacing clamp is HOST-BAKED, exactly as <see cref="Repeat"/> bakes it (KEEP IN SYNC with
        // SDF_OP_REPEAT_LIMITED in Assets/Shaders/Sdf/sdf-vm.hlsli). Clamped WITHOUT Abs, matching the shader's old
        // max(data0.xyz, 0.001) — a negative spacing must keep behaving as it did. Unlike Repeat there is no free lane
        // for the reciprocal (Data1.xyz carries the limit), so the shader keeps its divide.
        var clamped = Vector3.Max(spacing, new Vector3(0.001f));

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
    /// <summary>Folds the point's in-plane coordinates onto the fundamental cell of a wallpaper symmetry group — the
    /// shapes that follow repeat under the group's mirrors/rotations across the lattice. Every fold branch is an
    /// isometry, so distances are preserved; like <see cref="Repeat"/>, content must stay clear of cell boundaries
    /// (and of the rotation seams of P2/CMM/P4*) unless a mirror of the group protects that edge.</summary>
    /// <param name="group">The wallpaper group. P4/P4M/P4G and the hex groups (P3 and up) require a SQUARE cell —
    /// quarter-turns and the equilateral hex lattice are only isometries there (hex pitch = <paramref name="cell"/>.X).</param>
    /// <param name="cell">The lattice cell extents in the fold plane.</param>
    /// <param name="limit">The repeat-cell limit per plane axis (RepeatLimited semantics; axial indices for hex).</param>
    /// <param name="plane">The plane the fold acts on (the third axis is untouched).</param>
    /// <param name="materialStride">The parity-material stride: the cell key (checker parity for square lattices,
    /// the 3-coloring for hex) times this strides the material id of later shape wins in the chain, so each lattice
    /// cell selects its own row of the palette. 0 (the default) keeps the fold purely geometric.</param>
    /// <param name="lodDistance">The symmetry-LOD distance threshold: past it the lattice keeps its copy positions
    /// but skips the in-cell folds (upright copies, cheaper and shimmer-free at range). 0 (the default) = off.</param>
    public SdfProgramBuilder WallpaperFold(SdfWallpaperGroup group, Vector2 cell, Vector2 limit, SdfWallpaperPlane plane = SdfWallpaperPlane.XZ, int materialStride = 0, float lodDistance = 0f) {
        // The reciprocal cell extents are HOST-BAKED (Data0.zw): square lattices read them as 1/cell for the lattice
        // round; hex lattices (pitch = cell.x) read z = 1/pitch and w = 2/(√3·pitch) — the two divides in the axial
        // decompose (KEEP IN SYNC with the fold functions in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var isHex = (group >= SdfWallpaperGroup.P3);
        var inverseX = (1f / MathF.Max(cell.X, 0.0001f));
        var inverseY = (isHex ? ((2f / 1.7320508f) * inverseX) : (1f / MathF.Max(cell.Y, 0.0001f)));

        m_instructions.Add(item: new SdfInstruction(
            Blend: (uint)plane,
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
            Material: (uint)materialStride,
            Op: SdfOp.WallpaperFold,
            Shape: (uint)group
        ));

        return this;
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
    /// <summary>Reflection fold across an ARBITRARY plane — the general-normal superset of <see cref="SymmetryX"/>/
    /// <see cref="SymmetryY"/>/<see cref="SymmetryZ"/>: everything on the plane's negative side (<c>dot(p, normal) +
    /// offset &lt; 0</c>) is mirrored onto its positive side, so one authored half repeats mirror-imaged (a kaleidoscope
    /// leaf, a bilateral body, the reflect atom of a KIFS fold). A reflection is an ISOMETRY, so the field stays
    /// 1-Lipschitz (factor 1, NO step clamp) and no cull bound changes. Like the axis symmetries, keep authored content
    /// on the plane's positive (kept) side. The normal is normalized host-side. KEEP IN SYNC with SDF_OP_SYMMETRY_PLANE
    /// in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="normal">The plane normal (normalized here; the positive side, toward the normal, is the kept half).</param>
    /// <param name="offset">The plane's constant term: the mirror plane is <c>dot(p, normal) + offset = 0</c>, so it
    /// sits at signed distance <c>-offset</c> along the normal. A POSITIVE offset therefore moves the plane AGAINST the
    /// normal. 0 puts it through the local origin.</param>
    public SdfProgramBuilder SymmetryPlane(Vector3 normal, float offset = 0f) {
        // Normalized HOST-SIDE (the shader's reflect assumes a unit normal; a drifted one would scale the mirrored half).
        return Transform(
            data0: new Vector4(
                value: Vector3.Normalize(value: normal),
                w: offset
            ),
            op: SdfOp.SymmetryPlane
        );
    }
    public SdfProgramBuilder Sphere(float radius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: radius,
                y: 0f,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Sphere,
            smooth: smooth
        );
    }
    /// <summary>A vesica (lens): the intersection of two spheres of radius <paramref name="radius"/> whose centers are
    /// 2·<paramref name="halfSeparation"/> apart, revolved into a 3D lens pointed along ±Y (a disc of radius
    /// radius−halfSeparation in XZ). <paramref name="halfSeparation"/> is clamped below <paramref name="radius"/> so
    /// the tip half-height √(r²−d²) is real; it is HOST-BAKED (skips the per-eval sqrt) — KEEP IN SYNC with sdfVesica
    /// in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    public SdfProgramBuilder Vesica(float radius, float halfSeparation, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        var r = MathF.Abs(radius);
        var d = MathF.Min(MathF.Abs(halfSeparation), (r * 0.9999f)); // d < r keeps b = √(r²−d²) real and positive
        var b = MathF.Sqrt((r * r) - (d * d));

        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: r,
                y: d,
                z: b
            ),
            material: material,
            shape: SdfShapeType.Vesica,
            smooth: smooth
        );
    }
    /// <summary>A rounded rectangle (iq sdRoundedBox) lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a
    /// rounded slab/plaque, <see cref="SdfLift.Revolve"/> a rounded disc/puck. Exact and 1-Lipschitz. KEEP IN SYNC
    /// with sdfRoundedRect in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="halfWidth">Half-width of the rectangle (its local X half-extent).</param>
    /// <param name="halfHeight">Half-height of the rectangle (its local Y half-extent).</param>
    /// <param name="cornerRadius">Corner-rounding radius; clamped to the smaller half-extent (corners round inward).</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset (for <see cref="SdfLift.Revolve"/>) or the extrude half-height (for
    /// <see cref="SdfLift.Extrude"/>); clamped to ≥ 0.</param>
    public SdfProgramBuilder RoundedRectangle(float halfWidth, float halfHeight, float cornerRadius, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        var hw = MathF.Abs(halfWidth);
        var hh = MathF.Abs(halfHeight);

        return Shape(
            blend: blend,
            derived1: (float)(uint)lift,
            dimensions: new Vector4(
                w: MathF.Max(0f, liftAmount),
                x: hw,
                y: hh,
                z: Math.Clamp(cornerRadius, 0f, MathF.Min(hw, hh))
            ),
            material: material,
            shape: SdfShapeType.RoundedRectangle,
            smooth: smooth
        );
    }
    /// <summary>A regular convex <paramref name="sides"/>-gon (iq sdStar with the m = 2 regular-polygon case) lifted to
    /// a 3D solid — <see cref="SdfLift.Extrude"/> gives a prism (a nut, a column, a gem), <see cref="SdfLift.Revolve"/>
    /// a lathe of the polygon's profile. The half-sector π/n is HOST-BAKED. Exact and 1-Lipschitz. KEEP IN SYNC with
    /// sdfPolyStar/sdfStar2D in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="sides">The side count n (clamped to ≥ 3).</param>
    /// <param name="radius">The circumradius (centre to a vertex).</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    public SdfProgramBuilder RegularPolygon(int sides, float radius, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        var n = Math.Max(3, sides);

        return Shape(
            blend: blend,
            derived1: (float)(uint)lift,      // Data1.y = lift mode
            derived2: 1f,                     // Data1.z = ecs.y = 1 (m = 2: the regular-polygon case)
            dimensions: new Vector4(
                w: MathF.Max(0f, liftAmount),
                x: MathF.Abs(radius),
                y: (MathF.PI / n),            // an = π/n, HOST-BAKED
                z: 0f                         // ecs.x = 0
            ),
            material: material,
            shape: SdfShapeType.RegularPolygon,
            smooth: smooth
        );
    }
    /// <summary>An <paramref name="points"/>-pointed star (iq sdStar) lifted to a 3D solid — <see cref="SdfLift.Extrude"/>
    /// gives a star prism (a badge, a gem), <see cref="SdfLift.Revolve"/> a spiked lathe. The baked constants
    /// (π/n and ecs = (cos(π/m), sin(π/m))) are HOST-BAKED. Exact and 1-Lipschitz. KEEP IN SYNC with
    /// sdfPolyStar/sdfStar2D in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="points">The point count n (clamped to ≥ 2).</param>
    /// <param name="radius">The outer radius (centre to a point tip).</param>
    /// <param name="sharpness">The inner-radius control m, clamped to [2, n]: 2 is a convex n-gon, larger is sharper
    /// (deeper notches between points).</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    public SdfProgramBuilder Star(int points, float radius, float sharpness, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        var n = Math.Max(2, points);
        var m = Math.Clamp(sharpness, 2f, n);
        var en = (MathF.PI / m);

        return Shape(
            blend: blend,
            derived1: (float)(uint)lift,      // Data1.y = lift mode
            derived2: MathF.Sin(en),          // Data1.z = ecs.y = sin(π/m)
            dimensions: new Vector4(
                w: MathF.Max(0f, liftAmount),
                x: MathF.Abs(radius),
                y: (MathF.PI / n),            // an = π/n, HOST-BAKED
                z: MathF.Cos(en)             // ecs.x = cos(π/m), HOST-BAKED
            ),
            material: material,
            shape: SdfShapeType.Star,
            smooth: smooth
        );
    }
    /// <summary>An isosceles trapezoid (iq sdTrapezoid) lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a
    /// keystone/wedge prism, <see cref="SdfLift.Revolve"/> a frustum/lampshade/cup. Exact and 1-Lipschitz. KEEP IN
    /// SYNC with sdfTrapezoidSolid in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="bottomHalfWidth">Half-width of the bottom edge (at local −Y).</param>
    /// <param name="topHalfWidth">Half-width of the top edge (at local +Y).</param>
    /// <param name="halfHeight">Half-height of the trapezoid.</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    public SdfProgramBuilder Trapezoid(float bottomHalfWidth, float topHalfWidth, float halfHeight, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            derived1: (float)(uint)lift,
            dimensions: new Vector4(
                w: MathF.Max(0f, liftAmount),
                x: MathF.Abs(bottomHalfWidth),
                y: MathF.Abs(topHalfWidth),
                z: MathF.Abs(halfHeight)
            ),
            material: material,
            shape: SdfShapeType.Trapezoid,
            smooth: smooth
        );
    }
    /// <summary>An ellipse (iq's exact sdEllipse) lifted to a 3D solid — <see cref="SdfLift.Revolve"/> at offset 0 gives
    /// an exact SPHEROID (which, unlike the approximate <see cref="Ellipsoid(Vector3, int, SdfBlendOp, float)"/> #6,
    /// earns a real cull bound), <see cref="SdfLift.Extrude"/> an elliptic-cylinder prism. Exact and 1-Lipschitz.
    /// KEEP IN SYNC with sdfEllipseSolid in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="semiX">The semi-axis along local X.</param>
    /// <param name="semiY">The semi-axis along local Y.</param>
    /// <param name="lift">Whether to revolve the profile around Y (offset 0 ⇒ a spheroid) or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    public SdfProgramBuilder Ellipse(float semiX, float semiY, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        var ea = MathF.Max(MathF.Abs(semiX), 1e-4f);
        var eb = MathF.Max(MathF.Abs(semiY), 1e-4f);

        // The exact ellipse divides by (eb²−ea²); nudge a perfect circle apart so it never divides by zero (a circle is
        // better served by Sphere/Cylinder anyway). Sub-pixel at any sane authoring scale.
        if (MathF.Abs(ea - eb) < 1e-4f) {
            eb = (ea + 1e-4f);
        }

        return Shape(
            blend: blend,
            derived1: (float)(uint)lift,
            dimensions: new Vector4(
                w: MathF.Max(0f, liftAmount),
                x: ea,
                y: eb,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Ellipse,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Box(Vector3 halfExtents, float round, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: halfExtents,
                w: round
            ),
            material: material,
            shape: SdfShapeType.Box,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Torus(float majorRadius, float minorRadius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: majorRadius,
                y: minorRadius,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Torus,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Plane(Vector3 normal, float offset, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: Vector3.Normalize(value: normal),
                w: offset
            ),
            material: material,
            shape: SdfShapeType.Plane,
            smooth: smooth
        );
    }
    public SdfProgramBuilder RoundCone(float lowerRadius, float upperRadius, float height, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The slope terms are HOST-BAKED (Data0.w = b, Data1.y = a): the shader used to rederive a divide and a sqrt
        // per evaluation from constants (KEEP IN SYNC with sdfRoundCone in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var slope = ((lowerRadius - upperRadius) / MathF.Max(height, 0.0001f));

        return Shape(
            blend: blend,
            derived1: MathF.Sqrt(MathF.Max((1f - (slope * slope)), 0f)),
            dimensions: new Vector4(
                w: slope,
                x: lowerRadius,
                y: upperRadius,
                z: height
            ),
            material: material,
            shape: SdfShapeType.RoundCone,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Capsule(Vector3 endpoint, float radius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            // Data1.y carries the HOST-BAKED 1/dot(endpoint, endpoint): shapes evaluate millions of times per frame
            // while programs build once, and the shared multiply keeps both backends' shader codegen identical where a
            // per-eval divide contracted differently (KEEP IN SYNC with sdfCapsule in Assets/Shaders/Sdf/sdf-vm.hlsli).
            derived1: (1f / MathF.Max(Vector3.Dot(endpoint, endpoint), 0.0001f)),
            dimensions: new Vector4(
                value: endpoint,
                w: radius
            ),
            material: material,
            shape: SdfShapeType.Capsule,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Cylinder(float radius, float halfHeight, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                w: 0f,
                x: radius,
                y: halfHeight,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Cylinder,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Ellipsoid(Vector3 radii, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The degenerate-radius clamp and the inverse radii are HOST-BAKED (Data1.yzw): the shader used to pay two
        // vector divides per evaluation (KEEP IN SYNC with sdfEllipsoid in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var clamped = Vector3.Max(Vector3.Abs(value: radii), new Vector3(0.0001f));
        var inverse = (Vector3.One / clamped);

        return Shape(
            blend: blend,
            derived1: inverse.X,
            derived2: inverse.Y,
            derived3: inverse.Z,
            dimensions: new Vector4(
                value: clamped,
                w: 0f
            ),
            material: material,
            shape: SdfShapeType.Ellipsoid,
            smooth: smooth
        );
    }
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: halfExtents,
                w: round
            ),
            material: ScreenMaterialId,
            shape: SdfShapeType.ScreenSlab,
            smooth: smooth
        );
    }
    /// <summary>A <see cref="ScreenSlab"/> whose LIT face samples a bound screen source (see
    /// <see cref="SdfWorldEngine.SetScreenSource"/>) instead of the flat screen material, when one is bound this
    /// frame — a diegetic screen (an emulator's framebuffer, e.g.) on STATIC geometry. The slab's shape/distance field
    /// is identical to the plain overload (a rounded box); only shading differs. The world-space frame maps a hit
    /// point to the slab's <c>[0,1]²</c> UV: <paramref name="worldRight"/>/<paramref name="worldUp"/> must be unit and
    /// orthogonal to each other and to the slab's local Z (its front-face normal), and should match the rigid
    /// transform (<see cref="Translate"/>/<see cref="Rotate"/>) already applied to the point when this shape is
    /// declared — a mismatched frame sizes/rotates the sampled image wrong without affecting the geometry at all.</summary>
    /// <param name="halfExtents">The slab's local half-extents (as <see cref="ScreenSlab(Vector3, float, SdfBlendOp, float)"/>).</param>
    /// <param name="round">The corner-rounding radius.</param>
    /// <param name="worldOrigin">The front face's world-space center.</param>
    /// <param name="worldRight">The unit world-space axis the UV's U increases along (the slab's local +X, in world space).</param>
    /// <param name="worldUp">The unit world-space axis the UV's V increases against — V = 0 at the top (the slab's local +Y, in world space).</param>
    /// <param name="screenIndex">The screen source slot (0..7) this surface samples.</param>
    /// <param name="blend">The blend operator against the field accumulated so far.</param>
    /// <param name="smooth">The smooth-blend radius (meaningful only for a smooth <paramref name="blend"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..7</c>, or this
    /// program has already declared <see cref="MaxScreenSurfaces"/> screen surfaces.</exception>
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, Vector3 worldOrigin, Vector3 worldRight, Vector3 worldUp, int screenIndex, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A screen index must be 0..{MaxScreenSurfaces - 1}.");
        }

        if (m_screenSurfaces.Count >= MaxScreenSurfaces) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A program may declare at most {MaxScreenSurfaces} screen surfaces.");
        }

        m_screenSurfaces.Add(item: new SdfScreenSurface(
            HalfHeight: halfExtents.Y,
            HalfWidth: halfExtents.X,
            Origin: worldOrigin,
            Right: Vector3.Normalize(value: worldRight),
            ScreenIndex: screenIndex,
            Up: Vector3.Normalize(value: worldUp)
        ));

        // The screen-instance sentinel: ScreenMaterialId flags "screen shading" (as the flat-material overload), the
        // +1+screenIndex offset tells the shader WHICH declared surface (and thus which screen source) a hit belongs
        // to — decoded back by subtracting the same offset (KEEP IN SYNC with sdf-world.hlsli's screen shading).
        return Shape(
            blend: blend,
            dimensions: new Vector4(
                value: halfExtents,
                w: round
            ),
            material: (ScreenMaterialId + 1 + screenIndex),
            shape: SdfShapeType.ScreenSlab,
            smooth: smooth
        );
    }
    /// <summary>A sampled <see cref="ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>
    /// overload that derives the screen's world-space right/up axes from the slab's static orientation.</summary>
    /// <param name="halfExtents">The slab's local half-extents.</param>
    /// <param name="round">The corner-rounding radius.</param>
    /// <param name="worldOrigin">The front face's world-space center.</param>
    /// <param name="worldOrientation">The static slab orientation in world space.</param>
    /// <param name="screenIndex">The screen source slot (0..7) this surface samples.</param>
    /// <param name="blend">The blend operator against the field accumulated so far.</param>
    /// <param name="smooth">The smooth-blend radius.</param>
    /// <returns>This builder.</returns>
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, Vector3 worldOrigin, Quaternion worldOrientation, int screenIndex, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        var unit = Quaternion.Normalize(value: worldOrientation);

        return ScreenSlab(
            blend: blend,
            halfExtents: halfExtents,
            round: round,
            screenIndex: screenIndex,
            smooth: smooth,
            worldOrigin: worldOrigin,
            worldRight: Vector3.Transform(Vector3.UnitX, unit),
            worldUp: Vector3.Transform(Vector3.UnitY, unit)
        );
    }
    public SdfProgram Build() {
        if (m_openInstanceFirst >= 0) {
            throw new InvalidOperationException(message: "Build was called with an instance still open (unbalanced Begin/EndInstance).");
        }

        return new SdfProgram(
            instructions: m_instructions,
            instances: m_instances,
            materials: m_materials,
            screenSurfaces: m_screenSurfaces
        );
    }

    private SdfProgramBuilder Transform(SdfOp op, Vector4 data0 = default, Vector4 data1 = default) {
        m_instructions.Add(item: new SdfInstruction(
            Blend: 0,
            Data0: data0,
            Data1: data1,
            Material: 0,
            Op: op,
            Shape: 0
        ));

        return this;
    }
    // Data1.x is the ISA-wide smooth-blend radius; .yzw carry per-shape HOST-BAKED derived constants (the shader's
    // decode is per shape case — KEEP IN SYNC with sdf-vm.hlsli evaluateShape).
    private SdfProgramBuilder Shape(SdfShapeType shape, Vector4 dimensions, int material, SdfBlendOp blend, float smooth, float derived1 = 0f, float derived2 = 0f, float derived3 = 0f) {
        m_instructions.Add(item: new SdfInstruction(
            Blend: (uint)blend,
            Data0: dimensions,
            Data1: new Vector4(
                w: derived3,
                x: smooth,
                y: derived1,
                z: derived2
            ),
            Material: (uint)material,
            Op: SdfOp.ShapeBlend,
            Shape: (uint)shape
        ));

        return this;
    }
}
