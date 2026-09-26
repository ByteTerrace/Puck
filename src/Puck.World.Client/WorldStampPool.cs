using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The creation-stamp pool: the reserved dynamic-transform pool a creation renders through as per-shape dynamic
/// instances, presentation-only (never simulation state). Three root sources share the one pool and its one reserved
/// slot budget:
/// <list type="bullet">
/// <item><description>an animated placement (a creation carrying timeline frames) roots on the placement's static
/// stamped transform and replays its frames hold-style at the fixed cadence
/// (<see cref="WorldPlacementPolicy.TimelineSecondsPerFrame"/>);</description></item>
/// <item><description>a body-rooted stamp (an inhabited placement's body, or a crowd body wearing a creation look)
/// roots on the client's interpolated body pose, so an inhabited creation walks its authored walk cycle while its body
/// moves — that is the entire visual change over a static stamp.</description></item>
/// <item><description>an attached placement (<see cref="WorldPlacementAttach"/>) roots on the client's interpolated
/// body pose composed with the facet's local offset/yaw, so the row rides that body. It keys by placement id like an
/// animated row (a body may carry several attached rows, and the body keeps its own avatar), and its authored
/// transform is inert — <see cref="WorldPlacementStamper.IsStaticStamp"/> already skipped it.</description></item>
/// </list>
/// Reconciliation diffs delivered registrations by stable key against live ones (the same pattern camera reconciliation
/// uses): a pose/scale edit is a cheap property write (the replay clock survives), a creation-content change releases +
/// recreates (the clock resets), and a departed registration releases its pool slot at the delivery boundary (the
/// symmetric-release rule).
/// A body-rooted stamp whose look names a root <c>dynamics</c> row (<see cref="WorldLookMotion.Dynamics"/>) rides a
/// second-order follower instead of the raw interpolated body pose (<see cref="Puck.SdfVm.Views.SecondOrderFollower3"/>/
/// <c>Follower4</c>); a part named in <see cref="WorldLookMotion.PartDynamics"/> gets its own position-only follower
/// chasing the composed (already-followed) root — a secondary lag on top of the root's.
/// </summary>
/// <remarks>The pool reserves a constant dynamic-transform slot range
/// (<see cref="WorldPlacementPolicy.MaxStampRegistrations"/> × <see cref="SlotsPerPlacement"/>). Live programs emit
/// only registered geometry; unused transform slots do not need placeholder instances. The probe path
/// emits every slot in its worst-case form (full modifier envelope, worst placement scale) — the frame source measures
/// it once at construction, so a body-rooted stamp never grows the frozen floor. Single-threaded on the window-pump
/// thread, like every editor/render type here.</remarks>
public sealed partial class WorldStampPool {
    private const float GroupBoundMargin = 0.4f;

    private readonly Registration?[] m_pool = new Registration?[WorldPlacementPolicy.MaxStampRegistrations];
    private int m_packedSlotBase = -1;
    // The bounded volumes every live registration's creation authors (CreationDocument.Volumes), rebuilt by
    // PackTransforms each frame and riding the registration's root or a named shape's dynamic slot.
    private readonly List<SdfVolume> m_volumes = new(capacity: SdfProgramBuilder.MaxVolumes);

    // Latched by Tick, consumed once by the next PackTransforms — the frame delta the pool's root/part followers
    // step by (accumulates across a Tick called more than once before a pack, mirroring the replay clock above).
    private float m_pendingDeltaSeconds;

    // The placeholder document an unused/probe slot registers its constant-shape palette against.
    private static readonly CreationDocument EmptyDocument = new(
        Schema: CreationDocument.CurrentSchema,
        Name: "empty",
        Palette: null,
        Shapes: null,
        Frames: null
    );

    /// <summary>One body-rooted creation stamp the frame source requests: a population body index and the creation whose
    /// geometry rides that body's live pose (an inhabited placement's creature, or a crowd body wearing a creation
    /// look).</summary>
    /// <param name="BodyIndex">The population entity index whose interpolated pose roots the stamp.</param>
    /// <param name="Creation">The creation whose geometry the body wears.</param>
    /// <param name="Scale">The uniform render scale (a placement's scale, or a look's scale).</param>
    /// <param name="Look">The look the body wears, whose motion carries the cues, timeline replay, and the root/part
    /// second-order followers (<see cref="WorldLookMotion.Dynamics"/>/<see cref="WorldLookMotion.PartDynamics"/>), and
    /// whose state reads the registration's lease acquires when the body arrives.</param>
    public readonly record struct BodyStamp(int BodyIndex, WorldPrototype Creation, float Scale, WorldLook Look);

    // One live registration: the resolved creation, its root source (a placement row — static or attached — OR a body
    // index), and the replay cursor state.
    private sealed class Registration {
        // The body-rooted stamp's population index, or null for a row-rooted registration. An ATTACHED row leaves this
        // null deliberately: FindBody keys the one-per-body creation-look registration, and an attached row rides a body
        // WITHOUT owning its look or its part namespace.
        public int? BodyIndex;

        // The registration's reads of the client's state mirror: every lane operand, driver signal and gate, pose and
        // effector reference the look reads, bound to BodyIndex so a $body key names the wearing body. Released when
        // the registration retires, so the mirror stops reading what no body wears any more.
        public readonly WorldStateLease Reads = new();

        public float Clock;
        public required WorldPrototype Creation;
        // The look a body-rooted registration wears, or null for a row-rooted one: with Creation, the document objects
        // whose manifest templates Reads acquires when the body arrives.
        public WorldLook? Look;
        public int FrameCursor;
        // Cue state: the look's cues, each cue's timeline frame (1-based cursor, 0 = unresolved), when each next
        // self-fires on the cue clock, its fire count (the draw's seed), and the cue frame holding now (0 = none).
        public IReadOnlyList<WorldLookCue>? Cues;
        // The look's anonymous render-lane expressions (WorldLookMotion.Lanes), evaluated fresh every frame
        // against live state (WorldLookLaneEvaluator) and written into every dynamic slot this registration owns —
        // null for a row-rooted registration (row placements carry no WorldLookMotion) or an unauthored lane.
        public IReadOnlyList<ExpressionProgram?>? Lanes;

        // Whether the timeline replays on the render clock (an animated row always does; a body look only when its
        // motion says so — a cue-only timeline otherwise rests on frame 0, the live pose).
        public bool Replay = true;
        public int[] CueFrames = [];
        public float[] CueNextSeconds = [];
        public uint[] CueFires = [];

        public float CueClock;
        public int CueFrame;
        public float CueHoldUntil;

        // Pose state: each authored pose's timeline frame (1-based, 0 = unresolved) and state reference, and the pose
        // frame holding now (0 = none), re-read from the live state every PackTransforms.
        public int[] PoseFrames = [];
        public string[] PoseReferences = [];

        public int PoseFrame;

        // The cursor the frame reads: a holding pose overrides a firing cue, which overrides the replay cursor.
        public int EffectiveCursor => ((PoseFrame > 0) ? PoseFrame : ((CueFrame > 0) ? CueFrame : FrameCursor));

        // Memoized per-frame shape-id → pose index (a pure derivation of the immutable document).
        public Dictionary<int, FrameTransformDocument>?[] FramePoses = [];

        // The last text-run layout EmitOne computed for this registration's creation, and the scale it was computed
        // at (see ResolveTextLayouts) — null until the first hasText call. A creation-content change never reuses
        // this: Reconcile swaps in a brand-new Registration rather than mutating this one when the content hash
        // moves, so a stale layout can never survive onto different text.
        public TextLayoutResult[]? CachedTextLayouts;
        public float CachedTextLayoutScale;
        // The catalog the cached layouts' glyph bounds/UVs were resolved against: a definition.Text delivery packs a
        // new catalog while the creation hash (and so this Registration) survives, and a layout against the old
        // atlas must not be served against the new one.
        public object? CachedTextLayoutCatalog;
        public required AuthoredPartTable Parts;
        // The row-rooted placement (an ANIMATED or an ATTACHED one), or null for a body-rooted stamp.
        public WorldPlacement? Row;

        public float Scale = 1f;

        // The root position/orientation followers — set only for a body-rooted registration whose look names a root
        // Motion.Dynamics row (see ApplyMotion; a row-rooted registration never has this true). FollowedPosition/
        // FollowedOrientation are the values PackTransforms actually rendered this frame: the followers step at most
        // once per frame, there, so TryBodyPartAuthoredPose/TryShapePosition read the latch instead of re-stepping.
        public bool HasRootDynamics;

        // The body's WorldClient.PoseEpoch/EntityAddress this registration's followers last seeded against — -1/
        // default before the first pack. PackTransforms reseeds both root followers (and every part follower riding
        // this root) whenever either moves past this: PoseEpoch for a teleport or an over-threshold correction,
        // EntityAddress for a body index reused by a different inhabitant (a distinct address, even at the SAME
        // index and creation hash, so a same-content edit never inherits a stale follower position across it).
        public int RootEpoch = -1;

