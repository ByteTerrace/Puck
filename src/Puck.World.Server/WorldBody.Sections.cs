using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Memory;
using Puck.Maths;
using Puck.Networking;
using Puck.Physics;

namespace Puck.World.Server;

// A body's continuation as sections: the images a checkpoint entry carries and a continuation fold digests. Each
// section is built here from the fields it groups and written back to them on a restore; nothing else reads a body's
// state for a checkpoint. The crossing's transfer state and the refused sweep's snapshot (WorldBody.Transfer.cs,
// WorldBody.SweepRefusal.cs) are separate captures with their own purposes.
public sealed partial class WorldBody {
    // Scratch a view assembles into, reused from one view to the next and shared with no capture.
    private ulong[]? m_viewLaneRecency;
    private long[]? m_viewRegisters;
    private WorldTapeSegmentImage[] m_viewTape = [];
    private WorldRigidContactSlotImage[] m_viewContacts = [];
    private WorldRegisterWriteImage[]? m_viewRegisterWrites;

    /// <summary>Captures this body's sections, read live. A capture owns every table it holds. A view's tables are
    /// the body's own live storage, or scratch the body reuses for the ones that need assembling, so a view allocates
    /// nothing once that scratch has grown to this body's shape; it is valid only until the body next changes or is
    /// viewed again.</summary>
    /// <param name="alias">Whether to take a view instead of a capture.</param>
    /// <param name="sections">The sections.</param>
    /// <param name="registers">The body's register file as the arena's slot lanes hold it, one raw value per slot of
    /// the kit's compiled definitions: state the arena owns, read beside the sections for an entry's tail.</param>
    /// <param name="held">Whether to leave out the sections a holder digests (<see cref="HeldDigests"/>): a fold's
    /// view, which reads those digests from the holders and never the sections.</param>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private void CaptureCoreSections(out BodyPoseSection pose, out BodyMotionSection motion, out BodyClocksSection clocks, out BodyProgramSection program) {
        pose = new(
            Orientation: m_orientation,
            Position: m_position,
            PreviousPosition: m_previousPosition,
            Scale: m_scale,
            Up: m_up,
            Yaw: m_yaw
        );
        motion = new(
            AffectingSubject: m_affectingSubject,
            AtMediumBand: m_atMediumBand,
            AttachPreviousBit: m_attachPreviousBit,
            AttitudeLeaned: m_attitudeLeaned,
            AttitudeTurnRemainder: m_attitudeTurnAccumulator.Remainder,
            AttitudeUp: m_attitudeUp,
            CarriedBy: m_carriedByIndex,
            Carrying: m_carryingIndex,
            DetachPreviousBit: m_detachPreviousBit,
            DriveLatRemainder: m_driveLatAccumulator.Remainder,
            DriveLongRemainder: m_driveLongAccumulator.Remainder,
            DrivePitch: m_drivePitch,
            DriveResidualRemainder: m_driveResidualAccumulator.Remainder,
            Grounded: m_grounded,
            HoldAnchor: m_holdAnchor,
            HoldIndex: m_holdIndex,
            HoldNormal: m_holdNormal,
            HoldSpendRemainder: m_holdSpendAccumulator.Remainder,
            Home: m_home,
            InMedium: m_inMedium,
            MediumThrustRampRemainder: m_mediumThrustRampAccumulator.Remainder,
            OverlayRemainder: (m_overlayAccumulator.XRemainder, m_overlayAccumulator.YRemainder, m_overlayAccumulator.ZRemainder),
            OverlayVelocity: m_overlayVelocity,
            TimedMove: m_timedMove,
            PlanarFollowerPosition: (m_planarFollower.X.PositionRaw, m_planarFollower.Y.PositionRaw, m_planarFollower.Z.PositionRaw),
            PlanarFollowerPreviousTarget: m_planarPreviousTarget,
            PlanarFollowerSeeded: m_planarFollowerSeeded,
            PlanarFollowerVelocity: (m_planarFollower.X.VelocityRaw, m_planarFollower.Y.VelocityRaw, m_planarFollower.Z.VelocityRaw),
            PlanarRampRemainder: m_planarRampAccumulator.Remainder,
            PlanarVelocity: m_planarVelocity,
            PositionRemainder: (m_positionAccumulator.XRemainder, m_positionAccumulator.YRemainder, m_positionAccumulator.ZRemainder),
            RigidAngularVelocity: m_angularVelocity,
            RigidResting: m_resting,
            RigidVelocity: m_rigidVelocity,
            RotationRemainder: (m_rotationAccumulator.XRemainder, m_rotationAccumulator.YRemainder, m_rotationAccumulator.ZRemainder),
            Tether: m_tether?.CaptureState(),
            TetherAnchorBodyIndex: m_tetherAnchorBodyIndex,
            TetherAnchorPointOrLocalOffset: m_tetherAnchorPointOrLocalOffset,
            VerticalFollowerPositionRaw: m_verticalFollower.PositionRaw,
            VerticalFollowerPreviousTarget: m_verticalPreviousTarget,
            VerticalFollowerSeeded: m_verticalFollowerSeeded,
            VerticalFollowerVelocityRaw: m_verticalFollower.VelocityRaw,
            VerticalVelocity: m_verticalVelocity,
            VerticalVelocityRemainder: m_verticalVelocityAccumulator.Remainder
        );
        clocks = new(
            IdleSleepGravity: IdleSleepGravity,
            AsleepSinceTick: m_asleepSinceTick,
            ContactUpTurnRemainder: m_contactUpTurnAccumulator.Remainder,
            ContinuumConsumedThroughEngineTick: m_continuumConsumedThroughEngineTick,
            FieldUpTurnRemainder: m_upTurnAccumulator.Remainder,
            Frame: m_frame,
            OrdinaryAdvanceAdmitted: m_ordinaryAdvanceAdmitted,
            OverlayRemainingTicks: m_overlayRemaining,
            RigidRestingHoldTicks: m_restingHoldTicks,
            RigidSleepRootPlusOne: m_rigidSleepRootPlusOne,
            SleepIdleTicks: m_sleepIdleTicks,
            UpNeedsReseat: m_upNeedsReseat
        );
        program = new(
            BodyMotionProgramName: m_bodyMotionProgram.Name,
            PendingContinuum: m_pendingContinuum
        );
    }

    [System.Runtime.CompilerServices.SkipLocalsInit]
    internal void CaptureSections(bool alias, out WorldBodySections sections, out ReadOnlyMemory<long> registers, bool held = false) {
        CaptureCoreSections(clocks: out sections.Clocks, motion: out sections.Motion, pose: out sections.Pose, program: out sections.Program);
        sections.MotionRecency = new(Clocks: Table(alias: alias, live: m_motionRecency));

        // The two Input sections, the Action section, the lanes' clocks and the staged register writes live behind
        // holders, which digest them for a fold; a fold's view leaves them out, and the fold reads none of them from
        // the view. The two bookkeeping record sections are their holders' values as they stand.
        if (held) {
            Unsafe.SkipInit(value: out sections.InputImages);
            Unsafe.SkipInit(value: out sections.InputReadbacks);
            Unsafe.SkipInit(value: out sections.Action);
            sections.LaneRecency = default;
            sections.RegisterWrites = default;
        } else {
            var laneRecency = Assembled(alias: alias, length: m_laneRecency.Count, scratch: ref m_viewLaneRecency);
            var registerWrites = Assembled(alias: alias, length: m_registerWrites.Count, scratch: ref m_viewRegisterWrites);

            m_laneRecency.Read.CopyTo(destination: laneRecency);
            m_registerWrites.Read.CopyTo(destination: registerWrites);
            sections.InputImages = InputImages;
            sections.InputReadbacks = InputReadbacks;
            sections.Action = Action;
            sections.LaneRecency = new(Clocks: laneRecency);
            sections.RegisterWrites = new(Rows: registerWrites);
        }
        sections.DurableInput = DurableInput;
        sections.ScriptActors = ScriptActors;

        var tape = alias ? ArrayCapacity.EnsureScratch(ref m_viewTape, m_tapeCount) : (m_tapeCount == 0 ? [] : new WorldTapeSegmentImage[m_tapeCount]);

        for (var offset = 0; (offset < m_tapeCount); offset++) {
            ref readonly var segment = ref m_tape[((m_tapeHead + offset) % m_tape.Length)];

            tape[offset] = new(Actor: segment.Actor, Intent: segment.Intent, RemainingTicks: segment.RemainingTicks);
        }
        sections.Tape = new(Segments: tape.AsMemory(length: m_tapeCount, start: 0));

        var occupied = CountRigidContactSlots();
        var contacts = alias ? ArrayCapacity.EnsureScratch(ref m_viewContacts, occupied) : (occupied == 0 ? [] : new WorldRigidContactSlotImage[occupied]);

        CopyRigidContactImages(destination: contacts);
        sections.Contacts = new(Slots: contacts.AsMemory(length: occupied, start: 0));

        var registerImage = Assembled(alias: alias, length: m_actionStateDefinitions.Length, scratch: ref m_viewRegisters);

        ReadActionState(image: registerImage);
        registers = registerImage;
    }

    /// <summary>Digests the sections this body keeps behind holders, each through its holder: the cached digest
    /// while the section's version stands or its content is unchanged, a fresh encoding otherwise. A fold folds these
    /// in the sections' places, so a section that did not change is neither captured nor encoded.</summary>
    /// <param name="writer">The scratch the sections encode into.</param>
    /// <param name="digests">The digests, by <see cref="WorldContinuationSectionKind"/>; only the held sections' are written.</param>
    /// <param name="work">What each digest read did, by <see cref="WorldContinuationSectionKind"/>.</param>
    /// <returns>The held sections, one bit per <see cref="WorldContinuationSectionKind"/>.</returns>
    internal uint HeldDigests(WireWriter writer, Span<ulong> digests, Span<SectionDigestWork> work) {
        digests[((int)WorldContinuationSectionKind.Action)] = m_action.Digest<ContinuationSectionCodec<BodyActionSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.Action)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.ScriptActors)] = m_scriptActors.Digest<ContinuationSectionCodec<BodyScriptActorsSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.ScriptActors)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.LaneRecency)] = m_laneRecency.Digest<ContinuationTableCodec<BodyLaneRecencySection, ulong>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.LaneRecency)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.RegisterWrites)] = m_registerWrites.Digest<ContinuationTableCodec<BodyRegisterWritesSection, WorldRegisterWriteImage>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.RegisterWrites)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.DurableInput)] = m_durableInput.Digest<ContinuationSectionCodec<BodyDurableInputSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.DurableInput)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.InputImages)] = m_inputImages.Digest<ContinuationSectionCodec<BodyInputImagesSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.InputImages)],
            writer: writer
        );
        digests[((int)WorldContinuationSectionKind.InputReadbacks)] = m_inputReadbacks.Digest<ContinuationSectionCodec<BodyInputReadbacksSection>, WireWriter>(
            work: ref work[((int)WorldContinuationSectionKind.InputReadbacks)],
            writer: writer
        );
        return (
            (1U << ((int)WorldContinuationSectionKind.InputImages)) |
            (1U << ((int)WorldContinuationSectionKind.InputReadbacks)) |
            (1U << ((int)WorldContinuationSectionKind.Action)) |
            (1U << ((int)WorldContinuationSectionKind.LaneRecency)) |
            (1U << ((int)WorldContinuationSectionKind.RegisterWrites)) |
            (1U << ((int)WorldContinuationSectionKind.DurableInput)) |
            (1U << ((int)WorldContinuationSectionKind.ScriptActors))
        );
    }
    // Overwrites the staged incoming write from a captured one: the tick, and as many rows as both tables hold. The
    // table is replaced only when a row or the tick differs.
    private void RestoreDurableInput(ulong tick, ReadOnlySpan<WorldDurableInputImage> rows) {
        var current = DurableInput.Rows.AsSpan();
        var count = Math.Min(val1: current.Length, val2: rows.Length);

        if (current[..count].SequenceEqual(other: rows[..count])) {
            if (DurableInput.Tick != tick) { m_durableInput.Write = (DurableInput with { Tick = tick }); }
            return;
        }

        var restored = current.ToArray();

        rows[..count].CopyTo(destination: restored);
        m_durableInput.Write = new(Rows: ImmutableArray.Create(items: restored), Tick: tick);
    }
    // A table as a section holds it: the live array for a view, a copy for a capture.
    private static ReadOnlyMemory<T> Table<T>(bool alias, T[] live) => ((alias || (live.Length == 0))
        ? live
        : live.AsSpan().ToArray()
    );
    // An array of exactly length elements: the body's view scratch for a view (replaced only when this body's shape
    // changes), a fresh array for a capture.
    private static T[] Assembled<T>(bool alias, int length, ref T[]? scratch) {
        if (!alias) {
            return ((length == 0) ? [] : new T[length]);
        }
        if ((scratch is null) || (scratch.Length != length)) {
            scratch = ((length == 0) ? [] : new T[length]);
        }

        return scratch;
    }

    /// <summary>Restores this body's continuation from captured sections, onto a body already built for the entry's
    /// kit, scaled, and posed at the captured position. The order is fixed: the motion program is selected first,
    /// because switching it resets state every copy after it must have the last word on; then every section is
    /// copied; then the one process-local value, the contact-field observation, is rebased onto this population's
    /// counter. Island membership, the owned contacts and the work sets are the population's to adopt afterwards.
    /// Nothing here wakes the body: the sections carry the sleep state the captured body had.</summary>
    /// <param name="sections">The captured sections.</param>
    /// <param name="registers">The captured register file, written back into the arena's slot lanes.</param>
    /// <param name="contactFieldObservationCurrent">Whether the captured body had observed the contact field as it
    /// stood at capture.</param>
    /// <param name="contactFieldVersion">The restoring population's current contact-field version.</param>
    internal void RestoreSections(in WorldBodySections sections, ReadOnlySpan<long> registers, bool contactFieldObservationCurrent, ulong contactFieldVersion) {
        if (!string.IsNullOrEmpty(value: sections.Program.BodyMotionProgramName)) {
            SetBodyMotionProgram(programName: sections.Program.BodyMotionProgramName);
        }

        ref readonly var pose = ref sections.Pose;

        m_position = pose.Position;
        m_yaw = pose.Yaw;
        m_orientation = pose.Orientation;
        m_up = pose.Up;
        m_previousPosition = pose.PreviousPosition;

        ref readonly var motion = ref sections.Motion;

        m_planarVelocity = motion.PlanarVelocity;
        m_verticalVelocity = motion.VerticalVelocity;
        m_drivePitch = motion.DrivePitch;
        m_overlayVelocity = motion.OverlayVelocity;
        m_timedMove = (CanStartTimedMove ? motion.TimedMove : default);
        m_rigidVelocity = motion.RigidVelocity;
        m_angularVelocity = motion.RigidAngularVelocity;
        m_resting = motion.RigidResting;
        m_grounded = motion.Grounded;
        m_inMedium = motion.InMedium;
        m_atMediumBand = motion.AtMediumBand;
        m_affectingSubject = motion.AffectingSubject;
        m_home = motion.Home;
        m_carryingIndex = motion.Carrying;
        m_carriedByIndex = motion.CarriedBy;
        // A captured remainder was read off a live accumulator bound to the same engine tick base, whose own contract
        // keeps it inside that base, so FromRemainder never refuses one a capture produced.
        m_positionAccumulator = FixedVector3RateAccumulator.FromRemainders(
            ticksPerSecond: EngineTicksPerSecond,
            xRemainder: motion.PositionRemainder.X,
            yRemainder: motion.PositionRemainder.Y,
            zRemainder: motion.PositionRemainder.Z
        );
        m_rotationAccumulator = FixedVector3RateAccumulator.FromRemainders(
            ticksPerSecond: EngineTicksPerSecond,
            xRemainder: motion.RotationRemainder.X,
            yRemainder: motion.RotationRemainder.Y,
            zRemainder: motion.RotationRemainder.Z
        );
        m_verticalVelocityAccumulator = FixedRateAccumulator.FromRemainder(remainder: motion.VerticalVelocityRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_planarRampAccumulator = FixedRateAccumulator.FromRemainder(remainder: motion.PlanarRampRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_driveLongAccumulator = FixedRateAccumulator.FromRemainder(remainder: motion.DriveLongRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_driveLatAccumulator = FixedRateAccumulator.FromRemainder(remainder: motion.DriveLatRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_driveResidualAccumulator = FixedRateAccumulator.FromRemainder(remainder: motion.DriveResidualRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_mediumThrustRampAccumulator = FixedRateAccumulator.FromRemainder(remainder: motion.MediumThrustRampRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_overlayAccumulator = FixedVector3RateAccumulator.FromRemainders(
            ticksPerSecond: EngineTicksPerSecond,
            xRemainder: motion.OverlayRemainder.X,
            yRemainder: motion.OverlayRemainder.Y,
            zRemainder: motion.OverlayRemainder.Z
        );
        m_holdSpendAccumulator = FixedRateAccumulator.FromRemainder(remainder: motion.HoldSpendRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_attitudeTurnAccumulator = FixedRateAccumulator.FromRemainder(remainder: motion.AttitudeTurnRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_planarFollower = new SecondOrderState3(
            X: SecondOrderState.FromRawBits(positionRaw: motion.PlanarFollowerPosition.X, velocityRaw: motion.PlanarFollowerVelocity.X),
            Y: SecondOrderState.FromRawBits(positionRaw: motion.PlanarFollowerPosition.Y, velocityRaw: motion.PlanarFollowerVelocity.Y),
            Z: SecondOrderState.FromRawBits(positionRaw: motion.PlanarFollowerPosition.Z, velocityRaw: motion.PlanarFollowerVelocity.Z)
        );
        m_planarPreviousTarget = motion.PlanarFollowerPreviousTarget;
        m_planarFollowerSeeded = motion.PlanarFollowerSeeded;
        m_verticalFollower = SecondOrderState.FromRawBits(positionRaw: motion.VerticalFollowerPositionRaw, velocityRaw: motion.VerticalFollowerVelocityRaw);
        m_verticalPreviousTarget = motion.VerticalFollowerPreviousTarget;
        m_verticalFollowerSeeded = motion.VerticalFollowerSeeded;
        m_holdIndex = motion.HoldIndex;
        m_holdAnchor = motion.HoldAnchor;
        m_holdNormal = motion.HoldNormal;
        m_attitudeUp = motion.AttitudeUp;
        m_attitudeLeaned = motion.AttitudeLeaned;
        m_attachPreviousBit = motion.AttachPreviousBit;
        m_detachPreviousBit = motion.DetachPreviousBit;
        m_tether = ((motion.Tether is { } tetherState)
            ? FixedTetherConstraint.FromState(state: tetherState)
            : null
        );
        m_tetherAnchorBodyIndex = motion.TetherAnchorBodyIndex;
        m_tetherAnchorPointOrLocalOffset = motion.TetherAnchorPointOrLocalOffset;

        ref readonly var clocks = ref sections.Clocks;

        m_frame = clocks.Frame;
        m_upNeedsReseat = clocks.UpNeedsReseat;
        m_upTurnAccumulator = FixedRateAccumulator.FromRemainder(remainder: clocks.FieldUpTurnRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_contactUpTurnAccumulator = FixedRateAccumulator.FromRemainder(remainder: clocks.ContactUpTurnRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_asleepSinceTick = clocks.AsleepSinceTick;
        IdleSleepGravity = clocks.IdleSleepGravity;
        m_sleepIdleTicks = clocks.SleepIdleTicks;
        // The island root is not written here: the population parks the restored body under it
        // (WorldPopulation.RestoreSleepingIslands, AdoptRestoredIsland), the one writer of island membership.
        m_ordinaryAdvanceAdmitted = clocks.OrdinaryAdvanceAdmitted;
        m_continuumConsumedThroughEngineTick = clocks.ContinuumConsumedThroughEngineTick;
        m_overlayRemaining = clocks.OverlayRemainingTicks;
        m_restingHoldTicks = clocks.RigidRestingHoldTicks;
        CopyClamped(destination: m_motionRecency, source: sections.MotionRecency.Clocks.Span);

        InputImagesEdit = sections.InputImages;

        var readbacks = sections.InputReadbacks;

        // A body with no relative channel carries no fractions, whatever the capture held.
        if (m_channels.RelativeMask == 0) { readbacks.RelativeRemainders = default; }
        InputReadbacksEdit = readbacks;

        ActionEdit = sections.Action;

        // The clocks are laid out by this body's own lanes, in lane order: the table takes as many as it holds from
        // what the capture carried.
        RestoreTable(holder: m_laneRecency, source: sections.LaneRecency.Clocks.Span);
        RestoreActionState(image: registers);
        RestoreTable(holder: m_registerWrites, source: sections.RegisterWrites.Rows.Span);
        RestoreDurableInput(
            rows: (sections.DurableInput.Rows.IsDefault ? default : sections.DurableInput.Rows.AsSpan()),
            tick: sections.DurableInput.Tick
        );
        SetScriptActors(
            pending: (sections.ScriptActors.Pending.IsDefault ? default : sections.ScriptActors.Pending.AsSpan()),
            timers: (sections.ScriptActors.Timers.IsDefault ? default : sections.ScriptActors.Timers.AsSpan())
        );
        RestoreTape(segments: sections.Tape.Segments.Span);
        m_pendingContinuum = sections.Program.PendingContinuum;

        // A stale observation must stay stale against every version the next step can present. Versions only grow,
        // so the version just below the current one is never seen again; flipping the low bit would equal the next
        // version whenever the current one is even, and a sleeping body would then skip the wake the live run took.
        m_lastContactFieldVersion = (contactFieldObservationCurrent
            ? contactFieldVersion
            : unchecked((contactFieldVersion - 1UL))
        );
    }
}
