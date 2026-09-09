using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Builds an SDF program as an ordered stream of point transforms, field operations, shapes, and materials.</summary>
public sealed partial class SdfProgramBuilder {
    /// <summary>The largest <see cref="RepeatPolar"/> sector count this builder accepts: 2^24, the largest integer a
    /// 32-bit float represents exactly. <c>RepeatPolar</c> bakes the sector count into the packed program as a float
    /// (Data0.z) and the shader's per-sector material recolor re-derives the sector index from it via float wrap
    /// arithmetic (<c>sector - count*floor(sector/count)</c>); past this bound the int-to-float conversion of the
    /// count itself is inexact, so the shader's wrapped max can diverge from this builder's exact-integer
    /// <c>sectors - 1</c> claim — the Build()-time recolor-window refusal would then be judging a maximum the shader
    /// does not actually observe. Refusing the count outright keeps that claim honest without forking the shader's
    /// float arithmetic onto the host.</summary>
    public const int MaxExactFloatSectorCount = (1 << 24);
    /// <summary>The deepest a <see cref="PushField"/>/<see cref="PopField"/> scope may nest. Depth 1 covers every case
    /// that exists today — creator groups cannot nest and a chamfer wedge is depth 1 — enforced by a validator rule and
    /// not part of the packed word layout, so raising it never re-gates the stream. But it is not a one-line bump: the
    /// interpreter holds the parent accumulator in one non-indexed <c>(savedFieldDistance, savedFieldMaterial)</c> scalar
    /// pair in <c>mapCore</c> (Assets/Shaders/Sdf/sdf-vm.hlsli), and the <c>SDF_MAX_FIELD_SCOPE_DEPTH</c> there is
    /// documentation only — no shader expression reads it. Raising this depth means converting that save pair into an
    /// indexed array and giving push/pop real push/pop-by-depth stack semantics in the shader first, then bumping the
    /// <c>#define</c> and this constant. KEEP IN SYNC with SDF_MAX_FIELD_SCOPE_DEPTH in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    public const int MaxFieldScopeDepth = 1;
    /// <summary>The most octaves one <see cref="NoiseDisplace"/> may declare. The interpreter loops the count at
    /// runtime (Blend lane), so this bounds the per-sample hash cost (8 corner hashes per octave) and the
    /// <c>lacunarity^octaves</c> term inside the Lipschitz step clamp.</summary>
    public const int MaxNoiseOctaves = 8;

    // The largest |dot(unitRight, unitUp)| RequireOrthogonalBasis accepts: a cosine, so it reads as ~0.057 degrees.
    private const float BasisSkewTolerance = 1.0e-3f;

    /// <summary>The instance ceiling — the most instances one program may declare. The world renderer's per-tile
    /// mask is a derived ceil(instanceCount/32) uints (<see cref="SdfProgram.InstanceMaskWordCount"/>), so this caps
    /// it at 512 words per tile (16384/32). Everything downstream derives from the live program's instance count — the
    /// mask width, the host-pushed indexing, and the mask-buffer sizing all use
    /// <see cref="SdfProgram.InstanceMaskWordCountFor"/> — so a program declaring fewer instances than this cap packs
    /// byte-identically regardless of the cap's value; only the shader's <c>min(count, SDF_MAX_INSTANCES)</c> clamp
    /// constant tracks it. The ceiling's static cost is the per-tile mask buffer. KEEP IN SYNC with SDF_MAX_INSTANCES in
    /// Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    public const int MaxInstances = 16384;
    /// <summary>The maximum voxel count per brick axis: the <see cref="SampledRegion"/> shape packs each dim in 10 bits
    /// (see <see cref="SdfShapeType.SampledRegion"/>'s Data1.y layout), so 1023 is the hard ceiling. KEEP IN SYNC with the
    /// 0x3FFu unpack mask in sdfSampledRegion (Assets/Shaders/Sdf/sdf-vm.hlsli).</summary>
    public const int MaxSampledRegionDim = 1023;
    /// <summary>The shortest slant vector <c>(topHalfWidth − bottomHalfWidth, 2·halfHeight)</c> a
    /// <see cref="Trapezoid"/> profile may carry: shorter than this the deterministic fixed-point evaluator divides by
    /// its own squared length and the shader returns NaN, so the shape is refused rather than evaluated.</summary>
    /// <remarks>Sized to the Q48.16 representation. <c>FixedVector2.Dot</c> accumulates both products exactly and
    /// rounds once to nearest, so the squared slant reads zero exactly when
    /// <c>round(2^16·Δr)² + round(2^17·halfHeight)² &lt;= 2^15</c>. Each raw component can sit up to half a quantum
    /// above the real value it came from, so the widest real slant inside that set is
    /// <c>sqrt(2^15 + 2·181 + 0.5)/2^16 ≈ 0.002778</c>; this bound clears it. A slant this short spans well under one
    /// fixed-point quantum of profile, so nothing authorable is lost — a trapezoid with equal half-widths and real
    /// height is a rectangle, whose slant is <c>2·halfHeight</c> and never degenerate.</remarks>
    public const float MinTrapezoidProfileSlant = 0.003f;
    /// <summary>The most screen surfaces one program may declare (matches <c>Puck.SdfVm.SdfWorldEngine.MaxScreenSurfaces</c>
    /// — the kernels' <c>screenSurfaces[]</c>/<c>screenSources[]</c> array length; a contract separate from the
    /// viewport capacity <c>Puck.SdfVm.SdfWorldEngine.MaxViewports</c>). Capped at 32 by the single-<c>uint</c>
    /// <c>screenMask</c> the engine pushes per frame.</summary>
    public const int MaxScreenSurfaces = 32;
    // KEEP IN SYNC with SDF_SCREEN_MATERIAL in Assets/Shaders/Sdf/sdf-vm.hlsli.
    /// <summary>The reserved material identifier used by the plain procedural screen material.</summary>
    public const int ScreenMaterialId = 65535;

