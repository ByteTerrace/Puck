using System.Runtime.CompilerServices;
using Puck.Maths;
using Puck.Networking;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

// A body's continuation as sections: the images a checkpoint entry carries and a continuation fold digests. Each
// section is built here from the fields it groups and written back to them on a restore; nothing else reads a body's
// state for a checkpoint. The crossing's transfer state and the refused sweep's snapshot (WorldBody.Transfer.cs,
// WorldBody.SweepRefusal.cs) are separate captures with their own purposes.
public sealed partial class WorldBody {
    // The remainders of a body with no relative channel: the zeros its section stands for, shared and never written.
    private static readonly long[] NoRelativeRemainders = new long[ChannelLimits.MaxAxes];

    // Scratch a view assembles into, reused from one view to the next and shared with no capture.
    private ulong[]? m_viewLaneRecency;
    private long[]? m_viewRegisters;
    private WorldTapeSegmentImage[]? m_viewTape;
    private WorldRigidContactSlotImage[]? m_viewContacts;
    private WorldRegisterWriteImage[]? m_viewRegisterWrites;
    private WorldDurableInputImage[]? m_viewDurableInput;

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
    internal void CaptureSections(bool alias, out WorldBodySections sections, out ReadOnlyMemory<long> registers, bool held = false) {
        sections.Pose = new(
            Orientation: m_orientation,
            Position: m_position,
            PreviousPosition: m_previousPosition,
            Scale: m_scale,
            Up: m_up,
            Yaw: m_yaw
        );
        sections.Motion = new(
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
        sections.Clocks = new(
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
        sections.MotionRecency = new(Clocks: Table(alias: alias, live: m_motionRecency));
        sections.InputImages = new(
            Engaged: m_engaged,
            Epoch: InputEpoch,
            HasProducer: m_hasProducerIntent,
            HasSubmitted: m_hasSubmittedIntent,
            HasTransferHeld: m_hasTransferHeldChannels,
            Held: m_heldChannels,
            Mapping: InputMappingId,
            Producer: m_producerIntent,
            Source: m_source,
            Submitted: m_submittedIntent,
            TransferHeld: m_transferHeldChannels
        );
        sections.InputReadbacks = new(
            EngagedIntent: m_engagedIntent,
            ReadComposed: m_channelReadComposed,
            ReadHeld: m_channelReadHeld,
            ReadScalar: m_channelReadScalar,
            RelativeRemainders: ((m_relativeRemainders.Length == 0) ? NoRelativeRemainders : Table(alias: alias, live: m_relativeRemainders))
        );

        var recencyCount = 0;

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            recencyCount += (m_laneRecency[lane]?.Length ?? 0);
        }

        var laneRecency = Assembled(alias: alias, length: recencyCount, scratch: ref m_viewLaneRecency);
        var recencyStart = 0;

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            if (m_laneRecency[lane] is { } recency) {
                recency.CopyTo(array: laneRecency, index: recencyStart);
                recencyStart += recency.Length;
            }
        }

        // The Action section lives behind its holder, which digests it for a fold; a fold's view leaves it out.
        if (held) {
            Unsafe.SkipInit(value: out sections.Action);
        } else {
            sections.Action = Action;
        }
        sections.LaneRecency = new(Clocks: laneRecency);

        var registerWrites = Assembled(alias: alias, length: m_actionStateDirty.Length, scratch: ref m_viewRegisterWrites);
        var durableInput = Assembled(alias: alias, length: m_durableInputPresent.Length, scratch: ref m_viewDurableInput);

        for (var slot = 0; (slot < registerWrites.Length); slot++) {
            registerWrites[slot] = new(Dirty: m_actionStateDirty[slot], Kind: m_actionStateDirtyKind[slot], Operand: m_actionStateDirtyOperand[slot]);
        }
        for (var slot = 0; (slot < durableInput.Length); slot++) {
            durableInput[slot] = new(Present: m_durableInputPresent[slot], Timer: m_durableInputTimers[slot], Value: m_durableInputValues[slot], Writer: m_durableInputWriters[slot]);
        }
        sections.RegisterWrites = new(Rows: registerWrites);
        sections.DurableInput = new(Rows: durableInput, Tick: m_durableInputTick);
        sections.ScriptActors = ScriptActors;

        var tape = Grown(alias: alias, count: m_tapeCount, scratch: ref m_viewTape);

        for (var offset = 0; (offset < m_tapeCount); offset++) {
            ref readonly var segment = ref m_tape[((m_tapeHead + offset) % m_tape.Length)];

            tape[offset] = new(Actor: segment.Actor, Intent: segment.Intent, RemainingTicks: segment.RemainingTicks);
        }
        sections.Tape = new(Segments: tape.AsMemory(length: m_tapeCount, start: 0));
        sections.Program = new(
            BodyMotionProgramName: m_bodyMotionProgram.Name,
            PendingContinuum: m_pendingContinuum
        );

        var occupied = CountRigidContactSlots();
        var contacts = Grown(alias: alias, count: occupied, scratch: ref m_viewContacts);

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
        return ((1U << ((int)WorldContinuationSectionKind.Action)) | (1U << ((int)WorldContinuationSectionKind.ScriptActors)));
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
    // An array of at least count elements: the body's view scratch for a view, which grows and never shrinks so a
    // table whose length follows the step (the tape, the contacts) costs no allocation as it changes, or a fresh
    // array of exactly count for a capture.
    private static T[] Grown<T>(bool alias, int count, ref T[]? scratch) {
        if (!alias) {
            return ((count == 0) ? [] : new T[count]);
        }
        if ((scratch is null) || (scratch.Length < count)) {
            scratch = new T[Math.Max(val1: count, val2: ((scratch?.Length ?? 0) * 2))];
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
        m_sleepIdleTicks = clocks.SleepIdleTicks;
        // The island root is not written here: the population parks the restored body under it
        // (WorldPopulation.RestoreSleepingIslands, AdoptRestoredIsland), the one writer of island membership.
        m_ordinaryAdvanceAdmitted = clocks.OrdinaryAdvanceAdmitted;
        m_continuumConsumedThroughEngineTick = clocks.ContinuumConsumedThroughEngineTick;
        m_overlayRemaining = clocks.OverlayRemainingTicks;
        m_restingHoldTicks = clocks.RigidRestingHoldTicks;
        CopyClamped(destination: m_motionRecency, source: sections.MotionRecency.Clocks.Span);

        ref readonly var images = ref sections.InputImages;

        m_submittedIntent = images.Submitted;
        m_hasSubmittedIntent = images.HasSubmitted;
        m_heldChannels = images.Held;
        m_producerIntent = images.Producer;
        m_hasProducerIntent = images.HasProducer;
        m_transferHeldChannels = images.TransferHeld;
        m_hasTransferHeldChannels = images.HasTransferHeld;
        m_source = images.Source;
        m_engaged = images.Engaged;
        InputMappingId = images.Mapping;
        InputEpoch = images.Epoch;

        ref readonly var readbacks = ref sections.InputReadbacks;

        m_engagedIntent = readbacks.EngagedIntent;
        m_channelReadHeld = readbacks.ReadHeld;
        m_channelReadComposed = readbacks.ReadComposed;
        m_channelReadScalar = readbacks.ReadScalar;
        CopyClamped(destination: m_relativeRemainders, source: readbacks.RelativeRemainders.Span);

        ActionEdit = sections.Action;

        var laneRecency = sections.LaneRecency.Clocks.Span;
        var recencyStart = 0;

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            // The clocks are laid out by this body's own lanes: a lane takes as many as its compiled binding
            // declares, in lane order, from what the capture carried.
            if (m_laneRecency[lane] is { } recency) {
                var available = Math.Max(val1: 0, val2: Math.Min(val1: recency.Length, val2: (laneRecency.Length - recencyStart)));

                laneRecency.Slice(length: available, start: recencyStart).CopyTo(destination: recency);
                recencyStart += recency.Length;
            }
        }

        RestoreActionState(image: registers);

        var registerWrites = sections.RegisterWrites.Rows.Span;
        var durableInput = sections.DurableInput.Rows.Span;

        for (var slot = 0; (slot < Math.Min(val1: registerWrites.Length, val2: m_actionStateDirty.Length)); slot++) {
            m_actionStateDirty[slot] = registerWrites[slot].Dirty;
            m_actionStateDirtyKind[slot] = registerWrites[slot].Kind;
            m_actionStateDirtyOperand[slot] = registerWrites[slot].Operand;
        }
        for (var slot = 0; (slot < Math.Min(val1: durableInput.Length, val2: m_durableInputPresent.Length)); slot++) {
            m_durableInputPresent[slot] = durableInput[slot].Present;
            m_durableInputValues[slot] = durableInput[slot].Value;
            m_durableInputTimers[slot] = durableInput[slot].Timer;
            m_durableInputWriters[slot] = durableInput[slot].Writer;
        }
        m_durableInputTick = sections.DurableInput.Tick;
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