        // The per-driver animation state of this registration, advanced once per PackTransforms by
        // WorldGaitDrivers. A placement without a body reads its root pose and world state with no body facts;
        // time and ungated/state-driven swings work there too.
        public bool DriverSeeded;
        public WorldEntityAddress DriverAddress;
        public Vector3 DriverPosition;

        public Quaternion DriverOrientation = Quaternion.Identity;

        public float DriverSpeed;

        public readonly float[] DriverPhase = new float[CreationDocument.MaxDrivers];
        public readonly float[] DriverWeight = new float[CreationDocument.MaxDrivers];

        public WorldEntityAddress RootAddress;
        public SecondOrderResponse RootResponse;
        public SecondOrderFollower3 RootPositionFollower;
        public SecondOrderFollower4 RootOrientationFollower;
        public Vector3 FollowedPosition;

        public Quaternion FollowedOrientation = Quaternion.Identity;
        // Per-shape-slot part position followers, resolved from the look's PartDynamics map through Parts —
        // indexed by shape slot (the same index PackTransforms's per-shape loop and Parts.TryResolve's
        // transformSlot use), sized once to the fixed per-stamp shape budget.
        public readonly bool[] PartFollows = new bool[WorldPlacementPolicy.MaxAnimatedStampShapes];
        // The rigid delta each shape's animation produced this frame, kept so a later shape naming it as `parent`
        // rides it; PartParent is the resolved parent index per shape (−1 = the root), filled on first pack.
        public readonly Quaternion[] PartDeltaRotation = new Quaternion[WorldPlacementPolicy.MaxAnimatedStampShapes];
        public readonly Vector3[] PartDeltaTranslation = new Vector3[WorldPlacementPolicy.MaxAnimatedStampShapes];
        public readonly int[] PartParent = new int[WorldPlacementPolicy.MaxAnimatedStampShapes];

        public bool PartParentsResolved;

        // Each shape's OWN delta before the parent chain — kept because an effector folds its correction into a
        // bone's own delta and the whole chain then re-chains off these.
        public readonly Quaternion[] PartOwnRotation = new Quaternion[WorldPlacementPolicy.MaxAnimatedStampShapes];
        public readonly Vector3[] PartOwnTranslation = new Vector3[WorldPlacementPolicy.MaxAnimatedStampShapes];
        // The per-effector solve state: each effector's eased gate weight, its resolved bone/tip shape slots
        // (−1 = unresolved, so the effector is inert), and its plant latch. Sized to the document ceilings, resolved
        // once per registration.
        public readonly float[] EffectorWeight = new float[CreationDocument.MaxEffectors];
        public readonly bool[] EffectorPlanted = new bool[CreationDocument.MaxEffectors];
        public readonly Vector3[] EffectorPlantTarget = new Vector3[CreationDocument.MaxEffectors];
        // The world point each effector resolved this frame, latched purely so `body.rig` can echo the decision the
        // solve acted on; a frame that resolved nothing leaves EffectorHasTarget false.
        public readonly Vector3[] EffectorTarget = new Vector3[CreationDocument.MaxEffectors];
        public readonly bool[] EffectorHasTarget = new bool[CreationDocument.MaxEffectors];
        public readonly int[] EffectorBoneSlot = new int[(CreationDocument.MaxEffectors * CreationEffectorDocument.MaxChainBones)];
        public readonly int[] EffectorBoneCount = new int[CreationDocument.MaxEffectors];
        public readonly int[] EffectorTipSlot = new int[CreationDocument.MaxEffectors];

        public bool EffectorsResolved;

        public readonly SecondOrderResponse[] PartResponse = new SecondOrderResponse[WorldPlacementPolicy.MaxAnimatedStampShapes];
        public readonly SecondOrderFollower3[] PartFollower = new SecondOrderFollower3[WorldPlacementPolicy.MaxAnimatedStampShapes];
    }

    /// <summary>The whole pool's reserved dynamic-transform slot count — the frame source adds this onto the avatar
    /// catalog's frozen capacity.</summary>
    public static int DynamicSlotCount => (WorldPlacementPolicy.MaxStampRegistrations * SlotsPerPlacement);
    /// <summary>The dynamic-transform slots one registration reserves: its root + its full shape-slot pool.</summary>
    public static int SlotsPerPlacement => (1 + WorldPlacementPolicy.MaxAnimatedStampShapes);

    // Geometry keeps its authored transform slots; the bound may ride a member shared by the group's motion.
    // Groups with independent motion or followers retain the conservative creation-root bound.
    private static void EmitGroup(SdfProgramBuilder builder, IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex, int rootSlot, int[] paletteIds, float placementScale, bool probeWorstCase, int boundSlot, float boundRadius) {
        var groupNeedsScope = GroupNeedsScope(
            fromIndex: fromIndex,
            groupId: groupId,
            shapes: shapes
        );

        _ = builder.BeginInstanceDynamic(
            slot: boundSlot,
            boundOffset: Vector3.Zero,
            boundRadius: boundRadius
        );

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

        _ = builder.EndInstance();
    }
    // One pool slot's emission: palette, Pass 1 authored ungrouped shapes, Pass 2 blend groups, then the
    // creation's text runs as ONE root-anchored dynamic instance.
    private static void EmitOne(SdfProgramBuilder builder, WorldBakedColors colors, Registration? live, bool probeWorstCase, int rootSlot, float maxPlacementScale, PackedFontAtlasCatalog? textCatalog) {
        var document = live?.Creation.EngineDocument;
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
        var placementScale = (probeWorstCase
            ? maxPlacementScale
            : (live?.Scale ?? 1f)
        );
        // Text stays inside the probed envelope by trading capacity the validator already reserved: glyphs charge the
        // same stamp budget the boxes do (CreationDocument.StampShapeCount), each glyph chain is shorter than
        // the probe's full-modifier shape chain, and the one text instance takes the place of the last parked
        // placeholder (guaranteed parked, because a text-carrying creation has at most 47 shapes).
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

        // Pass 1 — one tight dynamic instance per authored ungrouped shape. Slot addresses remain stable even
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

            _ = builder.BeginInstanceDynamic(
                slot: slot,
                boundOffset: Vector3.Zero,
                boundRadius: boundRadius,
                active: active
            );
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
            _ = builder.EndInstance();
        }

