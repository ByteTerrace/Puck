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
    public const int MaxScreenSurfaces = 4;

    private readonly List<SdfInstanceRange> m_instances = [];
    private readonly List<SdfInstruction> m_instructions = [];
    private readonly List<SdfMaterial> m_materials = [];
    private readonly List<SdfScreenSurface> m_screenSurfaces = [];
    private int m_openInstanceFirst = -1;
    private bool m_openInstanceIsDynamic;
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
    /// <exception cref="InvalidOperationException">An instance is already open.</exception>
    public SdfProgramBuilder BeginInstanceDynamic(int slot, Vector3 boundOffset, float boundRadius) {
        BeginInstanceCore(isDynamic: true, center: boundOffset, radius: boundRadius, slot: slot);

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
            Slot: m_openInstanceSlot
        ));

        m_openInstanceFirst = -1;

        return this;
    }
    private void BeginInstanceCore(bool isDynamic, Vector3 center, float radius, int slot) {
        if (isDynamic && ((slot < 0) || (slot > SdfProgram.MaxDynamicTransformSlot))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot), message: $"Dynamic instance slots must be in [0, {SdfProgram.MaxDynamicTransformSlot}].");
        }

        if (m_openInstanceFirst >= 0) {
            throw new InvalidOperationException(message: "BeginInstance/BeginInstanceDynamic was called with an instance already open (nesting is not supported).");
        }

        m_openInstanceFirst = m_instructions.Count;
        m_openInstanceIsDynamic = isDynamic;
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
        return Transform(
            data0: new Vector4(
                value: scale,
                w: 0f
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
    /// <summary>Bends the YZ plane by <paramref name="rate"/> · y radians (the legacy quirk: keyed on Y, like
    /// <see cref="BendY"/>, so old creature content re-ports faithfully).</summary>
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
        return Transform(
            data0: new Vector4(
                value: spacing,
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
    public SdfProgramBuilder SymmetryX() {
        return Transform(op: SdfOp.SymmetryX);
    }
    public SdfProgramBuilder SymmetryY() {
        return Transform(op: SdfOp.SymmetryY);
    }
    public SdfProgramBuilder SymmetryZ() {
        return Transform(op: SdfOp.SymmetryZ);
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
    /// <param name="screenIndex">The screen source slot (0..3) this surface samples.</param>
    /// <param name="blend">The blend operator against the field accumulated so far.</param>
    /// <param name="smooth">The smooth-blend radius (meaningful only for a smooth <paramref name="blend"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..3</c>, or this
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
    /// <param name="screenIndex">The screen source slot (0..3) this surface samples.</param>
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
