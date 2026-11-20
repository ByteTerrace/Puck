using System.Numerics;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldCreationInstances {
    // Embossed runs bake apart from the shapes, while sharing the root transform. An engraved run carves the
    // shapes, so it shares their instance. Separate bake groups preserve the text's own detail and bounds.
    // Indexed, since the pack asks every registration each frame.
    private static bool TextApart(CreationDocument document) {
        if ((document.TextRuns is not { Count: > 0 } runs)) {
            return false;
        }
        for (var index = 0; (index < runs.Count); index++) {
            if (runs[index].Engraves) {
                return false;
            }
        }

        return true;
    }

    // One pooled shape's part of its instance (WorldShapeDetail.PartOf): its box in the frame of the slot it rides, where
    // it sits at its authored pose (ShapeEmission.Emit).
    private static SdfInstancePart PartOf(CreationDocument document, int index, int slot, float scale) =>
        WorldShapeDetail.PartOf(document: document, index: index, slot: slot, transform: new CreationStampTransform(Vector3.Zero, Quaternion.Identity, scale, null));
    // A coupled placement's parts of its one instance: each shown opaque shape's (PartOf), refined over its own box as a
    // blend group's members are, and its engraved text's, over the creation's reach about the root at the text's detail.
    // Its glass shapes bake apart, on instances of their own.
    private static List<SdfInstancePart> CoupledParts(CreationDocument document, bool[]? hidden, CreationMotionPlan motion, float reach, int rootSlot, float scale) {
        var parts = new List<SdfInstancePart>();
        var shapes = (document.Shapes ?? []);

        for (var index = 0; (index < shapes.Count); index++) {
            if (document.IsGlassShape(shape: shapes[index]) || ((hidden is not null) && hidden[index])) { continue; }
            parts.Add(item: PartOf(document: document, index: index, scale: scale, slot: (rootSlot + motion.ShapeSlots[index])));
        }
        if (WorldPlacementStamper.TextDetail(creation: document, scale: scale) is var text and > 0f) {
            parts.Add(item: new SdfInstancePart(Detail: text, High: new Vector3(value: reach), Low: new Vector3(value: -reach), Slot: rootSlot));
        }

        return parts;
    }
    // The text slot's offset from a registration's root slot: the first after its shapes'.
    // Whether a blend group keeps any member the isolation does not hide; a wholly hidden group emits no instance.
    private static bool AnyKept(IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex, bool[]? hidden) {
        if (hidden is null) {
            return true;
        }

        for (var member = fromIndex; ((member < shapes.Count)); member++) {
            if (((shapes[member].Group ?? 0) == groupId) && !hidden[member]) {
                return true;
            }
        }

        return false;
    }
    // Geometry keeps its authored transform slots; the bound may ride a member shared by the group's motion.
    // Groups with independent motion or followers retain the conservative creation-root bound. The group's instance
    // declares a part a shown member (PartOf).
    private static void EmitGroup(SdfProgramBuilder builder, CreationDocument document, IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex, int rootSlot, int[] paletteIds, float materialScale, int boundSlot, float boundRadius, bool ownInstance, bool[]? hidden, CreationMotionPlan motion, Vector3 boundOffset) {
        // A partition that borrows another owner's carve (CreationMotionPlan.Borrows) scopes it like any other carve.
        var groupNeedsScope = (GroupNeedsScope(
            fromIndex: fromIndex,
            groupId: groupId,
            shapes: shapes
        ) || Enumerable.Range(start: fromIndex, count: (shapes.Count - fromIndex)).Any(predicate: member => motion.Borrows(renderGroup: groupId, shape: member, slot: out _)));

        if (ownInstance) {
            // Each shown member declares its own part, so the group bakes on a union's final lattice as a static copy's run
            // of the same shapes does (SdfMeshBakeGroup.Members), each part refined over its own box. A moving cutter is an
            // operand instead (CreationMotionPlan.IsOperand): the bake reads it where its slot stands, and its cut face
            // refines with the surface it cuts.
            var parts = new List<SdfInstancePart>();
            var operands = new List<SdfInstancePart>();
            var detail = 0f;

            for (var member = fromIndex; (member < shapes.Count); member++) {
                if ((hidden is not null) && hidden[member]) { continue; }
                if (motion.IsOperand(shape: member)) {
                    if (((shapes[member].Group ?? 0) == groupId) || motion.Borrows(renderGroup: groupId, shape: member, slot: out _)) {
                        operands.Add(item: PartOf(document: document, index: member, scale: 1f, slot: (rootSlot + motion.ShapeSlots[member])));
                    }
                    continue;
                }
                if ((shapes[member].Group ?? 0) != groupId) { continue; }

                var part = PartOf(document: document, index: member, scale: 1f, slot: (rootSlot + motion.ShapeSlots[member]));

                parts.Add(item: part);
                detail = (((part.Detail > 0f) && ((detail <= 0f) || (part.Detail < detail))) ? part.Detail : detail);
            }
            // A group of one shape is no union: its one lattice refines its whole bound, as an ungrouped shape's does.
            if (parts.Count == 1) { parts.Clear(); }
            _ = builder.BeginInstanceDynamic(restPoseSkin: true,
                separateBake: true, boundInSlotFrame: true,
                slot: boundSlot,
                boundOffset: boundOffset,
                boundRadius: boundRadius,
                detail: detail,
                parts: parts,
                operands: operands
            );
        }

        if (groupNeedsScope) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }

        for (var member = fromIndex; (member < shapes.Count); member++) {
            var shape = shapes[member];
            var memberSlot = motion.ShapeSlots[member];

            if (
                (((shape.Group ?? 0) != groupId) && !motion.Borrows(renderGroup: groupId, shape: member, slot: out memberSlot)) ||
                ((hidden is not null) && hidden[member])
            ) {
                continue;
            }

            var frame = new PoolShapeFrame(motion: motion, paletteIds: paletteIds, rootSlot: rootSlot, slot: (rootSlot + memberSlot));

            ShapeEmission.Emit(
                builder: builder,
                frame: in frame,
                inScope: true,
                material: WorldPlacementStamper.MaterialForShape(builder, paletteIds[((shape.Material ?? 0) % paletteIds.Length)], shape, materialScale),
                shape: shape,
                shapes: shapes
            );
        }

        if (groupNeedsScope) {
            _ = builder.PopField();
        }

        if (ownInstance) { _ = builder.EndInstance(); }
    }
    // One pool slot's emission: palette, Pass 1 authored ungrouped shapes, Pass 2 blend groups, then the
    // creation's text runs. Engraved text and its host shapes share one scoped instance so engraving cannot consume
    // another placement's accumulator; embossed text is an instance of its own.
    private static void EmitOne(SdfProgramBuilder builder, WorldBakedColors colors, Registration live, int rootSlot, PackedFontAtlasCatalog? textCatalog, WorldPickMapBuilder? picks, int[]? screenMaterials) {
        var document = live.Motion.RenderDocument;
        var shapes = (document.Shapes ?? []);
        // The isolation's hidden shapes emit nothing, and with them the paints they carry, the creation's mesh and its
        // text; their slots still pack, so a hidden parent still poses what it carries.
        var hidden = live.Hidden;
        var paletteIds = WorldPlacementStamper.RegisterPalette(
            builder: builder,
            colors: colors,
            document: document,
            hidden: hidden,
            motion: live.Motion,
            pool: (rootSlot, live.Scale),
            tint: null
        );

        picks?.Materials(prototype: live.Creation.Id, ids: paletteIds);
        // A live creation's geometry is emitted at its own size, in its slots' own units: PackTransforms gives every slot
        // the registration's scale (DynamicTransform.Scale), the bake samples each group in its slot's units and the
        // group's draws scale it, so a body drawn at another size keeps every mesh, and every bound, reach and layout
        // below is in creation units. Its material layers stay in world units, at the registration's scale, since the
        // slots' poses the shading reads are world poses.
        var materialScale = live.Scale;

        // The mesh rides the registration's root, drawn by the mesh pass rather than emitted into the program; the
        // material is this build's registration of its palette entry.
        live.Mesh = (((hidden is null) && (live.Creation.Mesh is { } mesh))
            ? WorldPlacementStamper.MeshOf(mesh: mesh)
            : null);
        live.MeshMaterial = paletteIds[Math.Clamp(
            value: (live.Creation.Mesh?.Material ?? 0),
            max: (paletteIds.Length - 1),
            min: 0
        )];
        var hasText = ((hidden is null) && (textCatalog is not null) && (document.TextRuns is { Count: > 0 }));
        // Cached on the surviving Registration, keyed by the scale it was computed for: Reconcile only swaps in a fresh
        // Registration when the creation's content hash moves (see isRecreateRequired), so a same-content rebuild reuses
        // last rebuild's layout.
        var textLayouts = (hasText
            ? ResolveTextLayouts(
                catalog: textCatalog!,
                document: document,
                live: live,
                scale: 1f
            )
            : null
        );
        if (document.Noise is not null) {
            // CreationMotionPlan admits relief only when all shapes ride the root. Keep the same composed field
            // as a static stamp, baked once in creation units; PackTransforms alone moves and scales that field.
            var glass = document.HasGlass;
            var opaqueHidden = (glass ? shapes.Select((shape, index) => document.IsGlassShape(shape) || (hidden?[index] ?? false)).ToArray() : hidden);
            int MaterialFor(ShapeDocument shape) {
                var index = -1;
                if (screenMaterials is not null) {
                    for (var candidate = 0; candidate < shapes.Count; candidate++) {
                        if (shapes[candidate].Id == shape.Id) { index = candidate; break; }
                    }
                }
                var material = (((screenMaterials is { } carried) && (index >= 0) && (carried[index] >= 0))
                    ? carried[index] : paletteIds[(shape.Material ?? 0) % paletteIds.Length]);
                return WorldPlacementStamper.MaterialForShape(builder, material, shape, materialScale);
            }
            int TextMaterial(TextRunDocument run) => paletteIds[(run.Material ?? 0) % paletteIds.Length];

            if ((opaqueHidden is null) || opaqueHidden.Contains(false) || hasText) {
                WorldPlacementStamper.EmitRootSurface(builder, document, rootSlot, opaqueHidden, hasText, textCatalog,
                    MaterialFor, TextMaterial, textLayouts: textLayouts);
            }
            if (glass) {
                var glassHidden = shapes.Select((shape, index) => !document.IsGlassShape(shape) || (hidden?[index] ?? false)).ToArray();
                if (glassHidden.Contains(false)) {
                    WorldPlacementStamper.EmitRootSurface(builder, document, rootSlot, glassHidden, false, textCatalog,
                        MaterialFor, TextMaterial, glass: true);
                }
            }
            return;
        }
        var reach = CreationStampEmitter.RenderReach(
            document: document,
            scale: 1f,
            fontFor: ((textCatalog is { } catalog)
            ? name => catalog.Resolve(name: name)
            : null),
            textLayouts: textLayouts
        );
        var anyPartFollows = (Array.IndexOf(array: live.PartFollows, value: true) >= 0);
        // A bound about the root holds every point the rig can carry a part to (CreationRig.Reach: a swing about a distant
        // pivot, a slide, a frame's pose), which the rest geometry's reach alone does not.
        var rigReach = ((anyPartFollows || !float.IsFinite(f: live.Rig.Reach)) ? 0f : live.Rig.Reach);
        var rootRadius = ((anyPartFollows ? (2f * reach) : MathF.Max(x: reach, y: rigReach)) + GroupBoundMargin);
        // Engraved text carves the shapes, so it couples them into one scoped instance; embossed text only adds relief, so
        // the shapes keep their own instances and the text bakes as its own detailed group on a slot of its own (TextSlot),
        // as the static stamper bakes a text group apart: inside a group of shapes its relief falls below their lattice.
        var textApart = (hasText && TextApart(document: document));
        var coupled = (hasText && !textApart);

        if (coupled && (live.Motion.Owners.Length > 0)) {
            EmitEngravedOwners(builder: builder, document: document, materialScale: materialScale, motion: live.Motion, paletteIds: paletteIds, radius: rootRadius,
                rootSlot: rootSlot, textCatalog: textCatalog!, textLayouts: textLayouts);
            return;
        }

        // Pass 1 — one tight dynamic instance per authored ungrouped shape, unless text couples the whole placement.
        // A glass shape keeps its own instance whatever couples the rest, so it bakes and draws apart: under a coupled
        // placement the glass shapes emit first (round 0), before the coupled instance opens for the opaque ones.
        for (var round = 0; (round < 2); round++) {
            if (round == 1) {
                if (!coupled) { break; }
                _ = builder.BeginInstanceDynamic(restPoseSkin: true,
                    separateBake: true, boundInSlotFrame: true,
                    slot: rootSlot,
                    boundOffset: Vector3.Zero,
                    boundRadius: rootRadius,
                    detail: WorldPlacementStamper.TextDetail(creation: document, scale: 1f),
                    parts: CoupledParts(document: document, hidden: hidden, motion: live.Motion, reach: reach, rootSlot: rootSlot, scale: 1f)
                ).PushField(compose: SdfBlendOp.Union);
            }

            for (var index = 0; (index < shapes.Count); index++) {
                var placed = shapes[index];

                if (placed is { Group: not null and not 0 }) {
                    continue; // Pass 2 — the shape emits inside its group's instance.
                }
                if ((hidden is not null) && hidden[index]) {
                    continue;
                }

                var ownInstance = (!coupled || document.IsGlassShape(shape: placed));

                // Uncoupled, every shape emits in round 0; coupled, the glass shapes do and the rest wait for round 1.
                if ((round == 0) != ownInstance) {
                    continue;
                }

                var slot = (rootSlot + live.Motion.ShapeSlots[index]);
                var material = (((screenMaterials is { } carried) && (carried[index] >= 0))
                    ? carried[index]
                    : paletteIds[((placed.Material ?? 0) % paletteIds.Length)]);

                if (ownInstance) {
                    _ = builder.BeginInstanceDynamic(restPoseSkin: true,
                        separateBake: true, boundInSlotFrame: true,
                        slot: slot,
                        boundOffset: ((placed.Domain is { Count: > 0 }) ? Vector3.Zero : placed.Position.Value),
                        boundRadius: ShapeBoundRadius(document: document, index: index),
                        detail: WorldShapeDetail.Of(scale: 1f, shape: placed)
                    );
                }

                var frame = new PoolShapeFrame(motion: live.Motion, paletteIds: paletteIds, rootSlot: rootSlot, slot: slot);

                ShapeEmission.Emit(
                    builder: builder,
                    frame: in frame,
                    inScope: false,
                    material: WorldPlacementStamper.MaterialForShape(builder, material, placed, materialScale),
                    shape: placed,
                    shapes: shapes
                );
                if (ownInstance) { _ = builder.EndInstance(); }
            }
        }

        // Pass 2 — one instance per blend group. A group sharing one rigid animation delta can use a member's
        // tight traveling bound. Independent motion and followers retain the creation-root envelope.
        Span<int> emittedGroups = new int[shapes.Count];
        var emittedCount = 0;

        for (var index = 0; (index < shapes.Count); index++) {
            var groupId = (shapes[index].Group ?? 0);

            if (
                (groupId == 0) ||
                emittedGroups[..emittedCount].Contains(value: groupId)
            ) {
                continue;
            }

            emittedGroups[emittedCount++] = groupId;

            // A group of operands alone was partitioned away from the surfaces it cuts, each of which takes the operands.
            if (!live.Motion.HoldsSurface(renderGroup: groupId) || !AnyKept(fromIndex: index, groupId: groupId, hidden: hidden, shapes: shapes)) {
                continue;
            }

            var boundSlot = rootSlot;
            var boundOffset = Vector3.Zero;
            var boundRadius = rootRadius;

            if (!anyPartFollows && TryTightGroupRadius(document: document, fromIndex: index, groupId: groupId, live: live, radius: out var localRadius)) {
                boundSlot = (rootSlot + live.Motion.ShapeSlots[index]);
                boundOffset = shapes[index].Position.Value;
                boundRadius = localRadius;
            }
            // A group an operand cuts bakes in its one surface owner's slot, so no vertex rides another slot; the cut never
            // grows the surface, so the surface's bound holds it.
            if (live.Motion.TryCutSlot(renderGroup: groupId, slot: out var cutSlot) && (boundSlot != (rootSlot + cutSlot))) {
                boundSlot = (rootSlot + cutSlot);
                boundOffset = Vector3.Zero;
                boundRadius = rootRadius;
            }
            EmitGroup(
                boundOffset: boundOffset,
                boundRadius: boundRadius,
                boundSlot: boundSlot,
                builder: builder,
                document: document,
                fromIndex: index,
                groupId: groupId,
                hidden: hidden,
                materialScale: materialScale,
                motion: live.Motion,
                ownInstance: !coupled,
                paletteIds: paletteIds,
                rootSlot: rootSlot,
                shapes: shapes
            );
        }

        // Pass 3 — the creation's text runs, riding the root's pose (a run sits on the creation frame, so frame replay
        // moves the boxes while the lettering holds its authored surface). Emitted after the shapes so an engrave
        // run's Subtraction carves only this creation's scoped geometry, exactly as the static stamper orders it.
        if (hasText) {
            // Text and geometry can share the root pose while keeping separate bake groups and detail.
            var textSlot = rootSlot;

            if (textApart) {
                _ = builder.BeginInstanceDynamic(restPoseSkin: true,
                    separateBake: true, boundInSlotFrame: true,
                    slot: textSlot,
                    boundOffset: Vector3.Zero,
                    boundRadius: (CreationStampEmitter.TextReach(document: document, fontFor: textCatalog!.Resolve, scale: 1f, textLayouts: textLayouts) + GroupBoundMargin),
                    detail: WorldPlacementStamper.TextDetail(creation: document, scale: 1f)
                );
            }
            CreationStampEmitter.EmitTextDynamic(
                builder: builder,
                document: document,
                dynamicSlot: textSlot,
                scale: 1f,
                fontFor: textCatalog!.Resolve,
                materialFor: run => paletteIds[((run.Material ?? 0) % paletteIds.Length)],
                textLayouts: textLayouts
            );
            if (coupled) { _ = builder.PopField(); }
            _ = builder.EndInstance();
        }
    }
    // An ungrouped pooled shape's instance bound, in creation units: an influence sphere by contract, read per tile cone
    // by the cull and per sample by the interpreter's influence skip, so it covers the shape's reach and a trim's growth
    // (at most its inset before it can win against the plain shape); the packer adds the smooth halo. It is the static
    // stamper's per-shape bound about the shape's origin, except that a domain-bearing shape's slot carries its parent's
    // delta frame, not its own pose, so its sphere centres on the slot and takes the creation-origin reach.
    private static float ShapeBoundRadius(CreationDocument document, int index) {
        var shape = document.Shapes![index];
        var trimMargin = ((shape.Trims is { Count: > 0 })
            ? ShapeTrimDocument.MaxInset
            : 0f);

        return (((shape.Domain is { Count: > 0 })
            ? CreationStampEmitter.ShapeReach(shape: shape)
            : CreationStampEmitter.ShapeStampBound(document: document, shapeIndex: index, transform: new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1f, null)).Radius
        ) + trimMargin);
    }
}
