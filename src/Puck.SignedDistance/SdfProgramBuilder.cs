using System.Numerics;

using Puck.Maths;
using Puck.Text;

namespace Puck.SignedDistance;

/// <summary>Builds an SDF program as an ordered stream of point transforms, field operations, shapes, and materials.</summary>
public sealed class SdfProgramBuilder {
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
    /// <summary>The most screen surfaces one program may declare (matches <c>Puck.SdfVm.SdfWorldEngine.MaxScreenSurfaces</c>
    /// — the kernels' <c>screenSurfaces[]</c>/<c>screenSources[]</c> array length; a contract separate from the
    /// viewport capacity <c>Puck.SdfVm.SdfWorldEngine.MaxViewports</c>). Capped at 32 by the single-<c>uint</c>
    /// <c>screenMask</c> the engine pushes per frame.</summary>
    public const int MaxScreenSurfaces = 32;
    // KEEP IN SYNC with SDF_SCREEN_MATERIAL in Assets/Shaders/Sdf/sdf-vm.hlsli.
    /// <summary>The reserved material identifier used by the plain procedural screen material.</summary>
    public const int ScreenMaterialId = 65535;

    private readonly List<SdfInstanceRange> m_instances = [];
    private readonly List<SdfInstruction> m_instructions = [];
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
    private readonly List<SdfMaterial> m_materials = [];
    private readonly List<SdfScreenSurface> m_screenSurfaces = [];

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

