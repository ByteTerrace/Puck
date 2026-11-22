using System.Numerics;
using Puck.Abstractions.Collections;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>Resolves the pose a scene presents a catalog body at when the scene's own source may not hold it: a local
/// seat traveling another authority's route keeps its catalog range and follows the route's pose.</summary>
/// <param name="body">The body's index.</param>
/// <param name="position">The presented position.</param>
/// <param name="orientation">The presented orientation.</param>
/// <param name="address">The address of the entity presented.</param>
/// <returns><see langword="false"/> when the scene presents no avatar for the body this frame.</returns>
internal delegate bool WorldCatalogAvatarPose(int body, out Vector3 position, out Quaternion orientation, out WorldEntityAddress address);

/// <summary>
/// One scene's catalog avatars: the bodies that draw as a catalog rig (<see cref="WorldCatalogBand"/>), the look
/// identity the live program was compiled for, and each body's gait, root follower and transform owner. The boot scene
/// and every session scene pack their avatars through <see cref="Pack"/>, the one implementation of the walk.
/// </summary>
/// <remarks>A pack reads only the avatars something woke: the bodies the source's journal names
/// (<see cref="IWorldStampSource.BodyChanges"/>), those whose slots have not come to rest, and, where a scene resolves
/// poses of its own (<see cref="WorldCatalogAvatarPose"/>), the local seats, whose route pose is in no journal. A
/// prepare, a frame that owes the whole table, another source or slot base, and a source with no journal read every
/// avatar.</remarks>
internal sealed class WorldCatalogAvatars {
    private readonly int[] m_accentMaterials = new int[WorldBodiesLimits.CapacityCeiling];
    private readonly MarkedIndexSet m_awake = new();
    private readonly WorldBodyChangeCursor m_bodyCursor = new();
    private readonly int[] m_bodyMaterials = new int[WorldBodiesLimits.CapacityCeiling];
    private readonly List<int> m_changedBodies = [];
    // The source's Carries count each avatar's latched world-space state was last carried to.
    private readonly int[] m_carryEpoch = new int[WorldBodiesLimits.CapacityCeiling];
    private readonly bool[] m_follows = new bool[WorldBodiesLimits.CapacityCeiling];
    private readonly WorldAvatarGait[] m_gaits = new WorldAvatarGait[WorldBodiesLimits.CapacityCeiling];
    private readonly SecondOrderFollower4[] m_orientationFollower = new SecondOrderFollower4[WorldBodiesLimits.CapacityCeiling];
    private readonly WorldTransformOwners m_owners = new(capacity: WorldBodiesLimits.CapacityCeiling);
    private IWorldStampSource? m_packedSource;
    private int m_packedSlotBase = -1;
    private readonly SecondOrderFollower3[] m_positionFollower = new SecondOrderFollower3[WorldBodiesLimits.CapacityCeiling];
    private readonly SecondOrderResponse[] m_response = new SecondOrderResponse[WorldBodiesLimits.CapacityCeiling];
    private int[] m_visits = [];
    private bool m_wakeAll = true;

    /// <summary>Initializes a new instance of the <see cref="WorldCatalogAvatars"/> class.</summary>
    /// <param name="band">The bodies that draw as a catalog rig and the slots they hold.</param>
    public WorldCatalogAvatars(WorldCatalogBand band) {
        ArgumentNullException.ThrowIfNull(argument: band);

        Band = band;
        m_holdsSlot = body => (Band.Slots[body] >= 0);
        m_rigFor = body => Rigs[body];
        m_slotFor = body => Band.Slots[body];
    }

    // What the catalog emitter asks of each body, made once.
    private readonly Func<int, bool> m_holdsSlot;
    private readonly Func<int, int> m_rigFor;
    private readonly Func<int, int> m_slotFor;

    /// <summary>Gets the bodies that draw as a catalog rig and the slots they hold.</summary>
    public WorldCatalogBand Band { get; }
    /// <summary>Gets the gait amplitude each body's emitted look scales its phase by, latched when the program is
    /// built.</summary>
    public float[] GaitAmplitudes { get; } = new float[WorldBodiesLimits.CapacityCeiling];
    /// <summary>Gets the catalog rig each body's geometry was emitted with, latched when the program is built: the pack
    /// poses the rig the live program holds, never one a later delivery names.</summary>
    public int[] Rigs { get; } = new int[WorldBodiesLimits.CapacityCeiling];
    /// <summary>Gets the uniform render scale each body's geometry was emitted at, latched when the program is
    /// built.</summary>
    public float[] Scales { get; } = new float[WorldBodiesLimits.CapacityCeiling];
    /// <summary>Gets the number of avatars the latest <see cref="Pack"/> read.</summary>
    public int VisitCount { get; private set; }

