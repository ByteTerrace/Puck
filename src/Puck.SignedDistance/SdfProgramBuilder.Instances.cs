using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
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
            throw new SdfProgramCapacityException(
                capacity: "materials",
                limit: ScreenMaterialId,
                message: $"A program's composed material palette may declare at most {ScreenMaterialId} materials (ScreenMaterialId) — material index {ScreenMaterialId} would collide with the reserved screen-material sentinel range. Register fewer materials."
            );
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

        if (m_fieldScope is not null) {
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
            Active: m_openInstanceActive
        ));

        m_openInstanceFirst = -1;

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
