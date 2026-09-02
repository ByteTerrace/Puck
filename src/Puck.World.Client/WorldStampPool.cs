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
/// <remarks>The pool is emitted on every rebuild with a constant slot count
/// (<see cref="WorldPlacementPolicy.MaxStampRegistrations"/> × <see cref="SlotsPerPlacement"/>); an unused slot draws a
/// parked placeholder hidden below the floor, exactly like the avatar catalog's inactive-slot story. The probe path
/// emits every slot in its worst-case form (full modifier envelope, worst placement scale) — the frame source measures
/// it once at construction, so a body-rooted stamp never grows the frozen floor. Single-threaded on the window-pump
/// thread, like every editor/render type here.</remarks>
public sealed partial class WorldStampPool {
    private const float GroupBoundMargin = 0.4f;
    // Per-shape dynamic-instance bound at unit scale — a cull contract, not a policy: too tight clips a shape at
    // its own tile boundary.
    private const float InstanceRadiusUnitScale = 0.9f;

    private readonly Registration?[] m_pool = new Registration?[WorldPlacementPolicy.MaxStampRegistrations];
    private int m_packedSlotBase = -1;

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
    /// <param name="Motion">The look's motion — cues, timeline replay, and the root/part second-order followers
    /// (<see cref="WorldLookMotion.Dynamics"/>/<see cref="WorldLookMotion.PartDynamics"/>).</param>
    public readonly record struct BodyStamp(int BodyIndex, WorldPrototype Creation, float Scale, WorldLookMotion Motion);

    // One live registration: the resolved creation, its root source (a placement row — static or attached — OR a body
    // index), and the replay cursor state.
    private sealed class Registration {
        // The body-rooted stamp's population index, or null for a row-rooted registration. An ATTACHED row leaves this
        // null deliberately: FindBody keys the one-per-body creation-look registration, and an attached row rides a body
        // WITHOUT owning its look or its part namespace.
        public int? BodyIndex;
        public float Clock;
        public required WorldPrototype Creation;
        public int FrameCursor;
        // Cue state: the look's cues, each cue's timeline frame (1-based cursor, 0 = unresolved), when each next
        // self-fires on the cue clock, its fire count (the draw's seed), and the cue frame holding now (0 = none).
        public IReadOnlyList<WorldLookCue>? Cues;

        // Whether the timeline replays on the render clock (an animated row always does; a body look only when its
        // motion says so — a cue-only timeline otherwise rests on frame 0, the live pose).
        public bool Replay = true;
        public int[] CueFrames = [];
        public float[] CueNextSeconds = [];
        public uint[] CueFires = [];

        public float CueClock;
        public int CueFrame;
        public float CueHoldUntil;

        // The cursor the frame reads: a firing cue's frame overrides the replay cursor for its hold.
        public int EffectiveCursor => ((CueFrame > 0) ? CueFrame : FrameCursor);

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
        public required string Key;
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

        // The per-driver animation state of the body this registration rides, advanced once per PackTransforms by
        // WorldGaitDrivers. A registration with no BodyIndex never advances them, so its weights stay zero and every
        // authored swing/slide on it composes the identity — a placed creation has no body facts to gate on.
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
        public readonly int[] EffectorBoneSlot = new int[CreationDocument.MaxEffectors * CreationEffectorDocument.MaxChainBones];
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

    // follows widens the group's root-anchored cull bound for a member riding its own part follower: the bound
    // sphere is fixed at the root slot with the creation's static reach, but a followed member's own transform slot
    // carries the LAGGED world position, which can leave that fixed sphere once the lag exceeds GroupBoundMargin.
    // Doubling reach is a cull-only cost (no geometry moves), so it is charged only when this registration actually
    // has a part follower.
    private static void EmitGroup(SdfProgramBuilder builder, IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex, int rootSlot, int[] paletteIds, float placementScale, bool probeWorstCase, float reach, bool follows) {
        var groupNeedsScope = GroupNeedsScope(
            fromIndex: fromIndex,
            groupId: groupId,
            shapes: shapes
        );

        _ = builder.BeginInstanceDynamic(
            slot: rootSlot,
            boundOffset: Vector3.Zero,
            boundRadius: ((follows ? (2f * reach) : reach) + GroupBoundMargin)
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
                dilate: (shape.Dilate ?? 0f),
                domain: (probeWorstCase ? ShapeDomainOps.ProbeWorstCase : shape.Domain),
                inGroupScope: true,
                material: paletteIds[((shape.Material ?? 0) % paletteIds.Length)],
                onion: (shape.Onion ?? 0f),
                probeWorstCase: probeWorstCase,
                rootSlot: rootSlot,
                scale: (shape.Scale * placementScale),
                shapePosition: shape.Position,
                shapeRotation: shape.Rotation,
                slot: ((rootSlot + 1) + member),
                smooth: (shape.Smooth ?? 0f),
                twist: (shape.Twist ?? 0f),
                type: shape.Type
            );
        }