    private readonly List<SdfInstanceRange> m_instances;
    private readonly List<SdfInstruction> m_instructions;
    // Every positional recolor WINDOW the program has emitted: the recoloring op, the base material of the shape it
    // recolors, the largest delta that op can add to it, and the material SCOPE the shape was emitted in (null when
    // none was open). Recorded by Shape() — the palette is not final and the recolored shape does not exist when the
    // fold is declared, so the window cannot be judged before Build(), which is where the refusal below reads this list
    // against the span the window is allowed to reach. The scope is recorded by IDENTITY, not by extent: an open scope
    // can still grow, so only its close knows where it ends (SdfMaterialScope.MaterialEnd), and every scope has closed
    // by Build(). The delta and the window top are 64-bit because the reach is a PRODUCT of two caller-supplied ints: a
    // colossal stride overflows a 32-bit multiply into a negative reach, which would read as an in-range window and let
    // the very program this gate exists to refuse through. (The shader's own 32-bit product overflows too — that is a
    // reason to refuse the program, not to model it.)
    private readonly List<(SdfOp Op, int Material, long MaxDelta, SdfMaterialScope? Scope)> m_materialRecolorWindows = [];
    // The open material-scope stack (see SdfMaterialScope) — a list, not a single slot, so scopes can nest; every
    // positional-stride clamp (ApplyPositionalMaterialScopeClamp) resolves against ONLY the innermost (last) entry.
    // Empty for a scope-free program, so the clamp path below never runs and emission stays byte-identical to before
    // this mechanism existed.
    private readonly List<SdfMaterialScope> m_materialScopes = [];
    private readonly List<SdfMaterial> m_materials;
    private readonly List<SdfScreenSurface> m_screenSurfaces;

    /// <summary>Creates a builder, optionally pre-sizing its instruction/instance/material/screen-surface lists so a
    /// repeat-construction caller (a live composition rebuild) that already knows roughly how big the next program
    /// will be — from a previous build's own counts, e.g. <see cref="SdfProgram.InstructionCount"/>/
    /// <see cref="SdfProgram.Instances"/>.Count/<see cref="SdfProgram.MaterialCount"/>/<see cref="SdfProgram.ScreenSurfaces"/>.Count
    /// — avoids re-paying <see cref="List{T}"/>'s geometric-growth reallocation from empty on every rebuild. Every hint
    /// defaults to 0, matching <see cref="List{T}"/>'s own parameterless-constructor capacity, so a caller that
    /// supplies none behaves exactly as before this constructor existed.</summary>
    /// <param name="instructionCapacity">Initial capacity for the instruction list, or 0 to start empty.</param>
    /// <param name="instanceCapacity">Initial capacity for the instance list, or 0 to start empty.</param>
    /// <param name="materialCapacity">Initial capacity for the material list, or 0 to start empty.</param>
    /// <param name="screenSurfaceCapacity">Initial capacity for the screen-surface list, or 0 to start empty.</param>
    public SdfProgramBuilder(int instructionCapacity = 0, int instanceCapacity = 0, int materialCapacity = 0, int screenSurfaceCapacity = 0) {
        m_instances = new List<SdfInstanceRange>(capacity: Math.Max(
            val1: 0,
            val2: instanceCapacity
        ));
        m_instructions = new List<SdfInstruction>(capacity: Math.Max(
            val1: 0,
            val2: instructionCapacity
        ));
        m_materials = new List<SdfMaterial>(capacity: Math.Max(
            val1: 0,
            val2: materialCapacity
        ));
        m_screenSurfaces = new List<SdfScreenSurface>(capacity: Math.Max(
            val1: 0,
            val2: screenSurfaceCapacity
        ));
    }

    // The one open field scope (a PushField without its PopField yet), or null when none is open: carries the compose
    // blend + smooth radius PopField bakes onto its instruction, and the ShapeBlend count when it opened (so a
    // shape-less scope is rejected at close). Null for a scope-free program, so its packed words stay byte-identical.
    // A single nullable slot, not a list/array — MaxFieldScopeDepth (the depth cap) is 1, so there is never more than
    // one open scope; every call site below is an is-open/the-open-scope check, never an index. Raising the depth
    // cap needs converting this to an indexed structure (see MaxFieldScopeDepth's doc) — the depth guard below keeps
    // reading MaxFieldScopeDepth rather than hardcoding 1, so that conversion stays localized to this field + guard.
    private (SdfBlendOp Blend, float Smooth, int ShapeCountAtOpen)? m_fieldScope;
    // The SECOND mirror of the shader's parityMaterialDelta slot, and the one the Build()-time refusal below reads.
    // It exists beside m_positionalFold because the two answer different questions: m_positionalFold feeds the
    // material-scope CLAMP, whose repair vocabulary is a fold's per-unit stride, so it deliberately tracks only the two
    // strided folds; this slot tracks EVERY instruction that writes parityMaterialDelta — WallpaperFold, RepeatPolar,
    // AND CellJitter, whose hashed variant is not a stride and has no clamped form. Carries the writing instruction's
    // index and the largest delta ONE unit of its raw Material lane can produce (see MaxRecolorDelta, which reads the
    // raw lane back out of m_instructions so it sees any value the clamp already narrowed). Cleared by ResetPoint on
    // both sides (SDF_OP_RESET zeroes parityMaterialDelta).
    private (int InstructionIndex, int ReachPerUnit, SdfOp Op)? m_materialRecolor;
    private bool m_openInstanceActive;
    private Vector3 m_openInstanceCenter;
    private int m_openInstanceFirst = -1;
    private bool m_openInstanceIsDynamic;
    private float m_openInstanceRadius;
    private int m_openInstanceSlot;
    // Chain-local HOST MIRROR of the shader's parityMaterialDelta slot (Assets/Shaders/Sdf/sdf-vm.hlsli): which
    // recently emitted instruction (WallpaperFold or RepeatPolar), if any, is driving a positional material recolor
    // for the shape(s) that follow in the CURRENT ResetPoint..ResetPoint chain segment — its index in m_instructions,
    // the raw stride value packed into that instruction's Material lane, and the largest additional material offset
    // ONE unit of that raw stride can produce (2 for a hex wallpaper group's 3-coloring, 1 for every other wallpaper
    // group, sectorCount-1 for RepeatPolar). SDF_OP_RESET clears parityMaterialDelta on the GPU, so ResetPoint()
    // clears this mirror the same way; a zero-stride fold leaves it untouched on BOTH sides (the shader's own
    // `!= 0u` guard — see WallpaperFold/RepeatPolar below). Consumed (and, inside an open material scope, clamped) by
    // Shape() before a positional shape's material lands in the packed program — the clamp early-returns whenever
    // m_materialScopes is empty, so a scope-free program never mutates an already-emitted instruction.
    private (int InstructionIndex, int ReachPerUnit, int RawValue)? m_positionalFold;
    private int m_shapeCount;