    /// <summary>Registers each band body's two catalog materials and latches the look identity its geometry is emitted
    /// with (rig, scale, gait amplitude) and its root follower: an avatar whose look names a dynamics row lags toward its
    /// presented pose instead of drawing it directly. Called before <see cref="EmitBodies"/> in the same build, at the
    /// point of the program the scene registers its avatar materials.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="noseFactor">The accent-material color multiplier (<c>playerDefaults.noseFactor</c>).</param>
    /// <param name="dynamics">The document's dynamics rows.</param>
    public void EmitPalette(SdfProgramBuilder builder, float noseFactor, IReadOnlyList<DynamicsRow> dynamics) {
        foreach (var body in Band.Bodies) {
            var (rig, look, color) = Band.AppearanceOf(body: body);

            WorldMirroredAvatarBand.EmitPalette(
                accentMaterials: m_accentMaterials,
                bodyColor: new DisplayColor(rgb: color),
                bodyMaterials: m_bodyMaterials,
                builder: builder,
                catalogRig: rig,
                emittedGaitAmplitudes: GaitAmplitudes,
                emittedRigs: Rigs,
                emittedScales: Scales,
                identityIndex: body,
                look: look,
                materialIndex: body,
                noseFactor: noseFactor
            );
            m_follows[body] = WorldDynamicsResponse.TryResolveResponse(name: look.Motion.Dynamics, response: out m_response[body], rows: dynamics);
        }
    }
    /// <summary>Emits each band body's catalog rig as leaf-level dynamic instances riding the body's slots, in the
    /// materials <see cref="EmitPalette"/> registered: the program draws exactly the avatars <see cref="Pack"/> poses.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="slotBase">The band's first dynamic-transform slot.</param>
    /// <param name="picks">The presentation identity table the avatars are named in.</param>
    public void EmitBodies(SdfProgramBuilder builder, int slotBase, WorldPickMapBuilder picks) => WorldRigCatalog.Emit(
        accentMaterials: m_accentMaterials,
        bodyMaterials: m_bodyMaterials,
        builder: builder,
        isActive: m_holdsSlot,
        picks: picks,
        rigFor: m_rigFor,
        slotBase: slotBase,
        slotFor: m_slotFor
    );
    /// <summary>Prepares the band (<see cref="WorldCatalogBand.Prepare"/>): another set of bodies may hold the slots,
    /// so the next pack reads every avatar.</summary>
    public void Prepare() {
        Band.Prepare();
        m_wakeAll = true;
    }
    /// <summary>Packs the avatars something woke into their catalog slots: each rides its presented pose plus a
    /// distance-driven gait phase, through its root follower when its look names one, and a body the scene no longer
    /// presents parks its range once.</summary>
    /// <param name="slots">The whole shared dynamic-transform table.</param>
    /// <param name="context">The frame's emit context, whose slot base the band's first slot starts at.</param>
    /// <param name="moved">The frame's moved set.</param>
    /// <param name="source">The world the bodies' poses, epochs and carries are read from.</param>
    /// <param name="pool">The scene's stamp pool: a body it registered draws its creation there, so its avatar parks.</param>
    /// <param name="deltaSeconds">The seconds the frame advances each root follower by.</param>
    /// <param name="pose">What resolves a body's presented pose, or <see langword="null"/> to read it from
    /// <paramref name="source"/>.</param>
    /// <param name="footstep">What sounds a local seat's footfall at its site, one a gait half-cycle, or
    /// <see langword="null"/> for none.</param>
    public void Pack(DynamicTransform[] slots, in SdfEmitContext context, SdfMovedTransforms moved, IWorldStampSource source, WorldCreationInstances pool, float deltaSeconds, WorldCatalogAvatarPose? pose = null, Action<Vector3>? footstep = null) {
        var everything = (moved.Everything || m_wakeAll || (context.SlotBase != m_packedSlotBase) || !ReferenceEquals(objA: source, objB: m_packedSource));

        m_wakeAll = false;
        m_packedSlotBase = context.SlotBase;
        m_packedSource = source;
        m_changedBodies.Clear();
        everything |= !m_bodyCursor.TryCollect(bodies: m_changedBodies, source: source);

        if (everything) {
            m_awake.Clear();
            foreach (var body in Band.Bodies) {
                PackBody(body: body, context: in context, deltaSeconds: deltaSeconds, footstep: footstep, moved: moved, pool: pool, pose: pose, slots: slots, source: source);
            }

            VisitCount = Band.Bodies.Length;

            return;
        }

        foreach (var body in m_changedBodies) {
            if (Band.Slots[body] >= 0) { _ = m_awake.Add(index: body); }
        }
        // A seat a scene resolves the pose of may follow another authority's route, which no journal names.
        if (pose is not null) {
            for (var seat = 0; (seat < WorldBodiesLimits.LocalSeatCount); seat++) {
                if (Band.Slots[seat] >= 0) { _ = m_awake.Add(index: seat); }
            }
        }

        // A body its pack keeps awake goes into the set again, so the visits are taken first.
        var count = m_awake.Drain(members: ref m_visits);

        foreach (var body in m_visits.AsSpan(start: 0, length: count)) {
            if (Band.Slots[body] >= 0) {
                PackBody(body: body, context: in context, deltaSeconds: deltaSeconds, footstep: footstep, moved: moved, pool: pool, pose: pose, slots: slots, source: source);
            }
        }

        VisitCount = count;
    }

