using System.Numerics;
using Puck.World.Authoring;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Text;

namespace Puck.World.Client;

/// <summary>
/// Emits the world's STATIC placements into the program under construction — each materialized pattern or reflected
/// copy is a static <see cref="SdfProgramBuilder.BeginInstance"/> whose shapes replay the referenced creation's shape
/// list with the full placement transform baked into every shape's own segment. Animated placements (framed creations)
/// are NOT emitted here — they ride <see cref="WorldCreationInstances"/>'s reserved dynamic pool. A prototype's inline mesh
/// (<see cref="WorldPrototype.Mesh"/>) becomes one <see cref="SdfMeshDraw"/> per static placement instance.
/// </summary>
/// <remarks>Text runs share the same instance and transform as their creation and resolve through the world's packed
/// font catalog. They count against <see cref="CreationDocument.StampShapeCount"/> like every other emitted shape.</remarks>
public static class WorldPlacementStamper {
    /// <summary>Retains the static placements the contact evaluator can read. An unsupported placement excludes only
    /// itself, so a render-only fold cannot disable surface probes throughout the world.</summary>
    /// <param name="definition">The world supplying prototypes and placement frames.</param>
    /// <param name="placements">The candidate placements.</param>
    /// <param name="excluded">Receives each excluded placement and its evaluator refusal, when supplied.</param>
    /// <returns>The placements admitted by the same emission and evaluator used by the composed query field.</returns>
    public static IReadOnlyList<WorldPlacement> Queryable(WorldDefinition definition, IReadOnlyList<WorldPlacement> placements, Action<WorldPlacement, string>? excluded = null) {
        var usable = new List<WorldPlacement>();
        var seed = (definition.Generation?.WorldSeed ?? 0UL);

        foreach (var placement in placements) {
            var creation = WorldDefinitionRows.FindCreation(creations: definition.Creations, id: placement.ShownPrototypeId);
            var stamp = WorldPlacementStamp.Resolve(definition, placement, worldSeed: seed);
            var frame = stamp.Frame;

            // Emission also reads the composed parent frame and seeded pattern. The child row can stay identical
            // while either changes, including moving a previously admitted instance outside the evaluator's frame.
            if (!QueryVerdicts.TryGetValue(key: placement, value: out var verdict) || !ReferenceEquals(objA: verdict.Creation, objB: creation) ||
                (verdict.Frame != frame) || (verdict.Seed != seed) || !ReferenceEquals(objA: verdict.Stamp, objB: stamp)) {
                string? refusal = null;

                try {
                    var builder = new SdfProgramBuilder();

                    EmitStatic(builder: builder, creations: definition.Creations, definition: definition, placements: [placement]);

                    var program = builder.Build();
                    var queryFrame = ((double)new SdfFieldEvaluator(program: program).Frame);

                    // The composed field answers only inside the least of its parts' frames, so a placement its own
                    // field cannot answer at would refuse every query in the world.
                    foreach (var instance in program.Instances) {
                        if ((Math.Abs(value: instance.Center.X) > queryFrame) || (Math.Abs(value: instance.Center.Y) > queryFrame) || (Math.Abs(value: instance.Center.Z) > queryFrame)) {
                            refusal = $"its field answers only within {queryFrame.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} of the origin, short of the copy at {instance.Center.ToString(format: null, formatProvider: System.Globalization.CultureInfo.InvariantCulture)}";
                            break;
                        }
                    }
                } catch (ArgumentException exception) {
                    refusal = exception.Message.ReplaceLineEndings(replacementText: " ");
                }

                verdict = new QueryVerdict(
                    Creation: creation,
                    Frame: frame,
                    Refusal: refusal,
                    Seed: seed,
                    Stamp: stamp
                );
                QueryVerdicts.AddOrUpdate(key: placement, value: verdict);
            }

            if (verdict.Refusal is { } reason) {
                excluded?.Invoke(placement, reason);
            } else {
                usable.Add(item: placement);
            }
        }

        return usable;
    }

    // Each placement row's evaluator verdict and the external inputs its emission read.
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldPlacement, QueryVerdict> QueryVerdicts = new();

    private sealed record QueryVerdict(WorldPrototype? Creation, CompiledPlacementFrame Frame, CompiledPlacementStamp Stamp, ulong Seed, string? Refusal);

    // The instance-bound slack past a creation's own reach (a contract of the tile cull, not a policy: a too-tight
    // bound CLIPS real geometry at masked tile edges; a fat one only costs a rare extra evaluation).
    private const float PlacementBoundMargin = 0.4f;
    // How much longer than thick a shape is before the bake contours it at its half thickness (ShapeDetail).
    private const float ThinRatio = 8f;

    // The finest spacing, in metres, a shape declares (ShapeDetail, and a pooled shape's round section in
    // WorldCreationInstances): at the closest the chase and studio cameras come, about 0.4 m, a cell this size spans a
    // pixel or two, so a finer request buys triangles no view resolves. A feature that asks finer (a rounded profile's
    // quarter corner radius, a thin shell) bakes at this spacing.
    internal const float FinestDetail = 0.002f;

    // Probe instances are spaced far apart so the program's segment-merge pass can never collapse consecutive probe
    // segments — a merged probe would under-reserve the segment directory a real scattered world needs (contract).
    private const float ProbeSpread = 100f;

    // Each inline prototype mesh's engine-frame SdfMesh, keyed by the immutable mesh record (MeshOf).
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldPrototypeMesh, SdfMesh> Meshes = new();

