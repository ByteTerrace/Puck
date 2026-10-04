using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.World.Authoring;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Text;

namespace Puck.World.Client;

/// <summary>
/// Emits the world's STATIC placements into the program under construction — each materialized pattern or reflected
/// copy is a static <see cref="SdfProgramBuilder.BeginInstance"/> whose shapes replay the referenced creation's shape
/// list with the full placement transform baked into every shape's own segment. Animated placements (framed creations)
/// are NOT emitted here — they ride <see cref="WorldStampPool"/>'s reserved dynamic pool. A prototype's inline mesh
/// (<see cref="WorldPrototype.Mesh"/>) becomes one <see cref="SdfMeshDraw"/> per static placement instance.
/// </summary>
/// <remarks>Text runs share the same instance and transform as their creation and resolve through the world's packed
/// font catalog. They count against <see cref="CreationDocument.StampShapeCount"/> like every other emitted shape.</remarks>
public static class WorldPlacementStamper {
    // The instance-bound slack past a creation's own reach (a contract of the tile cull, not a policy: a too-tight
    // bound CLIPS real geometry at masked tile edges; a fat one only costs a rare extra evaluation).
    private const float PlacementBoundMargin = 0.4f;
    // Probe instances are spaced far apart so the program's segment-merge pass can never collapse consecutive probe
    // segments — a merged probe would under-reserve the segment directory a real scattered world needs (contract).
    private const float ProbeSpread = 100f;

    // Each inline prototype mesh's engine-frame SdfMesh, keyed by the immutable mesh record (MeshOf).
    private static readonly ConditionalWeakTable<WorldPrototypeMesh, SdfMesh> Meshes = new();

    /// <summary>Registers a creation's palette (16-slot clamp) with an optional tint lerp, returning program-relative
    /// material ids indexed like the creation's own palette slots.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="colors">The colors the build bakes, which a state-bound palette color resolves through.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="tint">The albedo tint (color + blend), or <see langword="null"/>.</param>
    internal static int[] RegisterPalette(SdfProgramBuilder builder, WorldBakedColors colors, CreationDocument document, (Vector3 Color, float Blend)? tint) {
        var ids = new int[PaletteLength(document: document)];

        FillPalette(
            builder: builder,
            colors: colors,
            document: document,
            ids: ids,
            tint: tint
        );

        return ids;
    }
    // The material ids a creation's palette registers: its slots up to the 16-slot clamp, and one when it has none.
    internal static int PaletteLength(CreationDocument document) =>
        Math.Max(
            val1: Math.Min(
                val1: (document.Palette?.Count ?? 0),
                val2: CreationDocument.PaletteSize
            ),
            val2: 1
        );
    // Registers a creation's palette into ids, which holds PaletteLength(document) entries.
    internal static void FillPalette(SdfProgramBuilder builder, WorldBakedColors colors, CreationDocument document, (Vector3 Color, float Blend)? tint, Span<int> ids) {
        var resolveLayerColor = colors.LayerColor;
        var palette = (document.Palette ?? []);
        var count = Math.Min(
            val1: palette.Count,
            val2: CreationDocument.PaletteSize
        );

        for (var index = 0; (index < ids.Length); index++) {
            var entry = ((index < count)
                ? palette[index]
                : null
            );
            var albedo = colors.Resolve(
                fallback: new Vector3(value: 0.7f),
                value: entry?.Color
            );

            if (tint is { } applied) {
                albedo = Vector3.Lerp(
                    amount: applied.Blend,
                    value1: albedo,
                    value2: applied.Color
                );
            }

            ids[index] = builder.AddMaterial(material: new SdfMaterial(
                Albedo: albedo,
                Emissive: (entry?.Emissive ?? 0f),
                Roughness: (entry?.Roughness ?? SdfMaterial.DefaultRoughness),
                Sheen: (entry?.Sheen ?? 0f),
                Specular: (entry?.Specular ?? 0f),
                Metal: (entry?.Metal ?? 0f),
                Coat: (entry?.Coat ?? 0f),
                Weathering: entry?.Weathering?.ToWeathering(resolve: resolveLayerColor),
                Wrap: (entry?.Wrap ?? 0f),
                Soften: (entry?.Soften ?? 0f),
                Bounce: colors.Resolve(
                    fallback: Vector3.Zero,
                    value: entry?.Bounce
                ),
                Inset: entry?.Inset?.ToInset(resolve: resolveLayerColor)
            ));
        }
    }