    /// <summary>Closes <paramref name="scope"/> (called by <see cref="SdfMaterialScope.Dispose"/> — not meant to be
    /// called directly).</summary>
    /// <param name="scope">The scope to close.</param>
    /// <exception cref="InvalidOperationException"><paramref name="scope"/> is not the innermost open scope.</exception>
    internal void EndMaterialScope(SdfMaterialScope scope) {
        if (
            (m_materialScopes.Count == 0) ||
            !ReferenceEquals(
            objA: m_materialScopes[^1],
            objB: scope
        )
        ) {
            throw new InvalidOperationException(message: "Material scopes must close in LIFO order — dispose the innermost open scope before an outer one (or this scope was already closed).");
        }

        // Seal the scope's span for the Build()-time recolor gate: a window recorded inside this scope could not know
        // where the scope would end (more AddMaterial calls could still land), so the end is stamped here, at the one
        // moment it becomes final.
        scope.MaterialEnd = m_materials.Count;

        m_materialScopes.RemoveAt(index: (m_materialScopes.Count - 1));
    }

    // The material-scope safety net (see SdfMaterialScope): mirrors the shader's parityMaterialDelta reach against the
    // innermost open scope and, if the active positional fold (m_positionalFold) could recolor `material` past the
    // scope's own added-material span, CLAMPS the fold instruction's raw stride down — retroactively (the fold
    // instruction already sits in m_instructions, so this rewrites it in place) — to the largest value that keeps
    // every reachable material inside the scope. A no-op whenever no scope is open or no positional fold is active in
    // the current chain, so scope-free emission never executes past the first condition.
    //
    // SOUNDNESS: this checks against m_materials.Count AT THIS SHAPE'S OWN EMISSION, which by the established
    // authoring convention (every emitter registers all of a scope's materials BEFORE emitting the fold + the shapes
    // it recolors — see SdfDriftMonolith.Emit) already equals the scope's final material count. Even when a caller
    // does not follow that convention, checking against the CURRENT count is still SAFE (never unsound): the true
    // final scope span can only be >= what exists right now, so a clamp computed against the interim count can only
    // be MORE conservative than strictly necessary, never less.
    private void ApplyPositionalMaterialScopeClamp(int material) {
        if (
            (m_materialScopes.Count == 0) ||
            (m_positionalFold is not { } fold) ||
            (fold.RawValue == 0)
        ) {
            return;
        }

        var scope = m_materialScopes[^1];

        // A shape whose OWN declared material doesn't belong to the open scope at all is a caller bug no clamp can
        // fix (the fold's reach is meaningless relative to a material the scope never added) — fail loud rather than
        // silently mis-clamping.
        if (
            (material < scope.MaterialBase) ||
            (material >= m_materials.Count)
        ) {
            throw new InvalidOperationException(message: $"A positionally-recolored shape's material (index {material}) does not belong to the open material scope (base {scope.MaterialBase}, {(m_materials.Count - scope.MaterialBase)} material(s) added so far). Add every material a scope's positional fold recolors through AddMaterial before emitting the fold and the shapes that use it.");
        }

        var allowedReach = ((m_materials.Count - 1) - material);
        var maxReach = (fold.ReachPerUnit * fold.RawValue);

        if (maxReach <= allowedReach) {
            return;
        }

        var clampedRaw = Math.Max(
            val1: 0,
            val2: (allowedReach / fold.ReachPerUnit)
        );
        var instruction = m_instructions[fold.InstructionIndex];

        m_instructions[fold.InstructionIndex] = (instruction with { Material = ((uint)clampedRaw) });
        // The clamp only ever narrows the reach going forward — a later shape under the SAME fold (in the same chain
        // segment) re-checks against this smaller value, so repeated shapes never re-widen a clamp a scope required.
        m_positionalFold = (fold.InstructionIndex, fold.ReachPerUnit, clampedRaw);
    }
    private void BeginInstanceCore(bool isDynamic, Vector3 center, float radius, int slot, bool active = true) {
        if (
            isDynamic &&
            ((slot < 0) || (slot > SdfProgram.MaxDynamicTransformSlot))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(slot),
                message: $"Dynamic instance slots must be in [0, {SdfProgram.MaxDynamicTransformSlot}]."
            );
        }

        if (m_openInstanceFirst >= 0) {
            throw new InvalidOperationException(message: "BeginInstance/BeginInstanceDynamic was called with an instance already open (nesting is not supported).");
        }