    /// <summary>Registers a creation's palette (16-slot clamp) with an optional tint lerp, returning program-relative
    /// material ids indexed like the creation's own palette slots.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="colors">The colors the build bakes, which a state-bound palette color resolves through.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="tint">The albedo tint (color + blend), or <see langword="null"/>.</param>
    /// <param name="pool">A pooled placement's root dynamic slot and placement scale, which parented paints ride; <see langword="null"/> for a
    /// static placement.</param>
    /// <param name="hidden">The shapes the isolation hides, whose parented paints are dropped, or <see langword="null"/>.</param>
    /// <param name="motion">The actual shape-to-motion-owner slot mapping.</param>
    internal static int[] RegisterPalette(SdfProgramBuilder builder, WorldBakedColors colors, CreationDocument document, (Vector3 Color, float Blend)? tint, (int RootSlot, float Scale)? pool = null, bool[]? hidden = null, CreationMotionPlan? motion = null) {
        var ids = new int[PaletteLength(document: document)];

        FillPalette(
            builder: builder,
            colors: colors,
            document: document,
            hidden: hidden,
            ids: ids,
            motion: motion,
            pool: pool,
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
    internal static void FillPalette(SdfProgramBuilder builder, WorldBakedColors colors, CreationDocument document, (Vector3 Color, float Blend)? tint, Span<int> ids, (int RootSlot, float Scale)? pool = null, bool[]? hidden = null, CreationMotionPlan? motion = null) {
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

            ids[index] = builder.AddMaterial(material: WorldSurfaceMaterials.Resolve(
                entry: entry,
                albedo: albedo,
                colors: colors,
                scale: (pool?.Scale ?? 1f),
                paints: Paints(colors: colors, document: document, entry: entry, hidden: hidden, motion: motion, pool: pool)
            ));
        }
    }

    // A palette slot's paints. A creation resolves a parented paint into its parent's motion-owner frame and
    // scales it by the placement; a static placement shares one palette across its placements and has no slot, so it
    // draws only the paints that ride the winning frame, as authored. A paint parented to a shape the isolation hides
    // goes with it.
    private static SdfPaint[]? Paints(WorldBakedColors colors, CreationDocument document, PaletteEntryDocument? entry, (int RootSlot, float Scale)? pool, bool[]? hidden, CreationMotionPlan? motion) {
        if (entry?.Paints is not { Count: > 0 } paints) {
            return null;
        }

        var shapes = (document.Shapes ?? []);
        var resolved = new List<SdfPaint>(capacity: paints.Count);

        foreach (var paint in paints) {
            if (paint.Parent is not { } parent) {
                resolved.Add(item: paint.ToPaint(resolve: colors.LayerColor, scale: (pool?.Scale ?? 1f)));

                continue;
            }
            if (pool is not { } frames) {
                continue;
            }

            // The canonicalizer refuses a parent that names no shape, so the parent is found.
            var shapeIndex = 0;

            while (!string.Equals(a: shapes[shapeIndex].Name?.Value, b: parent, comparisonType: StringComparison.Ordinal)) {
                shapeIndex++;
            }
            if ((hidden is not null) && hidden[shapeIndex]) {
                continue;
            }
            var authored = paint.ToPaint(resolve: colors.LayerColor, scale: frames.Scale, slot: (frames.RootSlot + (motion?.ShapeSlots[shapeIndex] ?? 0)));
            var shape = shapes[shapeIndex];

            resolved.Add(item: authored with {
                Origin = ((shape.Position.Value * frames.Scale) + Vector3.Transform(authored.Origin, shape.Rotation.Value)),
                Rotation = Quaternion.Normalize(value: (shape.Rotation.Value * authored.Rotation)),
            });
        }

        return [.. resolved];
    }

    // Material layers authored in the old winning shape frame are composed into its motion owner's frame.
    internal static int MaterialForShape(SdfProgramBuilder builder, int material, ShapeDocument shape, float scale, bool rigid = false) {
        if (material >= SdfProgramBuilder.ScreenMaterialId) { return material; }
        var source = builder.MaterialAt(index: material);

        if ((source.Inset is null) && (source.Paints is not { Count: > 0 })) { return material; }
        var rotation = shape.Rotation.Value;
        var origin = (shape.Position.Value * scale);
        var inset = ((source.Inset is { } layer) ? layer with {
            Origin = (origin + Vector3.Transform(layer.Origin, rotation)),
            Rotation = Quaternion.Normalize(value: (rotation * layer.Rotation)),
        } : null);
        var paints = source.Paints?.Select(selector: paint => ((paint.Slot == SdfPaint.WinningFrame) ? paint with {
            Origin = (origin + Vector3.Transform(paint.Origin, rotation)),
            Rotation = Quaternion.Normalize(value: (rotation * paint.Rotation)),
        } : (rigid ? paint with { Slot = SdfPaint.WinningFrame } : paint))).ToArray();

        return builder.AddMaterial(material: source with { Inset = inset, Paints = paints });
    }
    // A static instance bakes the placement frame (and, for a parented volume, the parent shape's rest pose) into
    // the volume itself: no slot moves it. A mirrored copy keeps the unmirrored frame — a flow column is
    // symmetric about its own axis, so only its offset would differ, and the mirror plane is not applied here.
    internal static void AppendStaticVolumes(CreationDocument creation, Vector3 origin, Quaternion rotation, float scale, ICollection<SdfVolume>? volumes, bool[]? hidden) {
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
                throw new InvalidOperationException(message: $"Creation '{creation.Name}' exceeds the {SdfProgramBuilder.MaxVolumes} bounded-volume frame capacity.");
            }

            var frameOrigin = origin;
            var frameRotation = rotation;

            if (volume.Parent is { } parent) {
                if (WorldShapeIsolation.IsHidden(hidden: hidden, name: parent, shapes: shapes)) {
                    continue;
                }

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
    private static void AppendMeshDraw(SdfMesh? mesh, int material, Vector3 origin, Quaternion rotation, float scale, Vector3? reflectionNormal, ICollection<SdfMeshDraw>? meshDraws, string? scope, string placement, int firstMesh) {
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
        ));
    }