    // A static instance bakes the placement frame (and, for a parented volume, the parent shape's rest pose) into
    // the volume itself: no slot moves it. A mirrored copy keeps the unmirrored frame — a flow column is
    // symmetric about its own axis, so only its offset would differ, and the mirror plane is not applied here.
    private static void AppendStaticVolumes(CreationDocument creation, Vector3 origin, Quaternion rotation, float scale, ICollection<SdfVolume>? volumes) {
        if (
            (volumes is null) ||
            (creation.Volumes is not { Count: > 0 } authored)
        ) {
            return;
        }

        var shapes = (creation.Shapes ?? []);

        foreach (var volume in authored) {
            if (!volume.Enabled) {
                continue;
            }
            if (volumes.Count >= SdfProgramBuilder.MaxVolumes) {
                return;
            }

            var frameOrigin = origin;
            var frameRotation = rotation;

            if (volume.Parent is { } parent) {
                foreach (var shape in shapes) {
                    if (string.Equals(
                        a: shape.Name?.Value,
                        b: parent,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        frameOrigin = (origin + Vector3.Transform(
                            rotation: rotation,
                            value: (shape.Position.Value * scale)
                        ));
                        frameRotation = Quaternion.Normalize(value: (rotation * shape.Rotation.Value));

                        break;
                    }
                }
            }

            volumes.Add(item: volume.ToVolume(
                dynamicSlot: SdfProgram.NoDynamicTransformSlot,
                origin: frameOrigin,
                rotation: frameRotation,
                scale: scale
            ));
        }
    }
    // A static instance's mesh draw, appended when the placement carries a mesh and the caller collects draws. Its
    // identity is its scope, its placement and its order among the placement's draws, which a rebuild that keeps the
    // placement keeps, so an edited placement's draw reads its edit as motion.
    private static void AppendMeshDraw(SdfMesh? mesh, int material, Vector3 origin, Quaternion rotation, float scale, Vector3? reflectionNormal, ICollection<SdfMeshDraw>? meshDraws, string? scope, string placement, int firstMesh, bool fieldBacked, SdfMeshLod? lod = null, SdfMeshImpostor? impostor = null) {
        if ((mesh is null) || (meshDraws is null)) {
            return;
        }

        meshDraws.Add(item: MeshDrawOf(
            identity: new StaticMeshIdentity(
                Ordinal: (meshDraws.Count - firstMesh),
                Placement: placement,
                Scope: scope
            ),
            material: material,
            mesh: mesh,
            origin: origin,
            reflectionNormal: reflectionNormal,
            rotation: rotation,
            scale: scale
        ) with {
            FieldBacked = fieldBacked,
            Impostor = impostor,
            Lod = lod,
        });
    }

    private readonly record struct StaticMeshIdentity(string? Scope, string Placement, int Ordinal);

    /// <summary>Poses a prototype's mesh as a draw: its engine-frame triangles under a uniform scale, an optional mirror (a
    /// plane through the origin in the local frame, as the shapes reflect), then the rotation and the origin, composed in
    /// the row-vector convention <see cref="SdfMeshDraw"/> carries. A static placement and a stamp-pool root pose it
    /// alike.</summary>
    /// <param name="mesh">The prototype's mesh (<see cref="MeshOf"/>).</param>
    /// <param name="material">The engine material the mesh's palette entry registered as.</param>
    /// <param name="origin">The world-space origin.</param>
    /// <param name="rotation">The orientation.</param>
    /// <param name="scale">The uniform scale.</param>
    /// <param name="identity">The draw's identity across frames (<see cref="SdfMeshDraw.Identity"/>).</param>
    /// <param name="reflectionNormal">The mirror plane's normal, or <see langword="null"/> for none.</param>
    /// <returns>The draw.</returns>
    internal static SdfMeshDraw MeshDrawOf(SdfMesh mesh, int material, Vector3 origin, Quaternion rotation, float scale, object identity, Vector3? reflectionNormal = null) {
        var local = Matrix4x4.CreateScale(scale: scale);

        if (reflectionNormal is { } normal) {
            local *= Matrix4x4.CreateReflection(value: new Plane(
                d: 0f,
                normal: Vector3.Normalize(value: normal)
            ));
        }

        return new SdfMeshDraw(
            Identity: identity,
            Material: material,
            Mesh: mesh,
            ObjectToWorld: ((local * Matrix4x4.CreateFromQuaternion(quaternion: rotation)) * Matrix4x4.CreateTranslation(position: origin))
        );
    }
    /// <summary>The engine-frame <see cref="SdfMesh"/> of an inline prototype mesh, converted once: prototype rows are
    /// replaced, never mutated, so every placement, stamp and rebuild of one row shares one mesh.</summary>
    /// <param name="mesh">The prototype's inline mesh.</param>
    /// <returns>The shared mesh.</returns>
    internal static SdfMesh MeshOf(WorldPrototypeMesh mesh) => Meshes.GetValue(
        createValueCallback: static authored => new SdfMesh(
            indices: authored.Indices.ToArray(),
            positions: authored.EngineVertices.ToArray()
        ),
        key: mesh
    );

    // Emits the creation's shapes, EACH its own segment carrying the FULL placement prefix — the shader splits the
    // stream at each ResetPoint and a segment's transforms are local to it, so a shared prefix segment would be dead.
    // Uniform placement scale commutes with the per-shape rotations (shear-free).
    private static void EmitPlacedShapes(SdfProgramBuilder builder, CreationDocument creation, int[] paletteIds, WorldPlacement placement, Vector3 placementOrigin, Quaternion placementRotation, Vector3? reflectionNormal, bool inScope) {
        CreationStampEmitter.Emit(
            builder: builder,
            document: creation,
            inScope: inScope,
            transform: new CreationStampTransform(
                Origin: placementOrigin,
                Rotation: placementRotation,
                Scale: placement.Scale,
                ReflectionNormal: reflectionNormal
            ),
            materialFor: shape => paletteIds[Math.Clamp(
                value: (shape.Material ?? 0),
                max: (paletteIds.Length - 1),
                min: 0
            )]
        );
    }
    private static void EmitPlacement(SdfProgramBuilder builder, CreationDocument creation, WorldDefinition definition, int[] paletteIds, WorldPlacement placement, PackedFontAtlasCatalog? textCatalog, ulong worldSeed, ICollection<SdfVolume>? volumes, SdfMesh? mesh, int meshMaterial, ICollection<SdfMeshDraw>? meshDraws, string? meshScope, int firstMesh, WorldBakedDraw? baked = null) {
        var frame = WorldDefinitionRows.ResolvedFrame(
            definition: definition,
            placement: placement
        );
        // Laid out ONCE here (rather than once for the reach measure below plus once per pattern/scatter instance
        // inside the visitor's EmitText call) — TextLayout.Layout is a pure function of (atlas, text, scale,
        // options), all fixed for this whole EmitPlacement call, so every reader below shares this one result.
        var hasText = (
            (textCatalog is not null) &&
            (creation.TextRuns is { Count: > 0 })
        );
        var textLayouts = (hasText
            ? CreationStampEmitter.LayoutTextRuns(
                document: creation,
                fontFor: textCatalog!.Resolve,
                scale: placement.Scale
            )
            : null
        );
        // A creation whose parts carve each other (or that carries noise relief) is one SCOPED candidate against the
        // world field. A scope-free, text-free creation instead emits one TIGHT instance per shape — union-family
        // members mask bit-identically, and per-shape bounds keep a big creation (a tree's whole-canopy reach) from
        // masking every tile it merely overlaps.
        var scoped = (
            (creation.Shapes is { Count: > 0 }) &&
            CreationStampEmitter.RequiresScope(document: creation)
        );
        var reach = CreationStampEmitter.RenderReach(
            document: creation,
            scale: placement.Scale,
            // A method group, never a lambda over the catalog, which would allocate a closure on every placement.
            fontFor: (hasText
                ? textCatalog!.Resolve
                : null),
            textLayouts: textLayouts,
            // One scoped instance holds the whole creation, so its bound is the blends' composition: an unbounded
            // lattice a finite shape clips is as finite as that shape.
            composeBlends: scoped
        );
        var rotation = Quaternion.CreateFromAxisAngle(
            axis: Vector3.UnitY,
            angle: (frame.YawDegrees * (MathF.PI / 180f))
        );
        var perShape = (
            !scoped &&
            !hasText &&
            (creation.TextRuns is not { Count: > 0 }) &&
            (creation.Shapes is { Count: > 0 })
        );

        var visitor = new StaticInstanceVisitor(
            baked: baked,
            builder: builder,
            creation: creation,
            firstMesh: firstMesh,
            hasText: hasText,
            mesh: mesh,
            meshDraws: meshDraws,
            meshMaterial: meshMaterial,
            meshScope: meshScope,
            paletteIds: paletteIds,
            perShape: perShape,
            placement: placement,
            reach: reach,
            rotation: rotation,
            scoped: scoped,
            textCatalog: textCatalog,
            textLayouts: textLayouts,
            volumes: volumes
        );

        CreationStampLattice.ForEachInstance(
            origin: frame.Position,
            rotation: rotation,
            pattern: WorldPlacementStamp.PatternFor(placement: placement),
            sampledOffsets: WorldPlacementStamp.SampledOffsetsFor(
                placement: placement,
                worldSeed: worldSeed
            ),
            mirror: WorldPlacementStamp.MirrorFor(placement: placement),
            visitor: ref visitor
        );
    }

    // One static placement's instances: its volumes and mesh draws, then either one tight instance per shape or one
    // instance holding the whole creation. A placement drawing its bake draws the baked mesh, its palette's first
    // material the base its triangles' entries add to, and keeps its instances camera-hidden, so its field still casts shadows and occludes. A struct the
    // lattice walk calls, so a placement allocates no closure.
    private readonly struct StaticInstanceVisitor(WorldBakedDraw? baked, SdfProgramBuilder builder, CreationDocument creation, bool hasText, SdfMesh? mesh, ICollection<SdfMeshDraw>? meshDraws, string? meshScope, int firstMesh, int meshMaterial, int[] paletteIds, bool perShape, WorldPlacement placement, float reach, Quaternion rotation, bool scoped, PackedFontAtlasCatalog? textCatalog, TextLayoutResult[]? textLayouts, ICollection<SdfVolume>? volumes) : ICreationStampVisitor {
        public void Visit(CreationStampInstance instance) {
            AppendStaticVolumes(
                creation: creation,
                origin: instance.Origin,
                rotation: rotation,
                scale: placement.Scale,
                volumes: volumes
            );
            AppendMeshDraw(
                fieldBacked: false,
                firstMesh: firstMesh,
                material: meshMaterial,
                mesh: mesh,
                meshDraws: meshDraws,
                origin: instance.Origin,
                placement: placement.Id,
                reflectionNormal: instance.ReflectionNormal,
                rotation: rotation,
                scale: placement.Scale,
                scope: meshScope
            );
            AppendMeshDraw(
                fieldBacked: true,
                firstMesh: firstMesh,
                lod: ((baked?.Impostor is { } nearImpostor)
                    ? SdfMeshLod.ForImpostor(far: false, impostor: nearImpostor)
                    : null),
                material: paletteIds[0],
                mesh: baked?.Mesh,
                meshDraws: meshDraws,
                origin: instance.Origin,
                placement: placement.Id,
                reflectionNormal: instance.ReflectionNormal,
                rotation: rotation,
                scale: placement.Scale,
                scope: meshScope
            );
            AppendMeshDraw(
                fieldBacked: true,
                firstMesh: firstMesh,
                impostor: baked?.Impostor,
                lod: ((baked?.Impostor is { } farImpostor)
                    ? SdfMeshLod.ForImpostor(far: true, impostor: farImpostor)
                    : null),
                material: paletteIds[0],
                mesh: ((baked?.Impostor is null)
                    ? null
                    : SdfMeshCard.Mesh),
                meshDraws: meshDraws,
                origin: instance.Origin,
                placement: placement.Id,
                reflectionNormal: instance.ReflectionNormal,
                rotation: rotation,
                scale: placement.Scale,
                scope: meshScope
            );

            if (perShape) {
                var stampTransform = new CreationStampTransform(
                    Origin: instance.Origin,
                    Rotation: rotation,
                    Scale: placement.Scale,
                    ReflectionNormal: instance.ReflectionNormal
                );

                for (var shapeIndex = 0; (shapeIndex < creation.Shapes!.Count); shapeIndex++) {
                    var shape = creation.Shapes[shapeIndex];
                    // A fold's copies leave any shape-local sphere, so a domain-bearing shape keeps the
                    // whole-creation bound.
                    var (boundCenter, boundRadius) = ((shape.Domain is { Count: > 0 })
                        ? (instance.Origin, reach)
                        : CreationStampEmitter.ShapeStampBound(
                            document: creation,
                            shapeIndex: shapeIndex,
                            transform: stampTransform
                        )
                    );

                    _ = builder.BeginInstance(
                        boundCenter: boundCenter,
                        boundRadius: (boundRadius + PlacementBoundMargin),
                        cameraHidden: (baked is not null)
                    );
                    CreationStampEmitter.EmitShapeStamp(
                        builder: builder,
                        document: creation,
                        shapeIndex: shapeIndex,
                        transform: stampTransform,
                        material: paletteIds[Math.Clamp(
                            value: (shape.Material ?? 0),
                            max: (paletteIds.Length - 1),
                            min: 0
                        )],
                        paletteIds: paletteIds
                    );
                    _ = builder.EndInstance();
                }

                return;
            }

            _ = builder.BeginInstance(
                boundCenter: instance.Origin,
                boundRadius: (reach + PlacementBoundMargin),
                cameraHidden: (baked is not null)
            );
            if (scoped) {
                _ = builder.PushField(compose: SdfBlendOp.Union);
            }
            EmitPlacedShapes(
                builder: builder,
                creation: creation,
                inScope: scoped,
                paletteIds: paletteIds,
                placement: placement,
                placementOrigin: instance.Origin,
                placementRotation: rotation,
                reflectionNormal: instance.ReflectionNormal
            );
            if (hasText) {
                // A lambda cannot read a struct's fields, so a run's material reads the palette through a local.
                var ids = paletteIds;

                CreationStampEmitter.EmitText(
                    builder: builder,
                    document: creation,
                    transform: new CreationStampTransform(
                        Origin: instance.Origin,
                        Rotation: rotation,
                        Scale: placement.Scale,
                        ReflectionNormal: instance.ReflectionNormal
                    ),
                    fontFor: textCatalog!.Resolve,
                    materialFor: run => ids[Math.Clamp(
                        value: (run.Material ?? 0),
                        max: (ids.Length - 1),
                        min: 0
                    )],
                    textLayouts: textLayouts
                );
            }
            if (
                scoped &&
                (creation.Noise is { } noise)
            ) {
                CreationStampEmitter.EmitNoise(
                    builder: builder,
                    noise: noise,
                    transform: new CreationStampTransform(
                        Origin: instance.Origin,
                        Rotation: rotation,
                        Scale: placement.Scale,
                        ReflectionNormal: instance.ReflectionNormal
                    )
                );
            }
            if (scoped) {
                _ = builder.PopField();
            }
            _ = builder.EndInstance();
        }
    }

    /// <summary>Emits the construction probe's placement reservation: <paramref name="reservedCount"/> worst-case
    /// stamps — each a distinct full 16-slot palette plus <see cref="WorldPlacementPolicy.MaxShapesPerStamp"/> shapes
    /// carrying the densest legal per-shape chain — so any real
    /// static emission within the placement policy fits the once-sized buffers by construction. Never rendered.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="reservedCount">The reserved SCOPED stamp count (scoped/text-carrying boot placements + the
    /// authoring headroom).</param>
    /// <param name="reservedShapeInstances">The reserved per-SHAPE instance count (scope-free boot placements'
    /// copies × <see cref="CreationStampEmitter.PerCopyInstanceCount"/> — a panelled shape charges two chains for its
    /// one instance — plus MaxShapesPerStamp for each authoring-headroom copy; see <see cref="StaticStampReservation"/>).</param>
    public static void EmitProbe(SdfProgramBuilder builder, int reservedCount, int reservedShapeInstances = 0) {
        builder.ReservePathTables(shapeCount: checked(((reservedCount * WorldPlacementPolicy.MaxShapesPerStamp) + reservedShapeInstances)));
        for (var index = 0; (index < reservedCount); index++) {
            // Worst-case distinct materials: every reserved stamp references a DISTINCT creation with a full palette
            // (the per-id cache only relaxes this; probing as if every stamp were unique is the conservative bound).
            var paletteIds = new int[CreationDocument.PaletteSize];

            for (var slot = 0; (slot < CreationDocument.PaletteSize); slot++) {
                paletteIds[slot] = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.5f)));
            }

