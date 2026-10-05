using System.Numerics;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldStampPool {
    // Geometry keeps its authored transform slots; the bound may ride a member shared by the group's motion.
    // Groups with independent motion or followers retain the conservative creation-root bound.
    private static void EmitGroup(SdfProgramBuilder builder, IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex, int rootSlot, int[] paletteIds, float placementScale, bool probeWorstCase, int boundSlot, float boundRadius, SdfIndirectParticipation indirect, bool ownInstance) {
        var groupNeedsScope = GroupNeedsScope(
            fromIndex: fromIndex,
            groupId: groupId,
            shapes: shapes
        );

        if (ownInstance) {
            _ = builder.BeginInstanceDynamic(
            slot: boundSlot,
            boundOffset: Vector3.Zero,
            boundRadius: boundRadius,
            indirect: indirect
        );
        }

        if (groupNeedsScope) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }

        for (var member = fromIndex; ((member < shapes.Count) && (member < WorldPlacementPolicy.MaxAnimatedStampShapes)); member++) {
            var shape = shapes[member];

            if ((shape.Group ?? 0) != groupId) {
                continue;
            }

            EmitShape(
                bend: (shape.Bend ?? 0f),
                blend: (shape.Blend ?? SdfBlendOp.Union),
                builder: builder,
                detail: (shape.Detail ?? false),
                // Field ops and the blend radius act on the running WORLD-space accumulator directly — never
                // re-multiplied by a chain's own Scale op the way a primitive's baked-local rounding/chamfer is — so
                // they take the placement scale unconditionally, domain-carrying member or not (unlike rounding/
                // chamfer below, whose domain exemption relies on exactly that re-multiply).
                dilate: ((shape.Dilate ?? 0f) * placementScale),
                domain: (probeWorstCase ? ShapeDomainOps.ProbeWorstCase : shape.Domain),
                flare: shape.Flare,
                shear: shape.Shear,
                bumps: shape.Bumps,
                erode: shape.Erode,
                cells: shape.Cells,
                inGroupScope: true,
                material: paletteIds[((shape.Material ?? 0) % paletteIds.Length)],
                onion: ((shape.Onion ?? 0f) * placementScale),
                placementScale: placementScale,
                probeWorstCase: probeWorstCase,
                rootSlot: rootSlot,
                // A domain-bearing member's chain carries the placement scale as a Scale op (EmitShape), so its
                // primitive is emitted at the shape's OWN scale; every other member bakes the product.
                scale: ((probeWorstCase || (shape.Domain is { Count: > 0 }))
                ? shape.Scale
                : (shape.Scale * placementScale)),
                shapePosition: shape.Position,
                shapeRotation: shape.Rotation,
                slot: ((rootSlot + 1) + member),
                smooth: ((shape.Smooth ?? 0f) * placementScale),
                twist: (shape.Twist ?? 0f),
                type: shape.Type,
                taper: (shape.Taper ?? 0.5f), profile: shape.Profile,
                lift: (shape.Lift ?? SdfLift.Extrude),
                // Creation-unit radii follow the primitive's own units: baked into world units with the product
                // scale, left alone under a domain member's Scale op (the static stamper's chain scales both the same
                // way through its Scale(transform.Scale) op).
                rounding: ((shape.Rounding ?? 0f) * ((probeWorstCase || (shape.Domain is { Count: > 0 })) ? 1f : placementScale)),
                chamfer: ((shape.Chamfer ?? 0f) * ((probeWorstCase || (shape.Domain is { Count: > 0 })) ? 1f : placementScale)),
                exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
                secondary: (shape.Secondary ?? true),
                curve: shape.Curve?.Parameters()
            );
        }

        if (groupNeedsScope) {
            _ = builder.PopField();
        }

        if (ownInstance) { _ = builder.EndInstance(); }
    }
    // One pool slot's emission: palette, Pass 1 authored ungrouped shapes, Pass 2 blend groups, then the
    // creation's text runs. Text and its host shapes share one scoped instance so engraving cannot consume another
    // placement's accumulator, and excluding this placement from indirect queries removes all its operands together.
    private static void EmitOne(SdfProgramBuilder builder, WorldBakedColors colors, Registration? live, bool probeWorstCase, int rootSlot, float maxPlacementScale, PackedFontAtlasCatalog? textCatalog, WorldPickMapBuilder? picks) {
        var document = live?.Creation.EngineDocument;
        var indirect = (live?.Row?.Indirect ?? (live?.Indirect ?? SdfIndirectParticipation.Default));
        var shapes = (document?.Shapes ?? []);
        // The probe reserves a FULL distinct palette per pool slot (the conservative material bound); a live slot
        // registers its creation's real palette, an unused one a single placeholder entry — both within the probe.
        var paletteIds = (probeWorstCase
            ? ProbePalette(builder: builder)
            : WorldPlacementStamper.RegisterPalette(
                builder: builder,
                colors: colors,
                document: (document ?? EmptyDocument),
                tint: null
            )
        );

        if (live is not null) { picks?.Materials(prototype: live.Creation.Id, ids: paletteIds); }
        var placementScale = (probeWorstCase
            ? maxPlacementScale
            : (live?.Scale ?? 1f)
        );

        // The mesh rides the registration's root, drawn by the mesh pass rather than emitted into the program, so the
        // probe reserves nothing for it; the material is this build's registration of its palette entry.
        if (!probeWorstCase && (live is not null)) {
            live.Mesh = ((live.Creation.Mesh is { } mesh)
                ? WorldPlacementStamper.MeshOf(mesh: mesh)
                : null);
            live.MeshMaterial = paletteIds[Math.Clamp(
                value: (live.Creation.Mesh?.Material ?? 0),
                max: (paletteIds.Length - 1),
                min: 0
            )];
        }
        // Text stays inside the probed envelope by trading capacity the validator already reserved: glyphs charge the
        // same stamp budget the boxes do (CreationDocument.StampShapeCount), each glyph chain is shorter than
        // the probe's full-modifier shape chain. The coupled creation uses fewer instance records than the per-shape
        // probe and keeps each shape's existing local field operations inside the creation's outer scope.
        var hasText = (!probeWorstCase && (textCatalog is not null) &&
            (document is { TextRuns.Count: > 0 }) && (shapes.Count < WorldPlacementPolicy.MaxAnimatedStampShapes));
        // Cached on the surviving Registration (hasText implies live is not null — document came from live.Creation),
        // keyed by the scale it was computed for: Reconcile only swaps in a fresh Registration when the creation's
        // content hash moves (see isRecreateRequired), so a same-content rebuild reuses last rebuild's layout, and a
        // resolved-scale change (a body look's scale edit; see Reconcile's update callback) still recomputes because
        // the cached scale no longer matches.
        var textLayouts = (hasText
            ? ResolveTextLayouts(
                catalog: textCatalog!,
                document: document!,
                live: live!,
                scale: placementScale
            )
            : null
        );
        // The probe's own worst-case raised-panel term: SdfSolidGeometry.MaxPanelReach derives it from
        // ShapePanelDocument's own |Depth| validation ceiling (twice the eroded copy's own half-extent, worst case
        // at zero inset) maximized across every primitive a panel can be authored on, at the placement scale
        // envelope's own ceiling — alongside the existing 2.5x per-shape reach term.
        // The probe emits the worst-case flare (ShapeFlareDocument.MaxAmount/MaxBulge) on every shape, so its reach
        // term carries the same ProbeReachFactor the per-shape bounds below take; the worst-case shear/bump term
        // mirrors ShearBumpExtra's own probe branch against this same 2.5x reach proxy.
        var reach = ((probeWorstCase || (document is null))
            ? ((((2.5f * maxPlacementScale) * (probeWorstCase ? ShapeFlareDocument.ProbeReachFactor : 1f)) + (probeWorstCase
                ? SdfSolidGeometry.MaxPanelReach(scale: new Vector3(value: maxPlacementScale))
                : 0f)) + (probeWorstCase
                ? (((2f * ShapeDocument.MaxShear) * (2.5f * maxPlacementScale)) + ((ShapeBumpDocument.MaxBumps * ShapeBumpDocument.MaxPushMagnitude) * maxPlacementScale))
                : 0f))
            : CreationStampEmitter.RenderReach(
                document: document!,
                scale: placementScale,
                fontFor: ((textCatalog is { } catalog)
                ? name => catalog.Resolve(name: name)
                : null),
                textLayouts: textLayouts
            )
        );

        var anyPartFollows = ((live is not null) && (Array.IndexOf(array: live.PartFollows, value: true) >= 0));

        if (hasText) {
            _ = builder.BeginInstanceDynamic(rootSlot, Vector3.Zero, ((anyPartFollows ? (2f * reach) : reach) + GroupBoundMargin), indirect: indirect)
                .PushField(compose: SdfBlendOp.Union);
        }

        // Pass 1 — one tight dynamic instance per authored ungrouped shape, unless text couples the whole placement.
        // Slot addresses remain stable even
        // though unused shapes emit nothing. The probe still emits every slot with the full modifier envelope.
        for (var index = 0; (index < WorldPlacementPolicy.MaxAnimatedStampShapes); index++) {
            var placed = ((index < shapes.Count)
                ? shapes[index]
                : null
            );

            if (!probeWorstCase && (placed is null)) {
                continue;
            }

            if (placed is { Group: not null and not 0 }) {
                continue; // Pass 2 — the shape emits inside its group's instance.
            }

            var slot = ((rootSlot + 1) + index);
            var scale = ((placed?.Scale ?? Vector3.One) * placementScale);
            var material = paletteIds[((placed?.Material ?? 0) % paletteIds.Length)];
            var panelMaterial = ((placed?.Panel is { } placedPanel)
                ? paletteIds[(placedPanel.Material % paletteIds.Length)]
                : 0);
            var active = (probeWorstCase || (placed is not null));
            // A domain-bearing shape rides its own per-shape slot too (see EmitShape's remarks), but that slot
            // carries its parent's DELTA FRAME, not the shape's composed pose: its geometry sits at its rest pose
            // inside that frame and its fold images lie wherever the domain ops carry it from the frame's origin.
            // So its bound is centred on the slot (the frame origin, which travels with the parent) with the radius
            // RenderReach charges the static stamper — rest offset plus fold displacement, in placement units, plus
            // the primitive's own reach and field ops — never the tight per-shape sphere, which assumes the primitive
            // sits AT the slot. The probe takes the creation-wide reach here (the radius costs no word either way).
            var domain = (probeWorstCase
                ? ShapeDomainOps.ProbeWorstCase
                : placed?.Domain
            );
            var hasDomain = (domain is { Count: > 0 });
            // Convert the radius to creation units before composing the inverse warp bounds.
            float WarpedReach(float primitiveReach) => (ShapeWarpReach.Expand(
                ((primitiveReach / placementScale) + (placed?.Cells?.PrimitiveReachPadding(placed.Scale, placed.Flare) ?? 0f)),
                (probeWorstCase ? new(ShapeFlareDocument.MaxAmount, ShapeFlareDocument.MaxBulge, 1f, StartScale: ShapeFlareDocument.MaxStartScale) : placed?.Flare),
                (probeWorstCase ? new(ShapeDocument.MaxShear, ShapeDocument.MaxShear, ShapeDocument.MaxShear) : placed?.Shear),
                (probeWorstCase ? (ShapeBumpDocument.MaxBumps * ShapeBumpDocument.MaxPushMagnitude) : ShapeBumpDocument.ReachExtra(bumps: placed?.Bumps))) * placementScale);

            // The per-shape bound is the primitive's TRUE reach at this scale (SdfSolidGeometry.Reach — the same
            // measure the static stamper's ShapeStampBound takes) plus the shape's own outward field ops; the
            // packer adds the smooth halo. It is an INFLUENCE sphere by contract, read per tile cone by the cull
            // and per SAMPLE by the interpreter's influence skip: a naive 0.9 x max(scale) does not cover
            // a unit sphere, let alone a box's corners — the halo hid the deficit at tile
            // granularity, and the per-sample skip exposed it on every shape of the avatar.
            // A Sweep carries no SdfSolidGeometry.Reach unit-scale law (its own control points already carry
            // creation-unit dimensions — see SdfSolidPrimitive.Sweep's remarks); a panel is refused on it, so
            // panelRaise never applies.
            var tightPrimitiveReach = ((placed?.Type == SdfSolidPrimitive.Sweep)
                ? ((placed.Curve?.Reach() ?? 0f) * scale.X)
                : SdfSolidGeometry.Reach(
                type: (placed?.Type ?? SdfSolidPrimitive.Sphere),
                scale: scale,
                lift: (placed?.Lift ?? SdfLift.Extrude),
                // Depth is a creation-unit value; the raise it adds to this world-unit bound scales with the placement.
                panelRaise: ((placed?.Panel is { Depth: < 0f } raisedPanel)
                ? (-raisedPanel.Depth * placementScale)
                : 0f)
            ));
            var dilateWorld = ((placed?.Dilate ?? 0f) * placementScale);
            var onionWorld = ((placed?.Onion ?? 0f) * placementScale);
            // A trim's own scope grows the host's copy outward by at most Inset (ShapeTrimDocument.MaxInset) before
            // it can ever win the Union race against this same shape's plain instance — reserved unconditionally
            // under the probe, since a live slot's real Trims are not known until content loads.
            var trimMargin = ((probeWorstCase || (placed?.Trims is { Count: > 0 }))
                ? (ShapeTrimDocument.MaxInset * placementScale)
                : 0f);
            var boundRadius = ((hasDomain
                ? (probeWorstCase
                    ? (reach + GroupBoundMargin)
                    : (((((placed!.Position.Value.Length() + ShapeDomainOps.Reach(domain: domain)) * placementScale)
                        + WarpedReach(primitiveReach: tightPrimitiveReach))
                        + dilateWorld) + onionWorld))
                : ((WarpedReach(primitiveReach: tightPrimitiveReach) + dilateWorld) + onionWorld)
            ) + trimMargin);

            if (!hasText) {
                _ = builder.BeginInstanceDynamic(
                slot: slot,
                boundOffset: Vector3.Zero,
                boundRadius: boundRadius,
                active: active,
                indirect: indirect
            );
            }
            EmitShape(
                bend: (placed?.Bend ?? 0f),
                builder: builder,
                detail: (placed?.Detail ?? false),
                // Field ops act on the running WORLD-space accumulator directly, never re-multiplied by a chain's
                // own Scale op the way a primitive's baked-local rounding/chamfer is (below), so they take the
                // placement scale unconditionally, domain-carrying shape or not.
                dilate: ((placed?.Dilate ?? 0f) * placementScale),
                domain: domain,
                flare: placed?.Flare,
                shear: placed?.Shear,
                bumps: placed?.Bumps,
                erode: placed?.Erode,
                cells: placed?.Cells,
                material: material,
                onion: ((placed?.Onion ?? 0f) * placementScale),
                panel: placed?.Panel,
                panelMaterial: panelMaterial,
                placementScale: placementScale,
                probeWorstCase: probeWorstCase,
                rootSlot: rootSlot,
                // A domain-bearing shape's chain carries the placement scale as a Scale op (EmitShape), so its
                // primitive is emitted at the shape's OWN scale; every other shape bakes the product.
                scale: (hasDomain
                ? (placed?.Scale ?? Vector3.One)
                : scale),
                shapePosition: (placed?.Position.Value ?? default),
                shapeRotation: (placed?.Rotation.Value ?? default),
                slot: slot,
                twist: (placed?.Twist ?? 0f),
                type: (placed?.Type ?? SdfSolidPrimitive.Sphere),
                taper: (placed?.Taper ?? 0.5f), profile: placed?.Profile,
                lift: (placed?.Lift ?? SdfLift.Extrude),
                // Creation-unit radii follow the primitive's own units: baked into world units with `scale`, left
                // alone under a domain shape's Scale op — the same rule Inset/Depth take, and the static stamper's
                // chain, which scales both through its Scale(transform.Scale) op.
                rounding: ((placed?.Rounding ?? 0f) * (hasDomain ? 1f : placementScale)),
                chamfer: ((placed?.Chamfer ?? 0f) * (hasDomain ? 1f : placementScale)),
                trims: placed?.Trims,
                allShapes: shapes,
                paletteIds: paletteIds,
                exponent: (placed?.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
                secondary: (placed?.Secondary ?? true),
                curve: placed?.Curve?.Parameters()
            );
            if (!hasText) { _ = builder.EndInstance(); }
        }

        // Pass 2 — one instance per blend group. A group sharing one rigid animation delta can use a member's
        // tight traveling bound. Independent motion and followers retain the creation-root envelope.
        Span<int> emittedGroups = stackalloc int[WorldPlacementPolicy.MaxAnimatedStampShapes];
        var emittedCount = 0;

        for (var index = 0; ((index < shapes.Count) && (index < WorldPlacementPolicy.MaxAnimatedStampShapes)); index++) {
            var groupId = (shapes[index].Group ?? 0);

            if (
                (groupId == 0) ||
                emittedGroups[..emittedCount].Contains(value: groupId)
            ) {
                continue;
            }

            emittedGroups[emittedCount++] = groupId;
            var boundSlot = rootSlot;
            var boundRadius = ((anyPartFollows ? (2f * reach) : reach) + GroupBoundMargin);

            if (!probeWorstCase && !anyPartFollows && (live is not null) &&
                TryTightGroupRadius(document: document!, fromIndex: index, groupId: groupId, live: live, radius: out var localRadius)) {
                boundSlot = ((rootSlot + 1) + index);
                boundRadius = (localRadius * placementScale);
            }

            EmitGroup(
                boundRadius: boundRadius,
                boundSlot: boundSlot,
                builder: builder,
                fromIndex: index,
                groupId: groupId,
                indirect: indirect,
                ownInstance: !hasText,
                paletteIds: paletteIds,
                placementScale: placementScale,
                probeWorstCase: probeWorstCase,
                rootSlot: rootSlot,
                shapes: shapes
            );
        }

        // Pass 3 — the creation's text runs, riding the ROOT slot (a run sits on the creation frame, so frame replay
        // moves the boxes while the lettering holds its authored surface). Emitted after the shapes so an engrave
        // run's Subtraction carves only this creation's scoped geometry, exactly as the static stamper orders it.
        if (hasText) {
            CreationStampEmitter.EmitTextDynamic(
                builder: builder,
                document: document!,
                dynamicSlot: rootSlot,
                scale: placementScale,
                fontFor: textCatalog!.Resolve,
                materialFor: run => paletteIds[((run.Material ?? 0) % paletteIds.Length)],
                textLayouts: textLayouts
            );
            _ = builder.PopField().EndInstance();
        }
    }
}
