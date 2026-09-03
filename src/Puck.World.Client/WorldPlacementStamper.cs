using System.Numerics;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Puck.Text;

namespace Puck.World.Client;

/// <summary>
/// Emits the world's STATIC placements into the program under construction — each materialized pattern or reflected
/// copy is a static <see cref="SdfProgramBuilder.BeginInstance"/> whose shapes replay the referenced creation's shape
/// list with the full placement transform baked into every shape's own segment. Animated placements (framed creations)
/// are NOT emitted here — they ride <see cref="WorldStampPool"/>'s reserved dynamic pool.
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

    /// <summary>Registers a creation's palette (16-slot clamp) with an optional tint lerp, returning program-relative
    /// material ids indexed like the creation's own palette slots.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="definition">The definition a state-bound palette entry resolves against.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="tint">The albedo tint (color + blend), or <see langword="null"/>.</param>
    internal static int[] RegisterPalette(SdfProgramBuilder builder, WorldDefinition definition, CreationDocument document, (Vector3 Color, float Blend)? tint) {
        var palette = (document.Palette ?? []);
        var count = Math.Min(
            val1: palette.Count,
            val2: CreationDocument.PaletteSize
        );
        var ids = new int[Math.Max(
            val1: count,
            val2: 1
        )];

        for (var index = 0; (index < ids.Length); index++) {
            var entry = ((index < count)
                ? palette[index]
                : null
            );
            var albedo = WorldColor.Resolve(
                definition: definition,
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
                Shininess: (entry?.Shininess ?? 32f),
                Specular: (entry?.Specular ?? 0f)
            ));
        }

        return ids;
    }

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
    private static void EmitPlacement(SdfProgramBuilder builder, CreationDocument creation, int[] paletteIds, WorldPlacement placement, PackedFontAtlasCatalog? textCatalog, ulong worldSeed) {
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
        var reach = CreationStampEmitter.RenderReach(
            document: creation,
            scale: placement.Scale,
            fontFor: ((textCatalog is { } catalog)
            ? name => catalog.Resolve(name: name)
            : null),
            textLayouts: textLayouts
        );
        var rotation = Quaternion.CreateFromAxisAngle(
            axis: Vector3.UnitY,
            angle: (placement.YawDegrees * (MathF.PI / 180f))
        );
        // A creation whose parts carve each other (or that carries noise relief) is one SCOPED candidate against the
        // world field. A scope-free, text-free creation instead emits one TIGHT instance per shape — union-family
        // members mask bit-identically, and per-shape bounds keep a big creation (a tree's whole-canopy reach) from
        // masking every tile it merely overlaps.
        var scoped = (
            (creation.Shapes is { Count: > 0 }) &&
            CreationStampEmitter.RequiresScope(document: creation)
        );
        var perShape = (
            !scoped &&
            !hasText &&
            (creation.TextRuns is not { Count: > 0 }) &&
            (creation.Shapes is { Count: > 0 })
        );

        CreationStampLattice.ForEachInstance(
            origin: placement.Position,
            rotation: rotation,
            pattern: WorldPlacementStamp.PatternFor(placement: placement),
            sampledOffsets: WorldPlacementStamp.SampledOffsetsFor(placement: placement, worldSeed: worldSeed),
            mirror: WorldPlacementStamp.MirrorFor(placement: placement),
            visitor: instance => {
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
                            boundRadius: (boundRadius + PlacementBoundMargin)
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
                            )]
                        );
                        _ = builder.EndInstance();
                    }

                    return;
                }

                _ = builder.BeginInstance(
                    boundCenter: instance.Origin,
                    boundRadius: (reach + PlacementBoundMargin)
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
                        materialFor: run => paletteIds[Math.Clamp(
                            value: (run.Material ?? 0),
                            max: (paletteIds.Length - 1),
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
        );
    }
    private static int[] ResolvePalette(SdfProgramBuilder builder, WorldDefinition definition, WorldPrototype creation, Dictionary<string, int[]> paletteById) {
        if (paletteById.TryGetValue(
            key: creation.Id,
            value: out var cached
        )) {
            return cached;
        }

        var ids = RegisterPalette(
            builder: builder,
            definition: definition,
            document: creation.Document,
            tint: null
        );

        paletteById[creation.Id] = ids;

        return ids;
    }

    /// <summary>Emits the construction probe's placement reservation: <paramref name="reservedCount"/> worst-case
    /// stamps — each a distinct full 16-slot palette plus <see cref="WorldPlacementPolicy.MaxShapesPerStamp"/> shapes
    /// carrying the densest legal per-shape chain — so any real
    /// static emission within the placement policy fits the once-sized buffers by construction. Never rendered.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="reservedCount">The reserved SCOPED stamp count (scoped/text-carrying boot placements + the
    /// authoring headroom).</param>
    /// <param name="reservedShapeInstances">The reserved per-SHAPE instance count (scope-free boot placements'
    /// copies × shapes — see <see cref="StaticStampReservation"/>).</param>
    public static void EmitProbe(SdfProgramBuilder builder, int reservedCount, int reservedShapeInstances = 0) {
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

            for (var shape = 0; (shape < WorldPlacementPolicy.MaxShapesPerStamp); shape++) {
                _ = SdfSolidGeometry.AppendPrimitive(
                    chain: builder.ResetPoint()
                        .Translate(offset: center)
                        .Rotate(rotation: Quaternion.Identity)
                        .Scale(scale: Vector3.One)
                        .Translate(offset: Vector3.Zero)
                        .Rotate(rotation: Quaternion.Identity)
                        .Scale(scale: Vector3.One),
                    type: SdfSolidPrimitive.Sphere,
                    material: paletteIds[(shape % CreationDocument.PaletteSize)]
                );
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

            // The per-shape field scope an eccentric primitive opens around itself (CreationStampEmitter.EmitShapeChain),
            // reserved for every shape since any shape may be authored eccentric.
            _ = SdfSolidGeometry.AppendPrimitive(
                chain: chain
                    .Translate(offset: Vector3.Zero)
                    .Rotate(rotation: Quaternion.Identity)
                    .PushField(compose: SdfBlendOp.Union),
                type: SdfSolidPrimitive.Sphere,
                material: material
            ).PopField();
            _ = builder.EndInstance();
        }
    }
    /// <summary>Emits every STATIC placement (animated rows skip — the animator owns them). Palettes register once per
    /// distinct untinted creation; a tinted stamp (selection amber / change shimmer) registers its own lerped palette
    /// (act-scale rare, never steady-state).</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="definition">The definition the rows belong to — what a state-bound palette color resolves
    /// against.</param>
    /// <param name="creations">The world's creation rows.</param>
    /// <param name="placements">The (possibly drag-composed) placement rows.</param>
    /// <param name="textCatalog">The packed world font catalog. Null omits creation text; local-world callers use
    /// null only when no catalog is declared, while remote projection callers currently have no transported font
    /// assets to resolve.</param>
    /// <param name="tintFor">Resolves a placement id's albedo tint (color + blend), or <see langword="null"/> untinted.</param>
    public static void EmitStatic(SdfProgramBuilder builder, WorldDefinition definition, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<WorldPlacement> placements, PackedFontAtlasCatalog? textCatalog = null, Func<string, (Vector3 Color, float Blend)?>? tintFor = null) {
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);
        var paletteById = new Dictionary<string, int[]>(comparer: StringComparer.Ordinal);

        foreach (var placement in placements) {
            if (
                (WorldDefinitionRows.FindCreation(
                creations: creations,
                id: placement.PrototypeId
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
                ? ResolvePalette(
                    builder: builder,
                    creation: creation,
                    definition: definition,
                    paletteById: paletteById
                )
                : RegisterPalette(
                    builder: builder,
                    definition: definition,
                    document: creation.Document,
                    tint: tint
                )
            );

            EmitPlacement(
                builder: builder,
                creation: creation.EngineDocument,
                paletteIds: paletteIds,
                placement: placement,
                textCatalog: textCatalog,
                worldSeed: worldSeed
            );
        }
    }
    /// <summary>The emitted instance count of one placement, including pattern/sampled and reflected copies.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>) — resolves a Noise/Scatter
    /// distribution's actual admitted count.</param>
    public static int InstanceCount(WorldPlacement placement, ulong worldSeed) {
        return CreationStampLattice.InstanceCount(
            pattern: WorldPlacementStamp.PatternFor(placement: placement),
            sampledCount: WorldPlacementStamp.SampledOffsetsFor(placement: placement, worldSeed: worldSeed)?.Count,
            mirror: WorldPlacementStamp.MirrorFor(placement: placement)
        );
    }
    /// <summary>Whether a creation row replays a timeline (frames present) — the static/animated fork every consumer
    /// shares.</summary>
    /// <param name="creation">The creation row.</param>
    public static bool IsAnimated(WorldPrototype creation) => (creation.Document.Frames is { Count: > 0 });
    /// <summary>Whether a placement renders as a STATIC furniture stamp — not when it is animated (the stamp pool replays
    /// it), not when it INHABITS (a live body renders its creation through a body-rooted stamp instead), and not when it
    /// ATTACHES (the stamp pool roots it on a live body's pose plus the facet's local offset, so its authored transform
    /// is inert). This ONE fork is what keeps an attached row from drawing twice — the static pass skips it here and its
    /// instances stop charging <see cref="StaticStampInstances"/>, because the constant-size pool already reserves
    /// them.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="creation">The placement's resolved creation.</param>
    public static bool IsStaticStamp(WorldPlacement placement, WorldPrototype creation) =>
        (!IsAnimated(creation: creation) && (placement.Inhabit is null) && (placement.Attach is null));
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
                id: placement.PrototypeId
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