            var center = new Vector3(
                x: (index * ProbeSpread),
                y: 4f,
                z: 0f
            );

            _ = builder.BeginInstance(
                boundCenter: center,
                boundRadius: 12f
            );

            // Each shape charge reserves the chain a text-carrying, scope-free creation's shape emits with its own
            // field scope (CreationStampEmitter.EmitShapeChain: a dilate/onion/warp/cells shape, or a panelled shape
            // whose two charges together cover its plate chain, copy chain, and shape pair).
            for (var shape = 0; (shape < WorldPlacementPolicy.MaxShapesPerStamp); shape++) {
                _ = SdfSolidGeometry.AppendPrimitive(
                    chain: builder.ResetPoint()
                        .Translate(offset: center)
                        .Rotate(rotation: Quaternion.Identity)
                        .Scale(scale: Vector3.One)
                        .Translate(offset: Vector3.Zero)
                        .Rotate(rotation: Quaternion.Identity)
                        .PushField(compose: SdfBlendOp.Union)
                        .Scale(scale: Vector3.One),
                    type: SdfSolidPrimitive.Sphere,
                    material: paletteIds[(shape % CreationDocument.PaletteSize)]
                ).ResetPoint().Translate(offset: center).Rotate(rotation: Quaternion.Identity)
                    .CellDisplace(
                    amplitude: 0.1f,
                    frequency: 1f,
                    mode: SdfCellMode.F1,
                    randomness: 0.2f,
                    seed: 0u
                ).PopField();
            }

