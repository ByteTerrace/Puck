using System.Collections.Immutable;
using Puck.Commands;
using Puck.Maths;
using Puck.Networking;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Where a body is and how it stands: the state a moving body changes on every tick it moves.</summary>
/// <param name="Position">The simulation position.</param>
/// <param name="Yaw">The stored heading scalar. A program that integrates the full attitude leaves it inert, and it is
/// carried as stored either way: the heading a reader is answered is a function of this, the orientation and the
/// program.</param>
/// <param name="Orientation">The full attitude.</param>
/// <param name="Up">The up axis the body's gravity opposes.</param>
/// <param name="PreviousPosition">The position at the top of the most recently completed advance.</param>
/// <param name="Scale">The body's scale.</param>
public readonly record struct BodyPoseSection(
    FixedVector3 Position,
    FixedQ4816 Yaw,
    FixedQuaternion Orientation,
    FixedVector3 Up,
    FixedVector3 PreviousPosition,
    FixedQ4816 Scale
) : IContinuationSection<BodyPoseSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Pose;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyPoseSection section) {
        writer.WriteFixedVector(value: section.Position);
        writer.WriteFixed(value: section.Yaw);
        writer.WriteFixedQuaternion(value: section.Orientation);
        writer.WriteFixedVector(value: section.Up);
        writer.WriteFixedVector(value: section.PreviousPosition);
        writer.WriteFixed(value: section.Scale);
    }
    /// <inheritdoc/>
    public static BodyPoseSection Read(ref WireReader reader) => new(
        Position: reader.ReadFixedVector(),
        Yaw: reader.ReadFixed(),
        Orientation: reader.ReadFixedQuaternion(),
        Up: reader.ReadFixedVector(),
        PreviousPosition: reader.ReadFixedVector(),
        Scale: reader.ReadFixed()
    );
}
/// <summary>How a body moves: its velocities, the integration remainders and followers that carry them from step to
/// step, and the support, hold, tether, carry and medium state its next step reads.</summary>
/// <param name="PlanarVelocity">The ramped horizontal velocity.</param>
/// <param name="VerticalVelocity">The vertical velocity.</param>
/// <param name="BallisticTangent">The airborne ballistic velocity across the body's up.</param>
/// <param name="BallisticTangentRemainder">The ballistic tangent accumulator's remainders.</param>
/// <param name="DrivePitch">The drive frame's climb attitude scalar.</param>
/// <param name="OverlayVelocity">The timed impulse overlay's world-space velocity.</param>
/// <param name="TimedMove">The active authored displacement and yaw, with their progress.</param>
/// <param name="RigidVelocity">A rigid kit's linear velocity.</param>
/// <param name="RigidAngularVelocity">A rigid kit's angular velocity.</param>
/// <param name="RigidResting">A rigid kit's resting latch.</param>
/// <param name="Grounded">Whether the last contact solve found support.</param>
/// <param name="InMedium">Whether the medium hold's last evaluation found the body below the free surface.</param>
/// <param name="AtMediumBand">Whether that evaluation found the body inside the equilibrium band.</param>
/// <param name="AffectingSubject">The body that applied the latest targeted effect, or -1.</param>
/// <param name="Home">The position the body was activated at.</param>
/// <param name="Carrying">The population index of the body this one carries, or -1.</param>
/// <param name="CarriedBy">The population index of the body carrying this one, or -1.</param>
/// <param name="PositionRemainder">The position accumulator's remainders.</param>
/// <param name="RotationRemainder">The rotation accumulator's remainders.</param>
/// <param name="VerticalVelocityRemainder">The vertical velocity accumulator's remainder.</param>
/// <param name="DirectVerticalVelocity">The vertical velocity the last step's own thrust drove directly, outside the
/// ballistic channel.</param>
/// <param name="PlanarRampRemainder">The whole-vector shaping ramp's remainder.</param>
/// <param name="DriveLongRemainder">The anisotropic row's longitudinal remainder.</param>
/// <param name="DriveLatRemainder">The anisotropic row's lateral remainder.</param>
/// <param name="DriveResidualRemainder">The anisotropic row's residual remainder.</param>
/// <param name="MediumThrustRampRemainder">The medium law's thrust ramp remainder.</param>
/// <param name="OverlayRemainder">The overlay accumulator's remainders.</param>
/// <param name="HoldSpendRemainder">The hold spend accumulator's remainder.</param>
/// <param name="AttitudeTurnRemainder">The drawn-axis turn accumulator's remainder.</param>
/// <param name="PlanarFollowerPosition">The planar dynamics follower's Q32 position raws.</param>
/// <param name="PlanarFollowerVelocity">The planar dynamics follower's Q32 velocity raws.</param>
/// <param name="PlanarFollowerPreviousTarget">The target the planar follower last saw.</param>
/// <param name="PlanarFollowerSeeded">Whether the planar follower has consumed its first target.</param>
/// <param name="VerticalFollowerPositionRaw">The vertical dynamics follower's Q32 position raw.</param>
/// <param name="VerticalFollowerVelocityRaw">The vertical dynamics follower's Q32 velocity raw.</param>
/// <param name="VerticalFollowerPreviousTarget">The target the vertical follower last saw.</param>
/// <param name="VerticalFollowerSeeded">Whether the vertical follower has consumed its first target.</param>
/// <param name="HoldIndex">The index of the held row in the kit's hold list, or -1.</param>
/// <param name="HoldAnchor">The held surface point.</param>
/// <param name="HoldNormal">The held surface's unit normal.</param>
/// <param name="AttitudeUp">The axis the body is drawn standing on.</param>
/// <param name="AttitudeLeaned">Whether a surface hold has leaned the drawn axis.</param>
/// <param name="AttachPreviousBit">The attach channel's previous threshold-crossing bit.</param>
/// <param name="DetachPreviousBit">The detach channel's previous threshold-crossing bit.</param>
/// <param name="Tether">The rope constraint's state, or <see langword="null"/> when none is attached.</param>
/// <param name="TetherAnchorBodyIndex">The anchor body's index, or -1 for a world-point tether.</param>
/// <param name="TetherAnchorPointOrLocalOffset">The world point or the body-local anchor offset.</param>
public readonly record struct BodyMotionSection(
    FixedVector3 PlanarVelocity,
    FixedQ4816 VerticalVelocity,
    FixedVector3 BallisticTangent,
    (long X, long Y, long Z) BallisticTangentRemainder,
    FixedQ4816 DrivePitch,
    FixedVector3 OverlayVelocity,
    WorldTimedBodyMove TimedMove,
    FixedVector3 RigidVelocity,
    FixedVector3 RigidAngularVelocity,
    bool RigidResting,
    bool Grounded,
    bool InMedium,
    bool AtMediumBand,
    int AffectingSubject,
    FixedVector3 Home,
    int Carrying,
    int CarriedBy,
    (long X, long Y, long Z) PositionRemainder,
    (long X, long Y, long Z) RotationRemainder,
    long VerticalVelocityRemainder,
    FixedQ4816 DirectVerticalVelocity,
    long PlanarRampRemainder,
    long DriveLongRemainder,
    long DriveLatRemainder,
    long DriveResidualRemainder,
    long MediumThrustRampRemainder,
    (long X, long Y, long Z) OverlayRemainder,
    long HoldSpendRemainder,
    long AttitudeTurnRemainder,
    (long X, long Y, long Z) PlanarFollowerPosition,
    (long X, long Y, long Z) PlanarFollowerVelocity,
    FixedVector3 PlanarFollowerPreviousTarget,
    bool PlanarFollowerSeeded,
    long VerticalFollowerPositionRaw,
    long VerticalFollowerVelocityRaw,
    FixedQ4816 VerticalFollowerPreviousTarget,
    bool VerticalFollowerSeeded,
    int HoldIndex,
    FixedVector3 HoldAnchor,
    FixedVector3 HoldNormal,
    FixedVector3 AttitudeUp,
    bool AttitudeLeaned,
    bool AttachPreviousBit,
    bool DetachPreviousBit,
    FixedTetherConstraintState? Tether,
    int TetherAnchorBodyIndex,
    FixedVector3 TetherAnchorPointOrLocalOffset
) : IContinuationSection<BodyMotionSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Motion;

    private static void WriteRaws(WireWriter writer, (long X, long Y, long Z) raws) {
        writer.WriteInt64(value: raws.X);
        writer.WriteInt64(value: raws.Y);
        writer.WriteInt64(value: raws.Z);
    }
    private static (long X, long Y, long Z) ReadRaws(ref WireReader reader) => (reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyMotionSection section) {
        writer.WriteFixedVector(value: section.PlanarVelocity);
        writer.WriteFixed(value: section.VerticalVelocity);
        writer.WriteFixedVector(value: section.BallisticTangent);
        WriteRaws(raws: section.BallisticTangentRemainder, writer: writer);
        writer.WriteFixed(value: section.DrivePitch);
        writer.WriteFixedVector(value: section.OverlayVelocity);
        WorldTimedBodyMove.Write(writer, section.TimedMove);
        writer.WriteFixedVector(value: section.RigidVelocity);
        writer.WriteFixedVector(value: section.RigidAngularVelocity);
        writer.WriteBoolean(value: section.RigidResting);
        writer.WriteBoolean(value: section.Grounded);
        writer.WriteBoolean(value: section.InMedium);
        writer.WriteBoolean(value: section.AtMediumBand);
        writer.WriteInt32(value: section.AffectingSubject);
        writer.WriteFixedVector(value: section.Home);
        writer.WriteInt32(value: section.Carrying);
        writer.WriteInt32(value: section.CarriedBy);
        WriteRaws(raws: section.PositionRemainder, writer: writer);
        WriteRaws(raws: section.RotationRemainder, writer: writer);
        writer.WriteInt64(value: section.VerticalVelocityRemainder);
        writer.WriteFixed(value: section.DirectVerticalVelocity);
        writer.WriteInt64(value: section.PlanarRampRemainder);
        writer.WriteInt64(value: section.DriveLongRemainder);
        writer.WriteInt64(value: section.DriveLatRemainder);
        writer.WriteInt64(value: section.DriveResidualRemainder);
        writer.WriteInt64(value: section.MediumThrustRampRemainder);
        WriteRaws(raws: section.OverlayRemainder, writer: writer);
        writer.WriteInt64(value: section.HoldSpendRemainder);
        writer.WriteInt64(value: section.AttitudeTurnRemainder);
        WriteRaws(raws: section.PlanarFollowerPosition, writer: writer);
        WriteRaws(raws: section.PlanarFollowerVelocity, writer: writer);
        writer.WriteFixedVector(value: section.PlanarFollowerPreviousTarget);
        writer.WriteBoolean(value: section.PlanarFollowerSeeded);
        writer.WriteInt64(value: section.VerticalFollowerPositionRaw);
        writer.WriteInt64(value: section.VerticalFollowerVelocityRaw);
        writer.WriteFixed(value: section.VerticalFollowerPreviousTarget);
        writer.WriteBoolean(value: section.VerticalFollowerSeeded);
        writer.WriteInt32(value: section.HoldIndex);
        writer.WriteFixedVector(value: section.HoldAnchor);
        writer.WriteFixedVector(value: section.HoldNormal);
        writer.WriteFixedVector(value: section.AttitudeUp);
        writer.WriteBoolean(value: section.AttitudeLeaned);
        writer.WriteBoolean(value: section.AttachPreviousBit);
        writer.WriteBoolean(value: section.DetachPreviousBit);
        writer.WriteBoolean(value: section.Tether.HasValue);
        if (section.Tether is { } tether) {
            writer.WriteFixed(value: tether.Length);
            writer.WriteFixed(value: tether.MinLength);
            writer.WriteInt64(value: tether.Remainder);
        }
        writer.WriteInt32(value: section.TetherAnchorBodyIndex);
        writer.WriteFixedVector(value: section.TetherAnchorPointOrLocalOffset);
    }
    /// <inheritdoc/>
    public static BodyMotionSection Read(ref WireReader reader) => new(
        PlanarVelocity: reader.ReadFixedVector(),
        VerticalVelocity: reader.ReadFixed(),
        BallisticTangent: reader.ReadFixedVector(),
        BallisticTangentRemainder: ReadRaws(reader: ref reader),
        DrivePitch: reader.ReadFixed(),
        OverlayVelocity: reader.ReadFixedVector(),
        TimedMove: WorldTimedBodyMove.Read(ref reader),
        RigidVelocity: reader.ReadFixedVector(),
        RigidAngularVelocity: reader.ReadFixedVector(),
        RigidResting: reader.ReadBoolean(),
        Grounded: reader.ReadBoolean(),
        InMedium: reader.ReadBoolean(),
        AtMediumBand: reader.ReadBoolean(),
        AffectingSubject: reader.ReadInt32(),
        Home: reader.ReadFixedVector(),
        Carrying: reader.ReadInt32(),
        CarriedBy: reader.ReadInt32(),
        PositionRemainder: ReadRaws(reader: ref reader),
        RotationRemainder: ReadRaws(reader: ref reader),
        VerticalVelocityRemainder: reader.ReadInt64(),
        DirectVerticalVelocity: reader.ReadFixed(),
        PlanarRampRemainder: reader.ReadInt64(),
        DriveLongRemainder: reader.ReadInt64(),
        DriveLatRemainder: reader.ReadInt64(),
        DriveResidualRemainder: reader.ReadInt64(),
        MediumThrustRampRemainder: reader.ReadInt64(),
        OverlayRemainder: ReadRaws(reader: ref reader),
        HoldSpendRemainder: reader.ReadInt64(),
        AttitudeTurnRemainder: reader.ReadInt64(),
        PlanarFollowerPosition: ReadRaws(reader: ref reader),
        PlanarFollowerVelocity: ReadRaws(reader: ref reader),
        PlanarFollowerPreviousTarget: reader.ReadFixedVector(),
        PlanarFollowerSeeded: reader.ReadBoolean(),
        VerticalFollowerPositionRaw: reader.ReadInt64(),
        VerticalFollowerVelocityRaw: reader.ReadInt64(),
        VerticalFollowerPreviousTarget: reader.ReadFixed(),
        VerticalFollowerSeeded: reader.ReadBoolean(),
        HoldIndex: reader.ReadInt32(),
        HoldAnchor: reader.ReadFixedVector(),
        HoldNormal: reader.ReadFixedVector(),
        AttitudeUp: reader.ReadFixedVector(),
        AttitudeLeaned: reader.ReadBoolean(),
        AttachPreviousBit: reader.ReadBoolean(),
        DetachPreviousBit: reader.ReadBoolean(),
        Tether: (reader.ReadBoolean()
            ? new FixedTetherConstraintState(
                Length: reader.ReadFixed(),
                MinLength: reader.ReadFixed(),
                Remainder: reader.ReadInt64()
            )
            : null),
        TetherAnchorBodyIndex: reader.ReadInt32(),
        TetherAnchorPointOrLocalOffset: reader.ReadFixedVector()
    );
}
/// <summary>The counts and frames that tick or settle while a body idles: the up-turn carry, the sleep floor, island
/// membership, the advance latch and the timed windows still running.</summary>
/// <param name="Frame">The carried rotation from world +Y into the body's up.</param>
/// <param name="UpNeedsReseat">Whether the next solved gravity direction reseats the up axis.</param>
/// <param name="FieldUpTurnRemainder">The solved-field up-turn accumulator's remainder.</param>
/// <param name="ContactUpTurnRemainder">The contact-normal up-turn accumulator's remainder.</param>
/// <param name="IdleSleepGravity">The acceleration a sleeping kit last observed, or null for ordinary kit gravity.</param>
/// <param name="AsleepSinceTick">The simulation tick the body fell asleep at, or 0 while awake.</param>
/// <param name="SleepIdleTicks">The idle engine ticks accumulated toward the sleep floor.</param>
/// <param name="RigidSleepRootPlusOne">Zero while awake; otherwise one plus the root slot of the parked island.</param>
/// <param name="OrdinaryAdvanceAdmitted">Whether the body's most recent step advanced under the ordinary path.</param>
/// <param name="ContinuumConsumedThroughEngineTick">The engine-time boundary already consumed from an inbound
/// continuum trajectory, or <see langword="null"/> when none is in flight.</param>
/// <param name="OverlayRemainingTicks">The engine ticks left on the live impulse overlay.</param>
/// <param name="RigidRestingHoldTicks">A rigid kit's engine ticks under the resting thresholds so far.</param>
public readonly record struct BodyClocksSection(
    FixedQuaternion Frame,
    bool UpNeedsReseat,
    long FieldUpTurnRemainder,
    long ContactUpTurnRemainder,
    FixedVector3? IdleSleepGravity,
    ulong AsleepSinceTick,
    ulong SleepIdleTicks,
    int RigidSleepRootPlusOne,
    bool OrdinaryAdvanceAdmitted,
    ulong? ContinuumConsumedThroughEngineTick,
    ulong OverlayRemainingTicks,
    ulong RigidRestingHoldTicks
) : IContinuationSection<BodyClocksSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Clocks;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyClocksSection section) {
        writer.WriteFixedQuaternion(value: section.Frame);
        var flags = (byte)((section.UpNeedsReseat ? 1 : 0) | (section.IdleSleepGravity.HasValue ? 2 : 0));
        writer.WriteByte(value: flags);
        if (section.IdleSleepGravity is { } gravity) { writer.WriteFixedVector(value: gravity); }
        writer.WriteInt64(value: section.FieldUpTurnRemainder);
        writer.WriteInt64(value: section.ContactUpTurnRemainder);
        writer.WriteUInt64(value: section.AsleepSinceTick);
        writer.WriteUInt64(value: section.SleepIdleTicks);
        writer.WriteInt32(value: section.RigidSleepRootPlusOne);
        writer.WriteBoolean(value: section.OrdinaryAdvanceAdmitted);
        writer.WriteOptional(
            value: section.ContinuumConsumedThroughEngineTick,
            writeValue: static (w, v) => w.WriteUInt64(value: v)
        );
        writer.WriteUInt64(value: section.OverlayRemainingTicks);
        writer.WriteUInt64(value: section.RigidRestingHoldTicks);
    }
    /// <inheritdoc/>
    public static BodyClocksSection Read(ref WireReader reader) {
        var frame = reader.ReadFixedQuaternion();
        var flags = reader.ReadByte();
        if ((flags & ~3) != 0) { reader.Fail(detail: "unknown body clock flags", refusal: WireRefusal.PayloadMalformed); }
        return new(
            Frame: frame,
            UpNeedsReseat: (flags & 1) != 0,
            IdleSleepGravity: ((flags & 2) != 0) ? reader.ReadFixedVector() : null,
            FieldUpTurnRemainder: reader.ReadInt64(),
            ContactUpTurnRemainder: reader.ReadInt64(),
            AsleepSinceTick: reader.ReadUInt64(),
            SleepIdleTicks: reader.ReadUInt64(),
            RigidSleepRootPlusOne: reader.ReadInt32(),
            OrdinaryAdvanceAdmitted: reader.ReadBoolean(),
            ContinuumConsumedThroughEngineTick: reader.ReadOptional(readValue: static (ref WireReader r) => r.ReadUInt64()),
            OverlayRemainingTicks: reader.ReadUInt64(),
            RigidRestingHoldTicks: reader.ReadUInt64()
        );
    }
}
/// <summary>The motion program's Recently-gate clocks, one per gate of the kit's shaping table.</summary>
/// <param name="Clocks">The clocks, in the table's slot order.</param>
public readonly record struct BodyMotionRecencySection(ReadOnlyMemory<ulong> Clocks) : IContinuationTableSection<BodyMotionRecencySection, ulong> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.MotionRecency;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyMotionRecencySection section) =>
        WriteTable(elements: section.Clocks.Span, writer: writer);
    /// <inheritdoc/>
    public static void WriteTable(WireWriter writer, ReadOnlySpan<ulong> elements) =>
        WorldAuthorityCheckpointCodec.WriteULongs(values: elements, writer: writer);
    /// <inheritdoc/>
    public static BodyMotionRecencySection Read(ref WireReader reader) =>
        new(Clocks: WorldAuthorityCheckpointCodec.ReadULongArray(field: "motion recency", reader: ref reader));
}
/// <summary>The input images that arrive at a body and the routing they arrive under: one-tick publications the next
/// advance consumes, the held image a restore keeps, and the source, engagement and mapping that say what fills the
/// intent and where it goes. The source can name a producer, so the section holds a reference and a body keeps it
/// behind a <see cref="VersionedRecord{T}"/>.</summary>
public record struct BodyInputImagesSection : IContinuationSection<BodyInputImagesSection> {
    /// <summary>The live stream's submitted image.</summary>
    public PlayerIntent Submitted;
    /// <summary>Whether a submission is staged for the next advance.</summary>
    public bool HasSubmitted;
    /// <summary>The live-held device image.</summary>
    public PlayerIntent Held;
    /// <summary>The server-side producer's staged image.</summary>
    public PlayerIntent Producer;
    /// <summary>Whether a producer image is staged for the next advance.</summary>
    public bool HasProducer;
    /// <summary>The held image a restore keeps until the next input publication.</summary>
    public PlayerIntent TransferHeld;
    /// <summary>Whether <see cref="TransferHeld"/> is live.</summary>
    public bool HasTransferHeld;
    /// <summary>What fills the intent between tape segments.</summary>
    public IntentSource Source;
    /// <summary>Whether the resolved intent is diverted from the avatar.</summary>
    public bool Engaged;
    /// <summary>The compiled input mapping the images are addressed in.</summary>
    public ushort Mapping;
    /// <summary>The input epoch the mapping was selected at.</summary>
    public ulong Epoch;

    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.InputImages;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyInputImagesSection section) {
        WorldWireCodec.WriteIntent(intent: section.Submitted, writer: writer);
        writer.WriteBoolean(value: section.HasSubmitted);
        WorldWireCodec.WriteIntent(intent: section.Held, writer: writer);
        WorldWireCodec.WriteIntent(intent: section.Producer, writer: writer);
        writer.WriteBoolean(value: section.HasProducer);
        WorldWireCodec.WriteIntent(intent: section.TransferHeld, writer: writer);
        writer.WriteBoolean(value: section.HasTransferHeld);
        WorldWireLeaves.WriteIntentSource(source: section.Source, writer: writer);
        writer.WriteBoolean(value: section.Engaged);
        writer.WriteUInt16(value: section.Mapping);
        writer.WriteUInt64(value: section.Epoch);
    }
    /// <inheritdoc/>
    public static BodyInputImagesSection Read(ref WireReader reader) => new() {
        Submitted = WorldWireCodec.ReadIntent(reader: ref reader),
        HasSubmitted = reader.ReadBoolean(),
        Held = WorldWireCodec.ReadIntent(reader: ref reader),
        Producer = WorldWireCodec.ReadIntent(reader: ref reader),
        HasProducer = reader.ReadBoolean(),
        TransferHeld = WorldWireCodec.ReadIntent(reader: ref reader),
        HasTransferHeld = reader.ReadBoolean(),
        Source = WorldWireCodec.ReadIntentSource(reader: ref reader),
        Engaged = reader.ReadBoolean(),
        Mapping = reader.ReadUInt16(),
        Epoch = reader.ReadUInt64(),
    };
}
/// <summary>The images the last advance resolved, kept for what reads them afterwards: the intent an engaged body
/// diverts, the held, composed and scalar read-backs rules and commands answer from, and the relative input's
/// fractions below scalar precision. The section holds no reference, so a body keeps it behind a
/// <see cref="Versioned{T}"/>.</summary>
public struct BodyInputReadbacksSection : IContinuationSection<BodyInputReadbacksSection>, IEquatable<BodyInputReadbacksSection> {
    /// <summary>The intent resolved on the most recent advance.</summary>
    public PlayerIntent EngagedIntent;
    /// <summary>The held overlay the last advance admitted.</summary>
    public PlayerIntent ReadHeld;
    /// <summary>The intent after the held overlay composed with the movement tier.</summary>
    public PlayerIntent ReadComposed;
    /// <summary>The step's scalar projection.</summary>
    public PlayerIntent ReadScalar;
    /// <summary>The conserved Q32.32 fractions, one per axis, inline; zeros for a body with no relative channel.</summary>
    public ChannelAxes RelativeRemainders;

    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.InputReadbacks;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyInputReadbacksSection section) {
        WorldWireCodec.WriteIntent(intent: section.EngagedIntent, writer: writer);
        WorldWireCodec.WriteIntent(intent: section.ReadHeld, writer: writer);
        WorldWireCodec.WriteIntent(intent: section.ReadComposed, writer: writer);
        WorldWireCodec.WriteIntent(intent: section.ReadScalar, writer: writer);
        WorldSectionWire.WriteFixed(count: ChannelLimits.MaxAxes, values: section.RelativeRemainders, writer: writer);
    }
    /// <inheritdoc/>
    public static BodyInputReadbacksSection Read(ref WireReader reader) {
        var section = new BodyInputReadbacksSection {
            EngagedIntent = WorldWireCodec.ReadIntent(reader: ref reader),
            ReadHeld = WorldWireCodec.ReadIntent(reader: ref reader),
            ReadComposed = WorldWireCodec.ReadIntent(reader: ref reader),
            ReadScalar = WorldWireCodec.ReadIntent(reader: ref reader),
        };

        foreach (ref var remainder in ((Span<long>)section.RelativeRemainders)) { remainder = reader.ReadInt64(); }
        return section;
    }

    /// <inheritdoc/>
    public readonly bool Equals(BodyInputReadbacksSection other) => (
        EngagedIntent.Equals(other: other.EngagedIntent) && ReadHeld.Equals(other: other.ReadHeld) &&
        ReadComposed.Equals(other: other.ReadComposed) && ReadScalar.Equals(other: other.ReadScalar) &&
        RelativeRemainders.Equals(other: other.RelativeRemainders)
    );
    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => ((obj is BodyInputReadbacksSection other) && Equals(other: other));
    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(EngagedIntent, ReadHeld, ReadComposed, ReadScalar, RelativeRemainders);
}/// <summary>One value per channel ordinal, stored inline: a table of <see cref="ChannelLimits.MaxChannels"/> elements
/// that is part of the value that holds it, so a section made of them holds no reference.</summary>
/// <typeparam name="T">The element type.</typeparam>
[System.Runtime.CompilerServices.InlineArray(length: ChannelLimits.MaxChannels)]
public struct PerChannel<T> : IEquatable<PerChannel<T>> where T : unmanaged {
    private T m_element;

