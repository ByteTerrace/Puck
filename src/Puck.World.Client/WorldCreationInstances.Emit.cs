using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldCreationInstances {
    // Whether a creation's text runs bake apart from its shapes on a slot of their own: every run embossed (an engraved
    // run carves the shapes, so it shares their instance) and a slot free after the shapes'. KEEP IN SYNC: EmitOne emits
    // the text on that slot and PackTransforms packs it with the root's pose.
    // Indexed, since the pack asks every registration each frame.
    private static bool TextApart(CreationDocument document) {
        if ((document.TextRuns is not { Count: > 0 } runs)) {
            return false;
        }
        for (var index = 0; (index < runs.Count); index++) {
            if (string.Equals(a: runs[index].Mode, b: TextRunDocument.ModeEngrave, comparisonType: StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }
    // A body's round section bakes at its radius over this many cells: each cell then turns about 19 degrees, under the
    // contour's 25-degree crease split (SdfContour.SplitAtCreases), so a strand or a finger shades smooth. At the bake's
    // own spacing (SdfMeshPathBake.MinimumCell) a section under 4.5 cm turns past it, and every vertex splits flat.
    private const float CellsPerRadius = 3f;
    // The finest spacing, in metres, a shape on an instance of its own declares: a finger or a rod, whose instance
    // refines only its own surface. Past it (a lash, a catchlight) the part bakes through the bake's own refinement on
    // its own small lattice. At the closest the chase and studio cameras come, about 0.4 m, a cell this size spans a
    // pixel or two.
    private const float OwnDetailFloor = 0.002f;
    // The finest spacing, in metres, a member of a blend group declares. A group refines its whole surface toward its
    // finest member's detail (SdfMeshBakeGroup.Detail), so a section thinner than this (a lid, a rivet, a cable) declares
    // none rather than refining the plate, shell or face it is grouped with; a hair lock, a thumb or a strand of a braid
    // declares its own.
    private const float GroupDetailFloor = 0.003f;

    // The spacing, in world units at `scale`, a body's shape asks of its bake: its round section's radius (RoundRadius)
    // over CellsPerRadius; zero when that is finer than `floor` or no finer than the bake's own lattice already is
    // (SdfMeshPathBake.MinimumCell).
    private static float BodyDetail(ShapeDocument shape, float scale, float floor) {
        var detail = (RoundRadius(shape: shape) * (scale / CellsPerRadius));

        return (((detail >= floor) && (detail < SdfMeshPathBake.MinimumCell)) ? detail : 0f);
    }
    // The radius of a shape's round section, in creation units: a strand's (its thicker end's: a lock tapers to a tip
    // that is a small part of it), a capsule's or a sphere's, or an ellipsoid's middle radius, tightened as a
    // superellipsoid's exponent squares it off, or an elliptic prism's outline's tightest bend (EllipseBend). The middle
    // radius is a finger's or a limb's girth, where its surface turns all the way round; a flattened ellipsoid (a plate,
    // a pad) turns tightly only along its rim, an edge the contour keeps as a crease, as it does a box's, another prism's
    // or a cylinder's: refining a shape's whole surface for an edge buys triangles on its broad faces and nothing on the
    // edge.
    private static float RoundRadius(ShapeDocument shape) {
        var size = (shape.Scale ?? Vector3.One);
        var middle = MathF.Max(x: MathF.Min(x: size.X, y: size.Y), y: MathF.Min(x: MathF.Max(x: size.X, y: size.Y), y: size.Z));

        return shape.Type switch {
            SdfSolidPrimitive.Sweep when (shape.Curve is { } curve) => (MathF.Max(x: curve.RadiusStart, y: curve.RadiusEnd) * size.X),
            SdfSolidPrimitive.Sphere or SdfSolidPrimitive.Ellipsoid => middle,
            SdfSolidPrimitive.Superellipsoid => (middle * (SdfProgramBuilder.MinSuperellipsoidExponent / (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent))),
            SdfSolidPrimitive.Prism when (shape.Profile is { Kind: SdfPrismProfileKind.Ellipse }) => EllipseBend(a: size.X, b: size.Y),
            SdfSolidPrimitive.Capsule => size.X,
            SdfSolidPrimitive.RoundCone => (0.5f * size.X),
            SdfSolidPrimitive.Torus => (0.4f * size.X),
            _ => 0f,
        };
    }
    // An ellipse's tightest bend: its smaller radius squared over its larger, at the larger radius's ends. An elliptic
    // prism's wall turns round it, and so does the crease it carves when it cuts (an eye socket in a face).
    private static float EllipseBend(float a, float b) => ((MathF.Max(x: a, y: b) > 0f) ? ((MathF.Min(x: a, y: b) * MathF.Min(x: a, y: b)) / MathF.Max(x: a, y: b)) : 0f);
    // The finest spacing the shown members of one instance ask of the bake (BodyDetail at GroupDetailFloor), starting from
    // `detail`: a blend group's when `groupId` names one, else a coupled placement's opaque shapes (its glass ones bake
    // apart, on instances of their own). Zero for none.
    private static float SharedDetail(CreationDocument document, int? groupId, bool[]? hidden, float scale, float detail = 0f) {
        var shapes = (document.Shapes ?? []);

        for (var index = 0; ((index < shapes.Count)); index++) {
            var shape = shapes[index];
            var member = ((groupId is { } id) ? ((shape.Group ?? 0) == id) : !document.IsGlassShape(shape: shape));

            if (!member || ((hidden is not null) && hidden[index])) { continue; }

            var own = BodyDetail(floor: GroupDetailFloor, scale: scale, shape: shape);

            detail = (((own > 0f) && ((detail <= 0f) || (own < detail))) ? own : detail);
        }

        return detail;
    }
    // The text slot's offset from a registration's root slot: the first after its shapes'.
    private static int TextSlot(int shapeCount) => (1 + shapeCount);
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
    // declares the finest spacing its members ask of the bake (SharedDetail); the probe's, with no document, none.
    private static void EmitGroup(SdfProgramBuilder builder, CreationDocument? document, IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex, int rootSlot, int[] paletteIds, float placementScale, bool probeWorstCase, int boundSlot, float boundRadius, bool ownInstance, bool[]? hidden, CreationMotionPlan? motion, Vector3 boundOffset) {
        var groupNeedsScope = GroupNeedsScope(
            fromIndex: fromIndex,
            groupId: groupId,
            shapes: shapes
        );

        if (ownInstance) {
            _ = builder.BeginInstanceDynamic(
                    separateBake: true,
            slot: boundSlot,
            boundOffset: boundOffset,
            boundRadius: boundRadius,
            detail: ((document is null) ? 0f : SharedDetail(document: document, groupId: groupId, hidden: hidden, scale: placementScale))
        );
        }

        if (groupNeedsScope) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }

        for (var member = fromIndex; ((member < shapes.Count)); member++) {
            var shape = shapes[member];

            if (((shape.Group ?? 0) != groupId) || ((hidden is not null) && hidden[member])) {
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
                warp: shape.Warp,
                displace: shape.Displace,
                elongate: shape.Elongate,
                steps: shape.Steps,
                morph: shape.Morph,
                inGroupScope: true,
                material: WorldPlacementStamper.MaterialForShape(builder, paletteIds[((shape.Material ?? 0) % paletteIds.Length)], shape, placementScale),
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
                slot: rootSlot + motion!.ShapeSlots[member],
                motion: motion,
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
                curve: shape.Curve?.Parameters()
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
    // shapeSlots bounds the shape slots the registration reserves: every shape slot the probe fills, and past every
    // shape a live creation carries.
    private static void EmitOne(SdfProgramBuilder builder, WorldBakedColors? colors, Registration? live, bool probeWorstCase, int rootSlot, float maxPlacementScale, PackedFontAtlasCatalog? textCatalog, WorldPickMapBuilder? picks, int[]? screenMaterials, int shapeSlots = 0) {
        var document = live?.Creation.EngineDocument;
        var shapes = (document?.Shapes ?? []);
        shapeSlots = shapes.Count;
        // The isolation's hidden shapes emit nothing, and with them the paints they carry, the creation's mesh and its
        // text; their slots still pack, so a hidden parent still poses what it carries.
        var hidden = (probeWorstCase ? null : live?.Hidden);
        // The probe reserves a FULL distinct palette per pool slot (the conservative material bound); a live slot
        // registers its creation's real palette, an unused one a single placeholder entry — both within the probe.
        var paletteIds = (probeWorstCase
            ? ProbePalette(builder: builder)
            : WorldPlacementStamper.RegisterPalette(
                builder: builder,
                colors: colors!,
                document: (document ?? EmptyDocument),
                pool: (rootSlot, (live?.Scale ?? 1f)),
                tint: null,
                hidden: hidden,
                motion: live?.Motion
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
            live.Mesh = (((hidden is null) && (live.Creation.Mesh is { } mesh))
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
        var hasText = (!probeWorstCase && (hidden is null) && (textCatalog is not null) &&
            (document is { TextRuns.Count: > 0 }));
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
        // Engraved text carves the shapes, so it couples them into one scoped instance; embossed text only adds relief, so
        // the shapes keep their own instances and the text bakes as its own detailed group on a slot of its own (TextSlot),
        // as the static stamper bakes a text group apart: inside a group of shapes its relief falls below their lattice.
        var textApart = (hasText && TextApart(document: document!));
        var coupled = (hasText && !textApart);

        // Pass 1 — one tight dynamic instance per authored ungrouped shape, unless text couples the whole placement.
        // Slot addresses remain stable even
        // though unused shapes emit nothing. The probe still emits every slot with the full modifier envelope. A glass
        // shape keeps its own instance whatever couples the rest, so it bakes and draws apart: under a coupled
        // placement the glass shapes emit first (round 0), before the coupled instance opens for the opaque ones.
        for (var round = 0; (round < 2); round++) {
            if (round == 1) {
                if (!coupled) { break; }
                _ = builder.BeginInstanceDynamic(
                    separateBake: true,
                    slot: rootSlot,
                    boundOffset: Vector3.Zero,
                    boundRadius: ((anyPartFollows ? (2f * reach) : reach) + GroupBoundMargin),
                    detail: SharedDetail(detail: WorldPlacementStamper.TextDetail(creation: document!, scale: placementScale), document: document!, groupId: null, hidden: hidden, scale: placementScale)
                ).PushField(compose: SdfBlendOp.Union);
            }

            for (var index = 0; (index < shapeSlots); index++) {
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
                if ((hidden is not null) && hidden[index]) {
                    continue;
                }
    
                var glass = ((placed is not null) && (document is not null) && document.IsGlassShape(shape: placed));
                var ownInstance = (!coupled || glass);
    
                // Uncoupled, every shape emits in round 0; coupled, the glass shapes do and the rest wait for round 1.
                if ((round == 0) != (ownInstance)) {
                    continue;
                }
    
                var slot = rootSlot + live!.Motion.ShapeSlots[index];
                var scale = ((placed?.Scale ?? Vector3.One) * placementScale);
                var material = (((screenMaterials is { } carried) && (carried[index] >= 0))
                    ? carried[index]
                    : paletteIds[((placed?.Material ?? 0) % paletteIds.Length)]);
                material = WorldPlacementStamper.MaterialForShape(builder, material, placed!, placementScale);
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
                    (probeWorstCase ? ShapeWarpReach.ProbePointReach : ((placed is null) ? 0f : ShapeWarpReach.PointReach(shape: placed)))) * placementScale);
    
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
                // A sine relief carries the surface outward by its amplitude, as a dilation does.
                var onionWorld = (((placed?.Onion ?? 0f) + (probeWorstCase ? ShapeDisplaceDocument.MaxAmplitude : (placed?.Displace?.Reach ?? 0f))) * placementScale);
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
    
                if (ownInstance) {
                    _ = builder.BeginInstanceDynamic(
                    separateBake: true,
                    slot: slot,
                    boundOffset: hasDomain ? Vector3.Zero : placed!.Position.Value * placementScale,
                    boundRadius: boundRadius,
                    active: active,
                    detail: ((probeWorstCase || (placed is null)) ? 0f : BodyDetail(floor: OwnDetailFloor, scale: placementScale, shape: placed))
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
                    warp: placed?.Warp,
                    displace: placed?.Displace,
                    elongate: placed?.Elongate,
                    steps: placed?.Steps,
                    morph: placed?.Morph,
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
                    motion: live!.Motion,
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
                    curve: placed?.Curve?.Parameters()
                );
                if (ownInstance) { _ = builder.EndInstance(); }
            }
        }

        // Pass 2 — one instance per blend group. A group sharing one rigid animation delta can use a member's
        // tight traveling bound. Independent motion and followers retain the creation-root envelope.
        Span<int> emittedGroups = new int[shapes.Count];
        var emittedCount = 0;

        for (var index = 0; ((index < shapes.Count)); index++) {
            var groupId = (shapes[index].Group ?? 0);

            if (
                (groupId == 0) ||
                emittedGroups[..emittedCount].Contains(value: groupId)
            ) {
                continue;
            }

            emittedGroups[emittedCount++] = groupId;

            if (!AnyKept(fromIndex: index, groupId: groupId, hidden: hidden, shapes: shapes)) {
                continue;
            }

            var boundSlot = rootSlot;
            var boundOffset = Vector3.Zero;
            var boundRadius = ((anyPartFollows ? (2f * reach) : reach) + GroupBoundMargin);


            if (!anyPartFollows && live is not null && TryTightGroupRadius(live, document!, index, groupId, out var localRadius)) {
                boundSlot = rootSlot + live.Motion.ShapeSlots[index];
                boundOffset = shapes[index].Position.Value * placementScale;
                boundRadius = localRadius * placementScale;
            }
            EmitGroup(
                boundOffset: boundOffset,
                boundRadius: boundRadius,
                boundSlot: boundSlot,
                builder: builder,
                document: (probeWorstCase ? null : document),
                fromIndex: index,
                groupId: groupId,
                ownInstance: !coupled,
                paletteIds: paletteIds,
                placementScale: placementScale,
                probeWorstCase: probeWorstCase,
                rootSlot: rootSlot,
                shapes: shapes,
                hidden: hidden,
                motion: live!.Motion
            );
        }

        // Pass 3 — the creation's text runs, riding the root's pose (a run sits on the creation frame, so frame replay
        // moves the boxes while the lettering holds its authored surface). Emitted after the shapes so an engrave
        // run's Subtraction carves only this creation's scoped geometry, exactly as the static stamper orders it.
        if (hasText) {
            // Apart, the text rides its own slot, which holds the root's pose, so its group holds the glyphs alone (a blend
            // group riding the root would otherwise bake with them at its own spacing), bounded by their reach.
            var textSlot = rootSlot;

            if (textApart) {
                _ = builder.BeginInstanceDynamic(
                    separateBake: true,
                    slot: textSlot,
                    boundOffset: Vector3.Zero,
                    boundRadius: (CreationStampEmitter.TextReach(document: document!, fontFor: textCatalog!.Resolve, scale: placementScale, textLayouts: textLayouts) + GroupBoundMargin),
                    detail: WorldPlacementStamper.TextDetail(creation: document!, scale: placementScale)
                );
            }
            CreationStampEmitter.EmitTextDynamic(
                builder: builder,
                document: document!,
                dynamicSlot: textSlot,
                scale: placementScale,
                fontFor: textCatalog!.Resolve,
                materialFor: run => paletteIds[((run.Material ?? 0) % paletteIds.Length)],
                textLayouts: textLayouts
            );
            if (coupled) { _ = builder.PopField(); }
            _ = builder.EndInstance();
        }
    }
}