    private void PackBody(int body, DynamicTransform[] slots, in SdfEmitContext context, SdfMovedTransforms moved, IWorldStampSource source, WorldCreationInstances pool, float deltaSeconds, WorldCatalogAvatarPose? pose, Action<Vector3>? footstep) {
        ref var gait = ref m_gaits[body];
        var position = Vector3.Zero;
        var orientation = Quaternion.Identity;
        var address = default(WorldEntityAddress);
        var presented = !pool.HasBodyRegistration(bodyIndex: body);

        if (presented) {
            if (pose is not null) {
                presented = pose(body: body, address: out address, orientation: out orientation, position: out position);
            } else if (source.IsActive(index: body)) {
                (position, orientation, address) = (source.Position(index: body), source.Orientation(index: body), source.EntityAddress(index: body));
            } else {
                presented = false;
            }
        }
        if (!presented) {
            WorldMirroredAvatarBand.Park(firstSlot: (context.SlotBase + Band.Slots[body]), gait: ref gait, moved: moved, owner: body, owners: m_owners, parkPosition: context.ParkPosition, table: slots);

            return;
        }

        // An in-place crossing moved the avatar continuously: its last position and followers ride the crossing's rigid
        // motion, so the gait and the eased pose carry on; a history too short to carry them is a cut.
        var crossings = source.Carries.Epoch(index: body);

        if (m_carryEpoch[body] != crossings) {
            if (gait.Seeded && source.Carries.TrySince(carry: out var carry, index: body, since: m_carryEpoch[body])) {
                gait.Position = carry.Point(point: gait.Position);
                m_positionFollower[body].Carry(rotation: carry.Rotation, translation: carry.Translation);
                m_orientationFollower[body].Carry(rotation: carry.Rotation);
            } else {
                gait.Seeded = false;
            }

            m_carryEpoch[body] = crossings;
        }

        if (!WorldMirroredAvatarBand.Wake(m_owners, body, ref gait, moved, address, source.PoseEpoch(index: body), position, orientation, out var continued, out var footfall, deltaSeconds)) {
            // A frame that advances no time leaves a restless avatar's settling work for the frame that does.
            if (m_owners.Restless(owner: body)) {
                _ = m_awake.Add(index: body);
            }

            return;
        }

        if (!continued) {
            m_positionFollower[body].Reseed();
            m_orientationFollower[body].Reseed();
        } else if (footfall && (body < WorldBodiesLimits.LocalSeatCount)) {
            // Only local seats sound their own steps, at the walking body's site.
            footstep?.Invoke(obj: position);
        }

        var follows = m_follows[body];

        if (follows) {
            (position, orientation) = SecondOrderPoseFollower.StepPose(
                deltaSeconds: deltaSeconds,
                orientation: ref m_orientationFollower[body],
                position: ref m_positionFollower[body],
                response: in m_response[body],
                targetOrientation: orientation,
                targetPosition: position
            );
        } else {
            m_positionFollower[body].Reseed();
            m_orientationFollower[body].Reseed();
        }

        WorldMirroredAvatarBand.Pack(
            m_owners, body, in gait, moved, slots, Band.Slots[body], body, context.SlotBase,
            Rigs[body], Scales[body], GaitAmplitudes[body], position, orientation, (follows ? deltaSeconds : 1f)
        );
        // An avatar whose slots moved is read again until a pack leaves them unchanged: its follower may still settle.
        if (m_owners.Restless(owner: body)) {
            _ = m_awake.Add(index: body);
        }
    }
}