        // Pass 2 — one instance per blend group. A group sharing one rigid animation delta can use a member's
        // tight traveling bound. Independent motion and followers retain the creation-root envelope.
        var anyPartFollows = ((live is not null) && (Array.IndexOf(array: live.PartFollows, value: true) >= 0));
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
                paletteIds: paletteIds,
                placementScale: placementScale,
                probeWorstCase: probeWorstCase,
                rootSlot: rootSlot,
                shapes: shapes
            );
        }

        // Pass 3 — the creation's text runs, riding the ROOT slot (a run sits on the creation frame, so frame replay
        // moves the boxes while the lettering holds its authored surface). Emitted after the shapes so an engrave
        // run's Subtraction carves the geometry accumulated before it, exactly as the static stamper orders it.
        if (hasText) {
            _ = builder.BeginInstanceDynamic(
                slot: rootSlot,
                boundOffset: Vector3.Zero,
                boundRadius: (reach + GroupBoundMargin)
            );
            CreationStampEmitter.EmitTextDynamic(
                builder: builder,
                document: document!,
                dynamicSlot: rootSlot,
                scale: placementScale,
                fontFor: textCatalog!.Resolve,
                materialFor: run => paletteIds[((run.Material ?? 0) % paletteIds.Length)],
                textLayouts: textLayouts
            );
            _ = builder.EndInstance();
        }
    }
    // The Registration-cached text layout for hasText's (document, scale): a cache hit when the last layout this
    // registration computed still matches the scale being rendered this call, a fresh CreationStampEmitter.LayoutTextRuns
    // otherwise (also the first call — CachedTextLayouts starts null on every freshly registered/recreated instance).
    private static TextLayoutResult[] ResolveTextLayouts(PackedFontAtlasCatalog catalog, Registration live, CreationDocument document, float scale) {
        if (
            (live.CachedTextLayouts is { } cached) &&
            (live.CachedTextLayoutScale == scale) &&
            ReferenceEquals(
                objA: live.CachedTextLayoutCatalog,
                objB: catalog
            )
        ) {
            return cached;
        }

        var layouts = CreationStampEmitter.LayoutTextRuns(
            document: document,
            fontFor: catalog.Resolve,
            scale: scale
        );

        live.CachedTextLayouts = layouts;
        live.CachedTextLayoutScale = scale;
        live.CachedTextLayoutCatalog = catalog;

        return layouts;
    }
    // One shape's emission: ResetPoint + TransformDynamic + [domain ops + static local pose, OR the per-shape slot's
    // own pre-composed pose] + [twist/bend point ops] + the scaled primitive + [dilate/onion field ops, scoped
    // outside a group; a warp or cells relief takes the same scope for its Lipschitz factor] — the fixed op sequence over the canonical CreationGeometry dimensions. probeWorstCase emits
    // EVERY op unconditionally (the probe binding rule).
    //
    // A domain-bearing shape rides its OWN per-shape slot, like any other shape — but PackTransforms packs that slot
    // with the RIGID DELTA the shape's parent chain imparts to creation space (identity when the shape has no
    // parent), never a composed root*shape pose: there is no seam to insert a domain op between "the composed pose"
    // and "this shape's own translate/rotate". So its chain mirrors CreationStampEmitter.EmitShapeChain's static
    // chain exactly, with the carried frame standing in for the placement frame: TransformDynamic(slot) ->
    // Scale(placementScale) -> the domain ops -> the shape's own (STATIC, rest-pose) Translate/Rotate -> the
    // primitive at the shape's OWN scale (the caller passes `scale` unbaked for this branch — the Scale op carries
    // the placement scale for the fold's offsets, spacings, and cells as well as the rest offset, which a baked
    // primitive scale could not). A domain-bearing shape therefore never carries its own swing/slide or a
    // frame-timeline pose (refused at validation) — only a parent's motion reaches it, and only through
    // PackTransforms's parent-delta chain (WorldStampPool.ChainPartDeltas).
    //
    // A panel's copy is emitted from a SECOND chain (its own ResetPoint + TransformDynamic + the same twist/bend
    // prefix) rather than after the plate's shape instruction, whose emission may leave a persistent Scale op on the
    // chain. placementScale converts the panel's creation-unit Inset/Depth into the world units `scale` is already
    // in, and is the Scale op a domain-bearing shape's chain carries (above).
    private static void EmitShape(SdfProgramBuilder builder, int slot, int rootSlot, SdfSolidPrimitive type, int material, Vector3 scale, bool probeWorstCase, IReadOnlyList<ShapeDomainOp>? domain = null, Vector3 shapePosition = default, Quaternion shapeRotation = default, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float twist = 0f, float bend = 0f, float dilate = 0f, float onion = 0f, bool inGroupScope = false, float taper = 0.5f, SdfPrismProfile? profile = null, SdfLift lift = SdfLift.Extrude, float rounding = 0f, float chamfer = 0f, ShapePanelDocument? panel = null, int panelMaterial = 0, float placementScale = 1f, IReadOnlyList<ShapeTrimDocument>? trims = null, IReadOnlyList<ShapeDocument>? allShapes = null, int[]? paletteIds = null, bool detail = false, ShapeFlareDocument? flare = null, float exponent = SdfProgramBuilder.MinSuperellipsoidExponent, bool secondary = true, ShapeShearDocument? shear = null, IReadOnlyList<ShapeBumpDocument>? bumps = null, SdfSweepParameters? curve = null, ShapeErodeDocument? erode = null, ShapeCellsDocument? cells = null) {
        if (probeWorstCase) { builder.ReservePathTables(shapeCount: 1); }
        // The worst-case reservation for a Sweep's fixed 3-uvec4 curve table entry — any admitted curve costs the
        // SAME table words (unlike ConvexPolygon's variable vertex count), so one representative curve inside the
        // admitted envelope (SdfProgramBuilder.MaxSweepBulgeRatio et al.) reserves it.
        var effectiveCurve = (probeWorstCase
            ? new SdfSweepParameters(A: Vector3.Zero, B: Vector3.UnitY, Bulge: 0.1f, C: (2f * Vector3.UnitY), RadiusEnd: 0.1f, RadiusStart: 0.1f, Strands: SdfProgramBuilder.MinSweepStrands, StrandOffset: 0f, Twist: 0f)
            : curve);

        SdfProgramBuilder BuildChain(bool withDomain) {
            var chain = builder.ResetPoint();
            var chainCarriesScale = (withDomain && (domain is { Count: > 0 }));

            if (chainCarriesScale) {
                chain = ShapeDomainOps.Apply(
                    chain: chain
                        .TransformDynamic(slot: slot)
                        .Scale(scale: new Vector3(value: placementScale)),
                    domain: domain
                )
                    .Translate(offset: shapePosition)
                    .Rotate(rotation: ((shapeRotation == default)
                    ? Quaternion.Identity
                    : Quaternion.Normalize(value: shapeRotation)));
            } else {
                chain = chain.TransformDynamic(slot: slot);
            }

            if (
                probeWorstCase ||
                (twist != 0f)
            ) {
                chain = chain.TwistY(rate: (probeWorstCase
                    ? 1f
                    : twist));
            }

            if (
                probeWorstCase ||
                (bend != 0f)
            ) {
                chain = chain.BendY(rate: (probeWorstCase
                    ? 1f
                    : bend));
            }

            if (
                probeWorstCase ||
                (flare is not null)
            ) {
                // Top/Span are creation-unit lengths. A domain-bearing chain already carries the placement scale as
                // its own Scale op (above), so they pass through in creation units there, exactly as on
                // CreationStampEmitter.EmitShapeChain's chain; every other chain has no Scale op, so — like the
                // rounding/chamfer the caller bakes — they take placementScale by hand. Amount/Bulge are
                // dimensionless ratios and pass through unscaled on both.
                var lengthScale = (chainCarriesScale ? 1f : placementScale);

                chain = chain.AxialProfile(
                    amount: (probeWorstCase ? ShapeFlareDocument.MaxAmount : flare!.Amount),
                    bulge: (probeWorstCase ? ShapeFlareDocument.MaxBulge : flare!.Bulge),
                    top: (probeWorstCase ? 0f : ((flare!.Top ?? 0f) * lengthScale)),
                    span: (probeWorstCase ? 1f : (flare!.Span * lengthScale)),
                    axis: (probeWorstCase ? 1 : flare!.Axis),
                    startScale: (probeWorstCase ? ShapeFlareDocument.MaxStartScale : flare!.StartScale)
                );
            }

            if (
                probeWorstCase ||
                (shear is not null)
            ) {
                // Linear is a dimensionless slope (like Flare's Amount/Bulge) and passes through unscaled; Quadratic
                // carries units of 1/length (quadratic*y*y must resolve to a length), so it takes the INVERSE of the
                // length conversion Top/Span above take — dividing by lengthScale keeps the same real-world shear
                // whichever unit space this chain's point sits in when Shear evaluates it.
                var lengthScale = (chainCarriesScale ? 1f : placementScale);

                chain = chain.Shear(
                    linear: (probeWorstCase ? ShapeDocument.MaxShear : shear!.Linear),
                    quadratic: (probeWorstCase ? ShapeDocument.MaxShear : (shear!.Quadratic / lengthScale)),
                    cubic: (probeWorstCase ? ShapeDocument.MaxShear : (shear!.Cubic / (lengthScale * lengthScale))),
                    target: (probeWorstCase ? 0 : shear!.Target),
                    driver: (probeWorstCase ? 1 : shear!.Driver)
                );
            }

            if (probeWorstCase) {
                // Worst-case reserves the widest declared bump list; every entry is otherwise identical for capacity
                // purposes (each costs the same single instruction) — Push length feeds the reach probe below,
                // not the instruction count.
                for (var i = 0; (i < ShapeBumpDocument.MaxBumps); i++) {
                    chain = chain.GaussianPush(center: Vector3.Zero, radii: Vector3.One, push: new Vector3(value: ShapeBumpDocument.MaxPushMagnitude));
                }
            } else {
                foreach (var bump in (bumps ?? [])) {
                    var lengthScale = (chainCarriesScale ? 1f : placementScale);

                    chain = chain.GaussianPush(
                        center: (bump.Center.Value * lengthScale),
                        radii: (bump.Radii.Value * lengthScale),
                        push: (bump.Push.Value * lengthScale)
                    );
                }
            }

            if (probeWorstCase || (erode is not null)) {
                // KEEP IN SYNC with CreationStampEmitter.EmitShapeChain's mirrored erode prefix. reach is this
                // shape's own SdfSolidGeometry.Reach in WORLD units — a domain-bearing chain carries the placement
                // scale as its own Scale op (chainCarriesScale), so reach takes it by hand there exactly as Flare's
                // Top/Span do; every other chain's `scale` parameter is already world-scaled.
                var reachScale = (chainCarriesScale ? placementScale : 1f);
                var erodeReach = (SdfSolidGeometry.Reach(
                    lift: lift,
                    scale: scale,
                    type: type
                ) * reachScale);

                chain = chain.LaneErode(
                    from: (probeWorstCase ? 0f : erode!.From),
                    lane: (probeWorstCase ? 0 : erode!.Lane),
                    noiseScale: (probeWorstCase ? 1f : (erode!.Noise ?? 1f)),
                    reach: erodeReach,
                    to: (probeWorstCase ? 1f : erode!.To)
                );
            }

            return chain;
        }

        var chain = BuildChain(withDomain: true);
        var wantsDilate = (probeWorstCase || (dilate != 0f));
        var wantsOnion = (probeWorstCase || (onion != 0f));
        // A panel takes the per-shape scope the field ops do — its subtraction/union must bite only this shape's own
        // candidate — and, like Group, is refused at validation whenever inGroupScope would apply here. A primitive
        // alone never needs one: every primitive is 1-Lipschitz, a non-uniformly scaled sphere or ellipsoid included
        // (the exact exponent-2 superellipsoid gauge).
        var wantsPanel = (probeWorstCase || (panel is not null));
        // A flare/shear/bump/erode is a warp (or, for erode, a noise-perturbed candidate whose Lipschitz factor
        // folds the same way — SdfProgram.Lipschitz.cs's LaneErode case), so its factor
        // (SdfProgram.FlareOperatorNorm/ShearOperatorNorm/GaussianPushLipschitz/NoiseDisplaceStepFactor) would
        // otherwise fold into the WHOLE program's step scale; its own scope clamps it onto this candidate at the pop
        // instead. Inside a group the group's scope already covers it (GroupNeedsScope). (The probe already emits the scope
        // unconditionally, so the envelope is unchanged.)
        var wantsFlare = (flare is not null);
        var wantsShear = (shear is not null);
        var wantsBumps = (bumps is { Count: > 0 });
        var wantsErode = (erode is not null);

        if (
            (wantsDilate || wantsOnion || wantsPanel || wantsFlare || wantsShear || wantsBumps || wantsErode || (cells is not null)) &&
            !inGroupScope
        ) {
            var scoped = SdfSolidGeometry.AppendScaledPrimitive(
                blend: SdfBlendOp.Union,
                chain: chain.PushField(
                    compose: blend,
                    smooth: smooth
                ),
                detail: detail,
                material: material,
                scale: scale,
                smooth: 0f,
                type: type,
                taper: taper, profile: profile,
                lift: lift, rounding: rounding, chamfer: chamfer, exponent: exponent,
                curve: effectiveCurve
            ).MarkSecondary(secondary: secondary);

            if (wantsDilate) {
                scoped = scoped.Dilate(radius: (probeWorstCase
                    ? ShapeDocument.MaxDilate
                    : dilate));
            }

            if (wantsOnion) {
                scoped = scoped.Onion(thickness: (probeWorstCase
                    ? ShapeDocument.MaxOnion
                    : onion));
            }

            if (wantsPanel) {
                // probeWorstCase reserves the recess form unconditionally at the shape's own maximum inset and a
                // full-extent depth — the instruction-word cost is the same either way; the bound a raised panel
                // needs is the caller's (the per-shape reach EmitOne packs this instance against). A panel is refused
                // with a domain, so the copy's chain never carries one; the probe's plate chain does, and dominates.
                var faceAxis = (probeWorstCase
                    ? ShapePanelDocument.DefaultFace
                    : ((panel!.Face is { } face)
                        ? face.Value
                        : ShapePanelDocument.DefaultFace));
                var placement = ShapePanelDocument.Resolve(
                    depth: (probeWorstCase
                    ? (2f * SdfSolidGeometry.HalfExtent(axis: faceAxis, lift: lift, scale: scale, type: type))
                    : (panel!.Depth * placementScale)),
                    faceAxis: faceAxis,
                    inset: (probeWorstCase
                    ? MinHalfExtent(lift: lift, scale: scale, type: type)
                    : (panel!.Inset * placementScale)),
                    lift: lift,
                    scale: scale,
                    type: type
                );

                _ = SdfSolidGeometry.AppendScaledPrimitive(
                    chain: BuildChain(withDomain: false).Translate(offset: (placement.FaceAxis * placement.Offset)),
                    type: type, taper: taper, profile: profile,
                    lift: lift, rounding: rounding, chamfer: chamfer, exponent: exponent,
                    scale: placement.ErodedScale,
                    material: (probeWorstCase ? material : panelMaterial),
                    blend: placement.Blend,
                    smooth: 0f
                );
            }

            if (probeWorstCase || (cells is not null)) {
                var relief = (cells ?? new ShapeCellsDocument(Amplitude: 0.1f, Frequency: 1f, Mode: SdfCellMode.F1, Randomness: 0.2f, Seed: 0u));
                var cellChain = builder.ResetPoint().TransformDynamic(slot: slot);

                if (domain is { Count: > 0 }) {
                    cellChain = cellChain.Translate(offset: (shapePosition * placementScale))
                        .Rotate(rotation: ((shapeRotation == default) ? Quaternion.Identity : shapeRotation));
                }
                // A capacity probe may have no live placement scale. It reserves instructions,
                // so its representative relief keeps unit coordinates instead of dividing by zero.
                var cellScale = (probeWorstCase ? 1f : placementScale);

                _ = cellChain.CellDisplace((relief.Frequency / cellScale),
                    (relief.Amplitude * cellScale), relief.Seed, relief.Mode, relief.Randomness);
            }
            _ = builder.PopField();
            EmitTrims(
                allShapes: allShapes, builder: builder, chamfer: chamfer, exponent: exponent, lift: lift, material: material,
                paletteIds: paletteIds, placementScale: placementScale, probeWorstCase: probeWorstCase, profile: profile, rootSlot: rootSlot,
                rounding: rounding, scale: scale,
                slot: slot, taper: taper, trims: trims, type: type
            );

            return;
        }

        var afterShape = SdfSolidGeometry.AppendScaledPrimitive(
            blend: blend,
            chain: chain,
            chamfer: chamfer,
            curve: effectiveCurve,
            detail: detail,
            exponent: exponent,
            lift: lift,
            material: material, profile: profile,
            rounding: rounding, scale: scale, smooth: smooth, taper: taper,
            type: type
        ).MarkSecondary(secondary: secondary);

        if (wantsDilate) {
            afterShape = afterShape.Dilate(radius: (probeWorstCase
                ? ShapeDocument.MaxDilate
                : dilate));
        }

        if (wantsOnion) {
            _ = afterShape.Onion(thickness: (probeWorstCase
                ? ShapeDocument.MaxOnion
                : onion));
        }

        if (!inGroupScope) {
            EmitTrims(
                allShapes: allShapes, builder: builder, chamfer: chamfer, exponent: exponent, lift: lift, material: material,
                paletteIds: paletteIds, placementScale: placementScale, probeWorstCase: probeWorstCase, profile: profile, rootSlot: rootSlot,
                rounding: rounding, scale: scale,
                slot: slot, taper: taper, trims: trims, type: type
            );
        }
    }
    // The panel probe's worst-case inset: the shape's smallest local half-extent, past which an eroded copy is
    // empty everywhere.
    private static float MinHalfExtent(SdfSolidPrimitive type, Vector3 scale, SdfLift lift) => MathF.Min(
        x: SdfSolidGeometry.HalfExtent(
            type: type,
            scale: scale,
            lift: lift,
            axis: Vector3.UnitX
        ),
        y: MathF.Min(
            x: SdfSolidGeometry.HalfExtent(
                type: type,
                scale: scale,
                lift: lift,
                axis: Vector3.UnitY
            ),
            y: SdfSolidGeometry.HalfExtent(
                type: type,
                scale: scale,
                lift: lift,
                axis: Vector3.UnitZ
            )
        )
    );
    private Registration? FindBody(int bodyIndex) {
        foreach (var live in m_pool) {
            if (
                (live is { BodyIndex: { } index }) &&
                (index == bodyIndex)
            ) {
                return live;
            }
        }

        return null;
    }
    private static BodyStamp? FindBodyStamp(IReadOnlyList<BodyStamp> bodyStamps, int bodyIndex) {
        foreach (var stamp in bodyStamps) {
            if (stamp.BodyIndex == bodyIndex) {
                return stamp;
            }
        }

        return null;
    }
    private Registration? FindRow(string id) {
        foreach (var live in m_pool) {
            if (
                (live is { BodyIndex: null }) &&
                string.Equals(
                a: live.Row!.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return live;
            }
        }

        return null;
    }
    // The memoized shape-id → pose index for one frame cursor (null at cursor 0 — the rest pose).
    private static Dictionary<int, FrameTransformDocument>? FramePoses(Registration live, int frameCursor) {
        if (
            (frameCursor <= 0) ||
            (live.Creation.EngineDocument.Frames is not { Count: > 0 } frames) ||
            (frameCursor > frames.Count)
        ) {
            return null;
        }

        if (live.FramePoses[frameCursor] is { } cached) {
            return cached;
        }

        var frame = frames[(frameCursor - 1)];
        var poses = new Dictionary<int, FrameTransformDocument>(capacity: frame.Transforms.Count);

        foreach (var pose in frame.Transforms) {
            poses[pose.Id] = pose;
        }

        live.FramePoses[frameCursor] = poses;

        return poses;
    }
    private int FreeSlot() {
        for (var index = 0; (index < m_pool.Length); index++) {
            if (m_pool[index] is null) {
                return index;
            }
        }

        return -1;
    }
    private static bool GroupNeedsScope(IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex) {
        for (var member = fromIndex; (member < shapes.Count); member++) {
            var shape = shapes[member];

            // A warp or relief member taxes every march in the frame unless its chain sits inside a scope, whose pop
            // clamps the group's own 1/L onto its candidate instead (SdfProgram.AnalyzeLipschitz).
            if (
                ((shape.Group ?? 0) == groupId) &&
                (((shape.Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union) || ((shape.Onion ?? 0f) != 0f) || ((shape.Dilate ?? 0f) != 0f) || (shape.Flare is not null) || (shape.Shear is not null) || (shape.Bumps is { Count: > 0 }) || (shape.Erode is not null) || (shape.Cells is not null))
            ) {
                return true;
            }
        }

        return false;
    }
    // Whether a placement row renders through THIS pool rather than as a static stamp: an animated creation (a replayed
    // timeline) or an attached row (a live body root). The exact complement of WorldPlacementStamper.IsStaticStamp for a
    // non-inhabited row — an inhabited row roots through the body-stamp census instead.
    private static bool PoolRooted(WorldPlacement row, WorldPrototype creation) =>
        ((row.Inhabit is null) && (WorldPlacementStamper.IsAnimated(creation: creation) || (row.Attach is not null)));
    private static int[] ProbePalette(SdfProgramBuilder builder) {
        var ids = new int[CreationDocument.PaletteSize];

        for (var index = 0; (index < ids.Length); index++) {
            ids[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.5f)));
        }

        return ids;
    }
    private static Registration RegisterBody(BodyStamp stamp) {
        var registration = new Registration {
            BodyIndex = stamp.BodyIndex,
            Creation = stamp.Creation,
            Look = stamp.Look,
            Parts = CreationPartCompiler.Compile(document: stamp.Creation.Document),
            Scale = stamp.Scale,
            FramePoses = new Dictionary<int, FrameTransformDocument>?[((stamp.Creation.Document.Frames?.Count ?? 0) + 1)],
            Cues = stamp.Look.Motion.Cues,
            Replay = stamp.Look.Motion.ReplayFrames,
            Lanes = stamp.Look.Motion.Lanes,
        };

        if (stamp.Look.Motion.Poses is { Count: > 0 } poses) {
            var frames = (stamp.Creation.Document.Frames ?? []);

            ResolvePoses(
                frames: frames,
                poses: poses,
                references: out registration.PoseReferences,
                timelineFrames: out registration.PoseFrames
            );
        }

        if (stamp.Look.Motion.Cues is { Count: > 0 } cues) {
            var frames = (stamp.Creation.Document.Frames ?? []);

            registration.CueFrames = new int[cues.Count];
            registration.CueNextSeconds = new float[cues.Count];
            registration.CueFires = new uint[cues.Count];

            for (var index = 0; (index < cues.Count); index++) {
                for (var frame = 0; (frame < frames.Count); frame++) {
                    if (string.Equals(a: frames[frame]?.Name, b: cues[index].Frame, comparisonType: StringComparison.Ordinal)) {
                        registration.CueFrames[index] = (frame + 1);

                        break;
                    }
                }

                registration.CueNextSeconds[index] = CueRest(
                    cue: cues[index],
                    fires: 0,
                    seed: ((uint)stamp.BodyIndex)
                );
            }
        }

        return registration;
    }
    // Resolves a body-rooted registration's follower response state from its look's Motion against the definition's
    // declared dynamics rows: HasRootDynamics/RootResponse from Motion.Dynamics, and PartFollows/PartResponse from
    // Motion.PartDynamics resolved through Parts. A row-rooted registration never calls this (Row-rooted rows carry
    // no WorldLookMotion), so HasRootDynamics/PartFollows stay at their all-false default for every animated/attached
    // placement. A dangling row or part id here — never authored, since the validator refuses one at document scope —
    // simply carries no follower for that entry, mirroring the camera compiler's own dangling-op rule. Called on
    // every Reconcile (both the same-content-edit branch and right after a fresh RegisterBody), so a live dynamics-
    // row retune takes effect on the same rebuild while every follower's Value/Velocity/Seeded state survives.
    private static void ApplyMotion(Registration live, WorldLookMotion motion, IReadOnlyList<DynamicsRow> dynamics) {
        live.HasRootDynamics = WorldDynamicsResponse.TryResolveResponse(
            name: motion.Dynamics,
            response: out live.RootResponse,
            rows: dynamics
        );

        Array.Clear(array: live.PartFollows);

        if (motion.PartDynamics is not { Count: > 0 } partDynamics) {
            return;
        }

        foreach (var (partId, rowName) in partDynamics) {
            if (
                !live.Parts.TryResolve(
                partId: partId,
                transformSlot: out var shapeSlot
            ) ||
                (((uint)shapeSlot) >= ((uint)WorldPlacementPolicy.MaxAnimatedStampShapes)) ||
                !WorldDynamicsResponse.TryResolveResponse(
                name: rowName,
                response: out var response,
                rows: dynamics
            )
            ) {
                continue;
            }

            live.PartFollows[shapeSlot] = true;
            live.PartResponse[shapeSlot] = response;
        }
    }
    // Steps a body-rooted registration's root followers once (position and, hemisphere-matched, orientation) and
    // latches the result into FollowedPosition/FollowedOrientation for every other reader this frame.
    private static void StepRootFollower(Registration live, float deltaSeconds, Vector3 targetPosition, Quaternion targetRotation) {
        (live.FollowedPosition, live.FollowedOrientation) = SecondOrderPoseFollower.StepPose(
            deltaSeconds: deltaSeconds,
            orientation: ref live.RootOrientationFollower,
            position: ref live.RootPositionFollower,
            response: in live.RootResponse,
            targetOrientation: targetRotation,
            targetPosition: targetPosition
        );
    }
    // Steps one part's position follower once, in place, and returns the eased world position.
    private static Vector3 StepPartFollower(Registration live, int shapeSlot, float deltaSeconds, Vector3 target) => live.PartFollower[shapeSlot].Step(
        response: in live.PartResponse[shapeSlot],
        deltaSeconds: deltaSeconds,
        target: target
    );
    // The root pose FollowedRootPose falls back to before the first PackTransforms has ever latched one, or for a
    // registration whose root has no dynamics — the un-followed RootPose, bit for bit.
    private static (Vector3 Position, Quaternion Rotation, float Scale) FollowedRootPose(Registration live, WorldClient client) {
        var (position, rotation, scale) = RootPose(
            client: client,
            live: live
        );

        if (!live.HasRootDynamics) {
            return (position, rotation, scale);
        }

        return (
            (live.RootPositionFollower.Seeded ? live.FollowedPosition : position),
            (live.RootOrientationFollower.Seeded ? live.FollowedOrientation : rotation),
            scale
        );
    }
    // The rest before a cue's next self-fire: a uniform draw in min..max keyed by (body, fire count) — the same body
    // blinks the same way on every run, and no two bodies in step (each body is its own stream). Infinity for a
    // demand-only cue.
    private static float CueRest(WorldLookCue cue, uint fires, uint seed) {
        if ((cue.MinSeconds is not { } min) || (cue.MaxSeconds is not { } max)) {
            return float.PositiveInfinity;
        }

        var rng = Pcg32XshRr.Create(
            state: fires,
            stream: seed
        );
        var unit = ((float)((double)rng.NextUnitFraction32()));

        return (min + ((max - min) * unit));
    }

    /// <summary>Fires one of a body look's cues now — the door a driver (a face probe reading the player's camera, a
    /// dialogue line) blinks or mouths the avatar through; the cue's self-fire interval re-arms from this fire.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="frame">The cue's frame name.</param>
    /// <returns><see langword="true"/> when the body wears a creation look with such a cue.</returns>
    public bool TriggerCue(int bodyIndex, string frame) {
        if (!TryFindBody(bodyIndex: bodyIndex, live: out var live, poolIndex: out _) || (live.Cues is not { } cues)) {
            return false;
        }

        for (var index = 0; (index < cues.Count); index++) {
            if (!string.Equals(a: cues[index].Frame, b: frame, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            FireCue(index: index, live: live);

            return true;
        }

        return false;
    }

    private static void FireCue(Registration live, int index) {
        var cue = live.Cues![index];

        live.CueFrame = live.CueFrames[index];
        live.CueHoldUntil = (live.CueClock + cue.HoldSeconds);
        live.CueFires[index]++;
        live.CueNextSeconds[index] = (live.CueHoldUntil + CueRest(
            cue: cue,
            fires: live.CueFires[index],
            seed: ((uint)(live.BodyIndex ?? 0))
        ));
    }
    private static Registration RegisterRow(WorldPlacement row, WorldPrototype creation) => new() {
        Row = row,
        Creation = creation,
        Parts = CreationPartCompiler.Compile(document: creation.Document),
        Scale = row.Scale,
        FramePoses = new Dictionary<int, FrameTransformDocument>?[((creation.Document.Frames?.Count ?? 0) + 1)],
    };
    // The root pose of a live registration: a body-rooted stamp reads the client's interpolated body pose; an ATTACHED
    // row reads that same pose composed with its authored local offset/yaw; an animated placement reads its static
    // stamped transform.
    private static (Vector3 Position, Quaternion Rotation, float Scale) RootPose(Registration live, WorldClient client) {
        if (live.BodyIndex is { } bodyIndex) {
            return (client.Position(index: bodyIndex), client.Orientation(index: bodyIndex), live.Scale);
        }

        var row = live.Row!;

        if (row.Attach is { } attach) {
            // PRESENTATION float, deliberately: this rides the client's INTERPOLATED body pose so an attached row is as
            // smooth as the body it sits on. The authoritative answer is the fixed-point one
            // (WorldPlacementAttachment.TryResolve, what world.attachments echoes); this is its render-side image, the
            // same relationship every avatar pose already has to the tick pose it interpolates. Same composition order:
            // rotate the local offset into the body's own frame, then add.
            var bodyOrientation = client.Orientation(index: attach.BodyIndex);

            return (
                (client.Position(index: attach.BodyIndex) + Vector3.Transform(
                value: attach.LocalOffset,
                rotation: bodyOrientation
            )),
                Quaternion.Normalize(value: (bodyOrientation * Quaternion.CreateFromAxisAngle(
                axis: Vector3.UnitY,
                angle: (attach.LocalYawDegrees * (MathF.PI / 180f))
            ))),
                row.Scale
            );
        }

        // An ANIMATED row's own registration carries no WorldDefinition reference (KeyedReconciler's recreate/update
        // delegates are static, capturing nothing), so a Parent-carrying animated row rides its own authored
        // Position/YawDegrees here rather than its composed frame — the ordinary (non-animated) static-stamp path
        // (WorldPlacementStamper.EmitStatic) is the one every Parent-carrying placement in the shipped games rides.
        var rotation = Quaternion.CreateFromAxisAngle(
            axis: Vector3.UnitY,
            angle: (row.YawDegrees * (MathF.PI / 180f))
        );

        return (row.Position, rotation, row.Scale);
    }
    private bool TryFindBody(int bodyIndex, out int poolIndex, out Registration live) {
        for (var index = 0; (index < m_pool.Length); index++) {
            if (
                (m_pool[index] is { BodyIndex: { } candidate } registration) &&
                (candidate == bodyIndex)
            ) {
                poolIndex = index;
                live = registration;

                return true;
            }
        }

        poolIndex = -1;
        live = null!;

        return false;
    }

    /// <summary>Emits registered geometry: each live palette, ungrouped shapes as per-slot dynamic instances, and
    /// blend groups as bounded scoped instances. Rigid groups use a traveling member bound; other groups retain
    /// the creation-root envelope. Unused registrations and shape slots emit no instances;
    /// dynamic-transform addresses remain fixed. The probe path still takes the largest legal form.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="colors">The colors the build bakes, which a registration's state-bound palette color resolves
    /// through.</param>
    /// <param name="probeWorstCase">Emit the worst-case form for capacity measurement (never rendered).</param>
    /// <param name="maxPlacementScale">Live-consumed: the placement scale envelope's ceiling
    /// (<see cref="WorldPlacementPolicyDefaults.MaxPlacementScale"/>), read fresh at every call — it only feeds spatial-cull
    /// bound radii here, never a word-capacity term, so re-reading it live cannot desync the frozen probe.</param>
    /// <param name="slotBase">The pool's first dynamic-transform slot — the same value the matching
    /// <see cref="PackTransforms"/> call packs against. Supplied by the owning emitter (which derives it from its own
    /// <see cref="Puck.SdfVm.SdfEmitContext.SlotBase"/>) rather than latched here, so the pool carries no assumption
    /// about where in the composed buffer its owner sits.</param>
    /// <param name="textCatalog">The world's packed font catalog, or <see langword="null"/> when none is resolved (a
    /// remote projection) — a registration's text runs are then omitted, exactly as the static stamper omits
    /// them.</param>
    public void Emit(SdfProgramBuilder builder, WorldBakedColors colors, bool probeWorstCase, float maxPlacementScale, int slotBase, PackedFontAtlasCatalog? textCatalog = null) {
        ArgumentNullException.ThrowIfNull(argument: colors);

        for (var index = 0; (index < m_pool.Length); index++) {
            var live = (probeWorstCase
                ? null
                : m_pool[index]
            );

            if (!probeWorstCase && (live is null)) {
                continue;
            }

            var rootSlot = (slotBase + (index * SlotsPerPlacement));

            EmitOne(
                builder: builder,
                colors: colors,
                live: live,
                maxPlacementScale: maxPlacementScale,
                probeWorstCase: probeWorstCase,
                rootSlot: rootSlot,
                textCatalog: textCatalog
            );
        }
    }
    /// <summary>Whether a live body-rooted creation look owns the entity's part namespace.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    public bool HasBodyRegistration(int bodyIndex) => (FindBody(bodyIndex: bodyIndex) is not null);

    /// <summary>Gets the bounded volumes the pool's live registrations author, as packed by the latest
    /// <see cref="PackTransforms"/> — each riding its registration's root slot or its parent shape's slot.</summary>
    public IReadOnlyList<SdfVolume> Volumes => m_volumes;

    // A volume rides the slot of the shape its parent names (the shape's own live frame, so the volume's authored
    // offset is shape-local) or the root slot; a parent past the animated shape-slot budget has no slot and falls
    // back to the root. The list is capped at the engine's volume ceiling; later registrations' volumes are dropped.
    private void AppendVolumes(CreationDocument document, float placementScale, int rootSlot, int shapeCount) {
        if (document.Volumes is not { Count: > 0 } volumes) {
            return;
        }

        var shapes = (document.Shapes ?? []);

        foreach (var volume in volumes) {
            if (!volume.Enabled) {
                continue;
            }
            if (m_volumes.Count >= SdfProgramBuilder.MaxVolumes) {
                return;
            }

            var slot = rootSlot;

            if (volume.Parent is { } parent) {
                for (var shapeIndex = 0; (shapeIndex < shapeCount); shapeIndex++) {
                    if (string.Equals(
                        a: shapes[shapeIndex].Name?.Value,
                        b: parent,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        slot = ((rootSlot + 1) + shapeIndex);

                        break;
                    }
                }
            }

            m_volumes.Add(item: volume.ToVolume(
                dynamicSlot: slot,
                origin: Vector3.Zero,
                rotation: Quaternion.Identity,
                scale: placementScale
            ));
        }
    }
    // The shape's rest pose, or the timeline frame's snapshot of it when the registration's cursor names one.
    private static (Vector3 Position, Quaternion Rotation) BasePose(ShapeDocument shape, Dictionary<int, FrameTransformDocument>? poses) => (((poses is not null) && poses.TryGetValue(
        key: shape.Id,
        value: out var pose
    ))
        ? (pose.Position.Value, pose.Rotation.Value)
        : (shape.Position.Value, shape.Rotation.Value)
    );
    // Chains every shape's own delta under its parent's chained delta, in declaration order — a parent is validated to
    // precede its children, so one forward pass resolves the whole skeleton.
    private static void ChainPartDeltas(Registration live, int shapeCount) {
        for (var shapeIndex = 0; (shapeIndex < shapeCount); shapeIndex++) {
            var rotation = live.PartOwnRotation[shapeIndex];
            var translation = live.PartOwnTranslation[shapeIndex];
            var parent = live.PartParent[shapeIndex];

            if (parent >= 0) {
                WorldGaitDrivers.Chain(
                    parentRotation: live.PartDeltaRotation[parent],
                    parentTranslation: live.PartDeltaTranslation[parent],
                    rotation: ref rotation,
                    translation: ref translation
                );
            }

            live.PartDeltaRotation[shapeIndex] = rotation;
            live.PartDeltaTranslation[shapeIndex] = translation;
        }
    }
    // Resolves each shape's `parent` name to the index of an EARLIER shape (−1 = the root); the canonicalizer refuses a
    // parent that is missing or declared later, so an unresolved name here can only be a bypassed document.
    private static void ResolvePartParents(Registration live, IReadOnlyList<ShapeDocument> shapes) {
        Array.Fill(
            array: live.PartParent,
            value: -1
        );

        var bound = Math.Min(
            val1: shapes.Count,
            val2: WorldPlacementPolicy.MaxAnimatedStampShapes
        );

        for (var child = 0; (child < bound); child++) {
            if (shapes[child].Parent is not { } parent) {
                continue;
            }

            for (var candidate = 0; (candidate < child); candidate++) {
                if (string.Equals(
                    a: shapes[candidate].Name?.Value,
                    b: parent,
                    comparisonType: StringComparison.Ordinal
                )) {
                    live.PartParent[child] = candidate;

                    break;
                }
            }
        }

        live.PartParentsResolved = true;
    }

    /// <summary>Reconciles the pool against a delivered definition (call at the delivery boundary, before the program
    /// rebuild): the animated placements root statically, the attached ones root on their target body, and the body
    /// stamps root on a population body. Diff-by-stable-key, cheap pose edits in place, release+recreate on
    /// creation-content change, symmetric release on removal. Row-rooted placements are admitted first; body stamps fill
    /// the remaining free slots.</summary>
    /// <param name="placements">The delivered placement rows.</param>
    /// <param name="creations">The delivered creation rows.</param>
    /// <param name="dynamics">The delivered <c>dynamics</c> rows — resolves each body-rooted registration's root/part
    /// followers against its look's <see cref="WorldLookMotion.Dynamics"/>/<see cref="WorldLookMotion.PartDynamics"/>.</param>
    /// <param name="bodyStamps">The resolved body-rooted stamps (inhabitants + crowd creation-looks) this frame.</param>
    public void Reconcile(IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<DynamicsRow> dynamics, IReadOnlyList<BodyStamp> bodyStamps) {
        // Diff-by-stable-key, shared by both root kinds a slot can hold (KeyedReconciler.Reconcile): the entry's
        // current row resolves the fate — gone releases the slot, changed content releases+recreates, otherwise the
        // entry updates in place (clock preserved, and the refreshed Row/Creation carries any edited offset).
        BodyStamp? TryFindBodyStamp(Registration entry) => FindBodyStamp(
            bodyIndex: entry.BodyIndex!.Value,
            bodyStamps: bodyStamps
        );
        (WorldPlacement Row, WorldPrototype Creation)? TryFindPoolRootedRow(Registration entry) {
            if (
                (WorldDefinitionRows.FindPlacement(
                placements: placements,
                id: entry.Row!.Id
            ) is not { } presentRow) ||
                (WorldDefinitionRows.FindCreation(
                creations: creations,
                id: presentRow.ShownPrototypeId
            ) is not { } presentCreation) ||
                !PoolRooted(
                creation: presentCreation,
                row: presentRow
            )
            ) {
                return null;
            }

            return (presentRow, presentCreation);
        }

        // Pass 1 — retire: a registration whose backing row/stamp vanished, went static (lost its frames or its attach
        // facet), or changed creation content releases its slot here; a same-content edit updates in place.
        for (var index = 0; (index < m_pool.Length); index++) {
            if (m_pool[index] is not { } live) {
                continue;
            }

            var reconciled = ((live.BodyIndex is not null)
                ? KeyedReconciler.Reconcile(
                    live: live,
                    tryFindRow: TryFindBodyStamp,
                    isRecreateRequired: static (entry, stamp) => !string.Equals(
                        a: stamp.Creation.Hash,
                        b: entry.Creation.Hash,
                        comparisonType: StringComparison.Ordinal
                    ),
                    recreate: stamp => {
                        var fresh = RegisterBody(stamp: stamp);

                        ApplyMotion(
                            live: fresh,
                            motion: stamp.Look.Motion,
                            dynamics: dynamics
                        );

                        return fresh;
                    },
                    update: (entry, stamp) => {
                        entry.Creation = stamp.Creation;
                        entry.Look = stamp.Look;
                        entry.Scale = stamp.Scale;
                        ApplyMotion(
                            live: entry,
                            motion: stamp.Look.Motion,
                            dynamics: dynamics
                        );
                    }
                )
                : KeyedReconciler.Reconcile(
                    live: live,
                    tryFindRow: TryFindPoolRootedRow,
                    isRecreateRequired: static (entry, found) => !string.Equals(
                        a: found.Creation.Hash,
                        b: entry.Creation.Hash,
                        comparisonType: StringComparison.Ordinal
                    ),
                    recreate: static found => RegisterRow(
                        creation: found.Creation,
                        row: found.Row
                    ),
                    update: static (entry, found) => {
                        entry.Row = found.Row;
                        entry.Creation = found.Creation;
                    }
                )
            );

            if (!ReferenceEquals(
                objA: reconciled,
                objB: live
            )) {
                live.Reads.Release();
            }

            m_pool[index] = reconciled;
        }

        // Pass 2 — admit new row-rooted (animated or attached) rows into free slots (the validator holds the ceiling; a
        // race past it skips loudly rather than corrupting a neighbor's slot).
        foreach (var placement in placements) {
            if (
                (WorldDefinitionRows.FindCreation(
                creations: creations,
                id: placement.ShownPrototypeId
            ) is not { } creation) ||
                !PoolRooted(
                creation: creation,
                row: placement
            ) ||
                (FindRow(id: placement.Id) is not null)
            ) {
                continue;
            }

            var slot = FreeSlot();

            if (slot < 0) {
                Console.Error.WriteLine(value: $"[world.placement: {((placement.Attach is null)
                    ? "animated"
                    : "attached")} '{placement.Id}' has no free stamp slot — the {WorldPlacementPolicy.MaxStampRegistrations}-slot pool is full]");

                continue;
            }

            m_pool[slot] = RegisterRow(
                creation: creation,
                row: placement
            );
        }

        // Pass 3 — admit new body-rooted stamps into the remaining free slots.
        foreach (var stamp in bodyStamps) {
            if (FindBody(bodyIndex: stamp.BodyIndex) is not null) {
                continue;
            }

            var slot = FreeSlot();

            if (slot < 0) {
                Console.Error.WriteLine(value: $"[world.placement: creation-stamp body {stamp.BodyIndex} has no free stamp slot — the {WorldPlacementPolicy.MaxStampRegistrations}-slot pool is full; it renders as a catalog avatar]");

                continue;
            }

            var fresh = RegisterBody(stamp: stamp);

            m_pool[slot] = fresh;
            ApplyMotion(
                live: fresh,
                motion: stamp.Look.Motion,
                dynamics: dynamics
            );
        }
    }
    /// <summary>Advances every live replay cursor on the render clock (hold-style: each frame holds
    /// <see cref="WorldPlacementPolicy.TimelineSecondsPerFrame"/>, looping 1..N; whole crossed frames subtract so a
    /// hitch lands on the right frame), and latches <paramref name="deltaSeconds"/> for the pool's root/part
    /// followers, stepped once by the next <see cref="PackTransforms"/>.</summary>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void Tick(float deltaSeconds) {
        m_pendingDeltaSeconds += deltaSeconds;

        foreach (var live in m_pool) {
            if (live is null) {
                continue;
            }

            var frames = (live.Creation.Document.Frames ?? []);

            if (frames.Count == 0) {
                continue;
            }

            if (live.Cues is { Count: > 0 } cues) {
                live.CueClock += deltaSeconds;

                if ((live.CueFrame > 0) && (live.CueClock >= live.CueHoldUntil)) {
                    live.CueFrame = 0;
                }

                for (var index = 0; (index < cues.Count); index++) {
                    if ((live.CueFrames[index] > 0) && (live.CueClock >= live.CueNextSeconds[index])) {
                        FireCue(index: index, live: live);
                    }
                }
            }

            if (!live.Replay) {
                continue;
            }

            live.Clock += deltaSeconds;

            while (live.Clock >= WorldPlacementPolicy.TimelineSecondsPerFrame) {
                live.Clock -= WorldPlacementPolicy.TimelineSecondsPerFrame;
                live.FrameCursor = ((live.FrameCursor % frames.Count) + 1);
            }
        }
    }
    /// <summary>Resolves a body-rooted creation look's current part pose without a packed buffer.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="client">The client supplying the entity root pose.</param>
    /// <param name="pose">The current part pose, or default when unresolved.</param>
    /// <returns><see langword="true"/> when the live creation look publishes the part.</returns>
    /// <remarks>The pose carries the shape's COMPOSED delta — its own swings and slides, the parent chain, and any
    /// effector correction — read off the latch the last <see cref="PackTransforms"/> left, so an anchor consumer
    /// and the rendered geometry answer with the same pose. A shape with no animation has an identity delta, so its
    /// anchor is the authored pose exactly as before.</remarks>
    public bool TryBodyPartAuthoredPose(int bodyIndex, string partId, WorldClient client, out SdfAnchor pose) {
        if (
            !TryFindBody(
            bodyIndex: bodyIndex,
            live: out var live,
            poolIndex: out _
        ) ||
            !live.Parts.TryResolve(
            partId: partId,
            transformSlot: out var shapeSlot
        ) ||
            (live.Creation.EngineDocument.Shapes is not { } shapes) ||
            (((uint)shapeSlot) >= ((uint)shapes.Count))
        ) {
            pose = default;

            return false;
        }

        var shape = shapes[shapeSlot];
        var poses = FramePoses(
            frameCursor: live.EffectiveCursor,
            live: live
        );

        var (localPosition, localRotation) = BasePose(
            poses: poses,
            shape: shape
        );

        // The same composed delta the write pass applies, read off the last pack's latch rather than recomputed: the
        // drivers advance once a frame, so recomputing here would either double-advance them or answer at a phase
        // nothing was drawn at.
        if (shapeSlot < WorldPlacementPolicy.MaxAnimatedStampShapes) {
            WorldGaitDrivers.Apply(
                deltaRotation: live.PartDeltaRotation[shapeSlot],
                deltaTranslation: live.PartDeltaTranslation[shapeSlot],
                position: ref localPosition,
                rotation: ref localRotation
            );
        }

        var (rootPosition, rootRotation, scale) = FollowedRootPose(
            client: client,
            live: live
        );
        var worldPosition = (rootPosition + Vector3.Transform(
            rotation: rootRotation,
            value: (localPosition * scale)
        ));

        // Read the part follower's latched value (the last PackTransforms step), never re-step it here — a follower
        // steps at most once per frame.
        if (
            (shapeSlot < WorldPlacementPolicy.MaxAnimatedStampShapes) &&
            live.PartFollows[shapeSlot] &&
            live.PartFollower[shapeSlot].Seeded
        ) {
            worldPosition = live.PartFollower[shapeSlot].Value;
        }

        pose = new SdfAnchor(
            Position: worldPosition,
            Orientation: Quaternion.Normalize(value: (rootRotation * localRotation))
        );

        return true;
    }
    /// <summary>Resolves a body-rooted creation look's authored part pose from the current packed transforms.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="transforms">The current composed transform buffer.</param>
    /// <param name="pose">The live part pose, or default when unresolved.</param>
    /// <returns><see langword="true"/> when the live creation look publishes a packed part pose.</returns>
    /// <remarks>An ordinary shape's slot IS its composed pose. A domain-bearing shape's slot carries only the
    /// parent's delta frame (see <see cref="PackTransforms"/>), so its rest pose is composed onto that frame here —
    /// the same answer <see cref="TryBodyPartAuthoredPose"/> gives for it, read from the packed buffer instead of
    /// the latch.</remarks>
    public bool TryBodyPartPose(int bodyIndex, string partId, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose) {
        if (
            (m_packedSlotBase < 0) ||
            !TryFindBody(
            bodyIndex: bodyIndex,
            live: out var live,
            poolIndex: out var poolIndex
        ) ||
            !live.Parts.TryResolve(
            partId: partId,
            transformSlot: out var shapeSlot
        )
        ) {
            pose = default;

            return false;
        }

        var transformSlot = (((m_packedSlotBase + (poolIndex * SlotsPerPlacement)) + 1) + shapeSlot);

        if (((uint)transformSlot) >= ((uint)transforms.Length)) {
            pose = default;

            return false;
        }

        var transform = transforms[transformSlot];
        var shapes = (live.Creation.EngineDocument.Shapes ?? []);

        if (
            (((uint)shapeSlot) < ((uint)shapes.Count)) &&
            (shapes[shapeSlot] is { Domain: { Count: > 0 } } folded)
        ) {
            pose = new SdfAnchor(
                Position: (transform.Position + Vector3.Transform(
                    rotation: transform.Orientation,
                    value: (folded.Position.Value * live.Scale)
                )),
                Orientation: Quaternion.Normalize(value: (transform.Orientation * folded.Rotation.Value))
            );

            return true;
        }

        pose = new SdfAnchor(
            Position: transform.Position,
            Orientation: transform.Orientation
        );

        return true;
    }
    /// <summary>Resolves a live body's root dynamic transform, or false when its render stamp is absent.</summary>
    public bool TryBodyTransformSlot(int bodyIndex, out int transformSlot) {
        if ((m_packedSlotBase < 0) || !TryFindBody(bodyIndex: bodyIndex, live: out _, poolIndex: out var poolIndex)) {
            transformSlot = -1;
            return false;
        }
        transformSlot = (m_packedSlotBase + (poolIndex * SlotsPerPlacement));
        return true;
    }
    /// <summary>Resolves a body-rooted creation look's authored part id to its absolute packed transform slot.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="transformSlot">The absolute composed-buffer slot, or -1 when unresolved.</param>
    /// <returns><see langword="true"/> when the live creation look publishes the part and its pool range has packed.</returns>
    public bool TryBodyPartTransformSlot(int bodyIndex, string partId, out int transformSlot) {
        if (
            (m_packedSlotBase < 0) ||
            !TryFindBody(
            bodyIndex: bodyIndex,
            live: out var live,
            poolIndex: out var poolIndex
        ) ||
            !live.Parts.TryResolve(
            partId: partId,
            transformSlot: out var shapeSlot
        )
        ) {
            transformSlot = -1;

            return false;
        }

        transformSlot = (((m_packedSlotBase + (poolIndex * SlotsPerPlacement)) + 1) + shapeSlot);

        return true;
    }
    /// <summary>Resolves a live registration's current-frame world position for one of its shapes (or its root when
    /// <paramref name="shapeId"/> is null) — the placement-anchor seam the audio director rides. Returns
    /// <see langword="false"/> when no live registration holds the placement (a static placement resolves through the
    /// stamp math instead), and for an attached row whose target body is not active this frame (the row contributes
    /// nothing, the same verdict <see cref="PackTransforms"/> already renders as a hidden stamp).</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="shapeId">The creation shape id to ride, or <see langword="null"/> for the stamped root.</param>
    /// <param name="client">The client whose interpolated body poses root the body-rooted stamps.</param>
    /// <param name="position">The resolved world position.</param>
    public bool TryShapePosition(string placementId, int? shapeId, WorldClient client, out Vector3 position) {
        var live = FindRow(id: placementId);

        // An inhabited placement (a body-rooted stamp) resolves through the client's body pose, keyed by placement id.
        if (
            (live is null) &&
            client.TryInhabitantBody(
            index: out var bodyIndex,
            placementId: placementId
        )
        ) {
            live = FindBody(bodyIndex: bodyIndex);
        }

        if (live is null) {
            position = default;

            return false;
        }

        // An attached row whose target body is not live resolves no position — the SAME inactive-body verdict
        // PackTransforms already applies before hiding the render stamp below the floor, mirrored here for every
        // OTHER reader of the stamped position (the audio director's placement anchor is the caller today). Without
        // this, an inactive carrier would leave the caller reading a stale/default body pose instead of treating the
        // row as absent.
        if (
            (live.Row?.Attach is { } attach) &&
            ((((uint)attach.BodyIndex) >= ((uint)WorldClient.EntityCapacity)) || !client.IsActive(index: attach.BodyIndex))
        ) {
            position = default;

            return false;
        }

        var (rootPosition, rootRotation, placementScale) = FollowedRootPose(
            client: client,
            live: live
        );

        if (shapeId is not { } targetShapeId) {
            position = rootPosition;

            return true;
        }

        var poses = FramePoses(
            frameCursor: live.EffectiveCursor,
            live: live
        );
        var shapes = (live.Creation.EngineDocument.Shapes ?? []);

        for (var shapeIndex = 0; (shapeIndex < shapes.Count); shapeIndex++) {
            var shape = shapes[shapeIndex];

            if (shape.Id != targetShapeId) {
                continue;
            }

            if (
                (shapeIndex < WorldPlacementPolicy.MaxAnimatedStampShapes) &&
                live.PartFollows[shapeIndex] &&
                live.PartFollower[shapeIndex].Seeded
            ) {
                position = live.PartFollower[shapeIndex].Value;

                return true;
            }

            var local = (((poses is not null) && poses.TryGetValue(
                key: targetShapeId,
                value: out var pose
            ))
                ? pose.Position
                : shape.Position
            );

            position = (rootPosition + Vector3.Transform(
                rotation: rootRotation,
                value: (local * placementScale)
            ));

            return true;
        }

        position = rootPosition;

        return true;
    }
    /// <summary>Resolves a live registration's current dynamic-transform slot for one of its shapes (or its root when
    /// <paramref name="shapeId"/> is null) — the seam an anchored point light rides so its GPU-read position tracks
    /// the shape every frame without a per-frame CPU pose readback. Same live/inactive verdicts as
    /// <see cref="TryShapePosition"/>, and the same root/shape slot numbering <see cref="Emit"/> packs.</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="shapeId">The creation shape id to ride, or <see langword="null"/> for the stamped root.</param>
    /// <param name="client">The client whose inhabitant lookup resolves a body-rooted placement.</param>
    /// <param name="transformSlot">The absolute packed dynamic-transform slot, or -1 when unresolved.</param>
    public bool TryShapeTransformSlot(string placementId, int? shapeId, WorldClient client, out int transformSlot) {
        transformSlot = -1;

        if (m_packedSlotBase < 0) {
            return false;
        }

        var live = FindRow(id: placementId);

        if (
            (live is null) &&
            client.TryInhabitantBody(
            index: out var bodyIndex,
            placementId: placementId
        )
        ) {
            live = FindBody(bodyIndex: bodyIndex);
        }

        if ((live is null) || ((live.BodyIndex is { } activeBody) && !client.IsActive(index: activeBody))) {
            return false;
        }

        if (
            (live.Row?.Attach is { } attach) &&
            ((((uint)attach.BodyIndex) >= ((uint)WorldClient.EntityCapacity)) || !client.IsActive(index: attach.BodyIndex))
        ) {
            return false;
        }

        var poolIndex = Array.IndexOf(array: m_pool, value: live);

        if (poolIndex < 0) {
            return false;
        }

        var rootSlot = (m_packedSlotBase + (poolIndex * SlotsPerPlacement));

        if (shapeId is { } targetShapeId) {
            var shapes = (live.Creation.EngineDocument.Shapes ?? []);

            for (var shapeIndex = 0; (shapeIndex < shapes.Count); shapeIndex++) {
                if (shapes[shapeIndex].Id == targetShapeId) {
                    transformSlot = ((rootSlot + 1) + shapeIndex);

                    return true;
                }
            }
            return false;
        }

        transformSlot = rootSlot;

        return true;
    }
}