        if (m_fieldScope is not null) {
            throw new InvalidOperationException(message: "BeginInstance/BeginInstanceDynamic was called with a field scope open (PushField without its PopField). A scope must sit entirely inside one instance or entirely in the world set, never crossing an instance boundary.");
        }

        m_openInstanceFirst = m_instructions.Count;
        m_openInstanceIsDynamic = isDynamic;
        m_openInstanceActive = active;
        m_openInstanceCenter = center;
        m_openInstanceRadius = radius;
        m_openInstanceSlot = slot;
    }
    // THE ARGUMENT FLOOR. Every float a caller supplies reaches the GPU as a packed word bit-for-bit (SdfProgram's
    // WriteVector4 reinterprets, it never arithmetically normalizes), so a NaN or infinity is not absorbed anywhere
    // downstream: it survives into the instruction stream, poisons the program-wide Lipschitz step scale
    // (SdfProgram.AnalyzeLipschitz folds every shape dimension and warp rate into ONE scalar), and poisons the cull
    // bounds derived from the same lanes. The host-baked clamps (Vector3.Max, MathF.Max, Math.Clamp) absorb SIGN, never
    // NaN — MathF.Max(NaN, x) is NaN — which is why finiteness is checked here rather than left to them.
    private static Vector3 ClampSpacing(Vector3 spacing) =>
        Vector3.Max(
            value1: spacing,
            value2: new Vector3(value: 0.001f)
        );
    /// <summary>Owns finite-value validation and instruction encoding for signed scalar transforms.</summary>
    private SdfProgramBuilder FiniteScalarTransform(SdfOp op, float value, string paramName, string subject) {
        RequireFinite(
            paramName: paramName,
            subject: subject,
            value: value
        );

        return ScalarTransform(
            op: op,
            value: value
        );
    }
    // The largest value the shader's parityMaterialDelta can hold for `recolor`, read from the raw Material lane of the
    // recoloring instruction itself so a lane the scope clamp already narrowed is seen at its narrowed value. KEEP IN
    // SYNC with the three parityMaterialDelta writers in Assets/Shaders/Sdf/sdf-vm.hlsli: WallpaperFold multiplies its
    // stride by a cell key in 0..2 (a hex group's 3-coloring) or 0..1 (every other group's parity), RepeatPolar by a
    // sector index in 0..count-1, and CellJitter takes a hashed row in 0..variants-1 — a COUNT, not a stride, which is
    // the whole reason it subtracts one where the folds multiply.
    private long MaxRecolorDelta((int InstructionIndex, int ReachPerUnit, SdfOp Op) recolor) {
        var raw = ((long)((int)m_instructions[recolor.InstructionIndex].Material));

        if (raw == 0L) {
            return 0L;
        }

        return ((recolor.Op == SdfOp.CellJitter)
            ? (raw - 1L)
            : (recolor.ReachPerUnit * raw)
        );
    }
    // Host-side unorm2x16 pack of an atlas UV: 16-bit u in the low half, v in the high half, matching sdfGlyphUnpackUv
    // (KEEP IN SYNC). An integer pack reinterpreted as float bits, so it is bit-identical across both DXC backends.
    private static float PackUv(Vector2 uv) {
        var u = ((uint)Math.Clamp(
            value: ((int)MathF.Round(x: (MathF.Max(
                x: 0f,
                y: uv.X
            ) * 65535f))),
            min: 0,
            max: 65535
        ));
        var v = ((uint)Math.Clamp(
            value: ((int)MathF.Round(x: (MathF.Max(
                x: 0f,
                y: uv.Y
            ) * 65535f))),
            min: 0,
            max: 65535
        ));

        return BitConverter.UInt32BitsToSingle(value: u | (v << 16));
    }
    // Pairs the active positional recolor with the shape it recolors, producing the (base material, max delta, scope)
    // WINDOW the Build()-time gate judges once the palette is final. The scope recorded is the INNERMOST one open at
    // this shape's emission — the same one ApplyPositionalMaterialScopeClamp resolves against — because that is the
    // palette span this shape's contributor owns; null when the builder has no scope open at all. A shape carrying a
    // screen sentinel records nothing: the
    // shader applies the delta only under `material < SDF_SCREEN_MATERIAL`, so a screen face is never recolored (KEEP IN
    // SYNC with the SDF_OP_SHAPE parityMaterialDelta apply in Assets/Shaders/Sdf/sdf-vm.hlsli). A zero delta records
    // nothing either — the shape reaches only its own declared material, which this gate does not own.
    private void RecordPositionalMaterialWindow(int material) {
        if (
            (m_materialRecolor is not { } recolor) ||
            (material >= ScreenMaterialId)
        ) {
            return;
        }

        var maxDelta = MaxRecolorDelta(recolor: recolor);

        if (maxDelta <= 0L) {
            return;
        }

        m_materialRecolorWindows.Add(item: (recolor.Op, material, maxDelta, ((m_materialScopes.Count == 0)
            ? null
            : m_materialScopes[^1])));
    }
    // THE ENUM FLOOR. Every enum lane below is cast RAW to its packed uint (Blend/Shape/Material never validate it),
    // so an out-of-range value some other caller-supplied int was cast from reaches the GPU as whatever THAT bit
    // pattern decodes to in the shader's switch — a silently DIFFERENT op/shape/axis than authored, not a caught
    // mistake. Each enum here is a contiguous uint range starting at 0 (KEEP IN SYNC with its own file), so a single
    // upper-bound compare is the whole defined-set test — cheaper than Enum.IsDefined's reflection lookup and exact
    // for a contiguous enum.
    private static void RequireDefined(SdfBlendOp value, string paramName) =>
        RequirePackedEnumValue(
            value: ((uint)value),
            maximum: ((uint)SdfBlendOp.ChamferSubtraction),
            actualValue: value,
            enumName: nameof(SdfBlendOp),
            paramName: paramName
        );
    private static void RequireDefined(SdfPolarAxis value, string paramName) =>
        RequirePackedEnumValue(
            value: ((uint)value),
            maximum: ((uint)SdfPolarAxis.Z),
            actualValue: value,
            enumName: nameof(SdfPolarAxis),
            paramName: paramName
        );
    private static void RequireDefined(SdfNoiseFlavor value, string paramName) =>
        RequirePackedEnumValue(
            value: ((uint)value),
            maximum: ((uint)SdfNoiseFlavor.Gaussian),
            actualValue: value,
            enumName: nameof(SdfNoiseFlavor),
            paramName: paramName
        );
    private static void RequireDefined(SdfWallpaperGroup value, string paramName) =>
        RequirePackedEnumValue(
            value: ((uint)value),
            maximum: ((uint)SdfWallpaperGroup.P6M),
            actualValue: value,
            enumName: nameof(SdfWallpaperGroup),
            paramName: paramName
        );
    private static void RequireDefined(SdfWallpaperPlane value, string paramName) =>
        RequirePackedEnumValue(
            value: ((uint)value),
            maximum: ((uint)SdfWallpaperPlane.YZ),
            actualValue: value,
            enumName: nameof(SdfWallpaperPlane),
            paramName: paramName
        );
    private static void RequireDefined(SdfLift value, string paramName) =>
        RequirePackedEnumValue(
            value: ((uint)value),
            maximum: ((uint)SdfLift.Extrude),
            actualValue: value,
            enumName: nameof(SdfLift),
            paramName: paramName
        );
    // A direction the builder NORMALIZES host-side: a zero-length (or underflowing) vector divides by zero and packs
    // NaN into the lane the shader trusts to be a unit vector, so the normalized result is what has to be finite.
    private static void RequireDirection(Vector3 value, string paramName, string subject) {
        RequireFinite(
            paramName: paramName,
            subject: subject,
            value: value
        );

        var unit = Vector3.Normalize(value: value);

        if (
            !float.IsFinite(f: unit.X) ||
            !float.IsFinite(f: unit.Y) ||
            !float.IsFinite(f: unit.Z)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must have a non-zero length (it is normalized host-side).",
                paramName: paramName
            );
        }
    }
    private static void RequireFinite(float value, string paramName, string subject) {
        if (!float.IsFinite(f: value)) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must be finite.",
                paramName: paramName
            );
        }
    }
    private static void RequireFinite(Vector2 value, string paramName, string subject) {
        if (
            !float.IsFinite(f: value.X) ||
            !float.IsFinite(f: value.Y)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must be finite on every component.",
                paramName: paramName
            );
        }
    }
    private static void RequireFinite(Vector3 value, string paramName, string subject) {
        if (
            !float.IsFinite(f: value.X) ||
            !float.IsFinite(f: value.Y) ||
            !float.IsFinite(f: value.Z)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must be finite on every component.",
                paramName: paramName
            );
        }
    }
    // Shared by Box and both ScreenSlab overloads (a screen slab IS a rounded box): SdfProgram.TryGetLocalBound's
    // Box/ScreenSlab cull bound is halfExtents.Length() + |round| — Length() is a dot-product-shaped sum of squares,
    // so one huge half-extent component can overflow it past float.MaxValue even though every component individually
    // stays finite (the same overflow class Capsule's endpoint dot and Cylinder's hypotenuse have). Checked here
    // rather than left to the analysis pass that discovers it far from the shape that authored it.
    private static void RequireFiniteBoxBound(Vector3 halfExtents, float round, string shapeName) {
        if (!float.IsFinite(f: (halfExtents.Length() + MathF.Abs(x: round)))) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(halfExtents),
                message: $"A {shapeName}'s derived bound radius (from half-extents {halfExtents} and round {round}) is not finite. Narrow the half-extents."
            );
        }
    }
    // Shared by the whole 2D-primitive-lift family (RoundedRectangle/RegularPolygon/Star/Trapezoid/Ellipse):
    // SdfProgram.LiftedBoundRadius derives each one's cull bound from its own 2D reach (radius2D) and its lift amount
    // — sqrt(radius2D² + liftAmount²) for Extrude, radius2D + liftAmount for Revolve — and either form can overflow
    // past float.MaxValue from two individually-finite inputs, exactly like Torus's radii sum. KEEP IN SYNC with
    // LiftedBoundRadius's own formula. Checked at the shape that owns radius2D/liftAmount rather than left to the
    // analysis pass that discovers it far from the offending call.
    private static void RequireFiniteLiftedReach(float radius2D, float liftAmount, SdfLift lift, string shapeName) {
        var reach = ((lift == SdfLift.Extrude)
            ? MathF.Sqrt(x: ((radius2D * radius2D) + (liftAmount * liftAmount)))
            : (liftAmount + radius2D)
        );

        if (!float.IsFinite(f: reach)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(liftAmount),
                message: $"A {shapeName}'s derived lifted bound radius (2D reach {radius2D}, lift {liftAmount}) is not finite. Narrow the shape's dimensions or the lift amount."
            );
        }
    }
    // The four public instance openers share this bound check but name their centre argument differently (boundCenter
    // for a static instance, boundOffset for a dynamic one), so the caller's own parameter names are passed through
    // rather than reported as this helper's or BeginInstanceCore's. The bound is a world-space sphere the beam prepass
    // tests per tile (SdfProgram.WriteBound scales the radius and packs it): a negative radius describes a sphere that
    // covers nothing, so the instance would silently never be marched.
    private static void RequireInstanceBound(Vector3 center, string centerParamName, float radius, string radiusParamName) {
        RequireFinite(
            paramName: centerParamName,
            subject: "An instance bound centre",
            value: center
        );
        RequireNonNegative(
            paramName: radiusParamName,
            subject: "An instance bound radius",
            value: radius
        );
    }
    private static void RequireNonNegative(float value, string paramName, string subject) {
        if (
            !float.IsFinite(f: value) ||
            (value < 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must be finite and non-negative.",
                paramName: paramName
            );
        }
    }
    private static void RequireNonNegative(Vector2 value, string paramName, string subject) {
        if (
            !float.IsFinite(f: value.X) ||
            !float.IsFinite(f: value.Y) ||
            (value.X < 0f) ||
            (value.Y < 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must be finite and non-negative on every component.",
                paramName: paramName
            );
        }
    }
    private static void RequireNonNegative(Vector3 value, string paramName, string subject) {
        if (
            !float.IsFinite(f: value.X) ||
            !float.IsFinite(f: value.Y) ||
            !float.IsFinite(f: value.Z) ||
            (value.X < 0f) ||
            (value.Y < 0f) ||
            (value.Z < 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must be finite and non-negative on every component.",
                paramName: paramName
            );
        }
    }
    private bool DeclaresScreenIndex(int screenIndex) {
        foreach (var surface in m_screenSurfaces) {
            if (surface.ScreenIndex == screenIndex) {
                return true;
            }
        }

        return false;
    }
    private string DescribeScreenIndices() => ((m_screenSurfaces.Count == 0)
        ? "none"
        : string.Join(
            separator: ", ",
            values: m_screenSurfaces.Select(selector: static surface => surface.ScreenIndex)
        )
    );
    // The one basis door, shared by Text and the screen-surface ScreenSlab overload. Both pack the raw axes (a text
    // run's layout offsets; a screen surface's UV projection) beside an orthonormal quaternion built from the same
    // pair (each glyph's geometry; the slab's placement), so the pair must be orthogonal or the two halves describe
    // different solids. Spanning a plane is not sufficient: the skew is unbounded up to parallel.
    // The tolerance is a cosine and reads as an angle: 1e-3 admits ~0.057 degrees, above the rounding a
    // quaternion-derived pair (Vector3.Transform of UnitX/UnitY) or a fixed-point-derived world frame carries.
    private static void RequireOrthogonalBasis(Vector3 right, Vector3 up, string paramName, string subject) {
        var unitRight = Vector3.Normalize(value: right);
        var unitUp = Vector3.Normalize(value: up);
        var skew = Vector3.Dot(
            vector1: unitRight,
            vector2: unitUp
        );

        // NaN fails this comparison too (every comparison against NaN is false, so `!(|skew| <= tolerance)` is true),
        // which is the wanted answer: RequireDirection has already refused a non-finite axis, so a NaN here can only
        // come from an underflowing normalize the caller slipped past.
        if (!(MathF.Abs(x: skew) <= BasisSkewTolerance)) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must be orthogonal: the unit axes' dot product is {skew}, and at most {BasisSkewTolerance} is accepted. A skewed pair packs a frame whose UV/layout follows the authored axes while the geometry rides the orthonormal rotation derived from them, so the two describe different solids.",
                paramName: paramName
            );
        }
    }
    /// <summary>Owns the defined-range check for every contiguous enum packed into the SDF instruction stream.</summary>
    private static void RequirePackedEnumValue(uint value, uint maximum, object actualValue, string enumName, string paramName) {
        if (value > maximum) {
            throw new ArgumentOutOfRangeException(
                actualValue: actualValue,
                message: $"{actualValue} is not a defined {enumName}.",
                paramName: paramName
            );
        }
    }
    private static void RequirePositive(float value, string paramName, string subject) {
        if (
            !float.IsFinite(f: value) ||
            (value <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must be finite and greater than zero.",
                paramName: paramName
            );
        }
    }
    // The quaternion twin of RequireDirection: Rotate/ScreenSlab normalize before packing, and a zero quaternion
    // normalizes to NaN, which the shader's inverse-rotate would carry into every coordinate.
    private static void RequireRotation(Quaternion value, string paramName, string subject) {
        if (
            !float.IsFinite(f: value.W) ||
            !float.IsFinite(f: value.X) ||
            !float.IsFinite(f: value.Y) ||
            !float.IsFinite(f: value.Z)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must be finite on every component.",
                paramName: paramName
            );
        }

        var unit = Quaternion.Normalize(value: value);

        if (
            !float.IsFinite(f: unit.W) ||
            !float.IsFinite(f: unit.X) ||
            !float.IsFinite(f: unit.Y) ||
            !float.IsFinite(f: unit.Z)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"{subject} must have a non-zero length (it is normalized host-side).",
                paramName: paramName
            );
        }
    }
    /// <summary>Owns the instruction encoding shared by scalar transforms whose value occupies <c>Data0.x</c>.</summary>
    private SdfProgramBuilder ScalarTransform(SdfOp op, float value) =>
        Transform(
            data0: new Vector4(
                w: 0f,
                x: value,
                y: 0f,
                z: 0f
            ),
            op: op
        );
    private void ScopedInstance(bool isDynamic, Vector3 center, float radius, int slot, Action<SdfProgramBuilder> emit) {
        ArgumentNullException.ThrowIfNull(emit);

        var instanceCount = m_instances.Count;

        BeginInstanceCore(
            isDynamic: isDynamic,
            center: center,
            radius: radius,
            slot: slot
        );
        emit(this);

        // EndInstance always appends exactly one range, so a changed count directly detects an emitter that closed
        // the instance itself — including an End-then-Begin pair that would restore the open-index sentinel.
        if (m_instances.Count != instanceCount) {
            throw new InvalidOperationException(message: "A scoped instance emitter must leave its instance open; do not call BeginInstance/EndInstance inside the emitter.");
        }

        EndInstance();
    }
    // Data1.x is the ISA-wide smooth-blend radius; .yzw carry per-shape HOST-BAKED derived constants (the shader's
    // decode is per shape case — KEEP IN SYNC with sdf-vm.hlsli evaluateShape).
    private SdfProgramBuilder Shape(SdfShapeType shape, Vector4 dimensions, int material, SdfBlendOp blend, float smooth, float derived1 = 0f, float derived2 = 0f, float derived3 = 0f) {
        // The two arguments EVERY public shape method shares, checked once here rather than at twenty call sites.
        // material is cast to uint on the way into the packed lane, so a negative id would arrive as a huge positive
        // one and index past the palette. The UPPER bound is not checked here and cannot be: the palette is still
        // growing (a shape may name a material its own emitter registers a call later), so it is judged once at Build,
        // against the final count and skipping the screen sentinels — see the palette gate there.
        // smooth's SIGN is absorbed by the shader (blendShape clamps it with max(), and PopField already bakes the same
        // clamp), so only finiteness is refused here.
        if (material < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(material),
                message: "A shape material identifier must be non-negative."
            );
        }

        // Checked here too (the enum floor): every public shape method funnels its blend through this one call, so
        // one check here covers all of them, rather than twenty individually-fallible call sites.
        RequireDefined(
            value: blend,
            paramName: nameof(blend)
        );
        RequireFinite(
            value: smooth,
            paramName: nameof(smooth),
            subject: "A shape blend smooth radius"
        );

        // Counts ShapeBlend emissions so PopField can reject a shape-less scope. A scope-free program never reads it.
        m_shapeCount++;

        ApplyPositionalMaterialScopeClamp(material: material);
        // AFTER the clamp, never before: the clamp may have just narrowed the active fold's raw stride, and the window
        // recorded here must describe the reach the packed program actually carries, or a repaired program would be
        // refused for a reach it no longer has.
        RecordPositionalMaterialWindow(material: material);

        m_instructions.Add(item: new SdfInstruction(
            Blend: ((uint)blend),
            Data0: dimensions,
            Data1: new Vector4(
                w: derived3,
                x: smooth,
                y: derived1,
                z: derived2
            ),
            Material: ((uint)material),
            Op: SdfOp.ShapeBlend,
            Shape: ((uint)shape)
        ));

        return this;
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

    /// <summary>Compiles the authored instructions/instances/materials/screens into a packed <see cref="SdfProgram"/>.</summary>
    /// <param name="buildInstanceGrid">Whether to pack the world-space uniform-grid instance cull (default
    /// <see langword="true"/>). Pass <see langword="false"/> to force the beam's flat per-instance fallback over the same
    /// instances — the reference the grid-cull gate compares against; see <see cref="SdfProgram"/>.</param>
    /// <param name="gridWorkspace">Forwarded to <see cref="SdfProgram"/>'s constructor — see its
    /// <c>gridWorkspace</c> parameter. <see langword="null"/> (the default) keeps the allocating grid-build path.</param>
    public SdfProgram Build(bool buildInstanceGrid = true, SdfInstanceGrid.Workspace? gridWorkspace = null) {
        if (m_openInstanceFirst >= 0) {
            throw new InvalidOperationException(message: "Build was called with an instance still open (unbalanced Begin/EndInstance).");
        }

        if (m_fieldScope is not null) {
            throw new InvalidOperationException(message: "Build was called with a field scope still open (PushField without its PopField).");
        }

        if (m_materialScopes.Count > 0) {
            throw new InvalidOperationException(message: "Build was called with a material scope still open (BeginMaterialScope without disposing the returned scope).");
        }

        // THE PALETTE GATE. A shape's material id indexes the packed palette on the GPU through sdfMaterialLoad, and
        // the shader CLAMPS an out-of-range id to row 0 rather than faulting — so an id past the palette is not a
        // crash, it is a shape silently wearing the wrong material, which is exactly the failure an author will not
        // see. It can only be judged HERE, at Build: a shape may legitimately name a material its emitter registers
        // later in the same scope, so the palette is not final until now.
        //
        // The SCREEN SENTINELS are the one family that legitimately sits above the palette (ScreenMaterialId flags
        // screen shading and ScreenMaterialId + 1 + screenIndex names the surface — see ScreenSlab), and they are
        // distinguishable by that same threshold, so every id BELOW it is a palette row and is checked. Read off the
        // packed instructions rather than a parallel list: SdfOp.ShapeBlend is written at exactly one site (Shape),
        // so this covers every emitted shape by construction and cannot drift from one.
        //
        // The sentinel BAND is bounded on both sides, for the same reason the palette is. sampleScreenSurface
        // (sdf-world.hlsli) turns any id above ScreenMaterialId into a direct screenSurfaces[]/sdfDecalCells[] index
        // with no search, so an id naming no declared surface reads a slot the program never packed. The band's top is
        // judged HERE for the palette's reason: the screen list is still growing while shapes are emitted.
        // (AddMaterial owns the palette's own ceiling: it refuses the row that would collide with the sentinel.)
        for (var index = 0; (index < m_instructions.Count); index++) {
            var instruction = m_instructions[index];

            if (instruction.Op != SdfOp.ShapeBlend) {
                continue;   // Every other op's Material lane carries op-specific data (a fold's stride, a jitter's variant count), never a palette row.
            }

            var shapeMaterial = ((int)instruction.Material);   // Shape refuses a negative id at emission, so this cast round-trips.

            if (shapeMaterial >= ScreenMaterialId) {
                if (shapeMaterial == ScreenMaterialId) {
                    continue;   // The plain sentinel: procedural screen shading, and the one screen id that reads no side table at all.
                }

                var screenIndex = ((shapeMaterial - ScreenMaterialId) - 1);

                if (DeclaresScreenIndex(screenIndex: screenIndex)) {
                    continue;
                }

                throw new InvalidOperationException(message: $"A shape names screen material {shapeMaterial}, which decodes to screen index {screenIndex}, but the program declares no screen surface at that index (declared: {DescribeScreenIndices()}). The shader indexes the screen-surface and decal tables directly with it, so this would read a slot the program never packed. Declare the surface (ScreenSlab's screen overload emits both halves together), or use {ScreenMaterialId} for the plain procedural screen material.");
            }

            if (shapeMaterial < m_materials.Count) {
                continue;
            }

            throw new InvalidOperationException(message: $"A shape names material {shapeMaterial}, but the program declares {m_materials.Count} material(s) — every non-screen material id must be below {m_materials.Count}. The shader clamps an out-of-range id to material 0, so this would shade with a material the program never registered instead of failing. Add the material, or correct the shape's id.");
        }

        // THE POSITIONAL-RECOLOR GATE. A CellJitter variant, a WallpaperFold stride, and a RepeatPolar stride are all
        // added to a shape's material id on the GPU, and the sum goes straight into sdfMaterialLoad, which indexes the
        // packed palette. A window that leaves the span it may address is a defect, and it too can only be judged HERE:
        // when the fold is declared the palette is not final and the shape it recolors does not exist yet.
        // This REFUSES rather than repairs — the scope clamp is a first-party convenience that narrows the one stride it
        // can see, this is the gate, and an author gets a throw instead of silently recolored pixels. If the two ever
        // disagree, the gate wins by refusing.
        //
        // THE SPAN A WINDOW MAY ADDRESS is its OWN material scope when it was emitted inside one, and the whole palette
        // only when it was not. BeginMaterialScope is per-CONTRIBUTOR, so judging every window against the whole palette
        // would pass a contributor's oversized recolor whenever ANOTHER contributor happened to register enough
        // materials — memory-safe (the read lands inside the table) but reading, and shading with, a neighbour's
        // palette. With no scope open the whole palette IS the scope, so a direct build is judged exactly as before.
        //
        // A SPAN HAS TWO ENDS, AND BOTH ARE CHECKED. Judging only the window's TOP left the BASE unguarded, and a
        // window can leave its scope downward just as easily: open scope A and add material 0, close it, open scope B
        // and add materials 1 and 2, then recolor a shape whose base material is 0 by up to +1. The top is 1, well
        // inside scope B's 0..2 — yet the shape alternates between scope B's material and scope A's, which is
        // precisely the cross-contributor bleed the scope-relative gate exists to prevent. CellJitter is the route
        // that reaches this (the strided folds are narrowed at emit time by ApplyPositionalMaterialScopeClamp, which
        // refuses an out-of-scope base of its own), but the check is on the WINDOW, not on the op, so no future
        // recolor op has to remember to opt in.
        foreach (var window in m_materialRecolorWindows) {
            var top = (window.Material + window.MaxDelta);   // 64-bit: see m_materialRecolorWindows on why a 32-bit top lies
            var scope = window.Scope;
            var limit = ((scope?.MaterialEnd) ?? m_materials.Count);

            if (
                (scope is not null) &&
                (window.Material < scope.MaterialBase)
            ) {
                var baseLever = ((window.Op == SdfOp.CellJitter)
                    ? "materialVariants"
                    : "materialStride"
                );

                throw new InvalidOperationException(message: $"A {window.Op} positional material recolor STARTS BELOW THE MATERIAL SCOPE it was emitted in: a shape whose base material is {window.Material}, recolored by up to +{window.MaxDelta}, reaches material ids {window.Material}..{top}, but that scope spans material ids {scope.MaterialBase}..{(limit - 1)} ({(limit - scope.MaterialBase)} material(s)) — every id must be at least {scope.MaterialBase}. The shader adds the recolor to the base, so this would shade with a material this contributor never registered: another contributor's palette. Give the shape a base material this scope added, or lower the {baseLever}.");
            }

            if (top >= limit) {
                var lever = ((window.Op == SdfOp.CellJitter)
                    ? "materialVariants"
                    : "materialStride"
                );

                throw new InvalidOperationException(message: ((scope is null)
                    ? $"A {window.Op} positional material recolor reaches past the palette: a shape whose base material is {window.Material}, recolored by up to +{window.MaxDelta}, reaches material id {top}, but the program declares {limit} material(s) — every id must stay below {limit}. The shader adds the recolor before sdfMaterialLoad, so this would read past the packed material table on the GPU. Add the materials the recolor reaches, or lower the {lever}."
                    : $"A {window.Op} positional material recolor LEFT THE MATERIAL SCOPE it was emitted in: a shape whose base material is {window.Material}, recolored by up to +{window.MaxDelta}, reaches material id {top}, but that scope spans material ids {scope.MaterialBase}..{(limit - 1)} ({(limit - scope.MaterialBase)} material(s)) — every id must stay below {limit}. The shader adds the recolor before sdfMaterialLoad, so this would shade with a material this contributor never registered: another contributor's palette, or (past the {m_materials.Count} the program declares) the packed material table's end. Add the materials the recolor reaches to this scope, or lower the {lever}."));
            }
        }

        return new SdfProgram(
            buildInstanceGrid: buildInstanceGrid,
            gridWorkspace: gridWorkspace,
            instances: m_instances,
            instructions: m_instructions,
            materials: m_materials,
            screenSurfaces: m_screenSurfaces
        );
    }
}