    internal readonly record struct StaticMeshIdentity(string? Scope, string Placement, int Ordinal);

    /// <summary>Poses a prototype's mesh as a draw: its engine-frame triangles under a uniform scale, an optional mirror (a
    /// plane through the origin in the local frame, as the shapes reflect), then the rotation and the origin, composed in
    /// the row-vector convention <see cref="SdfMeshDraw"/> carries. A static placement and a stamp-pool root pose it
    /// alike. Under a mirror a closed mesh draws with its windings reversed, so its faces stay outward for the opaque
    /// pass; the mirrored matrix then makes it a thin caster in a cascade's exit layer
    /// (<see cref="SdfMeshDraw.EnclosesSolid"/>).</summary>
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
            Mesh: (((reflectionNormal is not null) && mesh.Encloses) ? Mirrored(mesh: mesh) : mesh),
            ObjectToWorld: ((local * Matrix4x4.CreateFromQuaternion(quaternion: rotation)) * Matrix4x4.CreateTranslation(position: origin))
        );
    }
    /// <summary>The engine-frame <see cref="SdfMesh"/> of an inline prototype mesh, converted once: prototype rows are
    /// replaced, never mutated, so every placement, stamp and rebuild of one row shares one mesh. An authored mesh has no
    /// winding contract, and the opaque mesh pass culls back faces (<c>GpuCullMode.Back</c>), as the bake's meshes,
    /// wound outward, need. Triangles that close a volume, each edge between exactly two of them wound opposite ways
    /// (<see cref="ClosedOutward"/>), are wound outward as the bake's are and enclose a solid
    /// (<see cref="SdfMesh.Encloses"/>); any other mesh carries each triangle in both windings, so whichever side faces
    /// the camera draws, and bounds no solid.</summary>
    /// <param name="mesh">The prototype's inline mesh.</param>
    /// <returns>The shared mesh.</returns>
    internal static SdfMesh MeshOf(WorldPrototypeMesh mesh) => Meshes.GetValue(
        derive: static authored => {
            var positions = authored.EngineVertices.ToArray();

            return ((ClosedOutward(indices: authored.Indices, positions: positions) is { } outward)
                ? new SdfMesh(indices: outward, positions: positions)
                : new SdfMesh(indices: TwoSided(indices: authored.Indices), positions: positions) { Encloses = false });
        },
        key: mesh
    );

    // A closed mesh drawn under a mirror: the same triangles with each one's winding reversed, which the mirror reverses
    // back, so its faces stay outward where it is drawn.
    private static SdfMesh Mirrored(SdfMesh mesh) => MirroredMeshes.GetValue(key: mesh, createValueCallback: static closed => {
        var indices = closed.Indices.ToArray();

        for (var index = 0; (index < indices.Length); index += 3) {
            (indices[(index + 1)], indices[(index + 2)]) = (indices[(index + 2)], indices[(index + 1)]);
        }

        return new SdfMesh(indices: indices, positions: closed.Positions);
    });

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SdfMesh, SdfMesh> MirroredMeshes = new();

    // The triangles wound outward when they close a volume: every edge, its vertices matched by position, is shared by
    // exactly two triangles that run it in opposite directions, and the volume they enclose is not zero. Triangles
    // reversed as a whole (a negative volume) are flipped. Null for an open, nonmanifold or inconsistently wound mesh.
    private static uint[]? ClosedOutward(IReadOnlyList<uint> indices, Vector3[] positions) {
        var canonical = new int[positions.Length];
        var first = new Dictionary<Vector3, int>();

        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            canonical[vertex] = (first.TryGetValue(key: positions[vertex], value: out var seen) ? seen : (first[positions[vertex]] = vertex));
        }

        var edges = new Dictionary<(int From, int To), int>();
        var volume = 0.0;

        for (var index = 0; (index < indices.Count); index += 3) {
            var a = canonical[indices[index]];
            var b = canonical[indices[(index + 1)]];
            var c = canonical[indices[(index + 2)]];

            if ((a == b) || (b == c) || (c == a)) {
                continue;
            }
            foreach (var edge in new[] { (a, b), (b, c), (c, a) }) {
                edges[edge] = (edges.GetValueOrDefault(key: edge) + 1);
            }
            volume += Vector3.Dot(vector1: positions[a], vector2: Vector3.Cross(vector1: positions[b], vector2: positions[c]));
        }
        if ((edges.Count == 0) || (volume == 0.0)) {
            return null;
        }
        foreach (var ((from, to), runs) in edges) {
            if ((runs != 1) || (edges.GetValueOrDefault(key: (to, from)) != 1)) {
                return null;
            }
        }

        var outward = indices.ToArray();

        if (volume < 0.0) {
            for (var index = 0; (index < outward.Length); index += 3) {
                (outward[(index + 1)], outward[(index + 2)]) = (outward[(index + 2)], outward[(index + 1)]);
            }
        }

        return outward;
    }
    // The triangles in their authored winding, then each again with its last two vertices swapped.
    private static uint[] TwoSided(IReadOnlyList<uint> indices) {
        var doubled = new uint[(indices.Count * 2)];

        for (var index = 0; (index < indices.Count); index += 3) {
            doubled[index] = indices[index];
            doubled[(index + 1)] = indices[(index + 1)];
            doubled[(index + 2)] = indices[(index + 2)];
            doubled[(indices.Count + index)] = indices[index];
            doubled[((indices.Count + index) + 1)] = indices[(index + 2)];
            doubled[((indices.Count + index) + 2)] = indices[(index + 1)];
        }

        return doubled;
    }
    // Emits the creation's shapes, EACH its own segment carrying the FULL placement prefix — the shader splits the
    // stream at each ResetPoint and a segment's transforms are local to it, so a shared prefix segment would be dead.
    // Uniform placement scale commutes with the per-shape rotations (shear-free).
    private static void EmitPlacedShapes(SdfProgramBuilder builder, CreationDocument creation, int[] paletteIds, WorldPlacement placement, Vector3 placementOrigin, Quaternion placementRotation, Vector3? reflectionNormal, bool inScope, bool[]? hidden) {
        CreationStampEmitter.Emit(
            builder: builder,
            document: creation,
            hidden: hidden,
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

    internal static void EmitPlacement(SdfProgramBuilder builder, CreationDocument creation, WorldDefinition definition, int[] paletteIds, WorldPlacement placement, PackedFontAtlasCatalog? textCatalog, ulong worldSeed, ICollection<SdfVolume>? volumes, SdfMesh? mesh, int meshMaterial, ICollection<SdfMeshDraw>? meshDraws, string? meshScope, int firstMesh, bool[]? hidden, CompiledPlacementFrame? frameOverride = null) {
        // An isolation that hides every shape leaves the placement nothing to draw.
        if ((hidden is not null) && (Array.IndexOf(array: hidden, value: false) < 0)) {
            return;
        }
        // Nor does a creation with no shapes, text, mesh or volume (a response's "nothing shown" entry).
        if ((creation.Shapes is not { Count: > 0 }) && (creation.TextRuns is not { Count: > 0 }) && (mesh is null) && (creation.Volumes is not { Count: > 0 })) {
            return;
        }

        var stamp = WorldPlacementStamp.Resolve(definition: definition, frameOverride: frameOverride, placement: placement, worldSeed: worldSeed);
        // A glass shape emits apart from the opaque shapes, in the glass instance after them: `hiddenOpaque` hides it
        // from the opaque emission, and `glassHidden` hides everything but the glass shapes from the glass one.
        var (hiddenOpaque, glassHidden) = GlassMasks(creation: creation, hidden: hidden);
        // Laid out ONCE here (rather than once for the reach measure below plus once per pattern/scatter instance
        // inside the visitor's EmitText call) — TextLayout.Layout is a pure function of (atlas, text, scale,
        // options), all fixed for this whole EmitPlacement call, so every reader below shares this one result.
        var hasText = (
            (hidden is null) &&
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
        // world field. A scope-free creation instead emits one TIGHT instance per shape, and its embossed text one more — union-family
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
        var rotation = stamp.Rotation;
        var perShape = (
            !scoped &&
            (creation.Shapes is { Count: > 0 })
        );

        var visitor = new StaticInstanceVisitor(
            builder: builder,
            creation: creation,
            firstMesh: firstMesh,
            glassHidden: glassHidden,
            hasText: hasText,
            hidden: hidden,
            hiddenOpaque: hiddenOpaque,
            mesh: ((hidden is null) ? mesh : null),
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

        stamp.ForEach(visitor: ref visitor);
    }

    // The masks a creation with glass shapes emits through: the opaque emission's hidden shapes (the isolation's and
    // every glass shape) and the glass emission's (everything but the isolation's visible glass shapes). Both null for a
    // creation with no visible glass shape, which emits as it stands.
    private static (bool[]? HiddenOpaque, bool[]? GlassHidden) GlassMasks(CreationDocument creation, bool[]? hidden) {
        if (!creation.HasGlass) {
            return (hidden, null);
        }

        var shapes = creation.Shapes!;
        var hiddenOpaque = new bool[shapes.Count];
        var glassHidden = new bool[shapes.Count];
        var any = false;

        for (var index = 0; (index < shapes.Count); index++) {
            var shown = ((hidden is null) || !hidden[index]);
            var glass = (shown && creation.IsGlassShape(shape: shapes[index]));

            hiddenOpaque[index] = (!shown || glass);
            glassHidden[index] = !glass;
            any |= glass;
        }

        return (hiddenOpaque, (any ? glassHidden : null));
    }

    // The finest feature a creation's text runs carry, in world units: the thinner of a run's relief and its stroke, about
    // a tenth of its em. A text instance declares it (SdfInstanceRange.Detail), static here or pooled (WorldCreationInstances).
    internal static float TextDetail(CreationDocument creation, float scale) {
        var detail = float.PositiveInfinity;

        foreach (var run in (creation.TextRuns ?? [])) {
            detail = MathF.Min(x: detail, y: MathF.Min(x: (run.Depth ?? 0.02f), y: (0.1f * run.EmHeight)));
        }

        return (float.IsFinite(f: detail) ? (detail * scale) : 0f);
    }
    // A warped field stays separate from its neighbours under the existing static bake partition.
    internal static bool BakesAlone(ShapeDocument shape) => ShapePointOps.NeedsScope(shape: shape);

    // A bound measures a shape's reach, not its smallest curved feature. Thin strands, shells and warped edges
    // need their own spacing even when the unwarped primitive or the containing creation has a large bound. A request finer
    // than FinestDetail bakes at FinestDetail.
    private static float ShapeDetail(ShapeDocument shape, float scale) {
        var detail = float.PositiveInfinity;
        Vector3 size = (shape.Scale ?? Vector3.One);

        if (shape.Cells is { } cells) {
            detail = MathF.Min(x: detail, y: (1f / (32f * cells.Frequency)));
        }
        if (shape.Curve is { } curve) {
            detail = MathF.Min(x: detail, y: ((MathF.Min(x: curve.RadiusStart, y: curve.RadiusEnd) * size.X) / 8f));
        }
        if (shape.Onion is > 0f) {
            detail = MathF.Min(x: detail, y: (shape.Onion.Value / 4f));
        }
        if (shape.Profile is { Kind: SdfPrismProfileKind.Convex or SdfPrismProfileKind.RoundedRectangle } profile) {
            var radius = (profile.CornerRadius * MathF.Min(x: size.X, y: size.Y));

            if (profile.Kind == SdfPrismProfileKind.Convex) {
                var inradius = SdfSolidGeometry.ScaledConvexProfile(profile: profile, scale: size).Inradius;

                radius = SdfProgramBuilder.ClampRounding(
                    rounding: ((profile.CornerRadius * inradius) + (shape.Rounding ?? 0f)),
                    profileInradius: inradius,
                    lift: (shape.Lift ?? SdfLift.Extrude),
                    liftAmount: size.Z
                );
            }
            if (radius > 0f) { detail = MathF.Min(x: detail, y: (radius / 4f)); }
        }
        // A zero twist or bend is none: the canonicalizer writes an absent one as zero, so a live edit's document
        // reads it as authored.
        if (ShapePointOps.NeedsScope(shape: shape)) {
            var lift = (shape.Lift ?? SdfLift.Extrude);
            var x = SdfSolidGeometry.HalfExtent(type: shape.Type, scale: size, lift: lift, axis: Vector3.UnitX);
            var y = SdfSolidGeometry.HalfExtent(type: shape.Type, scale: size, lift: lift, axis: Vector3.UnitY);
            var z = SdfSolidGeometry.HalfExtent(type: shape.Type, scale: size, lift: lift, axis: Vector3.UnitZ);
            var extent = MathF.Min(x: x, y: MathF.Min(x: y, y: z));

            if (extent > 0f) { detail = MathF.Min(x: detail, y: (extent / 32f)); }
        }

        var plate = PlateDetail(scale: 1f, shape: shape);

        detail = ((plate > 0f) ? MathF.Min(x: detail, y: plate) : detail);

        return (float.IsFinite(f: detail) ? MathF.Max(x: (detail * scale), y: FinestDetail) : 0f);
    }

    // A plate, a slab, a strip or a streamer: its bound measures its length, but its thickness is the feature. A lattice
    // sized by the bound steps clean over a sheet thinner than its cell, and the sheet bakes to nothing, so a shape far
    // thinner than it is long is contoured no coarser than its half thickness, in world units at `scale`; zero for a shape
    // that is not thin. A static shape (ShapeDetail) and a pooled blend group's part (WorldCreationInstances.PartDetail)
    // both declare it.
    internal static float PlateDetail(ShapeDocument shape, float scale) {
        var size = (shape.Scale ?? Vector3.One);
        var lifted = (shape.Lift ?? SdfLift.Extrude);
        var halfX = SdfSolidGeometry.HalfExtent(type: shape.Type, scale: size, lift: lifted, axis: Vector3.UnitX);
        var halfY = SdfSolidGeometry.HalfExtent(type: shape.Type, scale: size, lift: lifted, axis: Vector3.UnitY);
        var halfZ = SdfSolidGeometry.HalfExtent(type: shape.Type, scale: size, lift: lifted, axis: Vector3.UnitZ);
        var thinnest = MathF.Min(x: halfX, y: MathF.Min(x: halfY, y: halfZ));
        var longest = MathF.Max(x: halfX, y: MathF.Max(x: halfY, y: halfZ));

        return (((thinnest > 0f) && (longest > (ThinRatio * thinnest))) ? (thinnest * scale) : 0f);
    }

    // One static placement's instances: its volumes and mesh draws, then either one tight instance per shape or one
    // instance holding the whole creation. A struct the lattice walk calls, so a placement allocates no closure.
    private readonly struct StaticInstanceVisitor(SdfProgramBuilder builder, CreationDocument creation, bool hasText, bool[]? hidden, bool[]? hiddenOpaque, bool[]? glassHidden, SdfMesh? mesh, ICollection<SdfMeshDraw>? meshDraws, string? meshScope, int firstMesh, int meshMaterial, int[] paletteIds, bool perShape, WorldPlacement placement, float reach, Quaternion rotation, bool scoped, PackedFontAtlasCatalog? textCatalog, TextLayoutResult[]? textLayouts, ICollection<SdfVolume>? volumes) : ICreationStampVisitor {
        private static float CreationDetail(CreationDocument document, float scale, bool[]? hidden = null) {
            var detail = float.PositiveInfinity;
            var shapes = (document.Shapes ?? []);

            for (var index = 0; (index < shapes.Count); index++) {
                if ((hidden is not null) && hidden[index]) { continue; }

                var own = ShapeDetail(scale: scale, shape: shapes[index]);

                if (own > 0f) { detail = MathF.Min(x: detail, y: own); }
            }

            return (float.IsFinite(f: detail) ? detail : 0f);
        }
        // A scoped creation's noise relief declares its own spacing (CreationNoiseDocument.DetailSpacing): the shapes'
        // bounds size its lattice by the creation's extent, which steps over relief a few cells long.
        private static float ReliefDetail(float detail, CreationNoiseDocument? noise, float scale) {
            var relief = ((noise is null) ? 0f : (noise.DetailSpacing() * scale));

            return ((relief <= 0f) ? detail : ((detail <= 0f) ? relief : MathF.Min(x: detail, y: relief)));
        }
        // The creation's glass shapes, one static instance of their own after the opaque shapes and text: their own
        // group, so they never bake with an opaque shape, carve or are carved by one, and draw in the glass pass. They
        // take a field scope of their own only when one of them composes by something other than a union, so a hollow
        // shell cut from a glass box stays glass.
        private void EmitGlassShapes(CreationStampInstance instance) {
            if (glassHidden is null) {
                return;
            }

            var glassScoped = creation.GlassNeedsScope();

            _ = builder.BeginInstance(
                boundCenter: instance.Origin,
                boundRadius: (reach + PlacementBoundMargin),
                detail: CreationDetail(document: creation, hidden: glassHidden, scale: placement.Scale)
            );
            if (glassScoped) {
                _ = builder.PushField(compose: SdfBlendOp.Union);
            }
            EmitPlacedShapes(
                builder: builder,
                creation: creation,
                hidden: glassHidden,
                inScope: glassScoped,
                paletteIds: paletteIds,
                placement: placement,
                placementOrigin: instance.Origin,
                placementRotation: rotation,
                reflectionNormal: instance.ReflectionNormal
            );
            if (glassScoped) {
                _ = builder.PopField();
            }
            _ = builder.EndInstance();
        }
        private void EmitTextRuns(CreationStampInstance instance) {
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

        public void Visit(CreationStampInstance instance) {
            AppendStaticVolumes(
                creation: creation,
                hidden: hidden,
                origin: instance.Origin,
                rotation: rotation,
                scale: placement.Scale,
                volumes: volumes
            );
            AppendMeshDraw(
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
            if (perShape) {
                var stampTransform = new CreationStampTransform(
                    Origin: instance.Origin,
                    Rotation: rotation,
                    Scale: placement.Scale,
                    ReflectionNormal: instance.ReflectionNormal
                );

                var emitted = 0;
                var previousWarped = false;

                for (var shapeIndex = 0; (shapeIndex < creation.Shapes!.Count); shapeIndex++) {
                    var shape = creation.Shapes[shapeIndex];

                    if ((hiddenOpaque is not null) && hiddenOpaque[shapeIndex]) {
                        continue;
                    }
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

                    // The copy's shapes keep their tight instances but bake as one mesh, their union, so two plates
                    // that share a face draw it once rather than fighting over it. The copy's first emitted shape opens
                    // the union, so a hidden first shape never joins this copy to the previous one. A warped shape bakes
                    // alone, and so does the shape after it: its scope divides its field by its warp's bound, so beside an
                    // exact neighbour the union's field sags below a distance, and the bake reads the sag as a sheet it
                    // closes into slabs across the neighbour.
                    var warped = BakesAlone(shape: shape);
                    var box = CreationStampEmitter.ShapeStampBox(document: creation, shapeIndex: shapeIndex, transform: stampTransform);
                    var margin = new Vector3(value: PlacementBoundMargin);

                    _ = builder.BeginInstance(
                        bakesWithPrevious: ((emitted++ > 0) && !warped && !previousWarped),
                        boundCenter: boundCenter,
                        boundRadius: (boundRadius + PlacementBoundMargin),
                        detail: ShapeDetail(shape: shape, scale: placement.Scale),
                        box: ((box is { } tight) ? ((tight.Low - margin), (tight.High + margin)) : null)
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
                    previousWarped = warped;
                }

                // Embossed text bakes as a group of its own, refined to its glyphs' detail: inside the shapes' union its
                // relief falls below the union's lattice and perturbs the contour of every shape it bakes with.
                if (hasText) {
                    _ = builder.BeginInstance(
                        boundCenter: instance.Origin,
                        boundRadius: (reach + PlacementBoundMargin),
                        detail: TextDetail(creation: creation, scale: placement.Scale)
                    );
                    EmitTextRuns(instance: instance);
                    _ = builder.EndInstance();
                }
                EmitGlassShapes(instance: instance);

                return;
            }

            // Text that carves the shapes (engraved) or takes their noise stays inside their scope; embossed text on a
            // noiseless creation bakes as its own detailed group after it, as in the per-shape branch above. A creation
            // whose every shown shape is glass has no opaque instance for the text to stay in, so it goes apart too.
            var anyOpaque = ((hiddenOpaque is null) || (Array.IndexOf(array: hiddenOpaque, value: false) >= 0));
            var textApart = (hasText && (!anyOpaque || ((creation.Noise is null) && !(creation.TextRuns ?? []).Any(predicate: static run => string.Equals(a: run.Mode, b: TextRunDocument.ModeEngrave, comparisonType: StringComparison.Ordinal)))));

            if (anyOpaque) {
                _ = builder.BeginInstance(
                    boundCenter: instance.Origin,
                    boundRadius: (reach + PlacementBoundMargin),
                    detail: ReliefDetail(detail: CreationDetail(document: creation, hidden: hiddenOpaque, scale: placement.Scale), noise: (scoped ? creation.Noise : null), scale: placement.Scale)
                );
                if (scoped) {
                    _ = builder.PushField(compose: SdfBlendOp.Union);
                }
                EmitPlacedShapes(
                    builder: builder,
                    creation: creation,
                    hidden: hiddenOpaque,
                    inScope: scoped,
                    paletteIds: paletteIds,
                    placement: placement,
                    placementOrigin: instance.Origin,
                    placementRotation: rotation,
                    reflectionNormal: instance.ReflectionNormal
                );
                if (hasText && !textApart) {
                    EmitTextRuns(instance: instance);
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

            if (textApart) {
                _ = builder.BeginInstance(
                    boundCenter: instance.Origin,
                    boundRadius: (reach + PlacementBoundMargin),
                    detail: TextDetail(creation: creation, scale: placement.Scale)
                );
                EmitTextRuns(instance: instance);
                _ = builder.EndInstance();
            }
            EmitGlassShapes(instance: instance);
        }
    }

    // Registers the palette of a responding row's authored creation and of each response entry's, in that order.
    private static void RegisterAlternatives(SdfProgramBuilder builder, WorldBakedColors colors, IReadOnlyList<WorldPrototype> creations, WorldPlacement placement, WorldStaticPalettes registered, IReadOnlyList<WorldPlacementResponse> responses) {
        for (var index = -1; (index < responses.Count); index++) {
            var id = ((index < 0) ? placement.PrototypeId : responses[index].PrototypeId);

            if (WorldDefinitionRows.FindCreation(creations: creations, id: id) is { } creation) {
                _ = registered.Resolve(builder: builder, colors: colors, creation: creation);
            }
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
    /// <param name="picks">The presentation identity table populated alongside emission, or null.</param>
    /// <param name="meshScope">What distinguishes these placements' mesh draws from another emission's placements of the
    /// same ids (an adjacent world's projection), or <see langword="null"/> for the world's own; part of each draw's
    /// <see cref="SdfMeshDraw.Identity"/>.</param>
    /// <param name="isolation">The operator's shape isolation, whose hidden shapes, with the volumes parented to them,
    /// the prototype's mesh and its text, emit nothing; <see langword="null"/> emits every shape.</param>
    /// <param name="sharedAssets">The visual scene's admitted local copies; null keeps ordinary world-space field
    /// emission, including query fields.</param>
    public static void EmitStatic(SdfProgramBuilder builder, WorldDefinition definition, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<WorldPlacement> placements, PackedFontAtlasCatalog? textCatalog = null, Func<string, (Vector3 Color, float Blend)?>? tintFor = null, ICollection<SdfVolume>? volumes = null, ICollection<SdfMeshDraw>? meshDraws = null, WorldBakedColors? colors = null, WorldStaticPalettes? palettes = null, WorldPickMapBuilder? picks = null, string? meshScope = null, WorldShapeIsolation? isolation = null, WorldRigidAssets? sharedAssets = null) {
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

            if (sharedAssets?.ContainsPlacement(placement: placement) ?? false) { continue; }

            var tint = tintFor?.Invoke(arg: placement.Id);

            // A responding row registers every creation it can show, in its authored order, before the one it shows: the
            // program's material ids then stand whichever entry holds, so a response swaps one row's materials and moves no
            // other group's, and the mesh library hands every other group its mesh as it was.
            if ((tint is null) && (placement.Respond is { Count: > 0 } responses)) {
                RegisterAlternatives(builder: builder, colors: baked, creations: creations, placement: placement, registered: registered, responses: responses);
            }

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
            var hidden = isolation?.HiddenFor(document: creation.EngineDocument, prototype: creation.Id.Value);
            var stamp = WorldPlacementStamp.Resolve(definition, placement, worldSeed: worldSeed);
            var frame = stamp.Frame;
            var key = new WorldStaticPalettes.EmissionKey(creation, textCatalog, hidden, paletteIds[0], frame.Position, stamp.Rotation, worldSeed, meshScope, (meshDraws is not null), stamp);
            var retain = ((palettes is not null) && (tint is null));

            if (retain && registered.Emissions.TryGetValue(key: placement, value: out var held) && (held.Key == key) && builder.CanAppendEmission(emission: held.Program)) {
                builder.AppendEmission(emission: held.Program);
                foreach (var volume in held.Volumes) {
                    if (volumes is not null) {
                        if (volumes.Count >= SdfProgramBuilder.MaxVolumes) { throw new InvalidOperationException(message: $"A world frame holds at most {SdfProgramBuilder.MaxVolumes} bounded volumes."); }
                        volumes.Add(item: volume);
                    }
                }
                foreach (var draw in held.Draws) { meshDraws?.Add(item: draw); }
            } else {
                var mark = (retain ? builder.MarkEmission() : default);
                var emittedVolumes = (retain ? new List<SdfVolume>() : null);
                var emittedDraws = ((retain && (meshDraws is not null)) ? new List<SdfMeshDraw>() : null);

                EmitPlacement(
                    builder: builder,
                    creation: creation.EngineDocument,
                    definition: definition,
                    paletteIds: paletteIds,
                    placement: placement,
                    textCatalog: textCatalog,
                    worldSeed: worldSeed,
                    volumes: (retain ? emittedVolumes : volumes),
                    mesh: (((meshDraws is not null) && (creation.Mesh is { } mesh))
                        ? MeshOf(mesh: mesh)
                        : null),
                    meshMaterial: paletteIds[Math.Clamp(
                        value: (creation.Mesh?.Material ?? 0),
                        max: (paletteIds.Length - 1),
                        min: 0
                    )],
                    meshDraws: (retain ? emittedDraws : meshDraws),
                    meshScope: meshScope,
                    firstMesh: (retain ? 0 : firstMesh),
                    hidden: hidden
                );
                foreach (var volume in (emittedVolumes ?? [])) {
                    if (volumes is not null) {
                        if (volumes.Count >= SdfProgramBuilder.MaxVolumes) { throw new InvalidOperationException(message: $"A world frame holds at most {SdfProgramBuilder.MaxVolumes} bounded volumes."); }
                        volumes.Add(item: volume);
                    }
                }
                if (emittedDraws is not null) {
                    foreach (var draw in emittedDraws) { meshDraws!.Add(item: draw); }
                }
                if (retain) {
                    registered.Emissions.AddOrUpdate(key: placement, value: new(key, builder.RetainEmission(mark: mark), [.. emittedVolumes!], ((emittedDraws is null) ? [] : [.. emittedDraws])));
                }
            }
            if (picks is not null) {
                var target = new WorldPickTarget(Placement: placement.Id, BodyIndex: null) { Prototype = creation.Id };

                picks.Materials(prototype: creation.Id, ids: paletteIds);

                picks.Instances(first: firstInstance, end: builder.InstanceCount, target: target);
                picks.Meshes(first: firstMesh, end: (meshDraws?.Count ?? 0), target: target);
            }
        }
    }
    /// <summary>Emits, into a builder of its own that nothing renders, each static responding row as every creation it
    /// can show, so the documents those creations compile to, their compiled profiles and meshes, and the code that
    /// emits them are ready before a response first shows one. Text is left to its first emission. Safe on any thread:
    /// it reads <paramref name="definition"/> and writes only its own builder and the shared compiled-product caches.</summary>
    /// <param name="definition">The definition whose responding rows are emitted.</param>
    /// <returns>How many row-and-creation pairs were emitted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static int PrepareResponses(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var shows = new List<WorldPlacement>();

        foreach (var placement in definition.Placements) {
            if (placement.Respond is not { Count: > 0 } responses) {
                continue;
            }

            shows.Add(item: (placement with { Holding = 0 }));

            for (var index = 0; (index < Math.Min(val1: responses.Count, val2: WorldResponseCapacity.MaxEntries)); index++) {
                shows.Add(item: (placement with { Holding = (1 << index) }));
            }
        }

        if (shows.Count > 0) {
            EmitStatic(
                builder: new SdfProgramBuilder(),
                creations: definition.Creations,
                definition: definition,
                meshDraws: new List<SdfMeshDraw>(),
                placements: shows,
                volumes: new List<SdfVolume>()
            );
        }

        return shows.Count;
    }
    /// <summary>Whether a creation row animates — the static/animated fork every consumer shares. A driver or an
    /// inverse-kinematics effector animates; a timeline frame animates only where one of its transforms differs from its
    /// shape's authored position or rotation (a state-bound value always differs from a literal). A creation whose frames
    /// carry no transforms, or only its shapes' authored poses, moves nothing, so it stamps statically.</summary>
    /// <param name="creation">The creation row.</param>
    /// <returns><see langword="true"/> when the creation's drivers, effectors or frames move a shape.</returns>
    public static bool IsAnimated(WorldPrototype creation) => CreationMotion.Animates(document: creation.Document);
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
    /// <param name="definition">The world supplying canonical placement and surface dependencies.</param>
    /// <param name="placements">The placement rows.</param>
    public static int StaticStampInstances(WorldDefinition definition, IReadOnlyList<WorldPlacement> placements) {
        var (scopedStamps, shapeInstances) = StaticStampReservation(
            definition: definition,
            placements: placements
        );

        return checked((scopedStamps + shapeInstances));
    }
    /// <summary>The static stamp reservation split by emission class: SCOPED stamps (one whole-creation instance per
    /// copy — a scoped or text-carrying creation) and per-SHAPE instances (every other copy materializes one instance
    /// per shape). KEEP IN SYNC with <c>EmitPlacement</c>'s split and <c>EmitProbe</c>'s two reservation forms.</summary>
    /// <param name="definition">The world supplying canonical placement and surface dependencies.</param>
    /// <param name="placements">The placement rows.</param>
    /// <returns>The scoped-stamp count and the per-shape instance count.</returns>
    public static (int ScopedStamps, int ShapeInstances) StaticStampReservation(WorldDefinition definition, IReadOnlyList<WorldPlacement> placements) {
        var scopedStamps = 0;
        var shapeInstances = 0;

        foreach (var placement in placements) {
            if (
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.ShownPrototypeId
            ) is not { } creation) ||
                !IsStaticStamp(
                creation: creation,
                placement: placement
            )
            ) {
                continue;
            }

            var copies = WorldPlacementStamp.Resolve(definition, placement).Count;
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