    /// <inheritdoc/>
    public readonly bool Equals(PerChannel<T> other) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: ((ReadOnlySpan<T>)this)).SequenceEqual(other: System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: ((ReadOnlySpan<T>)other)));
    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => ((obj is PerChannel<T> other) && Equals(other: other));
    /// <inheritdoc/>
    public override readonly int GetHashCode() {
        var hash = default(HashCode);

        hash.AddBytes(value: System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: ((ReadOnlySpan<T>)this)));
        return hash.ToHashCode();
    }
}
/// <summary>The action track: per channel, a timed press and its value, the previous threshold-crossing bit and a
/// pending tap; per lane, the press latch and the fact-edge memory. Every table holds one element per channel
/// ordinal, inline, so the section holds no reference and a body keeps it behind a <see cref="Versioned{T}"/>.</summary>
public struct BodyActionSection : IContinuationSection<BodyActionSection>, IEquatable<BodyActionSection> {
    /// <summary>The ticks left on each channel's timed press.</summary>
    public PerChannel<ulong> TimerTicks;
    /// <summary>The value each timed press holds while live.</summary>
    public PerChannel<FixedQ4816> TimerValues;
    /// <summary>Each channel's previous threshold-crossing bit.</summary>
    public PerChannel<bool> PreviousBit;
    /// <summary>Whether a tap is staged on each channel and not yet a timer.</summary>
    public PerChannel<bool> PendingPress;
    /// <summary>The value each staged tap holds.</summary>
    public PerChannel<FixedQ4816> PendingValue;
    /// <summary>Each lane's press latch.</summary>
    public PerChannel<ulong> LaneLatch;
    /// <summary>Each lane's previous-evaluation fact bits.</summary>
    public PerChannel<ulong> LaneFactHeld;

    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Action;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyActionSection section) {
        WorldSectionWire.WriteFixed(count: ChannelLimits.MaxChannels, values: section.TimerTicks, writer: writer);
        WorldSectionWire.WriteFixed(count: ChannelLimits.MaxChannels, values: section.TimerValues, writer: writer);
        WorldSectionWire.WriteFixed(count: ChannelLimits.MaxChannels, values: section.PreviousBit, writer: writer);
        WorldSectionWire.WriteFixed(count: ChannelLimits.MaxChannels, values: section.PendingPress, writer: writer);
        WorldSectionWire.WriteFixed(count: ChannelLimits.MaxChannels, values: section.PendingValue, writer: writer);
        WorldSectionWire.WriteFixed(count: ChannelLimits.MaxChannels, values: section.LaneLatch, writer: writer);
        WorldSectionWire.WriteFixed(count: ChannelLimits.MaxChannels, values: section.LaneFactHeld, writer: writer);
    }
    /// <inheritdoc/>
    public static BodyActionSection Read(ref WireReader reader) {
        var section = default(BodyActionSection);

        foreach (ref var ticks in ((Span<ulong>)section.TimerTicks)) { ticks = reader.ReadUInt64(); }
        foreach (ref var value in ((Span<FixedQ4816>)section.TimerValues)) { value = reader.ReadFixed(); }
        foreach (ref var bit in ((Span<bool>)section.PreviousBit)) { bit = reader.ReadBoolean(); }
        foreach (ref var pending in ((Span<bool>)section.PendingPress)) { pending = reader.ReadBoolean(); }
        foreach (ref var value in ((Span<FixedQ4816>)section.PendingValue)) { value = reader.ReadFixed(); }
        foreach (ref var latch in ((Span<ulong>)section.LaneLatch)) { latch = reader.ReadUInt64(); }
        foreach (ref var held in ((Span<ulong>)section.LaneFactHeld)) { held = reader.ReadUInt64(); }
        return section;
    }

    /// <inheritdoc/>
    public readonly bool Equals(BodyActionSection other) => (
        TimerTicks.Equals(other: other.TimerTicks) && TimerValues.Equals(other: other.TimerValues) && PreviousBit.Equals(other: other.PreviousBit) &&
        PendingPress.Equals(other: other.PendingPress) && PendingValue.Equals(other: other.PendingValue) &&
        LaneLatch.Equals(other: other.LaneLatch) && LaneFactHeld.Equals(other: other.LaneFactHeld)
    );
    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => ((obj is BodyActionSection other) && Equals(other: other));
    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(TimerTicks, TimerValues, PreviousBit, PendingPress, PendingValue, LaneLatch, LaneFactHeld);
}
/// <summary>The action lanes' Recently-gate clocks, every lane's in lane order. Which lane owns which clocks is the
/// kit's compiled bindings to say, so the table carries the clocks alone.</summary>
/// <param name="Clocks">The clocks, lane by lane.</param>
public readonly record struct BodyLaneRecencySection(ReadOnlyMemory<ulong> Clocks) : IContinuationTableSection<BodyLaneRecencySection, ulong> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.LaneRecency;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyLaneRecencySection section) => WriteTable(elements: section.Clocks.Span, writer: writer);
    /// <inheritdoc/>
    public static void WriteTable(WireWriter writer, ReadOnlySpan<ulong> elements) =>
        WorldAuthorityCheckpointCodec.WriteULongs(values: elements, writer: writer);
    /// <inheritdoc/>
    public static BodyLaneRecencySection Read(ref WireReader reader) =>
        new(Clocks: WorldAuthorityCheckpointCodec.ReadULongArray(field: "lane recency", reader: ref reader));
}
/// <summary>One register's staged durable write.</summary>
/// <param name="Dirty">Whether a write is staged for the register.</param>
/// <param name="Kind">The staged write's kind.</param>
/// <param name="Operand">The staged write's operand.</param>
public readonly record struct WorldRegisterWriteImage(bool Dirty, WorldDocumentWriteKind Kind, FixedQ4816 Operand);
/// <summary>The durable register writes staged for the profile and not yet drained, one row per register of the kit's
/// register file.</summary>
/// <param name="Rows">The rows, in register order.</param>
public readonly record struct BodyRegisterWritesSection(ReadOnlyMemory<WorldRegisterWriteImage> Rows) : IContinuationTableSection<BodyRegisterWritesSection, WorldRegisterWriteImage> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.RegisterWrites;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyRegisterWritesSection section) => WriteTable(elements: section.Rows.Span, writer: writer);
    /// <inheritdoc/>
    public static void WriteTable(WireWriter writer, ReadOnlySpan<WorldRegisterWriteImage> elements) {
        writer.WriteArrayCount(count: elements.Length);
        for (var row = 0; (row < elements.Length); row++) {
            writer.WriteBoolean(value: elements[row].Dirty);
            writer.WriteByte(value: ((byte)elements[row].Kind));
            writer.WriteFixed(value: elements[row].Operand);
        }
    }
    /// <inheritdoc/>
    public static BodyRegisterWritesSection Read(ref WireReader reader) => new(Rows: reader.ReadArray(
        field: "register writes",
        maximum: WorldAuthorityCheckpointCodec.MaxCollectionCount,
        readItem: static (ref WireReader r) => {
            var dirty = r.ReadBoolean();
            var kind = ((WorldDocumentWriteKind)r.ReadByte());
            var operand = r.ReadFixed();

            if (!r.Failed && !Enum.IsDefined(value: kind)) {
                r.Fail(
                    detail: $"{nameof(WorldDocumentWriteKind)} wire value {((byte)kind)} is not declared",
                    refusal: WireRefusal.EnumValueUnknown
                );
            }
            return new WorldRegisterWriteImage(Dirty: dirty, Kind: kind, Operand: operand);
        }
    ));
}
/// <summary>One register's staged incoming durable write.</summary>
/// <param name="Present">Whether a value is staged for the register.</param>
/// <param name="Value">The staged value.</param>
/// <param name="Timer">The staged timer ticks.</param>
/// <param name="Writer">The staged writer id.</param>
public readonly record struct WorldDurableInputImage(bool Present, FixedQ4816 Value, ulong Timer, string Writer);
/// <summary>An incoming durable register write staged for a tick and not yet applied, one row per register of the
/// kit's register file. A row names its writer, so the table is immutable and a change replaces it: a body keeps the
/// section behind a <see cref="VersionedRecord{T}"/>.</summary>
/// <param name="Tick">The simulation tick the staged input targets.</param>
/// <param name="Rows">The rows, in register order.</param>
public readonly record struct BodyDurableInputSection(ulong Tick, ImmutableArray<WorldDurableInputImage> Rows) : IContinuationSection<BodyDurableInputSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.DurableInput;

    /// <summary>Returns the section of a body with the given number of registers and nothing staged.</summary>
    /// <param name="registers">The number of registers.</param>
    /// <returns>The section.</returns>
    public static BodyDurableInputSection Empty(int registers) => new(
        Rows: ((registers == 0) ? [] : ImmutableArray.Create(items: new WorldDurableInputImage[registers])),
        Tick: 0UL
    );

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyDurableInputSection section) {
        var rows = (section.Rows.IsDefault ? default : section.Rows.AsSpan());

        writer.WriteUInt64(value: section.Tick);
        writer.WriteArrayCount(count: rows.Length);
        for (var row = 0; (row < rows.Length); row++) {
            writer.WriteBoolean(value: rows[row].Present);
            writer.WriteFixed(value: rows[row].Value);
            writer.WriteUInt64(value: rows[row].Timer);
            writer.WriteString(value: rows[row].Writer);
        }
    }
    /// <inheritdoc/>
    public static BodyDurableInputSection Read(ref WireReader reader) => new(
        Tick: reader.ReadUInt64(),
        Rows: ImmutableArray.Create(items: reader.ReadArray(
            field: "durable input",
            maximum: WorldAuthorityCheckpointCodec.MaxCollectionCount,
            readItem: static (ref WireReader r) => new WorldDurableInputImage(
                Present: r.ReadBoolean(),
                Value: r.ReadFixed(),
                Timer: r.ReadUInt64(),
                Writer: r.ReadString(field: "durable input writer")
            )
        ))
    );
}
/// <summary>Who authorized each channel's scripted input: the actor of its timed press and of its pending tap, one
/// per channel ordinal. The tape's actors travel with its segments (<see cref="BodyTapeSection"/>). The tables are
/// immutable: a change replaces a table, so a body keeps the section behind a <see cref="VersionedRecord{T}"/>.</summary>
/// <param name="Timers">The actor of each channel's timed press.</param>
/// <param name="Pending">The actor of each channel's pending tap.</param>
public readonly record struct BodyScriptActorsSection(
    ImmutableArray<Principal> Timers,
    ImmutableArray<Principal> Pending
) : IContinuationSection<BodyScriptActorsSection> {
    /// <summary>Gets the section of a body with no scripted input: one unset actor per channel in both tables.</summary>
    public static BodyScriptActorsSection Empty { get; } = new(
        Pending: ImmutableArray.Create(items: new Principal[ChannelLimits.MaxChannels]),
        Timers: ImmutableArray.Create(items: new Principal[ChannelLimits.MaxChannels])
    );

    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.ScriptActors;

    private static void WriteActors(WireWriter writer, ImmutableArray<Principal> actors) {
        var table = (actors.IsDefault ? default : actors.AsSpan());

        if (table.Length > ChannelLimits.MaxChannels) {
            throw new InvalidOperationException(message: $"a script-actors table holds {table.Length} actors for {ChannelLimits.MaxChannels} channels.");
        }
        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            WorldAuthorityCheckpointCodec.WriteScriptActor(actor: ((ordinal < table.Length) ? table[ordinal] : default), writer: writer);
        }
    }
    private static ImmutableArray<Principal> ReadActors(ref WireReader reader) {
        var actors = new Principal[ChannelLimits.MaxChannels];

        for (var ordinal = 0; (ordinal < actors.Length); ordinal++) { actors[ordinal] = WorldAuthorityCheckpointCodec.ReadScriptActor(reader: ref reader); }
        return ImmutableArray.Create(items: actors);
    }

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyScriptActorsSection section) {
        WriteActors(actors: section.Timers, writer: writer);
        WriteActors(actors: section.Pending, writer: writer);
    }
    /// <inheritdoc/>
    public static BodyScriptActorsSection Read(ref WireReader reader) => new(
        Timers: ReadActors(reader: ref reader),
        Pending: ReadActors(reader: ref reader)
    );
    /// <summary>Returns a table holding the given actors in its first ordinals and unset actors after them: the
    /// present table when it already does, so a write that changes nothing replaces nothing.</summary>
    /// <param name="current">The present table.</param>
    /// <param name="actors">The actors, by channel ordinal; ordinals past the channel count are dropped.</param>
    /// <returns>The table.</returns>
    public static ImmutableArray<Principal> Table(ImmutableArray<Principal> current, ReadOnlySpan<Principal> actors) {
        var count = Math.Min(val1: actors.Length, val2: ChannelLimits.MaxChannels);
        var table = (current.IsDefault ? default : current.AsSpan());

        if ((table.Length == ChannelLimits.MaxChannels) && table[..count].SequenceEqual(other: actors[..count]) && (table[count..].IndexOfAnyExcept(value: default(Principal)) < 0)) {
            return current;
        }

        var replaced = new Principal[ChannelLimits.MaxChannels];

        actors[..count].CopyTo(destination: replaced);
        return ImmutableArray.Create(items: replaced);
    }
}/// <summary>One segment of a body's scripted tape.</summary>
/// <param name="Intent">The intent the segment holds while live.</param>
/// <param name="RemainingTicks">The engine ticks the segment has left.</param>
/// <param name="Actor">The actor who authorized the segment.</param>
public readonly record struct WorldTapeSegmentImage(PlayerIntent Intent, ulong RemainingTicks, Principal Actor);
/// <summary>The scripted tape: its live segments, oldest first.</summary>
/// <param name="Segments">The segments, in dequeue order.</param>
public readonly record struct BodyTapeSection(ReadOnlyMemory<WorldTapeSegmentImage> Segments) : IContinuationSection<BodyTapeSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Tape;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyTapeSection section) {
        var segments = section.Segments.Span;

        writer.WriteArrayCount(count: segments.Length);
        for (var index = 0; (index < segments.Length); index++) {
            WorldWireCodec.WriteIntent(intent: segments[index].Intent, writer: writer);
            writer.WriteUInt64(value: segments[index].RemainingTicks);
            WorldAuthorityCheckpointCodec.WriteScriptActor(actor: segments[index].Actor, writer: writer);
        }
    }
    /// <inheritdoc/>
    public static BodyTapeSection Read(ref WireReader reader) => new(Segments: reader.ReadArray(
        field: "tape segments",
        maximum: WorldAuthorityCheckpointCodec.MaxCollectionCount,
        readItem: static (ref WireReader r) => new WorldTapeSegmentImage(
            Intent: WorldWireCodec.ReadIntent(reader: ref r),
            RemainingTicks: r.ReadUInt64(),
            Actor: WorldAuthorityCheckpointCodec.ReadScriptActor(reader: ref r)
        )
    ));
}
/// <summary>The motion program a body executes, by name, and the adjacency segment it has yet to resolve.</summary>
/// <param name="BodyMotionProgramName">The live motion program's name.</param>
/// <param name="PendingContinuum">The already-evaluated adjacency segment awaiting ownership resolution, or
/// <see langword="null"/> when the body may advance normally.</param>
public readonly record struct BodyProgramSection(
    string BodyMotionProgramName,
    WorldContinuumTrajectory? PendingContinuum
) : IContinuationSection<BodyProgramSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Program;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyProgramSection section) {
        writer.WriteString(value: section.BodyMotionProgramName);
        writer.WriteOptional(
            value: section.PendingContinuum,
            writeValue: WorldWireLeaves.WriteContinuum
        );
    }
    /// <inheritdoc/>
    public static BodyProgramSection Read(ref WireReader reader) => new(
        BodyMotionProgramName: reader.ReadString(field: "body motion program name"),
        PendingContinuum: reader.ReadOptional(readValue: static (ref WireReader r) => WorldWireLeaves.ReadContinuum(reader: ref r))
    );
}
/// <summary>The contacts a rigid body owns: every occupied manifold slot, in contact then slot order. They carry the
/// warm-start impulses the next rigid step solves from and the edges a sleeping island wakes along.</summary>
/// <param name="Slots">The occupied slots.</param>
public readonly record struct BodyContactsSection(ReadOnlyMemory<WorldRigidContactSlotImage> Slots) : IContinuationTableSection<BodyContactsSection, WorldRigidContactSlotImage> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Contacts;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in BodyContactsSection section) => WriteTable(elements: section.Slots.Span, writer: writer);
    /// <inheritdoc/>
    public static void WriteTable(WireWriter writer, ReadOnlySpan<WorldRigidContactSlotImage> elements) {
        var slots = elements;

        writer.WriteArrayCount(count: slots.Length);
        for (var index = 0; (index < slots.Length); index++) {
            ref readonly var slot = ref slots[index];

            writer.WriteInt32(value: slot.Partner);
            writer.WriteInt32(value: slot.Slot);
            writer.WriteInt32(value: slot.SourceId);
            writer.WriteInt32(value: slot.FeatureId);
            writer.WriteFixedVector(value: slot.AnchorA);
            writer.WriteFixedVector(value: slot.AnchorB);
            writer.WriteFixedVector(value: slot.Normal);
            writer.WriteInt64(value: slot.NormalImpulseRaw);
            writer.WriteFixedVector(value: slot.FrictionImpulse);
            writer.WriteInt32(value: slot.IdleSteps);
        }
    }
    /// <inheritdoc/>
    public static BodyContactsSection Read(ref WireReader reader) => new(Slots: reader.ReadArray(
        field: "rigid contacts",
        maximum: WorldPopulation.MaximumRigidContactSlotsPerBody,
        readItem: static (ref WireReader r) => new WorldRigidContactSlotImage(
            Partner: r.ReadInt32(),
            Slot: r.ReadInt32(),
            SourceId: r.ReadInt32(),
            FeatureId: r.ReadInt32(),
            AnchorA: r.ReadFixedVector(),
            AnchorB: r.ReadFixedVector(),
            Normal: r.ReadFixedVector(),
            NormalImpulseRaw: r.ReadInt64(),
            FrictionImpulse: r.ReadFixedVector(),
            IdleSteps: r.ReadInt32()
        )
    ));
}
/// <summary>The sections a body owns of its slot's continuation, as one capture
/// (<see cref="WorldBody.CaptureSections"/>).</summary>
public struct WorldBodySections {
    /// <summary>Where the body is and how it stands.</summary>
    public BodyPoseSection Pose;
    /// <summary>How the body moves.</summary>
    public BodyMotionSection Motion;
    /// <summary>The counts and frames that tick while the body idles.</summary>
    public BodyClocksSection Clocks;
    /// <summary>The motion program's Recently-gate clocks.</summary>
    public BodyMotionRecencySection MotionRecency;
    /// <summary>The input images that arrive and their routing.</summary>
    public BodyInputImagesSection InputImages;
    /// <summary>The images the last advance resolved.</summary>
    public BodyInputReadbacksSection InputReadbacks;
    /// <summary>The action track.</summary>
    public BodyActionSection Action;
    /// <summary>The action lanes' Recently-gate clocks.</summary>
    public BodyLaneRecencySection LaneRecency;
    /// <summary>The staged durable register writes.</summary>
    public BodyRegisterWritesSection RegisterWrites;
    /// <summary>The staged incoming durable write.</summary>
    public BodyDurableInputSection DurableInput;
    /// <summary>The actors of the channels' scripted input.</summary>
    public BodyScriptActorsSection ScriptActors;
    /// <summary>The scripted tape.</summary>
    public BodyTapeSection Tape;
    /// <summary>The motion program and the pending adjacency segment.</summary>
    public BodyProgramSection Program;
    /// <summary>The owned rigid contacts.</summary>
    public BodyContactsSection Contacts;
}
