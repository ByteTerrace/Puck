using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>
/// The rest state of a band of dynamic-transform owners — catalog bodies, stamp registrations — which decides which
/// owners an emitter repacks on an incremental frame. An owner wakes when an input it was last packed from moves, and
/// stays restless until one repack leaves its slots unchanged; a frame that advanced no time never puts an owner to
/// rest, because its followers did not step. See <see cref="SdfMovedTransforms"/> for the table's side.
/// </summary>
public sealed class WorldTransformOwners {
    private readonly bool[] m_casts;
    private readonly Quaternion[] m_orientation;
    private readonly Vector3[] m_position;
    private readonly bool[] m_resident;
    private readonly bool[] m_restless;
    private readonly int[] m_version;

    /// <summary>Initializes a new instance of the <see cref="WorldTransformOwners"/> class.</summary>
    /// <param name="capacity">The number of owners the band holds.</param>
    public WorldTransformOwners(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);

        m_casts = new bool[capacity];
        m_orientation = new Quaternion[capacity];
        m_position = new Vector3[capacity];
        m_resident = new bool[capacity];
        m_restless = new bool[capacity];
        m_version = new int[capacity];
    }

    /// <summary>Decides whether an owner repacks this frame, and records the inputs it is packed from.</summary>
    /// <param name="owner">The owner's index in the band.</param>
    /// <param name="moved">The frame's moved set; a frame owing everything repacks every owner.</param>
    /// <param name="position">The owner's root position this frame.</param>
    /// <param name="orientation">The owner's root orientation this frame.</param>
    /// <param name="castsSoftShadow">The owner's soft-shadow participation this frame.</param>
    /// <param name="discontinuity">Whether the owner's pose jumped this frame (a teleport, a reused body index), which
    /// always repacks it.</param>
    /// <param name="version">Any other input the owner's pack reads, as a counter that moves when it does (a state
    /// delivery); zero when there is none.</param>
    /// <returns><see langword="true"/> when the owner repacks.</returns>
    public bool Wake(int owner, SdfMovedTransforms moved, Vector3 position, Quaternion orientation, bool castsSoftShadow, bool discontinuity = false, int version = 0) {
        ArgumentNullException.ThrowIfNull(argument: moved);

        var wake = (
            discontinuity ||
            moved.Everything ||
            !m_resident[owner] ||
            m_restless[owner] ||
            (m_position[owner] != position) ||
            (m_orientation[owner] != orientation) ||
            (m_casts[owner] != castsSoftShadow) ||
            (m_version[owner] != version)
        );

        m_position[owner] = position;
        m_orientation[owner] = orientation;
        m_casts[owner] = castsSoftShadow;
        m_version[owner] = version;

        return wake;
    }
    /// <summary>Records an owner's repack: it stays restless while the repack moved its slots, or while the frame
    /// advanced no time.</summary>
    /// <param name="owner">The owner's index in the band.</param>
    /// <param name="moved">Whether the repack changed any of the owner's slots.</param>
    /// <param name="deltaSeconds">The seconds the frame advanced the owner's followers by.</param>
    public void Settle(int owner, bool moved, float deltaSeconds) {
        m_resident[owner] = true;
        m_restless[owner] = (moved || (deltaSeconds <= 0f));
    }
    /// <summary>Forgets an owner that no longer draws, reporting whether its slots still hold a packed pose that must
    /// be parked and owed.</summary>
    /// <param name="owner">The owner's index in the band.</param>
    /// <param name="moved">The frame's moved set; after a frame owing everything the host has parked the table.</param>
    /// <returns><see langword="true"/> when the caller parks and owes the owner's range.</returns>
    public bool Vacate(int owner, SdfMovedTransforms moved) {
        ArgumentNullException.ThrowIfNull(argument: moved);

        var resident = m_resident[owner];

        m_resident[owner] = false;
        m_restless[owner] = false;

        return (resident && !moved.Everything);
    }
    /// <summary>Packs one catalog body into its reserved range and settles it with the moved set.</summary>
    /// <param name="table">The whole shared dynamic-transform table.</param>
    /// <param name="catalogBase">The table slot the catalog's first body range starts at.</param>
    /// <param name="avatar">The 0-based body index.</param>
    /// <param name="moved">The frame's moved set.</param>
    /// <param name="rootPosition">The body's root position.</param>
    /// <param name="rootOrientation">The body's root orientation.</param>
    /// <param name="gaitPhase">The body's gait phase, radians.</param>
    /// <param name="castsSoftShadow">The body's soft-shadow participation.</param>
    /// <param name="rig">The resolved catalog rig, or -1 for the body's default pick.</param>
    /// <param name="scale">The look's uniform render scale.</param>
    /// <returns><see langword="true"/> when the pack moved any of the body's slots.</returns>
    public static bool PackBody(Span<DynamicTransform> table, int catalogBase, int avatar, SdfMovedTransforms moved, Vector3 rootPosition, Quaternion rootOrientation, float gaitPhase, bool castsSoftShadow, int rig, float scale) {
        ArgumentNullException.ThrowIfNull(argument: moved);

        var (first, count) = WorldRigCatalog.BodySlots(avatar: avatar);
        var start = (catalogBase + first);
        Span<DynamicTransform> previous = stackalloc DynamicTransform[count];

        table.Slice(
            length: count,
            start: start
        ).CopyTo(destination: previous);
        WorldRigCatalog.PackTransforms(
            avatar: avatar,
            castsSoftShadow: castsSoftShadow,
            gaitPhase: gaitPhase,
            rig: rig,
            rootOrientation: rootOrientation,
            rootPosition: rootPosition,
            scale: scale,
            transforms: table.Slice(
                length: WorldRigCatalog.DynamicTransformCapacity,
                start: catalogBase
            )
        );

        return moved.Commit(
            previous: previous,
            slots: table,
            start: start
        );
    }
    /// <summary>Parks one catalog body's reserved range and owes it.</summary>
    /// <param name="table">The whole shared dynamic-transform table.</param>
    /// <param name="catalogBase">The table slot the catalog's first body range starts at.</param>
    /// <param name="avatar">The 0-based body index.</param>
    /// <param name="moved">The frame's moved set.</param>
    /// <param name="parkPosition">Where a hidden slot parks.</param>
    public static void ParkBody(Span<DynamicTransform> table, int catalogBase, int avatar, SdfMovedTransforms moved, Vector3 parkPosition) {
        ArgumentNullException.ThrowIfNull(argument: moved);

        var (first, count) = WorldRigCatalog.BodySlots(avatar: avatar);

        Park(
            count: count,
            moved: moved,
            parkPosition: parkPosition,
            start: (catalogBase + first),
            table: table
        );
    }
    /// <summary>Parks a range of the table and owes it.</summary>
    /// <param name="table">The whole shared dynamic-transform table.</param>
    /// <param name="start">The range's first slot.</param>
    /// <param name="count">The range's length.</param>
    /// <param name="moved">The frame's moved set.</param>
    /// <param name="parkPosition">Where a hidden slot parks.</param>
    public static void Park(Span<DynamicTransform> table, int start, int count, SdfMovedTransforms moved, Vector3 parkPosition) {
        ArgumentNullException.ThrowIfNull(argument: moved);

        table.Slice(
            length: count,
            start: start
        ).Fill(value: new DynamicTransform(
            Orientation: Quaternion.Identity,
            Position: parkPosition
        ));
        moved.Owe(
            count: count,
            start: start
        );
    }
}