    /// <summary>Adds a material to the program palette.</summary>
    /// <param name="material">The material to add.</param>
    /// <returns>The zero-based material identifier used by shape instructions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A component of <paramref name="material"/> is not finite, or is
    /// negative.</exception>
    /// <exception cref="InvalidOperationException">The composed palette already holds <see cref="ScreenMaterialId"/>
    /// materials (see the ceiling check below).</exception>
    public int AddMaterial(SdfMaterial material) {
        // Every field lands verbatim in the two packed palette words (SdfProgram writes Albedo/Emissive, then
        // Specular/Shininess), so all four are shading inputs with no host-side normalization: a negative reflectance,
        // emissive strength, specular strength, or Blinn-Phong exponent has no physical reading, and a NaN in any of
        // them propagates into the shaded colour of every pixel the material wins.
        RequireNonNegative(
            value: material.Albedo,
            paramName: nameof(material),
            subject: "A material albedo"
        );
        RequireNonNegative(
            value: material.Emissive,
            paramName: nameof(material),
            subject: "A material emissive strength"
        );
        RequireNonNegative(
            value: material.Specular,
            paramName: nameof(material),
            subject: "A material specular strength"
        );
        RequireNonNegative(
            value: material.Shininess,
            paramName: nameof(material),
            subject: "A material shininess exponent"
        );

        // THE PALETTE/SENTINEL COLLISION GATE. A shape's material id is a plain composed index below ScreenMaterialId,
        // or a screen sentinel AT OR ABOVE it (ScreenMaterialId itself, or ScreenMaterialId + 1 + screenIndex — see
        // ScreenSlab) — and Build()'s own palette-range gate deliberately EXEMPTS every id >= ScreenMaterialId rather
        // than refusing it (that range is legitimately screen shading, not an out-of-palette row). So a caller whose
        // OWN ordinal is perfectly in range (e.g. a puck.sdf.v1 document naming ordinal 0) can still have it translate,
        // once composed onto a shared builder alongside enough EARLIER materials from another contributor, into a raw
        // index at or past ScreenMaterialId — silently reinterpreted downstream as a screen-surface reference instead
        // of refused as out-of-range. Refuse the growth HERE, at the one place that assigns the composed index, naming
        // the ceiling, rather than let a document's palette-local ordinal reach another host table.
        if (m_materials.Count >= ScreenMaterialId) {
            throw new InvalidOperationException(message: $"A program's composed material palette may declare at most {ScreenMaterialId} materials (ScreenMaterialId) — material index {ScreenMaterialId} would collide with the reserved screen-material sentinel range. Register fewer materials.");
        }

        m_materials.Add(item: material);

        return (m_materials.Count - 1);
    }
    /// <summary>Opens a static per-object instance: every instruction until the matching <see cref="EndInstance"/>
    /// belongs to it, and the world renderer's tile-cull beam prepass tests <paramref name="boundCenter"/>/
    /// <paramref name="boundRadius"/> (a world-space bounding sphere) per tile, evaluating the instance's instruction
    /// slice only for tiles the sphere may cover. Instructions declared outside any instance are the world set:
    /// always evaluated, unmasked (floors/walls/unbounded shapes).</summary>
    /// <param name="boundCenter">The instance's world-space bounding-sphere center.</param>
    /// <param name="boundRadius">The instance's world-space bounding-sphere radius.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="boundCenter"/> is not finite, or
    /// <paramref name="boundRadius"/> is not finite and non-negative.</exception>
    /// <exception cref="InvalidOperationException">An instance is already open.</exception>
    public SdfProgramBuilder BeginInstance(Vector3 boundCenter, float boundRadius) {
        RequireInstanceBound(
            center: boundCenter,
            centerParamName: nameof(boundCenter),
            radius: boundRadius,
            radiusParamName: nameof(boundRadius)
        );
        BeginInstanceCore(
            isDynamic: false,
            center: boundCenter,
            radius: boundRadius,
            slot: 0
        );

        return this;
    }
    /// <summary>Opens a dynamic per-object instance: like <see cref="BeginInstance"/>, but the bound center resolves
    /// on the GPU each frame as (dynamic-transform <paramref name="slot"/>'s position + <paramref name="boundOffset"/>)
    /// — no quaternion rotate, the entity's orientation is folded into the host-baked <paramref name="boundRadius"/>
    /// (as the per-shape/segment bounds gate already does). Pairs with a <see cref="SdfOp.TransformDynamic"/> the
    /// instance's own instructions apply.</summary>
    /// <param name="slot">The dynamic-transform slot index (0-based) this instance's bound tracks.</param>
    /// <param name="boundOffset">The bound's pre-dynamic offset (added to the slot's per-frame position).</param>
    /// <param name="boundRadius">The instance's bounding-sphere radius (post-dynamic geometry folded in).</param>
    /// <param name="active">Whether the instance participates in the tile-cull scan. Pass <see langword="false"/> to park a
    /// reserved-pool slot that carries no live content this rebuild (the classic "hidden below the floor" placeholder):
    /// the slot still exists (so the pool's live emission always fits the once-sized buffers), but the beam prepass skips
    /// its per-tile sphere test with a single branch (<see cref="SdfInstanceRange.Active"/>), so a parked slot costs
    /// almost nothing. Its mask bit is always 0 — Stage 1 never marches it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside the dynamic-transform slot
    /// range, <paramref name="boundOffset"/> is not finite, or <paramref name="boundRadius"/> is not finite and
    /// non-negative.</exception>
    /// <exception cref="InvalidOperationException">An instance is already open.</exception>
    public SdfProgramBuilder BeginInstanceDynamic(int slot, Vector3 boundOffset, float boundRadius, bool active = true) {
        RequireInstanceBound(
            center: boundOffset,
            centerParamName: nameof(boundOffset),
            radius: boundRadius,
            radiusParamName: nameof(boundRadius)
        );
        BeginInstanceCore(
            active: active,
            center: boundOffset,
            isDynamic: true,
            radius: boundRadius,
            slot: slot
        );

        return this;
    }
    /// <summary>Opens a material-authoring scope (see <see cref="SdfMaterialScope"/>): while open, any positional
    /// material recolor from <see cref="WallpaperFold"/>/<see cref="RepeatPolar"/> is clamped so it can only ever
    /// reach a material this scope itself added (via <see cref="AddMaterial"/>) — never a material an outer scope, or
    /// a different emitter sharing this builder, added. Dispose the returned scope (a <see langword="using"/> block)
    /// to close it; scopes nest strictly LIFO, and the innermost open scope is the one every positional stride
    /// resolves against. Opening no scope at all (the default for every caller that existed before this mechanism)
    /// leaves every positional recolor exactly as unclamped as before — this call is the only thing that changes
    /// emission behavior.</summary>
    /// <returns>The opened scope — dispose it to close.</returns>
    public SdfMaterialScope BeginMaterialScope() {
        var scope = new SdfMaterialScope(
            builder: this,
            materialBase: m_materials.Count
        );

        m_materialScopes.Add(item: scope);

        return scope;
    }
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
    public SdfProgramBuilder Box(Vector3 halfExtents, float round, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The decoder is `q = abs(p) - (halfExtents - round); length(max(q, 0)) + min(max(q), 0) - round`. A negative
        // half-extent turns the box inside out, and a negative round is a corner INSET the shape has no spelling for —
        // RoundedRectangle, the 2D sibling, already clamps its corner radius to [0, min(half-extents)], which is the
        // file's settled position that a corner radius is non-negative. A round LARGER than a half-extent stays legal:
        // TryGetLocalBound deliberately adds it as bound slack "against degenerate authoring".
        RequireNonNegative(
            value: halfExtents,
            paramName: nameof(halfExtents),
            subject: "A box half-extent"
        );
        RequireNonNegative(
            value: round,
            paramName: nameof(round),
            subject: "A box corner radius"
        );
        RequireFiniteBoxBound(
            halfExtents: halfExtents,
            round: round,
            shapeName: "box"
        );

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
    /// <summary>Compiles the authored instructions/instances/materials/screens into a packed <see cref="SdfProgram"/>.</summary>
    /// <param name="buildInstanceGrid">Whether to pack the world-space uniform-grid instance cull (default
    /// <see langword="true"/>). Pass <see langword="false"/> to force the beam's flat per-instance fallback over the same
    /// instances — the reference the grid-cull gate compares against; see <see cref="SdfProgram"/>.</param>
    public SdfProgram Build(bool buildInstanceGrid = true) {
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
        for (var index = 0; (index < m_instructions.Count); index++) {
            var instruction = m_instructions[index];

            if (instruction.Op != SdfOp.ShapeBlend) {
                continue;   // Every other op's Material lane carries op-specific data (a fold's stride, a jitter's variant count), never a palette row.
            }

            var shapeMaterial = ((int)instruction.Material);   // Shape refuses a negative id at emission, so this cast round-trips.

            if (
                (shapeMaterial >= ScreenMaterialId) ||
                (shapeMaterial < m_materials.Count)
            ) {
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
            instances: m_instances,
            instructions: m_instructions,
            materials: m_materials,
            screenSurfaces: m_screenSurfaces
        );
    }
    public SdfProgramBuilder Capsule(Vector3 endpoint, float radius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The endpoint is a signed local-space offset (the segment's far end), so only finiteness is refused; the
        // radius closes the segment into a capsule exactly as Sphere's does — `length(...) - radius`.
        RequireFinite(
            value: endpoint,
            paramName: nameof(endpoint),
            subject: "A capsule endpoint"
        );
        RequireNonNegative(
            value: radius,
            paramName: nameof(radius),
            subject: "A capsule radius"
        );

        // The endpoint's raw components are each individually finite, but dot(endpoint, endpoint) is not: a component
        // near float's ~1.84e19 sqrt-of-max threshold squares past float.MaxValue and overflows to +Infinity — baking
        // a silent reciprocal ZERO into derived1 below (poisoning the capsule's own distance field) while
        // SdfProgram.TryGetLocalBound's endpoint.Length() (the SAME dot, square-rooted) derives an INFINITE cull
        // bound. Refuse rather than clamp — a clamped dot would silently shorten the capsule to some other length
        // than authored.
        var dotEndpoint = Vector3.Dot(
            vector1: endpoint,
            vector2: endpoint
        );

        if (!float.IsFinite(f: dotEndpoint)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(endpoint),
                message: $"A capsule endpoint {endpoint}'s derived dot(endpoint, endpoint) is not finite. Narrow the endpoint."
            );
        }

        return Shape(
            blend: blend,
            // Data1.y carries the HOST-BAKED 1/dot(endpoint, endpoint): shapes evaluate millions of times per frame
            // while programs build once, and the shared multiply keeps both backends' shader codegen identical where a
            // per-eval divide contracted differently (KEEP IN SYNC with sdfCapsule in Assets/Shaders/Sdf/sdf-vm.hlsli).
            derived1: (1f / MathF.Max(
                x: dotEndpoint,
                y: 0.0001f
            )),
            dimensions: new Vector4(
                value: endpoint,
                w: radius
            ),
            material: material,
            shape: SdfShapeType.Capsule,
            smooth: smooth
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
                message: "CellJitter jitter/2 must be < min(spacing)/2, or jittered content crosses the cell boundary and holes the march. The caller must ALSO keep jitter/2 + prototype radius <= min(spacing)/2 (the prototype is emitted later, so this builder cannot check it) — and even then the single-cell round() fold overestimates near cell walls (containment does not guarantee the nearest copy; boundary seams and grazing-angle hole risk persist), so keep jitter conservative.",
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
    public SdfProgramBuilder Cylinder(float radius, float halfHeight, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The decoder subtracts both from magnitudes — `float2(length(p.xz), abs(p.y)) - float2(radius, halfHeight)` —
        // so a negative one leaves that axis with no surface, and the cull bound reads them as a right triangle's legs.
        RequireNonNegative(
            value: radius,
            paramName: nameof(radius),
            subject: "A cylinder radius"
        );
        RequireNonNegative(
            value: halfHeight,
            paramName: nameof(halfHeight),
            subject: "A cylinder half-height"
        );

        // Both legs are individually finite, but SdfProgram.TryGetLocalBound's Cylinder cull bound is
        // sqrt(radius² + halfHeight²) — the same dot-product-shaped overflow Capsule's endpoint has: either leg
        // squared can overflow past float.MaxValue well before the leg itself does.
        if (!float.IsFinite(f: MathF.Sqrt(x: ((radius * radius) + (halfHeight * halfHeight))))) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(halfHeight),
                message: $"A cylinder's derived bound radius (from radius {radius} and halfHeight {halfHeight}) is not finite. Narrow one or both dimensions."
            );
        }

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
    /// <summary>Inflates the entire field accumulated so far by a radius (rounds and fattens everything before it) —
    /// a field op: order it after everything it should inflate.</summary>
    /// <param name="radius">The inflation radius. A negative radius is legal and erodes instead — the decoder is a
    /// plain <c>d -= radius</c>, exact and 1-Lipschitz in both directions.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is not finite.</exception>
    public SdfProgramBuilder Dilate(float radius) {
        // Sign DELIBERATELY unconstrained (unlike Onion's thickness): `result.distance -= data0.x` is a pure offset of
        // a signed distance field, so a negative radius is an erosion — real geometry, not an empty field — and
        // SdfProgram's cull margin already folds MathF.Abs of this lane.
        return FiniteScalarTransform(
            op: SdfOp.Dilate,
            value: radius,
            paramName: nameof(radius),
            subject: "A dilation radius"
        );
    }
    /// <summary>Adds a bounded sinusoidal displacement to the field accumulated so far — surface relief (bumps,
    /// corrugation, a rippled skin) evaluated at the current point: the SDF-native answer to height/parallax mapping,
    /// where the relief is real geometry (it shadows and self-occludes). A field op (like <see cref="Onion"/>/
    /// <see cref="Dilate"/>): order it after the shapes it should displace. The separable <c>sin·sin·sin</c> basis is
    /// deterministic across both backends. Not 1-Lipschitz — the relief's gradient reaches <c>amplitude·‖frequency‖</c>,
    /// so the field can overestimate true distance by up to <c>1 + amplitude·‖frequency‖</c> and <c>AnalyzeLipschitz</c>
    /// bakes that as a conservative step clamp; keep <c>amplitude·‖frequency‖</c> moderate (a large product clamps the
    /// march to tiny steps). KEEP IN SYNC with SDF_OP_DISPLACE in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="frequency">Per-axis angular frequency of the sinusoid (radians per world unit).</param>
    /// <param name="amplitude">Peak displacement added to the field (world units; 0 = an exact identity).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frequency"/> or <paramref name="amplitude"/> is
    /// not finite.</exception>
    public SdfProgramBuilder Displace(Vector3 frequency, float amplitude) {
        // Both are signed by construction (a negative frequency or amplitude reverses the relief's phase, which is a
        // real authoring choice), and every consumer of the amplitude takes its magnitude — SdfProgram's cull margin
        // and DisplaceWarpLipschitz both read MathF.Abs — so only finiteness is refused.
        RequireFinite(
            value: frequency,
            paramName: nameof(frequency),
            subject: "A displacement frequency"
        );
        RequireFinite(
            value: amplitude,
            paramName: nameof(amplitude),
            subject: "A displacement amplitude"
        );

        return Transform(
            data0: new Vector4(
                value: frequency,
                w: amplitude
            ),
            op: SdfOp.Displace
        );
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
    /// <summary>Declares a dynamic per-object instance with balanced begin/end handling around <paramref name="emit"/>.
    /// If <paramref name="emit"/> throws, the builder is left with the instance open and partial state — discard it
    /// (no builder path rolls back on a throw).</summary>
    /// <param name="slot">The dynamic-transform slot index (0-based) this instance's bound tracks.</param>
    /// <param name="boundOffset">The bound's pre-dynamic offset (added to the slot's per-frame position).</param>
    /// <param name="boundRadius">The instance's bounding-sphere radius (post-dynamic geometry folded in).</param>
    /// <param name="emit">The instructions that belong to the instance.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside the dynamic-transform slot
    /// range, <paramref name="boundOffset"/> is not finite, or <paramref name="boundRadius"/> is not finite and
    /// non-negative.</exception>
    public SdfProgramBuilder DynamicInstance(int slot, Vector3 boundOffset, float boundRadius, Action<SdfProgramBuilder> emit) {
        RequireInstanceBound(
            center: boundOffset,
            centerParamName: nameof(boundOffset),
            radius: boundRadius,
            radiusParamName: nameof(boundRadius)
        );
        ScopedInstance(
            center: boundOffset,
            emit: emit,
            isDynamic: true,
            radius: boundRadius,
            slot: slot
        );

        return this;
    }
    /// <summary>Adds an ellipse (the exact ellipse 2D SDF) lifted to a 3D solid — <see cref="SdfLift.Revolve"/> at offset 0 gives
    /// an exact spheroid (which, unlike the approximate <see cref="Ellipsoid(Vector3, int, SdfBlendOp, float)"/> #6,
    /// earns a real cull bound), <see cref="SdfLift.Extrude"/> an elliptic-cylinder prism. Exact and 1-Lipschitz.
    /// KEEP IN SYNC with sdfEllipseSolid in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="semiX">The semi-axis along local X.</param>
    /// <param name="semiY">The semi-axis along local Y.</param>
    /// <param name="lift">Whether to revolve the profile around Y (offset 0 ⇒ a spheroid) or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="semiX"/>, <paramref name="semiY"/> or
    /// <paramref name="liftAmount"/> is not finite, the derived lifted bound radius (see remarks) is not finite,
    /// <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or
    /// <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder Ellipse(float semiX, float semiY, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Signs are absorbed (MathF.Abs then a 1e-4 floor on both semi-axes, MathF.Max(0) on the lift); a NaN would
        // survive both MathF.Max calls and then poison the circle-degeneracy nudge below.
        RequireFinite(
            value: semiX,
            paramName: nameof(semiX),
            subject: "An ellipse semi-axis"
        );
        RequireFinite(
            value: semiY,
            paramName: nameof(semiY),
            subject: "An ellipse semi-axis"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var ea = MathF.Max(
            x: MathF.Abs(x: semiX),
            y: 1e-4f
        );
        var eb = MathF.Max(
            x: MathF.Abs(x: semiY),
            y: 1e-4f
        );

        // The exact ellipse divides by (eb²−ea²); nudge a perfect circle apart so it never divides by zero (a circle is
        // better served by Sphere/Cylinder anyway). Sub-pixel at any sane authoring scale.
        if (MathF.Abs(x: (ea - eb)) < 1e-4f) {
            eb = (ea + 1e-4f);
        }

        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );

        RequireFiniteLiftedReach(
            radius2D: MathF.Max(
                x: ea,
                y: eb
            ),
            liftAmount: clampedLift,
            lift: lift,
            shapeName: "ellipse"
        );

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
            dimensions: new Vector4(
                w: clampedLift,
                x: ea,
                y: eb,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Ellipse,
            smooth: smooth
        );
    }
    public SdfProgramBuilder Ellipsoid(Vector3 radii, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The sign is absorbed by the Vector3.Abs clamp below (and the 1e-4 floor keeps the reciprocal finite), so
        // only NaN/infinity — which neither absorbs — are refused.
        RequireFinite(
            value: radii,
            paramName: nameof(radii),
            subject: "An ellipsoid radius"
        );

        // The degenerate-radius clamp and inverse radii are HOST-BAKED (Data1.yzw) to avoid two vector divides per
        // evaluation (KEEP IN SYNC with sdfEllipsoid in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var clamped = Vector3.Max(
            value1: Vector3.Abs(value: radii),
            value2: new Vector3(value: 0.0001f)
        );
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
    /// <summary>Closes the currently open instance (see <see cref="BeginInstance"/>/<see cref="BeginInstanceDynamic"/>).</summary>
    /// <exception cref="InvalidOperationException">No instance is open.</exception>
    public SdfProgramBuilder EndInstance() {
        if (m_openInstanceFirst < 0) {
            throw new InvalidOperationException(message: "EndInstance was called with no open instance (unbalanced Begin/EndInstance).");
        }

        if (m_fieldScope is not null) {
            throw new InvalidOperationException(message: "EndInstance was called with a field scope still open (PushField without its PopField). Close every scope opened inside the instance before EndInstance.");
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
    /// <summary>Adds a single glyph cell sampled from a bound font atlas (see <c>Puck.SdfVm.SdfWorldEngine.SetGlyphAtlas</c>) as
    /// a distance-level field — text as real world geometry (marchable, liftable, blendable, and with
    /// <see cref="SdfBlendOp.Subtraction"/> engravable into any surface). The glyph is the atlas letter where the atlas
    /// is bound (the world-lit render) and the conservative extruded cell box everywhere else. Most callers use
    /// <see cref="Text(FontAtlas, string, Vector3, Vector3, Vector3, float, int, SdfBlendOp, float, float, TextLayoutOptions, int?)"/>, which
    /// bakes these arguments from a laid-out string; this primitive is the one-cell seam.
    /// <para>The cell must map with uniform scale — <paramref name="halfWidth"/>/<paramref name="halfHeight"/>
    /// proportional to the atlas cell's texel width/height — for the field to stay 1-Lipschitz (factor 1, no step
    /// clamp); a stretched cell is the caller's risk, exactly as <see cref="Repeat"/>'s in-cell rule is. The atlas UVs
    /// are unorm2x16-packed host-side into two lanes so the ISA-wide <paramref name="smooth"/> radius keeps its lane
    /// (KEEP IN SYNC with SDF_SHAPE_GLYPH / sdfGlyphUnpackUv in Assets/Shaders/Sdf/sdf-vm.hlsli).</para></summary>
    /// <param name="uvBottomLeft">The atlas UV (in <c>[0, 1]²</c>) at the cell's local <c>(-halfWidth, -halfHeight)</c> corner.</param>
    /// <param name="uvTopRight">The atlas UV at the cell's local <c>(+halfWidth, +halfHeight)</c> corner.</param>
    /// <param name="halfWidth">The cell's local X half-extent, in world units.</param>
    /// <param name="halfHeight">The cell's local Y half-extent, in world units.</param>
    /// <param name="extrudeHalfDepth">The half-depth the glyph extrudes along local Z (clamped to ≥ 0).</param>
    /// <param name="distanceScale">The atlas distance range (in texels) times the world size of one texel: converts the
    /// encoded <c>[0, 1]</c> distance to world units. Host-baked (foot-gun discipline).</param>
    /// <param name="material">The material id the letter shades with.</param>
    /// <param name="blend">The blend against the field accumulated so far (Subtraction engraves).</param>
    /// <param name="smooth">The smooth/chamfer radius (meaningful only for a smooth/chamfer <paramref name="blend"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException">A UV or cell dimension is not finite,
    /// <paramref name="distanceScale"/> is not finite and non-negative, <paramref name="material"/> is negative,
    /// <paramref name="blend"/> is not a defined <see cref="SdfBlendOp"/>, or <paramref name="smooth"/> is not
    /// finite.</exception>
    public SdfProgramBuilder Glyph(Vector2 uvBottomLeft, Vector2 uvTopRight, float halfWidth, float halfHeight, float extrudeHalfDepth, float distanceScale, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // PackUv clamps the UVs into [0, 1] and the half-extents/extrusion take MathF.Abs / MathF.Max(0), so only
        // finiteness is refused there. distanceScale is the ONE lane packed raw: the decoder gates the atlas tap on
        // `dQuad < 0.5 * distanceScale` and then converts with `(0.5 - encoded) * distanceScale`, so a negative scale
        // inverts inside and outside.
        RequireFinite(
            value: uvBottomLeft,
            paramName: nameof(uvBottomLeft),
            subject: "A glyph atlas UV"
        );
        RequireFinite(
            value: uvTopRight,
            paramName: nameof(uvTopRight),
            subject: "A glyph atlas UV"
        );
        RequireFinite(
            value: halfWidth,
            paramName: nameof(halfWidth),
            subject: "A glyph cell half-width"
        );
        RequireFinite(
            value: halfHeight,
            paramName: nameof(halfHeight),
            subject: "A glyph cell half-height"
        );
        RequireFinite(
            value: extrudeHalfDepth,
            paramName: nameof(extrudeHalfDepth),
            subject: "A glyph extrude half-depth"
        );
        RequireNonNegative(
            value: distanceScale,
            paramName: nameof(distanceScale),
            subject: "A glyph distance scale"
        );

        return Shape(
            blend: blend,
            derived1: MathF.Abs(x: halfWidth),   // Data1.y = halfWidth
            derived2: MathF.Abs(x: halfHeight),  // Data1.z = halfHeight
            dimensions: new Vector4(
                w: MathF.Max(
                    x: 0f,
                    y: extrudeHalfDepth
                ),  // Data0.w = extrudeHalfDepth
                x: PackUv(uv: uvBottomLeft),         // Data0.x = packed uvMin
                y: PackUv(uv: uvTopRight),           // Data0.y = packed uvMax
                z: distanceScale                     // Data0.z = distanceScale
            ),
            material: material,
            shape: SdfShapeType.Glyph,
            smooth: smooth
        );
    }
    /// <summary>Declares a static per-object instance with balanced begin/end handling around <paramref name="emit"/>.
    /// If <paramref name="emit"/> throws, the builder is left with the instance open and partial state — discard it
    /// (no builder path rolls back on a throw).</summary>
    /// <param name="boundCenter">The instance's world-space bounding-sphere center.</param>
    /// <param name="boundRadius">The instance's world-space bounding-sphere radius.</param>
    /// <param name="emit">The instructions that belong to the instance.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="boundCenter"/> is not finite, or
    /// <paramref name="boundRadius"/> is not finite and non-negative.</exception>
    public SdfProgramBuilder Instance(Vector3 boundCenter, float boundRadius, Action<SdfProgramBuilder> emit) {
        RequireInstanceBound(
            center: boundCenter,
            centerParamName: nameof(boundCenter),
            radius: boundRadius,
            radiusParamName: nameof(boundRadius)
        );
        ScopedInstance(
            center: boundCenter,
            emit: emit,
            isDynamic: false,
            radius: boundRadius,
            slot: 0
        );

        return this;
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
    /// <summary>Shells the entire field accumulated so far into a hollow skin of the given thickness — a field op:
    /// order it after everything it should shell.</summary>
    /// <param name="thickness">The shell half-thickness.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not finite and non-negative.</exception>
    public SdfProgramBuilder Onion(float thickness) {
        // The decoder is `d = abs(d) - thickness`: a negative thickness leaves the field strictly positive everywhere,
        // so the shell has no zero set at all — the op silently erases the geometry it was ordered after. (Unlike
        // Dilate, whose negative branch is a real erosion; see its remarks.)
        RequireNonNegative(
            value: thickness,
            paramName: nameof(thickness),
            subject: "An onion shell thickness"
        );

        return ScalarTransform(
            op: SdfOp.Onion,
            value: thickness
        );
    }
    public SdfProgramBuilder Plane(Vector3 normal, float offset, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Normalized host-side, so a zero normal packs NaN; the offset is signed by construction (it slides the plane).
        RequireDirection(
            value: normal,
            paramName: nameof(normal),
            subject: "A plane normal"
        );
        RequireFinite(
            value: offset,
            paramName: nameof(offset),
            subject: "A plane offset"
        );

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
    /// <summary>Closes the scope opened by the matching <see cref="PushField"/> and composes its field back into the
    /// parent as one candidate, using the compose blend + smooth radius that <see cref="PushField"/> recorded. KEEP IN
    /// SYNC with SDF_OP_POP_FIELD in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <exception cref="InvalidOperationException">No field scope is open, or the scope emitted no shape.</exception>
    public SdfProgramBuilder PopField() {
        if (m_fieldScope is not { } scope) {
            throw new InvalidOperationException(message: "PopField was called with no open field scope (unbalanced PushField/PopField).");
        }

        m_fieldScope = null;

        if (m_shapeCount == scope.ShapeCountAtOpen) {
            throw new InvalidOperationException(message: "A field scope (PushField/PopField) must contain at least one shape — an empty scope composes SDF_FAR_DISTANCE and would carve nothing.");
        }

        // The POP carries the compose blend (Blend lane, header.z) and its smooth radius (Data1.x) — the SAME lanes a
        // ShapeBlend uses, because the shader treats a POP as just another candidate through the shared blend tail.
        m_instructions.Add(item: new SdfInstruction(
            Blend: ((uint)scope.Blend),
            Data0: default,
            Data1: new Vector4(
                w: 0f,
                x: MathF.Max(
                    x: 0f,
                    y: scope.Smooth
                ),
                y: 0f,
                z: 0f
            ),
            Material: 0,
            Op: SdfOp.PopField,
            Shape: 0
        ));

        return this;
    }
    /// <summary>Opens a scoped field accumulator (<see cref="SdfOp.PushField"/>): every accumulator-reading op emitted
    /// until the matching <see cref="PopField"/> — the intersection family, and the <see cref="Onion"/>/
    /// <see cref="Dilate"/>/<see cref="Displace"/> field ops — acts on this scope's shapes alone, not on everything
    /// emitted before it. Pair it with <see cref="PopField"/> to compose the scope back into the parent field; the
    /// <paramref name="compose"/> blend + <paramref name="smooth"/> given here are baked onto the pop instruction (a
    /// Union compose keeps the scope far-neutral, so a scoped instance stays cullable and segment-eligible; an
    /// intersection-family compose composes the scope globally, unmaskable). The scope must contain at least one shape,
    /// nest no deeper than <see cref="MaxFieldScopeDepth"/>, and close (via <see cref="PopField"/>) before
    /// <see cref="Build"/> or an enclosing <see cref="EndInstance"/>. A scope touches only the field, not the point, so
    /// per-shape cull bounds inside it stay sound and <see cref="ResetPoint"/> works as usual. KEEP IN SYNC with
    /// SDF_OP_PUSH_FIELD in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="compose">How the closed scope's field composes back into the parent (default <see cref="SdfBlendOp.Union"/>).</param>
    /// <param name="smooth">The smooth/chamfer radius of the <paramref name="compose"/> blend (ignored by the hard blends).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="smooth"/> is not finite, or
    /// <paramref name="compose"/> is not a defined <see cref="SdfBlendOp"/>.</exception>
    /// <exception cref="InvalidOperationException">The scope would nest deeper than <see cref="MaxFieldScopeDepth"/>.</exception>
    public SdfProgramBuilder PushField(SdfBlendOp compose = SdfBlendOp.Union, float smooth = 0f) {
        // PopField bakes MathF.Max(0f, smooth) onto the POP instruction, which absorbs a negative radius but not NaN.
        RequireFinite(
            value: smooth,
            paramName: nameof(smooth),
            subject: "A field-scope compose smooth radius"
        );
        // compose doesn't flow through Shape() (PopField writes it directly onto its own instruction), so it needs its
        // own enum-floor check rather than inheriting Shape()'s.
        RequireDefined(
            value: compose,
            paramName: nameof(compose)
        );

        // The depth guard reads MaxFieldScopeDepth (rather than just testing m_fieldScope is not null) so raising the
        // cap past 1 stays a localized change to this field + guard (see m_fieldScope's doc).
        var openDepth = ((m_fieldScope is null)
            ? 0
            : 1
        );

        if (openDepth >= MaxFieldScopeDepth) {
            throw new InvalidOperationException(message: $"PushField would nest a field scope deeper than the depth-{MaxFieldScopeDepth} cap. Close the open scope (PopField) before opening another.");
        }

        m_fieldScope = (compose, smooth, m_shapeCount);

        // A bare marker: the compose blend + smooth ride the POP instruction (a POP is the candidate), so the PUSH
        // carries no data — the shader only saves the accumulator and reseeds. Not routed through Transform() because
        // that path is byte-for-byte the pre-scope emission and must not gain a new caller here.
        m_instructions.Add(item: new SdfInstruction(
            Blend: 0,
            Data0: default,
            Data1: default,
            Material: 0,
            Op: SdfOp.PushField,
            Shape: 0
        ));

        return this;
    }
    /// <summary>Adds a regular convex <paramref name="sides"/>-gon (the exact star-polygon SDF with the m = 2 regular-polygon case) lifted to
    /// a 3D solid — <see cref="SdfLift.Extrude"/> gives a prism (a nut, a column, a gem), <see cref="SdfLift.Revolve"/>
    /// a lathe of the polygon's profile. The half-sector π/n is host-baked. Exact and 1-Lipschitz. KEEP IN SYNC with
    /// sdfPolyStar/sdfStar2D in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="sides">The side count n (clamped to ≥ 3).</param>
    /// <param name="radius">The circumradius (centre to a vertex).</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> or <paramref name="liftAmount"/> is not
    /// finite, the derived lifted bound radius (see remarks) is not finite, <paramref name="material"/> is negative,
    /// <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or <paramref name="smooth"/> is not
    /// finite.</exception>
    public SdfProgramBuilder RegularPolygon(int sides, float radius, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Signs are absorbed (MathF.Abs on the radius, MathF.Max(0) on the lift); sides is an int clamped to >= 3.
        RequireFinite(
            value: radius,
            paramName: nameof(radius),
            subject: "A polygon circumradius"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var n = Math.Max(
            val1: 3,
            val2: sides
        );
        var absRadius = MathF.Abs(x: radius);
        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );

        RequireFiniteLiftedReach(
            lift: lift,
            liftAmount: clampedLift,
            radius2D: absRadius,
            shapeName: "regular polygon"
        );

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),      // Data1.y = lift mode
            derived2: 1f,                     // Data1.z = ecs.y = 1 (m = 2: the regular-polygon case)
            dimensions: new Vector4(
                w: clampedLift,
                x: absRadius,
                y: (MathF.PI / n),            // an = π/n, HOST-BAKED
                z: 0f                         // ecs.x = 0
            ),
            material: material,
            shape: SdfShapeType.RegularPolygon,
            smooth: smooth
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
    public SdfProgramBuilder RoundCone(float lowerRadius, float upperRadius, float height, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Both radii are sphere radii in the decoder (`length(q) - lowerRadius`, `length(q - (0, height)) - upperRadius`)
        // — the same argument as Sphere. The height must be non-negative because the slope below is baked against
        // MathF.Max(height, 0.0001f) while the decoder places the top cap at the RAW +height: a negative height puts
        // the cap below the origin with a slope constant computed for a positive one, so the two disagree.
        RequireNonNegative(
            value: lowerRadius,
            paramName: nameof(lowerRadius),
            subject: "A round-cone lower radius"
        );
        RequireNonNegative(
            value: upperRadius,
            paramName: nameof(upperRadius),
            subject: "A round-cone upper radius"
        );
        RequireNonNegative(
            value: height,
            paramName: nameof(height),
            subject: "A round-cone height"
        );

        // The slope terms are HOST-BAKED (Data0.w = b, Data1.y = a) to avoid a divide and square root per evaluation
        // (KEEP IN SYNC with sdfRoundCone in Assets/Shaders/Sdf/sdf-vm.hlsli).
        var slope = ((lowerRadius - upperRadius) / MathF.Max(
            x: height,
            y: 0.0001f
        ));

        // The three raw inputs are each individually finite (RequireNonNegative above already refuses NaN/infinity),
        // but their RATIO is not: a huge radius difference over a near-zero height overflows a finite numerator by a
        // finite (floored) denominator into +/-Infinity, which the shape method above cannot see or clamp — it packs
        // straight into Data0.w and poisons derived1 and the program-wide Lipschitz step scale. Refuse rather than
        // clamp: a clamped slope would silently cone the shape at some other angle than authored.
        if (!float.IsFinite(f: slope)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(height),
                message: $"A round-cone's derived slope (lowerRadius {lowerRadius} minus upperRadius {upperRadius}, divided by height {height}) is not finite. Raise the height or narrow the radius difference."
            );
        }

        // A SECOND, independent derived value: SdfProgram.TryGetLocalBound's RoundCone cull bound is
        // |height/2| + max(lowerRadius, upperRadius) — the same sum-overflow class as Torus's radii, reachable even
        // when the slope above stays finite (equal enormous radii keep the slope's ratio at 0, but this sum still
        // overflows).
        var boundRadius = (MathF.Abs(x: (height * 0.5f)) + MathF.Max(
            x: lowerRadius,
            y: upperRadius
        ));

        if (!float.IsFinite(f: boundRadius)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(height),
                message: $"A round-cone's derived bound radius (half-height {(height * 0.5f)} plus the larger end radius {MathF.Max(
                    x: lowerRadius,
                    y: upperRadius
                )}) is not finite. Narrow the radii or the height."
            );
        }

        return Shape(
            blend: blend,
            derived1: MathF.Sqrt(x: MathF.Max(
                x: (1f - (slope * slope)),
                y: 0f
            )),
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
    /// <summary>Adds a rounded rectangle (exact rounded-box 2D SDF) lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a
    /// rounded slab/plaque, <see cref="SdfLift.Revolve"/> a rounded disc/puck. Exact and 1-Lipschitz. KEEP IN SYNC
    /// with sdfRoundedRect in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="halfWidth">Half-width of the rectangle (its local X half-extent).</param>
    /// <param name="halfHeight">Half-height of the rectangle (its local Y half-extent).</param>
    /// <param name="cornerRadius">Corner-rounding radius; clamped to the smaller half-extent (corners round inward).</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset (for <see cref="SdfLift.Revolve"/>) or the extrude half-height (for
    /// <see cref="SdfLift.Extrude"/>); clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not finite, the derived lifted bound radius (see
    /// remarks) is not finite, <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined
    /// <see cref="SdfLift"/>, or <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder RoundedRectangle(float halfWidth, float halfHeight, float cornerRadius, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Every sign is absorbed below (MathF.Abs on the half-extents, Math.Clamp to [0, min] on the corner radius,
        // MathF.Max(0) on the lift), and none of those absorb NaN.
        RequireFinite(
            value: halfWidth,
            paramName: nameof(halfWidth),
            subject: "A rounded-rectangle half-width"
        );
        RequireFinite(
            value: halfHeight,
            paramName: nameof(halfHeight),
            subject: "A rounded-rectangle half-height"
        );
        RequireFinite(
            value: cornerRadius,
            paramName: nameof(cornerRadius),
            subject: "A rounded-rectangle corner radius"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var hw = MathF.Abs(x: halfWidth);
        var hh = MathF.Abs(x: halfHeight);
        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );

        RequireFiniteLiftedReach(
            radius2D: new Vector2(
                x: hw,
                y: hh
            ).Length(),
            liftAmount: clampedLift,
            lift: lift,
            shapeName: "rounded-rectangle"
        );

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
            dimensions: new Vector4(
                w: clampedLift,
                x: hw,
                y: hh,
                z: Math.Clamp(
                    cornerRadius,
                    0f,
                    MathF.Min(
                        x: hw,
                        y: hh
                    )
                )
            ),
            material: material,
            shape: SdfShapeType.RoundedRectangle,
            smooth: smooth
        );
    }
    /// <summary>Adds a sampled distance-field brick (<see cref="SdfShapeType.SampledRegion"/>) — the settled-carve union field,
    /// pre-baked into a <paramref name="dimX"/>x<paramref name="dimY"/>x<paramref name="dimZ"/> cubic-voxel lattice at
    /// <paramref name="brickWordOffset"/> in the engine's <c>sdfBrickPool</c> buffer, sampled O(1) by manual trilinear
    /// interpolation and composed as one ordinary <see cref="SdfBlendOp.Subtraction"/> instance. The distance channel is
    /// pre-scaled c/λ (λ folded in at bake time, so this op applies no step clamp), and <paramref name="boundaryFloor"/>
    /// (= margin/λ, host-baked) is the outside-box lower-bound offset. Where the pool is not bound the shape falls back to
    /// the conservative union hull (SDF_FAR_DISTANCE — the subtraction never bites), so a brick program renders uncarved
    /// but never holes. The lane packing (Data0 = boxMin.xyz + cellSize; Data1 = smooth + packedDims + brickWordOffset +
    /// boundaryFloor) is KEEP IN SYNC with sdfSampledRegion in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="boxMin">The brick box's minimum corner in the chain's local space (voxel (0,0,0)'s cell origin).</param>
    /// <param name="cellSize">The cubic voxel edge (world units per voxel); must be finite and greater than zero.</param>
    /// <param name="dimX">Voxel count along local X, in [1, <see cref="MaxSampledRegionDim"/>].</param>
    /// <param name="dimY">Voxel count along local Y, in [1, <see cref="MaxSampledRegionDim"/>].</param>
    /// <param name="dimZ">Voxel count along local Z, in [1, <see cref="MaxSampledRegionDim"/>].</param>
    /// <param name="brickWordOffset">The brick's base word index in the pool buffer (from the planner's slot layout); ≥ 0.</param>
    /// <param name="boundaryFloor">The host-baked outside-box lower-bound offset (margin/λ); finite and ≥ 0.</param>
    /// <param name="material">The material id the carved region shades with (unused where subtraction only removes).</param>
    /// <param name="blend">The compose against the accumulated field; <see cref="SdfBlendOp.Subtraction"/> by default (a
    /// brick carves). Smooth and chamfered carves remain analytic and must not use this sampled representation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="boxMin"/> is not finite, a dim is out of range,
    /// <paramref name="cellSize"/> is not positive and finite, <paramref name="brickWordOffset"/> is negative,
    /// <paramref name="boundaryFloor"/> is negative or not finite, or <paramref name="blend"/> is not a defined
    /// <see cref="SdfBlendOp"/>.</exception>
    public SdfProgramBuilder SampledRegion(Vector3 boxMin, float cellSize, int dimX, int dimY, int dimZ, int brickWordOffset, float boundaryFloor, int material, SdfBlendOp blend = SdfBlendOp.Subtraction) {
        // The one lane this method did not check: the box corner is signed by construction (it is a position) but
        // still reaches Data0.xyz raw, and TryGetLocalBound derives the brick's whole cull sphere from it.
        RequireFinite(
            value: boxMin,
            paramName: nameof(boxMin),
            subject: "A sampled-region box corner"
        );

        if (
            !float.IsFinite(f: cellSize) ||
            (cellSize <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(cellSize),
                message: "A sampled-region cell size must be finite and greater than zero."
            );
        }

        if (
            (dimX < 1) ||
            (dimX > MaxSampledRegionDim)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(dimX),
                message: $"A sampled-region dimension must be in [1, {MaxSampledRegionDim}]."
            );
        }

        if (
            (dimY < 1) ||
            (dimY > MaxSampledRegionDim)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(dimY),
                message: $"A sampled-region dimension must be in [1, {MaxSampledRegionDim}]."
            );
        }

        if (
            (dimZ < 1) ||
            (dimZ > MaxSampledRegionDim)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(dimZ),
                message: $"A sampled-region dimension must be in [1, {MaxSampledRegionDim}]."
            );
        }