        if (groupNeedsScope) {
            _ = builder.PopField();
        }

        _ = builder.EndInstance();
    }
    // One pool slot's emission: palette, Pass 1 ungrouped shapes / parked placeholders, Pass 2 blend groups, then the
    // creation's text runs as ONE root-anchored dynamic instance.
    private static void EmitOne(SdfProgramBuilder builder, WorldDefinition definition, Registration? live, bool probeWorstCase, int rootSlot, float maxPlacementScale, PackedFontAtlasCatalog? textCatalog) {
        var document = live?.Creation.EngineDocument;
        var shapes = (document?.Shapes ?? []);
        // The probe reserves a FULL distinct palette per pool slot (the conservative material bound); a live slot
        // registers its creation's real palette, an unused one a single placeholder entry — both within the probe.
        var paletteIds = (probeWorstCase
            ? ProbePalette(builder: builder)
            : WorldPlacementStamper.RegisterPalette(
                builder: builder,
                definition: definition,
                document: (document ?? EmptyDocument),
                tint: null
            )
        );
        var placementScale = (probeWorstCase
            ? maxPlacementScale
            : (live?.Scale ?? 1f)
        );
        // Text stays inside the probed envelope by trading capacity the validator already reserved: glyphs charge the
        // same 48-shape stamp budget the boxes do (CreationDocument.StampShapeCount), each glyph chain is shorter than
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
        var reach = ((probeWorstCase || (document is null))
            ? (2.5f * maxPlacementScale)
            : CreationStampEmitter.RenderReach(
                document: document!,
                scale: placementScale,
                fontFor: ((textCatalog is { } catalog)
                ? name => catalog.Resolve(name: name)
                : null),
                textLayouts: textLayouts
            )
        );

        // Pass 1 — ungrouped shapes and unused slots: one tight dynamic instance per shape slot; parked when absent
        // (the beam cull skips it with one branch). The probe stays fully active with the full modifier envelope.
        for (var index = 0; (index < WorldPlacementPolicy.MaxAnimatedStampShapes); index++) {
            var placed = ((index < shapes.Count)
                ? shapes[index]
                : null
            );

            if (
                hasText &&
                (placed is null) &&
                (index == (WorldPlacementPolicy.MaxAnimatedStampShapes - 1))
            ) {
                continue; // The text instance below spends this parked placeholder's slot in the probed envelope.
            }

            if (placed is { Group: not null and not 0 }) {
                continue; // Pass 2 — the shape emits inside its group's instance.
            }

            var slot = ((rootSlot + 1) + index);
            var scale = ((placed?.Scale ?? Vector3.One) * placementScale);
            var material = paletteIds[((placed?.Material ?? 0) % paletteIds.Length)];
            var active = (probeWorstCase || (placed is not null));
            // A domain-bearing shape cannot ride its own per-shape slot (see EmitShape's remarks); its geometry rides
            // the ROOT slot instead, so its bound must too — the tight per-shape bound below assumes the primitive
            // sits AT the per-shape slot's own transform, which a domain fold's reach and translated local pose both
            // violate. The probe always takes this (larger) form: it dominates the per-shape bound's word/segment
            // cost for any real content at this index.
            var domain = (probeWorstCase
                ? ShapeDomainOps.ProbeWorstCase
                : placed?.Domain
            );
            var hasDomain = (domain is { Count: > 0 });

            _ = builder.BeginInstanceDynamic(
                slot: (hasDomain ? rootSlot : slot),
                boundOffset: Vector3.Zero,
                boundRadius: (hasDomain
                ? (reach + GroupBoundMargin)
                : (InstanceRadiusUnitScale * MaxComponent(scale: scale))),
                active: active
            );
            EmitShape(
                bend: (placed?.Bend ?? 0f),
                builder: builder,
                dilate: (placed?.Dilate ?? 0f),
                domain: domain,
                material: material,
                onion: (placed?.Onion ?? 0f),
                probeWorstCase: probeWorstCase,
                rootSlot: rootSlot,
                scale: scale,
                shapePosition: (placed?.Position.Value ?? default),
                shapeRotation: (placed?.Rotation.Value ?? default),
                slot: slot,
                twist: (placed?.Twist ?? 0f),
                type: (placed?.Type ?? SdfSolidPrimitive.Sphere)
            );
            _ = builder.EndInstance();
        }

        // Pass 2 — blend groups, first-appearance order: ONE dynamic instance anchored on the ROOT slot (the
        // travelling bound), members in document order, wrapped in a field scope when the group needs one (the
        // Intersection-wipe fix; see the accumulator rule on SdfBlendOp). Resolved once (Reconcile's ApplyMotion
        // already ran, so live.PartFollows reflects this frame's followers) rather than per group — precise
        // per-group membership is not worth a second scan since widening a group without a follower only relaxes
        // its cull, never its geometry.
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
            EmitGroup(
                builder: builder,
                follows: anyPartFollows,
                fromIndex: index,
                groupId: groupId,
                paletteIds: paletteIds,
                placementScale: placementScale,
                probeWorstCase: probeWorstCase,
                reach: reach,
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
    // outside a group] — the fixed op sequence over the canonical CreationGeometry dimensions. probeWorstCase emits
    // EVERY op unconditionally (the probe binding rule).
    //
    // A domain-bearing shape cannot ride its own per-shape slot: PackTransforms bakes that slot's dynamic transform
    // as the WHOLE composed root*shape pose, leaving no seam to insert a domain op between "the placement/registration
    // root" and "this shape's own translate/rotate" — exactly the seam CreationStampEmitter.Emit's static path opens
    // by chaining them as separate ops. So it rides the ROOT slot instead, applies its domain ops there, then bakes
    // its own (STATIC) local pose as ordinary Translate/Rotate — the same "ride root, bake local" shape
    // CreationStampEmitter.EmitTextDynamic already uses for a creation's text runs. A domain-bearing shape therefore
    // does not replay a per-shape animation-frame pose (PackTransforms still writes root*shape into its own slot for
    // part/anchor resolution — TryBodyPartPose and friends — but nothing reads it for this shape's GEOMETRY).
    private static void EmitShape(SdfProgramBuilder builder, int slot, int rootSlot, SdfSolidPrimitive type, int material, Vector3 scale, bool probeWorstCase, IReadOnlyList<ShapeDomainOp>? domain = null, Vector3 shapePosition = default, Quaternion shapeRotation = default, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float twist = 0f, float bend = 0f, float dilate = 0f, float onion = 0f, bool inGroupScope = false) {
        var chain = builder.ResetPoint();

        if (domain is { Count: > 0 }) {
            chain = ShapeDomainOps.Apply(
                chain: chain.TransformDynamic(slot: rootSlot),
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

        var wantsDilate = (probeWorstCase || (dilate != 0f));
        var wantsOnion = (probeWorstCase || (onion != 0f));

        if (
            (wantsDilate || wantsOnion) &&
            !inGroupScope
        ) {
            var scoped = SdfSolidGeometry.AppendScaledPrimitive(
                blend: SdfBlendOp.Union,
                chain: chain.PushField(
                    compose: blend,
                    smooth: smooth
                ),
                material: material,
                scale: scale,
                smooth: 0f,
                type: type
            );

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

            _ = scoped.PopField();

            return;
        }

        var afterShape = SdfSolidGeometry.AppendScaledPrimitive(
            blend: blend,
            chain: chain,
            material: material,
            scale: scale,
            smooth: smooth,
            type: type
        );

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
    }
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

            if (
                ((shape.Group ?? 0) == groupId) &&
                (((shape.Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union) || ((shape.Onion ?? 0f) != 0f) || ((shape.Dilate ?? 0f) != 0f))
            ) {
                return true;
            }
        }

        return false;
    }
    private static float MaxComponent(Vector3 scale) => MathF.Max(
        x: scale.X,
        y: MathF.Max(
            x: scale.Y,
            y: scale.Z
        )
    );
    // Whether a placement row renders through THIS pool rather than as a static stamp: an animated creation (a replayed
    // timeline) or an attached row (a live body root). The exact complement of WorldPlacementStamper.IsStaticStamp for a
    // non-inhabited row — an inhabited row roots through the body-stamp census instead.
    private static bool PoolRooted(WorldPlacement row, WorldPrototype creation) =>
        (WorldPlacementStamper.IsAnimated(creation: creation) || (row.Attach is not null));
    private static int[] ProbePalette(SdfProgramBuilder builder) {
        var ids = new int[CreationDocument.PaletteSize];

        for (var index = 0; (index < ids.Length); index++) {
            ids[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.5f)));
        }

        return ids;
    }
    private static Registration RegisterBody(BodyStamp stamp) {
        var registration = new Registration {
            Key = $"body:{stamp.BodyIndex}",
            BodyIndex = stamp.BodyIndex,
            Creation = stamp.Creation,
            Parts = CreationPartCompiler.Compile(document: stamp.Creation.Document),
            Scale = stamp.Scale,
            FramePoses = new Dictionary<int, FrameTransformDocument>?[((stamp.Creation.Document.Frames?.Count ?? 0) + 1)],
            Cues = stamp.Motion.Cues,
            Replay = stamp.Motion.ReplayFrames,
        };

        if (stamp.Motion.Cues is { Count: > 0 } cues) {
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
    private static void ApplyMotion(Registration live, WorldLookMotion motion, IReadOnlyList<WorldDynamicsRow> dynamics) {
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
        Key = row.Id,
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

    /// <summary>Emits the whole pool (constant slot count): per live registration its palette, ungrouped shapes as
    /// per-slot dynamic instances, and blend groups as root-anchored scoped instances (a traveling bound for the
    /// group); parked placeholders elsewhere. The probe path takes the largest legal form.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="definition">The live definition a registration's state-bound palette color resolves against.</param>
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
    public void Emit(SdfProgramBuilder builder, WorldDefinition definition, bool probeWorstCase, float maxPlacementScale, int slotBase, PackedFontAtlasCatalog? textCatalog = null) {
        for (var index = 0; (index < m_pool.Length); index++) {
            var live = (probeWorstCase
                ? null
                : m_pool[index]
            );
            var rootSlot = (slotBase + (index * SlotsPerPlacement));

            EmitOne(
                builder: builder,
                definition: definition,
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
    /// <summary>Packs the pool's per-frame transforms: each live registration's root rides its placement pose (animated),
    /// the client's interpolated body pose (body-rooted), or that pose composed with the attach facet's local offset
    /// (attached), and each shape holds its current frame's snapshot (composed root ∘ per-shape pose, positions scaled by
    /// the registration scale); unused slots — and an attached row whose target body is not live this frame — hide below
    /// the floor.</summary>
    /// <param name="transforms">The unified dynamic-transform buffer (the pool writes its own slot range).</param>
    /// <param name="client">The client whose interpolated body poses root the body-rooted and attached stamps.</param>
    /// <param name="slotBase">The pool's first dynamic-transform slot in <paramref name="transforms"/>, supplied by the
    /// emitter that owns the pool (see <see cref="Emit"/>).</param>
    /// <param name="parkPosition">Where an unused slot — or an attached row whose target body is not live this
    /// frame — parks, hidden below the floor (<see cref="SdfEmitContext.ParkPosition"/>).</param>
    public void PackTransforms(Span<DynamicTransform> transforms, WorldClient client, int slotBase, Vector3 parkPosition) {
        m_packedSlotBase = slotBase;

        var deltaSeconds = m_pendingDeltaSeconds;

        m_pendingDeltaSeconds = 0f;

        for (var index = 0; (index < m_pool.Length); index++) {
            var rootSlot = (slotBase + (index * SlotsPerPlacement));
            var live = m_pool[index];

            // An attached row whose body is not active contributes nothing this frame — the presentation mirror of
            // WorldPlacementAttachment.TryResolve's inactive-body verdict (which world.attachments echoes by reason).
            // The registration keeps its slot: occupancy changes tick to tick and a rebuild is not owed for one. The
            // range test is a belt-and-braces guard, not a live gap: WorldBodiesLimits.CapacityCeiling is
            // WorldClient.EntityCapacity, so the document validator's bound on population.capacity already keeps
            // every body index inside this client's view — this stays as the one place that still checks it
            // directly rather than trusting an upstream invariant transitively.
            if (
                (live is { Row.Attach: { } parked }) &&
                ((((uint)parked.BodyIndex) >= ((uint)WorldClient.EntityCapacity)) || !client.IsActive(index: parked.BodyIndex))
            ) {
                live = null;
            }

            if (live is null) {
                var hidden = new DynamicTransform(
                    Orientation: Quaternion.Identity,
                    Position: parkPosition
                );

                for (var slot = rootSlot; (slot < (rootSlot + SlotsPerPlacement)); slot++) {
                    transforms[slot] = hidden;
                }

                continue;
            }

            var (rootPosition, rootRotation, placementScale) = RootPose(
                client: client,
                live: live
            );

            // The pose-continuity watch is read for EVERY body-rooted registration, not only a follower-bearing one:
            // a teleport or a reused body slot invalidates a latched contact point the same way it invalidates a
            // follower — the world point a foot was planted at belongs to where the body WAS.
            if (live.BodyIndex is { } watched) {
                var epoch = client.PoseEpoch(index: watched);
                var address = client.EntityAddress(index: watched);

                if ((live.RootEpoch != epoch) || (live.RootAddress != address)) {
                    live.RootPositionFollower.Reseed();
                    live.RootOrientationFollower.Reseed();
                    live.RootEpoch = epoch;
                    live.RootAddress = address;

                    for (var shapeSlot = 0; (shapeSlot < WorldPlacementPolicy.MaxAnimatedStampShapes); shapeSlot++) {
                        live.PartFollower[shapeSlot].Reseed();
                    }

                    Array.Clear(array: live.EffectorPlanted);
                }
            }

            if (live.HasRootDynamics) {
                StepRootFollower(
                    deltaSeconds: deltaSeconds,
                    live: live,
                    targetPosition: rootPosition,
                    targetRotation: rootRotation
                );
                (rootPosition, rootRotation) = (live.FollowedPosition, live.FollowedOrientation);
            } else {
                live.RootPositionFollower.Reseed();
                live.RootOrientationFollower.Reseed();
                live.FollowedPosition = rootPosition;
                live.FollowedOrientation = rootRotation;
            }

            transforms[rootSlot] = new DynamicTransform(
                Orientation: rootRotation,
                Position: rootPosition
            );

            var document = live.Creation.EngineDocument;
            var drivers = document.Drivers;

            if (live.BodyIndex is { } drivenBody) {
                WorldGaitDrivers.Advance(
                    address: client.EntityAddress(index: drivenBody),
                    deltaSeconds: deltaSeconds,
                    drivers: drivers,
                    facts: client.Facts(index: drivenBody),
                    easedSpeed: ref live.DriverSpeed,
                    lastAddress: ref live.DriverAddress,
                    lastOrientation: ref live.DriverOrientation,
                    lastPosition: ref live.DriverPosition,
                    orientation: rootRotation,
                    phases: live.DriverPhase,
                    position: rootPosition,
                    seeded: ref live.DriverSeeded,
                    weights: live.DriverWeight,
                    definition: client.Definition,
                    tick: client.Tick
                );
            }

            var shapes = (document.Shapes ?? []);
            var poses = FramePoses(
                frameCursor: live.EffectiveCursor,
                live: live
            );
            var shapeCount = Math.Min(
                val1: shapes.Count,
                val2: WorldPlacementPolicy.MaxAnimatedStampShapes
            );

            // The animated facets compose in the creation's own space, on top of whichever rest/frame pose the write
            // pass chooses — a uniform placement scale commutes with a rotation about a scaled pivot, so scaling there
            // is the same pose either way. A shape's own delta chains under its parent's (already composed: a parent
            // is validated to precede its children), and is kept for the children that follow.
            if (!live.PartParentsResolved) {
                ResolvePartParents(
                    live: live,
                    shapes: shapes
                );
            }

            for (var shapeIndex = 0; (shapeIndex < shapeCount); shapeIndex++) {
                WorldGaitDrivers.ComposeDelta(
                    drivers: drivers,
                    phases: live.DriverPhase,
                    rotation: out var ownRotation,
                    shape: shapes[shapeIndex],
                    translation: out var ownTranslation,
                    weights: live.DriverWeight,
                    definition: client.Definition
                );

                live.PartOwnRotation[shapeIndex] = ownRotation;
                live.PartOwnTranslation[shapeIndex] = ownTranslation;
            }

            ChainPartDeltas(
                live: live,
                shapeCount: shapeCount
            );
            // The effectors correct the driver-posed chain, then everything downstream of a corrected bone re-chains
            // off the corrected own delta — so a hand parented to a solved forearm rides the solve with no effector
            // of its own.
            if (ApplyEffectors(
                client: client,
                deltaSeconds: deltaSeconds,
                document: document,
                live: live,
                placementScale: placementScale,
                poses: poses,
                rootPosition: rootPosition,
                rootRotation: rootRotation,
                shapeCount: shapeCount,
                shapes: shapes
            )) {
                ChainPartDeltas(
                    live: live,
                    shapeCount: shapeCount
                );
            }

            for (var shapeIndex = 0; (shapeIndex < WorldPlacementPolicy.MaxAnimatedStampShapes); shapeIndex++) {
                var slot = ((rootSlot + 1) + shapeIndex);

                if (shapeIndex >= shapeCount) {
                    transforms[slot] = new DynamicTransform(
                        Orientation: Quaternion.Identity,
                        Position: parkPosition
                    );

                    continue;
                }

                var (position, rotation) = BasePose(
                    poses: poses,
                    shape: shapes[shapeIndex]
                );

                WorldGaitDrivers.Apply(
                    deltaRotation: live.PartDeltaRotation[shapeIndex],
                    deltaTranslation: live.PartDeltaTranslation[shapeIndex],
                    position: ref position,
                    rotation: ref rotation
                );

                var worldPosition = (rootPosition + Vector3.Transform(
                    rotation: rootRotation,
                    value: (position * placementScale)
                ));

                if (live.PartFollows[shapeIndex]) {
                    worldPosition = StepPartFollower(
                        deltaSeconds: deltaSeconds,
                        live: live,
                        shapeSlot: shapeIndex,
                        target: worldPosition
                    );
                }

                transforms[slot] = new DynamicTransform(
                    Orientation: Quaternion.Normalize(value: (rootRotation * rotation)),
                    Position: worldPosition
                );
            }
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
    public void Reconcile(IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<WorldDynamicsRow> dynamics, IReadOnlyList<BodyStamp> bodyStamps) {
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
                id: presentRow.PrototypeId
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

            m_pool[index] = ((live.BodyIndex is not null)
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
                            motion: stamp.Motion,
                            dynamics: dynamics
                        );

                        return fresh;
                    },
                    update: (entry, stamp) => {
                        entry.Creation = stamp.Creation;
                        entry.Scale = stamp.Scale;
                        ApplyMotion(
                            live: entry,
                            motion: stamp.Motion,
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
        }

        // Pass 2 — admit new row-rooted (animated or attached) rows into free slots (the validator holds the ceiling; a
        // race past it skips loudly rather than corrupting a neighbor's slot).
        foreach (var placement in placements) {
            if (
                (WorldDefinitionRows.FindCreation(
                creations: creations,
                id: placement.PrototypeId
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
                motion: stamp.Motion,
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
    /// <summary>Resolves a body-rooted creation look's current authored part pose without a packed buffer.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="client">The client supplying the entity root pose.</param>
    /// <param name="pose">The current part pose, or default when unresolved.</param>
    /// <returns><see langword="true"/> when the live creation look publishes the part.</returns>
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

        var (localPosition, localRotation) = (((poses is not null) && poses.TryGetValue(
            key: shape.Id,
            value: out var framePose
        ))
            ? (framePose.Position, framePose.Rotation)
            : (shape.Position, shape.Rotation)
        );
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
    public bool TryBodyPartPose(int bodyIndex, string partId, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose) {
        if (
            !TryBodyPartTransformSlot(
            bodyIndex: bodyIndex,
            partId: partId,
            transformSlot: out var transformSlot
        ) ||
            (((uint)transformSlot) >= ((uint)transforms.Length))
        ) {
            pose = default;

            return false;
        }

        var transform = transforms[transformSlot];

        pose = new SdfAnchor(
            Position: transform.Position,
            Orientation: transform.Orientation
        );

        return true;
    }
    /// <summary>Resolves a body-rooted creation look's authored part pose from a list-backed packed transform buffer.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="transforms">The current composed transform buffer.</param>
    /// <param name="pose">The live part pose, or default when unresolved.</param>
    /// <returns><see langword="true"/> when the live creation look publishes a packed part pose.</returns>
    public bool TryBodyPartPose(int bodyIndex, string partId, IReadOnlyList<DynamicTransform> transforms, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(argument: transforms);

        if (
            !TryBodyPartTransformSlot(
            bodyIndex: bodyIndex,
            partId: partId,
            transformSlot: out var transformSlot
        ) ||
            (((uint)transformSlot) >= ((uint)transforms.Count))
        ) {
            pose = default;

            return false;
        }

        var transform = transforms[transformSlot];

        pose = new SdfAnchor(
            Position: transform.Position,
            Orientation: transform.Orientation
        );

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
}