            _ = builder.EndInstance();
        }

        // The PER-SHAPE reservation: one instance per shape of every scope-free static stamp, each a single
        // full-modifier chain (the domain envelope mirrors ShapeDomainOps.ProbeWorstCase — every domain kind costs one
        // instruction, so four symmetries dominate any authored combination). Spread like the stamps so segment
        // merging can never under-reserve the directory.
        for (var index = 0; (index < reservedShapeInstances); index++) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.5f)));
            var center = new Vector3(
                x: (index * ProbeSpread),
                y: -4f,
                z: ProbeSpread
            );

            _ = builder.BeginInstance(
                boundCenter: center,
                boundRadius: 12f
            );

            var chain = builder
                .ResetPoint()
                .Translate(offset: center)
                .Rotate(rotation: Quaternion.Identity)
                .Scale(scale: Vector3.One);

            for (var op = 0; (op < ShapeDocument.MaxDomainOps); op++) {
                chain = chain.SymmetryPlane(normal: Vector3.UnitX);
            }

            // The per-shape field scope a dilate/onion/warp/cells shape opens around itself
            // (CreationStampEmitter.EmitShapeChain), with the cells relief it may carry, reserved for every shape since
            // any shape may author one.
            _ = SdfSolidGeometry.AppendPrimitive(
                chain: chain
                    .Translate(offset: Vector3.Zero)
                    .Rotate(rotation: Quaternion.Identity)
                    .PushField(compose: SdfBlendOp.Union),
                type: SdfSolidPrimitive.Sphere,
                material: material
            ).ResetPoint().Translate(offset: center).Rotate(rotation: Quaternion.Identity)
                .CellDisplace(
                amplitude: 0.1f,
                frequency: 1f,
                mode: SdfCellMode.F1,
                randomness: 0.2f,
                seed: 0u
            ).PopField();
            _ = builder.EndInstance();
        }
    }
    /// <summary>Emits every STATIC placement (animated rows skip — the animator owns them). Palettes register once per
    /// distinct untinted creation; a tinted stamp (selection amber / change shimmer) registers its own lerped palette
    /// (act-scale rare, never steady-state).</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="definition">The definition the rows belong to.</param>
    /// <param name="creations">The world's creation rows.</param>
    /// <param name="placements">The (possibly drag-composed) placement rows.</param>
    /// <param name="textCatalog">The packed world font catalog. Null omits creation text; local-world callers use
    /// null only when no catalog is declared, while remote projection callers currently have no transported font
    /// assets to resolve.</param>
    /// <param name="tintFor">Resolves a placement id's albedo tint (color + blend), or <see langword="null"/> untinted.</param>
    /// <param name="volumes">Receives each static placement instance's bounded volumes (<see cref="CreationDocument.Volumes"/>)
    /// baked into world space with no dynamic slot, or <see langword="null"/> when the caller renders none.</param>
    /// <param name="meshDraws">Receives one draw per static placement instance of a prototype that carries a mesh
    /// (<see cref="WorldPrototype.Mesh"/>), or <see langword="null"/> when the caller draws none.</param>
    /// <param name="colors">The colors the build bakes, which a state-bound palette color resolves through, or
    /// <see langword="null"/> to read them through a mirror of <paramref name="definition"/> built only when a bound
    /// palette color is emitted (<see cref="WorldBakedColors.Of"/>).</param>
    /// <param name="palettes">The owner's palettes, reused across its rebuilds so a warm emission allocates none, or
    /// <see langword="null"/> for palettes this emission keeps alone.</param>
    /// <param name="bakedFor">Returns a prototype's baked draw when the presentation draws its bake, or
    /// <see langword="null"/>: its untinted placements then draw that mesh, or its impostor when the view sees the
    /// placement small (<see cref="SdfMeshLod"/>), and keep their field camera-hidden so it still casts shadows and
    /// occludes. A creation carrying text or noise relief, which its bake does not hold, draws through
    /// its field. Collected only with <paramref name="meshDraws"/>.</param>
    /// <param name="picks">The presentation identity table populated alongside emission, or null.</param>
    /// <param name="meshScope">What distinguishes these placements' mesh draws from another emission's placements of the
    /// same ids (an adjacent world's projection), or <see langword="null"/> for the world's own; part of each draw's
    /// <see cref="SdfMeshDraw.Identity"/>.</param>
    public static void EmitStatic(SdfProgramBuilder builder, WorldDefinition definition, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<WorldPlacement> placements, PackedFontAtlasCatalog? textCatalog = null, Func<string, (Vector3 Color, float Blend)?>? tintFor = null, ICollection<SdfVolume>? volumes = null, ICollection<SdfMeshDraw>? meshDraws = null, WorldBakedColors? colors = null, WorldStaticPalettes? palettes = null, Func<string, WorldBakedDraw?>? bakedFor = null, WorldPickMapBuilder? picks = null, string? meshScope = null) {
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);
        var baked = (colors ?? WorldBakedColors.Of(definition: definition));
        var registered = (palettes ?? new WorldStaticPalettes());

        registered.Begin();

        WorldBootWork.Count(kind: WorldBootWork.ShapeBuilds);

        // Indexed rather than foreach over the list's interface, which would box an enumerator on every emission.
        for (var placementIndex = 0; (placementIndex < placements.Count); placementIndex++) {
            var placement = placements[placementIndex];

            if (
                (WorldDefinitionRows.FindCreation(
                creations: creations,
                id: placement.ShownPrototypeId
            ) is not { } creation) ||
                !IsStaticStamp(
                creation: creation,
                placement: placement
            )
            ) {
                continue;
            }

            var tint = tintFor?.Invoke(arg: placement.Id);
            var paletteIds = ((tint is null)
                ? registered.Resolve(
                    builder: builder,
                    colors: baked,
                    creation: creation
                )
                : RegisterPalette(
                    builder: builder,
                    colors: baked,
                    document: creation.Document,
                    tint: tint
                )
            );

            var firstInstance = builder.InstanceCount;
            var firstMesh = (meshDraws?.Count ?? 0);
            var bakedDraw = (((meshDraws is not null) && (tint is null) && DrawsItsBake(creation: creation.EngineDocument))
                ? bakedFor?.Invoke(arg: placement.ShownPrototypeId) : null);

            EmitPlacement(
                builder: builder,
                creation: creation.EngineDocument,
                definition: definition,
                paletteIds: paletteIds,
                placement: placement,
                textCatalog: textCatalog,
                worldSeed: worldSeed,
                volumes: volumes,
                mesh: (((meshDraws is not null) && (creation.Mesh is { } mesh))
                    ? MeshOf(mesh: mesh)
                    : null),
                meshMaterial: paletteIds[Math.Clamp(
                    value: (creation.Mesh?.Material ?? 0),
                    max: (paletteIds.Length - 1),
                    min: 0
                )],
                meshDraws: meshDraws,
                meshScope: meshScope,
                firstMesh: firstMesh,
                baked: bakedDraw
            );
            if (picks is not null) {
                var target = new WorldPickTarget(Placement: placement.Id, BodyIndex: null) { DrawsBake = ((bakedDraw is not null) && (meshDraws!.Count > firstMesh)), Prototype = creation.Id };

                picks.Materials(prototype: creation.Id, ids: paletteIds);

                picks.Instances(first: firstInstance, end: builder.InstanceCount, target: target);
                picks.Meshes(first: firstMesh, end: (meshDraws?.Count ?? 0), target: target);
            }
        }
    }

    // Whether a creation's bake holds everything its placements show: a bake is the creation's contact field, so text
    // runs and noise relief, which only its presentation carries, keep it drawing through its field.
    private static bool DrawsItsBake(CreationDocument creation) => (
        (creation.TextRuns is not { Count: > 0 }) &&
        (creation.Noise is null)
    );

    /// <summary>The emitted instance count of one placement, including pattern/sampled and reflected copies.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>) — resolves a Noise/Scatter
    /// distribution's actual admitted count.</param>
    public static int InstanceCount(WorldPlacement placement, ulong worldSeed) {
        return CreationStampLattice.InstanceCount(
            pattern: WorldPlacementStamp.PatternFor(placement: placement),
            sampledCount: WorldPlacementStamp.SampledOffsetsFor(
                placement: placement,
                worldSeed: worldSeed
            )?.Count,
            mirror: WorldPlacementStamp.MirrorFor(placement: placement)
        );
    }
    /// <summary>Whether a creation row animates — the static/animated fork every consumer shares. A driver or an
    /// inverse-kinematics effector animates; a timeline frame animates only where one of its transforms differs from its shape's authored position, rotation or
    /// scale (a state-bound value always differs from a literal). A creation whose frames carry no transforms, or only
    /// its shapes' authored poses, moves nothing, so it stamps statically.</summary>
    /// <param name="creation">The creation row.</param>
    /// <returns><see langword="true"/> when the creation's drivers, effectors or frames move a shape.</returns>
    public static bool IsAnimated(WorldPrototype creation) {
        var document = creation.Document;

        if (
            (document.Drivers is { Count: > 0 }) ||
            (document.Effectors is { Count: > 0 })
        ) {
            return true;
        }

        var shapes = (document.Shapes ?? []);

        foreach (var frame in (document.Frames ?? [])) {
            foreach (var transform in (frame.Transforms ?? [])) {
                foreach (var shape in shapes) {
                    if (
                        (shape.Id == transform.Id) &&
                        (!Equals(objA: shape.Position, objB: transform.Position) || !Equals(objA: shape.Rotation, objB: transform.Rotation) || !Equals(objA: shape.Scale, objB: transform.Scale))
                    ) {
                        return true;
                    }
                }
            }
        }

        return false;
    }
    /// <summary>Whether a placement renders as a STATIC furniture stamp — not when it is animated (the stamp pool replays
    /// it), not when it INHABITS (a live body renders its creation through a body-rooted stamp instead), and not when it
    /// ATTACHES (the stamp pool roots it on a live body's pose plus the facet's local offset, so its authored transform
    /// is inert). This ONE fork is what keeps an attached row from drawing twice — the static pass skips it here and its
    /// instances stop charging <see cref="StaticStampInstances"/>, because the constant-size pool already reserves
    /// them. A dealt template (<see cref="WorldPlacement.Deal"/>) renders nothing itself: its dealt children are the
    /// static stamps.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="creation">The placement's resolved creation.</param>
    public static bool IsStaticStamp(WorldPlacement placement, WorldPrototype creation) =>
        (!IsAnimated(creation: creation) && (placement.Inhabit is null) && (placement.Attach is null) && (placement.Deal is null));
    /// <summary>The total static stamp instances of a placement set (animated rows ride the constant replay pool and
    /// charge nothing here) — the apply-time measure's placement charge unit.</summary>
    /// <param name="creations">The world's creation rows.</param>
    /// <param name="placements">The placement rows.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>) — resolves each row's
    /// Noise/Scatter distribution to its actual admitted count.</param>
    public static int StaticStampInstances(IReadOnlyList<WorldPrototype> creations, IReadOnlyList<WorldPlacement> placements, ulong worldSeed = 0UL) {
        var (scopedStamps, shapeInstances) = StaticStampReservation(
            creations: creations,
            placements: placements,
            worldSeed: worldSeed
        );

        return checked((scopedStamps + shapeInstances));
    }
    /// <summary>The static stamp reservation split by emission class: SCOPED stamps (one whole-creation instance per
    /// copy — a scoped or text-carrying creation) and per-SHAPE instances (every other copy materializes one instance
    /// per shape). KEEP IN SYNC with <c>EmitPlacement</c>'s split and <c>EmitProbe</c>'s two reservation forms.</summary>
    /// <param name="creations">The world's creation rows.</param>
    /// <param name="placements">The placement rows.</param>
    /// <param name="worldSeed">The world's reroll seed — resolves each row's Noise/Scatter distribution.</param>
    /// <returns>The scoped-stamp count and the per-shape instance count.</returns>
    public static (int ScopedStamps, int ShapeInstances) StaticStampReservation(IReadOnlyList<WorldPrototype> creations, IReadOnlyList<WorldPlacement> placements, ulong worldSeed = 0UL) {
        var scopedStamps = 0;
        var shapeInstances = 0;

        foreach (var placement in placements) {
            if (
                (WorldDefinitionRows.FindCreation(
                creations: creations,
                id: placement.ShownPrototypeId
            ) is not { } creation) ||
                !IsStaticStamp(
                creation: creation,
                placement: placement
            )
            ) {
                continue;
            }

            var copies = InstanceCount(
                placement: placement,
                worldSeed: worldSeed
            );
            var perCopy = CreationStampEmitter.PerCopyInstanceCount(document: creation.Document);

            if (perCopy == 1) {
                scopedStamps = checked((scopedStamps + copies));
            } else {
                shapeInstances = checked((shapeInstances + checked((copies * perCopy))));
            }
        }

        return (scopedStamps, shapeInstances);
    }

}