        if (brickWordOffset < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(brickWordOffset),
                message: "A sampled-region brick word offset must be non-negative."
            );
        }

        if (
            !float.IsFinite(f: boundaryFloor) ||
            (boundaryFloor < 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(boundaryFloor),
                message: "A sampled-region boundary floor must be finite and non-negative."
            );
        }

        // 3x10-bit dim pack; the two uint bit-fields (packedDims, brickWordOffset) ride the float lanes as reinterpreted
        // bits (like Glyph's PackUv) and round-trip exactly through SdfProgram's WriteVector4 — no arithmetic touches them.
        var packedDims = ((uint)dimX) | (((uint)dimY) << 10) | (((uint)dimZ) << 20);

        // Every dim is capped at MaxSampledRegionDim (1023) and cellSize is required positive and finite, but the
        // PRODUCT SdfProgram.TryGetLocalBound derives from them (dims * cellSize, the brick box's extent, feeding its
        // circumsphere radius) can still overflow float.MaxValue for a large-enough finite cellSize even though every
        // input independently passed its own check — the same overflow class Box/Cylinder/Torus/RoundCone/Capsule
        // refuse below.
        var extent = (new Vector3(
            x: dimX,
            y: dimY,
            z: dimZ
        ) * cellSize);

        if (!float.IsFinite(f: extent.Length())) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(cellSize),
                message: $"A sampled-region's derived box extent (dims {dimX}x{dimY}x{dimZ} at cellSize {cellSize}) is not finite. Narrow the cell size or the dimensions."
            );
        }

        return Shape(
            blend: blend,
            derived1: BitConverter.UInt32BitsToSingle(value: packedDims),                // Data1.y = packedDims (uint bits)
            derived2: BitConverter.UInt32BitsToSingle(value: ((uint)brickWordOffset)),      // Data1.z = brickWordOffset (uint bits)
            derived3: boundaryFloor,                                                      // Data1.w = boundaryFloor
            dimensions: new Vector4(
                w: cellSize,       // Data0.w = cellSize
                x: boxMin.X,       // Data0.xyz = box min corner
                y: boxMin.Y,
                z: boxMin.Z
            ),
            material: material,
            shape: SdfShapeType.SampledRegion,
            smooth: 0f             // Data1.x = smooth: a brick composes with HARD subtraction (smooth carves stay analytic)
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
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // A screen slab IS a rounded box (sdfBox decodes both shape types), so it carries Box's argument contract.
        RequireNonNegative(
            value: halfExtents,
            paramName: nameof(halfExtents),
            subject: "A screen-slab half-extent"
        );
        RequireNonNegative(
            value: round,
            paramName: nameof(round),
            subject: "A screen-slab corner radius"
        );
        RequireFiniteBoxBound(
            halfExtents: halfExtents,
            round: round,
            shapeName: "screen-slab"
        );

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
    /// <summary>Adds a screen slab whose lit face samples a bound screen source (see
    /// <c>Puck.SdfVm.SdfWorldEngine.SetScreenSource</c>) instead of the flat screen material, when one is bound this
    /// frame — a diegetic screen (an emulator's framebuffer, e.g.) on static geometry. The slab's shape/distance field
    /// is identical to the plain overload (a rounded box); only shading differs. The world-space frame maps a hit
    /// point to the slab's <c>[0,1]²</c> UV: <paramref name="worldRight"/>/<paramref name="worldUp"/> must be unit and
    /// orthogonal to each other and to the slab's local Z (its front-face normal), and should match the rigid
    /// transform (<see cref="Translate"/>/<see cref="Rotate(Quaternion)"/>) already applied to the point when this shape is
    /// declared — a mismatched frame sizes/rotates the sampled image wrong without affecting the geometry at all.</summary>
    /// <param name="halfExtents">The slab's local half-extents (as <see cref="ScreenSlab(Vector3, float, SdfBlendOp, float)"/>).</param>
    /// <param name="round">The corner-rounding radius.</param>
    /// <param name="worldOrigin">The front face's world-space center.</param>
    /// <param name="worldRight">The unit world-space axis the UV's U increases along (the slab's local +X, in world space).</param>
    /// <param name="worldUp">The unit world-space axis the UV's V increases against — V = 0 at the top (the slab's local +Y, in world space).</param>
    /// <param name="screenIndex">The screen source slot in the range 0 through <see cref="MaxScreenSurfaces"/> − 1.</param>
    /// <param name="blend">The blend operator against the field accumulated so far.</param>
    /// <param name="smooth">The smooth-blend radius (meaningful only for a smooth <paramref name="blend"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside the supported range, this
    /// program has already declared <see cref="MaxScreenSurfaces"/> screen surfaces, a slab dimension is not finite and
    /// non-negative, <paramref name="worldOrigin"/> is not finite, <paramref name="worldRight"/>/
    /// <paramref name="worldUp"/> is not finite or has zero length, <paramref name="worldRight"/> and
    /// <paramref name="worldUp"/> are parallel, or <paramref name="blend"/> is not a defined
    /// <see cref="SdfBlendOp"/>.</exception>
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, Vector3 worldOrigin, Vector3 worldRight, Vector3 worldUp, int screenIndex, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The slab geometry carries Box's contract; the UV frame's two axes are normalized host-side into the screen
        // surface record, so a zero axis would map every hit point to a NaN UV.
        RequireNonNegative(
            value: halfExtents,
            paramName: nameof(halfExtents),
            subject: "A screen-slab half-extent"
        );
        RequireNonNegative(
            value: round,
            paramName: nameof(round),
            subject: "A screen-slab corner radius"
        );
        RequireFiniteBoxBound(
            halfExtents: halfExtents,
            round: round,
            shapeName: "screen-slab"
        );
        RequireFinite(
            value: worldOrigin,
            paramName: nameof(worldOrigin),
            subject: "A screen-slab world origin"
        );
        RequireDirection(
            value: worldRight,
            paramName: nameof(worldRight),
            subject: "A screen-slab world right axis"
        );
        RequireDirection(
            value: worldUp,
            paramName: nameof(worldUp),
            subject: "A screen-slab world up axis"
        );

        // Two individually valid axes can still be parallel, and their cross product is then zero — normalizing it
        // would put NaN into a downstream frame built from right/up/forward (Text's identical hazard; same check).
        var forward = Vector3.Normalize(value: Vector3.Cross(
            vector1: Vector3.Normalize(value: worldRight),
            vector2: Vector3.Normalize(value: worldUp)
        ));

        if (
            !float.IsFinite(f: forward.X) ||
            !float.IsFinite(f: forward.Y) ||
            !float.IsFinite(f: forward.Z)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(worldUp),
                message: "A screen-slab world right and up axis must not be parallel — they span the slab's front-face frame."
            );
        }

        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}."
            );
        }

        if (m_screenSurfaces.Count >= MaxScreenSurfaces) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A program may declare at most {MaxScreenSurfaces} screen surfaces."
            );
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
            material: ((ScreenMaterialId + 1) + screenIndex),
            shape: SdfShapeType.ScreenSlab,
            smooth: smooth
        );
    }
    /// <summary>Adds a <see cref="ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>
    /// screen slab that derives the screen's world-space right/up axes from the slab's static orientation.</summary>
    /// <param name="halfExtents">The slab's local half-extents.</param>
    /// <param name="round">The corner-rounding radius.</param>
    /// <param name="worldOrigin">The front face's world-space center.</param>
    /// <param name="worldOrientation">The static slab orientation in world space.</param>
    /// <param name="screenIndex">The screen source slot in the range 0 through <see cref="MaxScreenSurfaces"/> − 1.</param>
    /// <param name="blend">The blend operator against the field accumulated so far.</param>
    /// <param name="smooth">The smooth-blend radius.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="worldOrientation"/> is not finite or has zero
    /// length, or the overload this forwards to refuses an argument.</exception>
    public SdfProgramBuilder ScreenSlab(Vector3 halfExtents, float round, Vector3 worldOrigin, Quaternion worldOrientation, int screenIndex, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Checked HERE rather than left to the forwarded overload: a non-unit orientation would have produced two
        // zero axes, and the refusal would then have named worldRight instead of the argument the caller supplied.
        RequireRotation(
            value: worldOrientation,
            paramName: nameof(worldOrientation),
            subject: "A screen-slab orientation"
        );

        var unit = Quaternion.Normalize(value: worldOrientation);

        return ScreenSlab(
            blend: blend,
            halfExtents: halfExtents,
            round: round,
            screenIndex: screenIndex,
            smooth: smooth,
            worldOrigin: worldOrigin,
            worldRight: Vector3.Transform(
                rotation: unit,
                value: Vector3.UnitX
            ),
            worldUp: Vector3.Transform(
                rotation: unit,
                value: Vector3.UnitY
            )
        );
    }
    /// <summary>Adds a sphere centered at the current local point.</summary>
    /// <param name="radius">The sphere radius.</param>
    /// <param name="material">The material identifier.</param>
    /// <param name="blend">The blend against the accumulated field.</param>
    /// <param name="smooth">The radius used by smooth and chamfer blends.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is not finite and non-negative, or
    /// <paramref name="material"/> is negative, <paramref name="blend"/> is not a defined <see cref="SdfBlendOp"/>, or
    /// <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder Sphere(float radius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The decoder is `length(p) - radius`, so a negative radius leaves the field strictly positive: the sphere has
        // no surface at all, while the program still spends an instruction, a cull bound (TryGetLocalBound packs
        // MathF.Abs of this very lane) and a Lipschitz reach on it. Zero is allowed — a degenerate point.
        RequireNonNegative(
            value: radius,
            paramName: nameof(radius),
            subject: "A sphere radius"
        );

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
    /// <summary>Adds an <paramref name="points"/>-pointed star (the exact star-polygon SDF) lifted to a 3D solid — <see cref="SdfLift.Extrude"/>
    /// gives a star prism (a badge, a gem), <see cref="SdfLift.Revolve"/> a spiked lathe. The baked constants
    /// (π/n and ecs = (cos(π/m), sin(π/m))) are host-baked. Exact and 1-Lipschitz. KEEP IN SYNC with
    /// sdfPolyStar/sdfStar2D in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="points">The point count n (clamped to ≥ 2).</param>
    /// <param name="radius">The outer radius (centre to a point tip).</param>
    /// <param name="sharpness">The inner-radius control m, clamped to [2, n]: 2 is a convex n-gon, larger is sharper
    /// (deeper notches between points).</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/>, <paramref name="sharpness"/> or
    /// <paramref name="liftAmount"/> is not finite, the derived lifted bound radius (see remarks) is not finite,
    /// <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or
    /// <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder Star(int points, float radius, float sharpness, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Signs are absorbed (MathF.Abs on the radius, Math.Clamp to [2, n] on the sharpness, MathF.Max(0) on the
        // lift), and a NaN sharpness would otherwise reach both baked trig constants.
        RequireFinite(
            value: radius,
            paramName: nameof(radius),
            subject: "A star outer radius"
        );
        RequireFinite(
            value: sharpness,
            paramName: nameof(sharpness),
            subject: "A star sharpness"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var n = Math.Max(
            val1: 2,
            val2: points
        );
        var m = Math.Clamp(
            max: n,
            min: 2f,
            value: sharpness
        );
        var en = (MathF.PI / m);
        var absRadius = MathF.Abs(x: radius);
        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );

        RequireFiniteLiftedReach(
            lift: lift,
            liftAmount: clampedLift,
            radius2D: absRadius,
            shapeName: "star"
        );

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),      // Data1.y = lift mode
            derived2: MathF.Sin(x: en),          // Data1.z = ecs.y = sin(π/m)
            dimensions: new Vector4(
                w: clampedLift,
                x: absRadius,
                y: (MathF.PI / n),            // an = π/n, HOST-BAKED
                z: MathF.Cos(x: en)             // ecs.x = cos(π/m), HOST-BAKED
            ),
            material: material,
            shape: SdfShapeType.Star,
            smooth: smooth
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
    /// <summary>Lays <paramref name="text"/> out against <paramref name="atlas"/> and emits one <see cref="Glyph"/> cell
    /// per drawn character, positioned on the plane spanned by <paramref name="right"/>/<paramref name="up"/> at
    /// <paramref name="origin"/> (the first line's baseline pen). Each glyph is a self-contained
    /// <see cref="ResetPoint"/> + transform + <see cref="Glyph"/> segment, so a whole string is a multi-segment run the
    /// caller wraps in one <see cref="BeginInstance"/>/<see cref="EndInstance"/> with a bound covering the block. The
    /// atlas must be uploaded to the engine (<c>Puck.SdfVm.SdfWorldEngine.SetGlyphAtlas</c>) for the letters to resolve;
    /// unbound, each cell renders as its conservative box.</summary>
    /// <param name="atlas">The font atlas providing glyph geometry, metrics, and per-glyph atlas rectangles.</param>
    /// <param name="text">The string to lay out (line feeds break lines; unmapped code points are skipped).</param>
    /// <param name="origin">The pen origin — the first line's baseline, left edge. World space, or the dynamic slot's
    /// local space when <paramref name="dynamicSlot"/> is supplied.</param>
    /// <param name="right">The unit axis local +X (advance direction) maps to, in the same space as <paramref name="origin"/>.</param>
    /// <param name="up">The unit axis local +Y (ascent direction) maps to; the glyphs extrude along right×up.</param>
    /// <param name="worldEmHeight">The world height of one em — the text's world scale.</param>
    /// <param name="material">The material id the letters shade with.</param>
    /// <param name="blend">The blend against the field accumulated so far (Subtraction engraves the text).</param>
    /// <param name="extrudeHalfDepth">The half-depth each glyph extrudes along the plane normal.</param>
    /// <param name="smooth">The smooth/chamfer radius for a smooth/chamfer <paramref name="blend"/>.</param>
    /// <param name="layout">The layout options (wrapping, alignment, tracking, line spacing) in the run's scaled
    /// units; <see langword="null"/> = <see cref="TextLayoutOptions.Default"/>.</param>
    /// <param name="dynamicSlot">A dynamic-transform slot each glyph's chain rides (<see cref="TransformDynamic"/>
    /// after its <see cref="ResetPoint"/>), so the whole run follows the slot's per-frame pose;
    /// <see langword="null"/> = a static run in world space.</param>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> or <paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="worldEmHeight"/> is not finite and greater than
    /// zero, <paramref name="origin"/> or <paramref name="extrudeHalfDepth"/> is not finite, <paramref name="right"/>
    /// and <paramref name="up"/> do not span a plane, or <paramref name="blend"/> is not a defined
    /// <see cref="SdfBlendOp"/>.</exception>
    public SdfProgramBuilder Text(FontAtlas atlas, string text, Vector3 origin, Vector3 right, Vector3 up, float worldEmHeight, int material, SdfBlendOp blend = SdfBlendOp.Union, float extrudeHalfDepth = 0.1f, float smooth = 0f, TextLayoutOptions? layout = null, int? dynamicSlot = null) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(text);

        // The pre-existing check missed NaN and +infinity: `NaN <= 0f` and `infinity <= 0f` are both false, so a
        // non-finite em height passed and divided into every glyph's world-per-texel scale.
        RequirePositive(
            value: worldEmHeight,
            paramName: nameof(worldEmHeight),
            subject: "A text world em height"
        );
        RequireFinite(
            value: origin,
            paramName: nameof(origin),
            subject: "A text origin"
        );
        RequireDirection(
            value: right,
            paramName: nameof(right),
            subject: "A text right axis"
        );
        RequireDirection(
            value: up,
            paramName: nameof(up),
            subject: "A text up axis"
        );
        RequireFinite(
            value: extrudeHalfDepth,
            paramName: nameof(extrudeHalfDepth),
            subject: "A text extrude half-depth"
        );

        // Uniform world-per-texel (atlas.Size = pixels per em): every glyph derives BOTH half-extents from it, so the
        // sampled field stays 1-Lipschitz (factor 1). distanceScale rides the same factor.
        var worldPerTexel = (worldEmHeight / atlas.Size);
        var distanceScale = (atlas.DistanceRange * worldPerTexel);
        // Local (right, up, forward=right×up) → world: the rotation whose rows are the basis (System.Numerics'
        // row-vector Transform), so Rotate places each glyph's authored local XY onto the text plane.
        var unitRight = Vector3.Normalize(value: right);
        var unitUp = Vector3.Normalize(value: up);

        // Two individually valid axes can still be parallel, and their cross product is then zero — normalizing it
        // would put NaN into the orientation quaternion every glyph rides.
        var forward = Vector3.Normalize(value: Vector3.Cross(
            vector1: unitRight,
            vector2: unitUp
        ));

        if (
            !float.IsFinite(f: forward.X) ||
            !float.IsFinite(f: forward.Y) ||
            !float.IsFinite(f: forward.Z)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(up),
                message: "A text right and up axis must not be parallel — they span the plane the glyphs are laid out on."
            );
        }

        var orientation = Quaternion.CreateFromRotationMatrix(matrix: new Matrix4x4(
            m11: unitRight.X,
            m12: unitRight.Y,
            m13: unitRight.Z,
            m14: 0f,
            m21: unitUp.X,
            m22: unitUp.Y,
            m23: unitUp.Z,
            m24: 0f,
            m31: forward.X,
            m32: forward.Y,
            m33: forward.Z,
            m34: 0f,
            m41: 0f,
            m42: 0f,
            m43: 0f,
            m44: 1f
        ));
        var laidOut = new TextLayout().Layout(
            atlas: atlas,
            options: (layout ?? TextLayoutOptions.Default),
            text: text,
            scale: worldEmHeight
        );
        var atlasWidth = ((float)atlas.Width);
        var atlasHeight = ((float)atlas.Height);

        foreach (var placement in laidOut.Placements) {
            var atlasBounds = placement.AtlasBounds;
            var planeBounds = placement.PlaneBounds;
            // Uniform half-extents from the atlas cell's texel size; the cell CENTRE from the laid-out plane bounds (the
            // pen already placed it in the block). The two agree up to the padded margin, which is empty field.
            var halfWidth = ((0.5f * (atlasBounds.Right - atlasBounds.Left)) * worldPerTexel);
            var halfHeight = ((0.5f * (atlasBounds.Bottom - atlasBounds.Top)) * worldPerTexel);
            var centre2D = new Vector2(
                x: (0.5f * (planeBounds.Left + planeBounds.Right)),
                y: (0.5f * (planeBounds.Bottom + planeBounds.Top))
            );
            var worldCentre = ((origin + (unitRight * centre2D.X)) + (unitUp * centre2D.Y));
            // Local (-hw,-hh) is the cell's bottom-left → atlas (uMin, vBottom = the LARGER texel row, top-down); local
            // (+hw,+hh) is top-right → (uMax, vTop). The lerp in the shader maps local→uv along this diagonal.
            var uvBottomLeft = new Vector2(
                x: (atlasBounds.Left / atlasWidth),
                y: (atlasBounds.Bottom / atlasHeight)
            );
            var uvTopRight = new Vector2(
                x: (atlasBounds.Right / atlasWidth),
                y: (atlasBounds.Top / atlasHeight)
            );

            var chain = ResetPoint();

            if (dynamicSlot is { } slot) {
                chain = chain.TransformDynamic(slot: slot);
            }

            _ = chain
                .Translate(offset: worldCentre)
                .Rotate(rotation: orientation)
                .Glyph(
                blend: blend,
                distanceScale: distanceScale,
                extrudeHalfDepth: extrudeHalfDepth,
                halfHeight: halfHeight,
                halfWidth: halfWidth,
                material: material,
                smooth: smooth,
                uvBottomLeft: uvBottomLeft,
                uvTopRight: uvTopRight
            );
        }

        return this;
    }
    public SdfProgramBuilder Torus(float majorRadius, float minorRadius, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // The decoder is `length(float2(length(p.xz) - major, p.y)) - minor`: both are radii of the revolved circle,
        // and TryGetLocalBound packs MathF.Abs of each, so a negative one both mis-shapes the ring and desynchronizes
        // the shape from its own cull bound.
        RequireNonNegative(
            value: majorRadius,
            paramName: nameof(majorRadius),
            subject: "A torus major radius"
        );
        RequireNonNegative(
            value: minorRadius,
            paramName: nameof(minorRadius),
            subject: "A torus minor radius"
        );

        // Both radii are individually finite, but SdfProgram.TryGetLocalBound's Torus cull bound is their SUM (the
        // ring's farthest reach from the local origin) — two radii each well under float.MaxValue can still sum past
        // it into +Infinity, handing the packer (and any segment it merges with) an infinite bound that was never
        // authored. Refuse here, at the shape that owns the radii, rather than at the analysis pass that discovers it.
        var boundRadius = (majorRadius + minorRadius);

        if (!float.IsFinite(f: boundRadius)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(minorRadius),
                message: $"A torus's derived bound radius (majorRadius {majorRadius} plus minorRadius {minorRadius}) is not finite. Narrow one or both radii."
            );
        }

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
    /// <summary>Adds an isosceles trapezoid (exact isosceles-trapezoid 2D SDF) lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a
    /// keystone/wedge prism, <see cref="SdfLift.Revolve"/> a frustum/lampshade/cup. Exact and 1-Lipschitz. KEEP IN
    /// SYNC with sdfTrapezoidSolid in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="bottomHalfWidth">Half-width of the bottom edge (at local −Y).</param>
    /// <param name="topHalfWidth">Half-width of the top edge (at local +Y).</param>
    /// <param name="halfHeight">Half-height of the trapezoid.</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not finite, the derived lifted bound radius (see
    /// remarks) is not finite, <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined
    /// <see cref="SdfLift"/>, or <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder Trapezoid(float bottomHalfWidth, float topHalfWidth, float halfHeight, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Signs are absorbed (MathF.Abs on all three half-extents, MathF.Max(0) on the lift).
        RequireFinite(
            value: bottomHalfWidth,
            paramName: nameof(bottomHalfWidth),
            subject: "A trapezoid bottom half-width"
        );
        RequireFinite(
            value: topHalfWidth,
            paramName: nameof(topHalfWidth),
            subject: "A trapezoid top half-width"
        );
        RequireFinite(
            value: halfHeight,
            paramName: nameof(halfHeight),
            subject: "A trapezoid half-height"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var bottomAbs = MathF.Abs(x: bottomHalfWidth);
        var topAbs = MathF.Abs(x: topHalfWidth);
        var heightAbs = MathF.Abs(x: halfHeight);
        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );
        var radius2D = MathF.Max(
            x: new Vector2(
                x: bottomAbs,
                y: heightAbs
            ).Length(),
            y: new Vector2(
                x: topAbs,
                y: heightAbs
            ).Length()
        );

        RequireFiniteLiftedReach(
            lift: lift,
            liftAmount: clampedLift,
            radius2D: radius2D,
            shapeName: "trapezoid"
        );

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
            dimensions: new Vector4(
                w: clampedLift,
                x: bottomAbs,
                y: topAbs,
                z: heightAbs
            ),
            material: material,
            shape: SdfShapeType.Trapezoid,
            smooth: smooth
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
    /// <summary>Adds a vesica (lens) — the intersection of two spheres of radius <paramref name="radius"/> whose centers are
    /// 2·<paramref name="halfSeparation"/> apart — revolved into a 3D lens pointed along ±Y (a disc of radius
    /// radius−halfSeparation in XZ). <paramref name="halfSeparation"/> is clamped below <paramref name="radius"/> so
    /// the tip half-height √(r²−d²) is real; it is host-baked (skips the per-eval sqrt) — KEEP IN SYNC with sdfVesica
    /// in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="radius">The two generating spheres' radius.</param>
    /// <param name="halfSeparation">Half the distance between their centres (clamped below <paramref name="radius"/>).</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> or <paramref name="halfSeparation"/> is
    /// not finite, the derived tip half-height (see remarks) is not finite, <paramref name="material"/> is negative,
    /// <paramref name="blend"/> is not a defined <see cref="SdfBlendOp"/>, or <paramref name="smooth"/> is not
    /// finite.</exception>
    public SdfProgramBuilder Vesica(float radius, float halfSeparation, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        // Both signs are absorbed by the MathF.Abs pair below, so only finiteness is refused.
        RequireFinite(
            value: radius,
            paramName: nameof(radius),
            subject: "A vesica radius"
        );
        RequireFinite(
            value: halfSeparation,
            paramName: nameof(halfSeparation),
            subject: "A vesica half-separation"
        );

        var r = MathF.Abs(x: radius);
        var d = MathF.Min(
            x: MathF.Abs(x: halfSeparation),
            y: (r * 0.9999f)
        ); // d < r keeps b = √(r²−d²) real and positive
        var b = MathF.Sqrt(x: ((r * r) - (d * d)));

        // r and d are each individually finite, but r*r (and d*d) can overflow past float.MaxValue for a large enough
        // radius even though r itself does not — at radius == halfSeparation == float.MaxValue both squares overflow
        // to +Infinity and their difference is +Infinity − +Infinity = NaN, which sqrt propagates. b is HOST-BAKED
        // straight into Data0.z (the shape's own tip half-height) AND is what SdfProgram.TryGetLocalBound reads back
        // as part of the shape's cull radius — refuse rather than clamp, the same overflow class RoundCone's slope
        // check exists for.
        if (!float.IsFinite(f: b)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(radius),
                message: $"A vesica's derived tip half-height (from radius {radius} and half-separation {halfSeparation}) is not finite. Narrow the radius or the half-separation."
            );
        }

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
