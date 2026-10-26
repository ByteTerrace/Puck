using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    private bool m_openInstanceBakesSeparately;
    /// <summary>Reads a registered material while composing local material frames.</summary>
    public SdfMaterial MaterialAt(int index) => m_materials[index];
    /// <summary>Adds a material to the program palette.</summary>
    /// <param name="material">The material to add.</param>
    /// <returns>The zero-based material identifier used by shape instructions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A material component or layer is invalid.</exception>
    /// <exception cref="InvalidOperationException">The composed palette already holds <see cref="ScreenMaterialId"/>
    /// materials (see the ceiling check below).</exception>
    public int AddMaterial(SdfMaterial material) {
        // Packed shading inputs retain their authored values; admission rejects invalid domains before either
        // the view or indirect transport can propagate a non-finite or negative color into its output.
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
        RequireUnitRange(
            value: material.Roughness,
            paramName: nameof(material),
            subject: "A material roughness"
        );
        RequireUnitRange(
            value: material.Sheen,
            paramName: nameof(material),
            subject: "A material sheen strength"
        );
        RequireUnitRange(
            value: material.Metal,
            paramName: nameof(material),
            subject: "A material metalness"
        );
        RequireUnitRange(
            value: material.Coat,
            paramName: nameof(material),
            subject: "A material coat strength"
        );
        RequireUnitRange(
            value: material.Wrap,
            paramName: nameof(material),
            subject: "A material wrap-lighting share"
        );
        RequireUnitRange(
            value: material.Soften,
            paramName: nameof(material),
            subject: "A material shading-normal soften"
        );
        RequireNonNegative(
            value: material.Fill,
            paramName: nameof(material),
            subject: "A material fill tint"
        );
        RequireNonNegative(value: (material.Bleed ?? Vector3.One), paramName: nameof(material), subject: "A material bleed tint");
        RequireNonNegative(value: material.Receive, paramName: nameof(material), subject: "A material indirect receive gain");
        if (!SdfMaterial.IsEmissiveLane(lane: material.EmissiveLane)) {
            throw new ArgumentOutOfRangeException(
                nameof(material),
                $"A material emissive lane must name a render lane in [0, 3]; got {material.EmissiveLane}."
            );
        }
        if (!SdfMaterialLayers.IsValid(
            inset: material.Inset,
            weathering: material.Weathering,
            paints: material.Paints
        )) {
            throw new ArgumentOutOfRangeException(
                nameof(material),
                $"Invalid inset, weathering or paint layer: inset {material.Inset}, weathering {material.Weathering}, paints [{string.Join(separator: ", ", values: (material.Paints ?? []))}]."
            );
        }
        if (!material.IsGlassValid) {
            throw new ArgumentOutOfRangeException(
                nameof(material),
                $"A glass material needs a finite, positive depth, an index of refraction in [{SdfGlass.MinIor}, {SdfGlass.MaxIor}] and an albedo (its tint) in [0, 1]; got {material.Glass} with albedo {material.Albedo}."
            );
        }
        if (material.Paints is { Count: > SdfProgram.MaxPaintsPerMaterial } paints) {
            throw new ArgumentOutOfRangeException(
                nameof(material),
                $"A material paints at most {SdfProgram.MaxPaintsPerMaterial} paints; got {paints.Count}."
            );
        }

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
            throw new SdfProgramCapacityException(
                capacity: "materials",
                limit: ScreenMaterialId,
                message: $"A program's composed material palette may declare at most {ScreenMaterialId} materials (ScreenMaterialId) — material index {ScreenMaterialId} would collide with the reserved screen-material sentinel range. Register fewer materials."
            );
        }

        m_materials.Add(item: material);

        return (m_materials.Count - 1);
    }

    /// <summary>Gets the number of instances closed so far — the count <see cref="Build"/> would report in
    /// <see cref="SdfProgram.Instances"/>; an instance still open (see <see cref="BeginInstance"/>) is not
    /// counted until <see cref="EndInstance"/> closes it.</summary>
    public int InstanceCount => m_instances.Count;
    /// <summary>Gets the number of materials registered so far — the count <see cref="Build"/> would report in
    /// <see cref="SdfProgram.MaterialCount"/>.</summary>
    public int MaterialCount => m_materials.Count;
    /// <summary>Gets the number of instructions emitted so far, before <see cref="Build"/> shares or compiles any.</summary>
    public int InstructionCount => m_instructions.Count;
    /// <summary>Gets the number of convex-polygon profiles emitted so far.</summary>
    public int ConvexPolygonCount => m_convexPolygonProfiles.Count;

    /// <summary>Opens a static per-object instance: every instruction until the matching <see cref="EndInstance"/>
    /// belongs to it, bounded by <paramref name="boundCenter"/>/<paramref name="boundRadius"/> (a world-space bounding
    /// sphere, <see cref="SdfInstanceRange"/>). Instructions declared outside any instance are the world set: always
    /// evaluated, unmasked (floors/walls/unbounded shapes).</summary>
    /// <param name="boundCenter">The instance's world-space bounding-sphere center.</param>
    /// <param name="boundRadius">The instance's world-space bounding-sphere radius.</param>
    /// <param name="bakesWithPrevious">Whether the instance bakes into one mesh with the instance declared just before
    /// it, when that one is static too (<see cref="SdfInstanceRange.BakesWithPrevious"/>): the shapes of one rigid
    /// copy, each with its own tight bound, baked as their union.</param>
    /// <param name="detail">The finest feature, in world units, the instance carries that its bound does not imply; zero
    /// for none, and a negative or non-finite value reads as zero (<see cref="SdfInstanceRange.Detail"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="boundCenter"/> is not finite, or
    /// <paramref name="boundRadius"/> is not finite and non-negative.</exception>
    /// <exception cref="InvalidOperationException">An instance is already open.</exception>
    public SdfProgramBuilder BeginInstance(Vector3 boundCenter, float boundRadius, bool bakesWithPrevious = false, float detail = 0f) {
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
        m_openInstanceBakesWithPrevious = bakesWithPrevious;
        m_openInstanceDetail = ((float.IsFinite(f: detail) && (detail > 0f)) ? detail : 0f);

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
    /// <param name="active">Whether the instance contributes to the field. Pass <see langword="false"/> to park a
    /// reserved-pool slot that carries no live content this rebuild (the classic "hidden below the floor" placeholder):
    /// the slot still exists (so the pool's live emission always fits the once-sized buffers), but the walk skips it
    /// with a single branch (<see cref="SdfInstanceRange.Active"/>), so a parked slot costs almost nothing.</param>
    /// <param name="detail">The finest feature, in world units, the instance carries that its bound does not imply; zero
    /// for none, and a negative or non-finite value reads as zero (<see cref="SdfInstanceRange.Detail"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside the dynamic-transform slot
    /// range, <paramref name="boundOffset"/> is not finite, or <paramref name="boundRadius"/> is not finite and
    /// non-negative.</exception>
    /// <exception cref="InvalidOperationException">An instance is already open.</exception>
    /// <param name="separateBake">Keep this geometry's lattice separate from other instances on its motion slot.</param>
    public SdfProgramBuilder BeginInstanceDynamic(int slot, Vector3 boundOffset, float boundRadius, bool active = true, float detail = 0f, bool separateBake = false) {
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
        m_openInstanceDetail = ((float.IsFinite(f: detail) && (detail > 0f)) ? detail : 0f);
        m_openInstanceBakesSeparately = separateBake;

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
    /// <summary>Closes the currently open instance (see <see cref="BeginInstance"/>/<see cref="BeginInstanceDynamic"/>).</summary>
    /// <exception cref="InvalidOperationException">No instance is open.</exception>
    public SdfProgramBuilder EndInstance() {
        if (m_openInstanceFirst < 0) {
            throw new InvalidOperationException(message: "EndInstance was called with no open instance (unbalanced Begin/EndInstance).");
        }

        if (m_fieldScopes is { Count: > 0 }) {
            throw new InvalidOperationException(message: "EndInstance was called with a field scope still open (PushField without its PopField). Close every scope opened inside the instance before EndInstance.");
        }

        if (m_instances.Count >= MaxInstances) {
            throw new SdfProgramCapacityException(
                capacity: "instances",
                limit: MaxInstances,
                message: $"A program may declare at most {MaxInstances} instances."
            );
        }

        m_instances.Add(item: new SdfInstanceRange(
            First: m_openInstanceFirst,
            End: m_instructions.Count,
            IsDynamic: m_openInstanceIsDynamic,
            Center: m_openInstanceCenter,
            Radius: m_openInstanceRadius,
            Slot: m_openInstanceSlot,
            Active: m_openInstanceActive,
            BakesWithPrevious: m_openInstanceBakesWithPrevious,
            Detail: m_openInstanceDetail
        ) { BakesSeparately = m_openInstanceBakesSeparately });

        m_openInstanceFirst = -1;
        m_openInstanceBakesSeparately = false;

        return this;
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
}
