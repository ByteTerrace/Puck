using System.Globalization;
using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // Copies a compiled kit's per-ordinal actions/thresholds/shapes into this body's own arrays (never aliasing the
    // kit's shared arrays, which RecompileKit would otherwise mutate out from under every body sharing the old kit row).
    private void CopyChannelBindings(CompiledActionSpec?[]? actions, FixedQ4816[]? actionThresholds, ChannelShape[]? actionShapes, bool[]? roleMask) {
        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            m_laneBindings[ordinal] = (((actions is { } bound) && (ordinal < bound.Length))
                ? bound[ordinal]
                : null
            );
            m_channelThresholds[ordinal] = (((actionThresholds is { } thresholds) && (ordinal < thresholds.Length))
                ? thresholds[ordinal]
                : FixedQ4816.Zero
            );
            m_channelShapes[ordinal] = (((actionShapes is { } shapes) && (ordinal < shapes.Length))
                ? shapes[ordinal]
                : ChannelShape.Bipolar
            );
            m_roleChannels[ordinal] = ((roleMask is { } roles) && (ordinal < roles.Length) && roles[ordinal]);
        }
    }
    // The shared clamped-copy every array field ApplyTransferState restores uses: never trusts the captured array's
    // length to match the restored body's own (a defensive habit, not a load-bearing one — the SAME seat kit always
    // produces the SAME lengths for a same-process abort/restore).
    private static void CopyClamped<T>(T[] source, T[] destination) {
        var count = Math.Min(
            val1: source.Length,
            val2: destination.Length
        );

        for (var index = 0; (index < count); index++) {
            destination[index] = source[index];
        }
    }
    // Advance the ring past its front segment (a FIFO dequeue): step the head and shrink the live count.
    private void DropFrontSegment() {
        m_tapeHead = ((m_tapeHead + 1) % m_tape.Length);
        m_tapeCount--;
    }
    // Grow the tape ring to twice its capacity, copying the live segments into FIFO order from index 0 (head reset).
    // Only when a burst would exceed the current slots; amortized O(1).
    private void GrowTape() {
        var grown = new TapeSegment[(m_tape.Length * 2)];

        for (var offset = 0; (offset < m_tapeCount); offset++) {
            grown[offset] = m_tape[((m_tapeHead + offset) % m_tape.Length)];
        }

        m_tape = grown;
        m_tapeHead = 0;
    }
    // Rewrites the WHOLE tape ring from a captured FIFO snapshot — the abort-restore counterpart to
    // CaptureTransferState's own tape read. Bypasses EnqueueRun's seconds-based API deliberately: re-deriving ticks
    // from a seconds-rounded value would drift the restored segment's remaining duration from the exact tick count
    // that was live at capture. Always called on a freshly constructed restore body (RestoreDetachedSeat never calls
    // ApplyTransferState on a live one), so there is never an existing segment to preserve or lose.
    private void RestoreTape(PlayerIntent[] intents, ulong[] remainingTicks) {
        var count = Math.Min(
            val1: intents.Length,
            val2: remainingTicks.Length
        );

        while (m_tape.Length < count) {
            GrowTape();
        }

        m_tapeHead = 0;
        m_tapeCount = count;

        for (var index = 0; (index < count); index++) {
            m_tape[index] = new TapeSegment { Intent = intents[index], RemainingTicks = remainingTicks[index] };
        }
    }
    private void SetBodyMotionProgram(CompiledBodyMotionProgram program) {
        if (string.Equals(
            a: program.Name,
            b: m_bodyMotionProgram.Name,
            comparisonType: StringComparison.Ordinal
        )) {
            m_bodyMotionProgram = program;
            return;
        }

        m_bodyMotionProgram = program;
        // A yaw-scalar program (the ordinary frame, or the drive frame — which levels its pitch scalar too) re-pins
        // the attitude from the extracted heading; the free 6DOF program keeps the attitude and re-seeds the scalar.
        var resolvesYawAttitude = (program.Contains(operation: BodyMotionOp.ResolveYawAttitudeAndPlanarFrame)
            || program.Contains(operation: BodyMotionOp.ResolveDriveFrame));

        if (resolvesYawAttitude) {
            m_yaw = ExtractYaw(orientation: m_orientation);
            m_drivePitch = FixedQ4816.Zero;
            m_orientation = FixedQuaternion.FromAxisAngle(
                angle: m_yaw,
                axis: UnitY
            );
        } else if (program.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)) {
            m_yaw = ExtractYaw(orientation: m_orientation);
        }

        m_motionRecency = ((m_tuning.RecencySlots > 0)
            ? new ulong[m_tuning.RecencySlots]
            : []
        );
        // A program that lacks the medium law must not leave stale medium facts behind — a switch away from one
        // clears them here; a switch back rewrites them next tick.
        m_inMedium = false;
        m_atMediumBand = false;
        CommitTeleport(resetVertical: program.OwnsVerticalContactState);
        m_continuity = EntityContinuity.Teleport;
    }
    // The one dispatch point from a kit's compiled locomotion tuning to the field this class integrates under —
    // never a hunt through Advance's op handlers, which stay generic over whatever the kit's body motion program
    // selects. WorldDefinitionValidator has already refused an incoherent pairing (a program whose operations need a
    // facet the declared row doesn't supply) before this ever runs.
    private void SetTuning(FixedMotionTuning tuning, FixedBodyHold[]? holds = null) {
        m_holds = (holds ?? []);

        if (m_holdIndex >= m_holds.Length) {
            // A retune that shortened (or dropped) the list cannot leave the body holding a row that no longer
            // exists; the next ResolveHold re-takes from the new list.
            m_holdIndex = -1;
            m_holdAnchor = FixedVector3.Zero;
            m_holdNormal = FixedVector3.Zero;
            m_holdSpendAccumulator.Reset();
        }

        m_tuning = tuning;
    }

    /// <summary>Clears the scripted tape, dropping every queued segment. The held keys (if any) resume driving.</summary>
    public void ClearTape() {
        // Drop the live range without releasing the ring's backing array — the slots are struct storage, reused next
        // enqueue. The stale segment structs are never read while m_tapeCount is 0.
        m_tapeHead = 0;
        m_tapeCount = 0;
    }
    /// <summary>Formats the avatar's planar pose for the roster's <c>world.players</c> glance — position X/Z and heading,
    /// culture-invariant. The full 6DOF pose is <see cref="DescribeWhere"/>.</summary>
    /// <returns>A line of the form <c>pos=(x.xx, z.zz) yaw=ddd°</c>.</returns>
    public string DescribePose() {
        var position = m_position.ToVector3();

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"pos=({position.X:0.00}, {position.Z:0.00}) yaw={CompassDegrees(radians: EulerRadians().Yaw):0}°"
        );
    }
    /// <summary>Formats the standalone <c>body.where</c> echo — the bracket-tagged, index-prefixed line a piped run
    /// asserts against — as the full 6DOF pose plus the fact mask:
    /// <c>[body.where: body:{N} pos=(x.xx, y.yy, z.zz) yaw=ddd° pitch=ddd° roll=ddd° facts=grounded|holdingunwalkable
    /// home=(x.xx, y.yy, z.zz) scale=s.ss com=(x.xx, y.yy, z.zz)]</c>. One format always. A grounded entity keeps a
    /// canonical level orientation — <c>pitch=0 roll=0</c> — while <c>y</c> is its resolved ground foot point
    /// (<c>0.00</c> on the flat plane, following the contact field where solids lift it). <c>facts=</c> is
    /// <see cref="Facts"/> spelled lower-case and <c>|</c>-joined in bit order (<c>none</c> when empty), the same
    /// mask the snapshot publishes. <c>scale=</c> is <see cref="Scale"/> — 1.00 for every body under a world
    /// authoring no <c>bodies.scaleRow</c>. <c>com=</c> trails only for a rigid-kit body — <see cref="RigidCenterOfMass"/>,
    /// which orbits away from <c>pos=</c> (the root) for a rolling or tumbling rigid body while <c>pos=</c> itself
    /// stays the root every kit shares. <c>carrying=</c> trails only while <see cref="Carrying"/> is set, and
    /// <c>carriedBy=</c> only while <see cref="CarriedBy"/> is — both absent for every body outside a carry
    /// relationship. <c>tether=</c>/<c>anchor=</c> trail only while <see cref="TetherLength"/> is set — absent for
    /// every body carrying no tether facet, or one that authors the facet but is not currently attached. The bare
    /// planar fragment is <see cref="DescribePose"/>.</summary>
    /// <param name="index">The 0-based body index to tag the line with.</param>
    /// <returns>The full bracketed <c>body.where</c> echo line.</returns>
    public string DescribeWhere(int index) {
        var (yaw, pitch, roll) = EulerRadians();
        var home = m_home.ToVector3();
        var position = m_position.ToVector3();
        var scale = ((double)m_scale);
        var com = (IsRigid ? RigidCenterOfMass.ToVector3() : default);
        var comSuffix = (IsRigid
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $" com=({com.X:0.00}, {com.Y:0.00}, {com.Z:0.00})"
            )
            : string.Empty
        );
        var carrySuffix = string.Concat(
            (Carrying is { } carrying ? $" carrying={carrying}" : string.Empty),
            (CarriedBy is { } carriedBy ? $" carriedBy={carriedBy}" : string.Empty)
        );
        var tetherSuffix = ((TetherLength is { } ropeLength)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $" tether={((double)ropeLength):0.00} anchor=({((double)TetherAnchorPointOrLocalOffset.X):0.00}, {((double)TetherAnchorPointOrLocalOffset.Y):0.00}, {((double)TetherAnchorPointOrLocalOffset.Z):0.00})"
            )
            : string.Empty
        );

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[body.where: body:{index} pos=({position.X:0.00}, {position.Y:0.00}, {position.Z:0.00}) yaw={CompassDegrees(radians: yaw):0}° pitch={CompassDegrees(radians: pitch):0}° roll={CompassDegrees(radians: roll):0}° facts={BodyFactVocabulary.Describe(facts: Facts)} home=({home.X:0.00}, {home.Y:0.00}, {home.Z:0.00}) scale={scale:0.00}{comSuffix}{carrySuffix}{tetherSuffix}]"
        );
    }
    /// <summary>Enqueues a timed scripted segment onto the tape: while it is live it drives the avatar with
    /// <paramref name="intent"/>, overriding the held keys (or, on a population entry, its wander), for
    /// <paramref name="seconds"/> of advance time. All six channels are clamped to <c>[-1, 1]</c> — the planar three
    /// three leave the 6DOF three at their zero default, and <c>body.fly</c>'s full six carry all of them.
    /// A non-positive duration is ignored.</summary>
    /// <param name="intent">The intent the segment holds while live.</param>
    /// <param name="seconds">How long (advance seconds) the segment drives before it expires.</param>
    public void EnqueueRun(PlayerIntent intent, float seconds) {
        var duration = FixedQ4816.FromDouble(value: seconds);

        if (duration <= FixedQ4816.Zero) {
            return;
        }

        // Grow the ring (doubling) before writing when it is full, so a burst never drops a segment; steady-state this
        // branch never fires, so the enqueue allocates nothing.
        if (m_tapeCount == m_tape.Length) {
            GrowTape();
        }

        var tail = ((m_tapeHead + m_tapeCount) % m_tape.Length);

        m_tape[tail] = new TapeSegment {
            Intent = Clamped(intent: in intent),
            RemainingTicks = FixedTickConversion.DurationEngineTicks(seconds: duration),
        };
        m_tapeCount++;
    }
    /// <summary>Reads this tick's continuity hint without consuming it — the non-consuming counterpart to
    /// <see cref="TakeContinuity"/> for a primer snapshot built for a newly attached sink
    /// (<c>WorldServer.AttachSink</c>/<c>BuildPrimerSnapshot</c>): a late attach must never steal the one-shot flag an
    /// already-attached sink is still due to observe via <see cref="TakeContinuity"/> on the ordinary next-tick
    /// broadcast.</summary>
    public EntityContinuity PeekContinuity() => m_continuity;
    /// <summary>Teleports the avatar to a full 6DOF pose — a free position and a Tait-Bryan attitude (yaw about world up,
    /// pitch about the body right, roll about the body forward). A hard teleport pops: the previous-pose anchor is reset to the new pose so the renderer never
    /// interpolates across the jump, and any in-flight <see cref="Reconcile"/> smoothing offset is dropped. The pose is
    /// written as-is regardless of model; a grounded entity's next <see cref="Advance"/> re-pins Y and levels the
    /// attitude to its yaw, so a full pose only persists under the free program.</summary>
    /// <param name="x">The world X coordinate.</param>
    /// <param name="y">The world Y coordinate.</param>
    /// <param name="z">The world Z coordinate.</param>
    /// <param name="yawRadians">The yaw about world up, radians.</param>
    /// <param name="pitchRadians">The pitch about the body right, radians.</param>
    /// <param name="rollRadians">The roll about the body forward, radians.</param>
    public void Pose(float x, float y, float z, float yawRadians, float pitchRadians, float rollRadians) {
        Pose(
            position: new FixedVector3(
                X: FixedQ4816.FromDouble(value: x),
                Y: FixedQ4816.FromDouble(value: y),
                Z: FixedQ4816.FromDouble(value: z)
            ),
            yawRadians: FixedQ4816.FromDouble(value: yawRadians),
            pitchRadians: FixedQ4816.FromDouble(value: pitchRadians),
            rollRadians: FixedQ4816.FromDouble(value: rollRadians)
        );
    }
    /// <summary>Teleports to a full pose already expressed in deterministic numerics.</summary>
    public void Pose(FixedVector3 position, FixedQ4816 yawRadians, FixedQ4816 pitchRadians, FixedQ4816 rollRadians) {
        m_position = position;
        m_yaw = yawRadians;
        // The drive pitch scalar mirrors the posed pitch inside its own clamp, so the next drive frame rebuilds
        // an equivalent (never inverted) attitude from its scalars.
        m_drivePitch = FixedQ4816.Clamp(
            maximum: MaxDrivePitch,
            minimum: -MaxDrivePitch,
            value: pitchRadians
        );
        m_orientation = OrientationFromEuler(
            pitch: pitchRadians,
            roll: rollRadians,
            yaw: m_yaw
        );
        // A teleport invalidates the CONTACT state, not just the pose. The body did not walk anywhere: it is
        // somewhere else, standing on nothing, and whatever surface it was grounded on a moment ago says nothing
        // about where it is now. Carrying that state forward keeps the held up axis too, and a body integrating the
        // new location's gravity along the OLD location's up falls sideways out of the world — a seat posed under a
        // planetoid is pulled toward it, along an axis still pointing at the floor it left.
        //
        // The axis is re-seated rather than steered: the rate limit that keeps a walked reorientation continuous is
        // exactly wrong here, because nothing continuous happened.
        m_grounded = false;
        m_lastContactCount = 0;
        m_obstructionWitness = FixedVector3.Zero;
        m_obstructionWitnessGraceTicks = 0;
        m_upNeedsReseat = true;

        CommitTeleport();
        m_continuity = EntityContinuity.Teleport;
    }
    /// <summary>Presses a channel for the default two-host-step tap, reaching any ordinal (movement roles included).
    /// The concrete engine-tick duration is derived by the next <see cref="Advance"/> from its <c>stepTicks</c> (see
    /// <see cref="MaterializeDefaultLanePresses"/>), which merges it under the same rule <see cref="PressChannel(int, FixedQ4816, float, FixedQ4816)"/>
    /// uses: a same-value re-press only extends an in-flight hold, a different value replaces it outright.</summary>
    /// <param name="ordinal">The channel ordinal to hold.</param>
    /// <param name="value">The raw fixed-point value to hold the channel at.</param>
    public void PressChannel(int ordinal, FixedQ4816 value) {
        if (
            (ordinal < 0) ||
            (ordinal >= ActionLaneCount)
        ) {
            return;
        }

        m_pendingDefaultChannelPress[ordinal] = true;
        m_pendingDefaultChannelValue[ordinal] = value;
    }
    /// <summary>Presses a channel for a timed auto-release — the scripted/wire path (<c>body.press</c>), reaching
    /// any ordinal: the channel reads held at <paramref name="value"/> for <paramref name="holdSeconds"/> of sim time
    /// (clamped to the row's authored ceiling and the <see cref="MaxActionHoldSeconds"/> engine backstop), decremented
    /// per sub-step, then releases
    /// itself. A short hold is a short hop (the release cuts the rising jump) on a composition channel bound to a
    /// vertical effect; on a movement-role channel it is a timed analog override. A re-press carrying the same value
    /// as the ordinal's in-flight hold never shortens it (the longer of the two durations wins — repeatedly resubmitting
    /// one held key must not truncate itself); a re-press carrying a different value is a distinct action and replaces
    /// the hold outright — its own duration, not merged with whatever remained — so a short full-brake press on a
    /// channel mid a long throttle hold takes effect immediately instead of being swallowed by the throttle's
    /// remaining ticks (see <see cref="MergeLaneTimer"/>, shared with <see cref="MaterializeDefaultLanePresses"/> so
    /// the timed and untimed press paths can never drift onto two different rules). Independent of the movement
    /// tape, so <c>body.fly … ; body.press jump</c> jumps a runner mid-segment. A non-positive (or NaN) hold is
    /// ignored outright — it never touches the lane timer at all, so it cannot cancel a genuine in-flight hold on the
    /// same ordinal under the different-value rule above. Unlike the device-held channel image (see
    /// <see cref="SetHeldChannels"/>), this wire path overlays under every <see cref="IntentSource"/>. N simultaneous
    /// presses in one tick still collapse to this one slot per ordinal (summing distinct presses as pooled fold
    /// contributions, rather than one replacing another, remains owed — see
    /// <c>WorldServer.FoldChannelContributions</c>'s remarks); this fix only stops a same-tick opposing press from
    /// being silently discarded.</summary>
    /// <param name="ordinal">The channel ordinal to hold.</param>
    /// <param name="value">The raw fixed-point value to hold the channel at.</param>
    /// <param name="holdSeconds">How long (sim seconds) the channel reads held before auto-releasing.</param>
    /// <param name="authoredMaximum">The deciding Drive grant row's compiled timed-press ceiling.</param>
    /// <returns>The effective hold (in sim seconds) and which cap, if any, decided it — <c>body.press</c>'s
    /// synchronous read-back, so its echo can name a silent truncation instead of assuming the request was
    /// honored.</returns>
    public PressOutcome PressChannel(int ordinal, FixedQ4816 value, float holdSeconds, FixedQ4816 authoredMaximum) {
        if (
            (ordinal < 0) ||
            (ordinal >= ActionLaneCount)
        ) {
            return new PressOutcome(
                EffectiveHoldSeconds: FixedQ4816.Zero,
                CapKind: PressHoldCapKind.None
            );
        }

        if (
            float.IsNaN(f: holdSeconds) ||
            (holdSeconds <= 0f)
        ) {
            return new PressOutcome(
                EffectiveHoldSeconds: FixedQ4816.Zero,
                CapKind: PressHoldCapKind.Ignored
            );
        }

        // The engine-backstop-safe conversion: values at or above the backstop map straight to its constant rather
        // than converting a possibly-huge float through FixedQ4816.FromDouble. "exceedsBackstop" is the STRICT form
        // (> not >=) because it answers a different question — whether the raw request actually needed truncating,
        // not merely "which branch is safe to convert" — a request of EXACTLY the backstop must not be reported as
        // capped by it.
        var exceedsBackstop = (holdSeconds > MaxActionHoldSeconds);
        var engineClamped = ((holdSeconds >= MaxActionHoldSeconds)
            ? MaxActionHoldSecondsFixed
            : FixedQ4816.FromDouble(value: holdSeconds)
        );
        var grantMaximum = FixedQ4816.Clamp(
            value: authoredMaximum,
            minimum: FixedQ4816.Zero,
            maximum: MaxActionHoldSecondsFixed
        );
        var hold = FixedQ4816.Min(
            x: engineClamped,
            y: grantMaximum
        );
        // The binder is decided STRUCTURALLY, from the two clamp inputs that produced "hold" — never by comparing
        // the final magnitude against a hardcoded 60, which cannot tell "the grant happens to equal the backstop"
        // apart from "the backstop is what is actually constraining this request". A grant ceiling authored below
        // the backstop is doing REAL narrowing and is credited as the binder even where it ties the backstop's own
        // value; only a grant that permits the full backstop (no narrowing of its own) lets the backstop take the
        // blame for a request that still exceeds it.
        var capKind = ((!exceedsBackstop && (hold >= engineClamped))
            ? PressHoldCapKind.None
            : ((grantMaximum < MaxActionHoldSecondsFixed)
                ? PressHoldCapKind.GrantBudget
                : PressHoldCapKind.EngineCeiling
        ));
        var holdTicks = FixedTickConversion.DurationEngineTicks(seconds: hold);

        MergeLaneTimer(
            holdTicks: holdTicks,
            ordinal: ordinal,
            value: value
        );

        return new PressOutcome(
            CapKind: capKind,
            EffectiveHoldSeconds: hold
        );
    }
    /// <summary>Swaps this body's compiled kit feel in place after a live kit retune — the once-at-the-boundary
    /// recompile of a mutated <see cref="WorldKit"/>: the fixed-point locomotion tuning, the channel bindings, and
    /// the body motion program. The body keeps its pose, velocity, tape, source, and engagement; only the compiled feel
    /// changes. The action runtime resets because it is bound to the old binding and named-state shapes, and an
    /// incompatible program switch re-pins the pose exactly as
    /// <c>body.motion</c> does (a no-op when unchanged).</summary>
    /// <param name="tuning">The kit's compiled locomotion tuning (<see cref="FixedWorldKit.Tuning"/>).</param>
    /// <param name="actions">The kit's compiled per-ordinal action bindings.</param>
    /// <param name="actionThresholds">The kit's per-ordinal binary crossing thresholds, parallel to <paramref name="actions"/>.</param>
    /// <param name="actionShapes">The world's per-ordinal declared channel shapes (every ordinal, not just bound ones).</param>
    /// <param name="roleMask">The world's compiled per-ordinal role predicate.</param>
    /// <param name="roleOrdinals">The kit's resolved engine motion role ordinals.</param>
    /// <param name="actionState">The kit's compiled named action-state register file.</param>
    /// <param name="program">The kit's compiled body motion program.</param>
    /// <param name="programs">The world's compiled body motion program table.</param>
    /// <param name="collider">The kit's compiled body volume, or <see langword="null"/> for a volumeless kit.</param>
    /// <param name="maxSmoothError">The compiled world-distance correction smoothing threshold.</param>
    /// <param name="holds">The kit's compiled ordered hold list (<see cref="FixedWorldKit.Holds"/>), or
    /// <see langword="null"/> for a kit authoring none.</param>
    /// <param name="rigid">The kit's compiled rigid-dynamics facet (<see cref="FixedWorldKit.Rigid"/>), or
    /// <see langword="null"/> for a locomotion kit.</param>
    /// <param name="carry">The kit's compiled carry facet (<see cref="FixedWorldKit.Carry"/>), or
    /// <see langword="null"/> for a kit that can never pick up a rigid body.</param>
    /// <param name="tether">The kit's compiled tether facet (<see cref="FixedWorldKit.Tether"/>), or
    /// <see langword="null"/> for a kit that carries no rope.</param>
    public void RecompileKit(FixedMotionTuning tuning, CompiledActionSpec?[]? actions, FixedQ4816[]? actionThresholds, ChannelShape[]? actionShapes, bool[]? roleMask, RoleChannelOrdinals roleOrdinals, CompiledActionStateSlot[]? actionState, CompiledBodyMotionProgram program, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, FixedWorldCollider? collider, FixedQ4816 maxSmoothError, FixedBodyHold[]? holds = null, FixedWorldRigid? rigid = null, FixedWorldCarry? carry = null, FixedWorldTether? tether = null) {
        SetTuning(
            holds: holds,
            tuning: tuning
        );
        CopyChannelBindings(
            actionShapes: actionShapes,
            actionThresholds: actionThresholds,
            actions: actions,
            roleMask: roleMask
        );
        m_roleOrdinals = roleOrdinals;
        CompileActionState(state: actionState);
        m_collider = collider;

        var hadRigidFacet = (m_rigid is not null);

        m_rigid = rigid;
        m_maxSmoothError = maxSmoothError;

        if (hadRigidFacet != (rigid is not null)) {
            // The rigid facet's own presence just changed (a document mutation swapped this slot between a rigid
            // kit and a locomotion one) — reset every rigid-solver-owned field rather than let the OTHER kind of
            // body's stale state leak forward: slot reuse must never inherit a previous occupant's simulation state.
            // A live retune that keeps the facet (mass/friction/etc change while staying rigid) does NOT hit this —
            // its velocity survives, on the same terms m_planarVelocity survives a locomotion retune.
            m_rigidVelocity = FixedVector3.Zero;
            m_angularVelocity = FixedVector3.Zero;
            m_resting = false;
            m_restingHoldTicks = 0UL;
            m_rigidGroundContacting = false;
            m_rigidObstructionContacting = false;
            m_rigidGroundMissStreak = 0;
            m_rigidObstructionMissStreak = 0;
        }

        // A facet loss (rigid above, carry here) leaves both carry relationship indices in place: only
        // WorldPopulation holds both sides, and its active-relationship pass clears the pair together on its next
        // visit.
        m_carry = carry;

        var previousTetherFacet = m_tetherFacet;
        var hadTetherFacet = (previousTetherFacet is not null);

        m_tetherFacet = tether;

        if (hadTetherFacet != (tether is not null)) {
            // The tether facet's own presence just changed (a document mutation swapped this slot's kit) — drop any
            // live attach rather than let it dangle against ordinals a new (or absent) facet no longer resolves.
            ClearTether();
            m_attachPreviousBit = false;
            m_detachPreviousBit = false;

            // The OLD facet's modeState row (bodyState/identityState declarations are world-global, so its ordinal
            // resolves identically under any kit) may still read 1 from before the swap — CompileActionState just
            // preserved every Durable slot's value across this recompile by name. Zero it directly: WriteTetherModeState
            // reads the NEW facet (already null on a loss, or a different ordinal on a gain), so it cannot be the one
            // to clear the old bit.
            var previousModeStateOrdinal = (previousTetherFacet?.ModeStateOrdinal ?? -1);

            if (previousModeStateOrdinal >= 0) {
                ApplyRawState(
                    reason: "tether.mode",
                    requested: FixedQ4816.Zero.Value,
                    slot: previousModeStateOrdinal,
                    writer: "tether"
                );
                MarkDurableDirty(slot: previousModeStateOrdinal);
            }
        }

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            m_laneActions[lane] = default;

            if (m_laneBindings[lane] is { RecencyFacts.Length: > 0 } binding) {
                m_laneActions[lane].Recency = new ulong[binding.RecencyFacts.Length];
            }
        }

        // The response recency clocks are bound to the OLD table shape (a new table may have a different Recently count),
        // so they reset on a recompile — but m_planarVelocity SURVIVES, because a live retune must not jerk the crowd.
        m_motionRecency = ((m_tuning.RecencySlots > 0)
            ? new ulong[m_tuning.RecencySlots]
            : []
        );

        m_bodyMotionPrograms = programs;
        SetBodyMotionProgram(program: program);
    }
    /// <summary>Applies a server correction: the sim pose snaps to the target instantly (an end-state identical to
    /// <see cref="Pose(float, float, float, float, float, float)"/>), and the tick's snapshot carries
    /// <see cref="EntityContinuityKind.Correction"/> so the client eases its render error to zero over
    /// <paramref name="seconds"/>. Snap escape: if the position error exceeds
    /// the world's <see cref="WorldMotionDefaults.MaxSmoothError"/> the snapshot reports a plain teleport instead, so a huge
    /// correction pops. Easing is client presentation state only — the sim never reads it and <c>body.where</c>
    /// never includes it.</summary>
    /// <param name="x">The authoritative world X coordinate.</param>
    /// <param name="z">The authoritative world Z coordinate.</param>
    /// <param name="yawRadians">The authoritative heading in radians (0 = facing -Z).</param>
    /// <param name="seconds">The smoothing window over which the client's render error eases to zero.</param>
    /// <returns>The continuity kind published for the correction.</returns>
    public EntityContinuityKind Reconcile(float x, float z, float yawRadians, float seconds) {
        var oldPosition = m_position;

        // Snap the sim pose immediately — the same end-state a Warp+Face would land (a planar, levelled correction).
        // The tape and any timed press are untouched: a correction is authority over the pose, not over what the
        // entity is trying to do.
        var fixedYaw = FixedQ4816.FromDouble(value: yawRadians);

        m_position = new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: oldPosition.Y,
            Z: FixedQ4816.FromDouble(value: z)
        );
        m_yaw = fixedYaw;
        m_drivePitch = FixedQ4816.Zero;
        m_orientation = FixedQuaternion.FromAxisAngle(
            angle: fixedYaw,
            axis: UnitY
        );
        CommitTeleport();

        // Over the ceiling the correction pops (a respawn/teleporter-scale jump would streak if eased); within it the
        // client eases from its previous rendered pose to authority over the window.
        var positionError = (oldPosition - m_position);

        m_continuity = ((positionError.Length > m_maxSmoothError)
            ? EntityContinuity.Teleport
            : EntityContinuity.Correction(seconds: seconds)
        );

        return m_continuity.Kind;
    }
    /// <summary>Overrides just this body's own linear velocity — the mapped-arrival half of a portal transfer (see
    /// <c>Puck.World.WorldPlacementPortal.Arrival</c>): the source's captured velocity, rotated into the
    /// destination's own frame by <c>Puck.World.Server.WorldPortalArrivalMath</c>, written after
    /// <see cref="Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/>'s own hard-teleport commit — the same
    /// "AFTER Pose, never before" ordering <see cref="ApplyTransferState"/> follows, so the discontinuity has already
    /// reset <see cref="FixedPreviousPosition"/> before this runs. Deliberately narrower than
    /// <see cref="ApplyTransferState"/>: the destination's own normal join already embodied the traveler fresh under
    /// its own kit (appearance, grants, action-track state, body motion program) — mapped arrival carries across
    /// only positional continuity, pose and velocity, never the source kit's dash overlay, timers, or tape.</summary>
    /// <param name="planarVelocity">The rotated planar velocity.</param>
    /// <param name="verticalVelocity">The (rotation-invariant) vertical velocity.</param>
    public void SetArrivalVelocity(FixedVector3 planarVelocity, FixedQ4816 verticalVelocity) {
        m_planarVelocity = planarVelocity;
        m_verticalVelocity = verticalVelocity;
    }
    /// <summary>Sets the entity's named body motion program as an authoritative pose switch.</summary>
    /// <param name="programName">The declared program name.</param>
    /// <returns><see langword="true"/> when the program exists.</returns>
    public bool SetBodyMotionProgram(string programName) {
        if (!m_bodyMotionPrograms.TryGetValue(
            key: programName,
            value: out var program
        )) {
            return false;
        }

        SetBodyMotionProgram(program: program);
        return true;
    }
    /// <summary>Sets the body's home — the position it was activated at. Written once per activation, beside the
    /// pose that put the body there; nothing on the step path moves it.</summary>
    /// <param name="home">The activation position.</param>
    public void SetHome(FixedVector3 home) {
        m_home = home;
    }
    /// <summary>Sets (or clears) the world contact field this body's grounded integrator solves its swept position
    /// against — the population hands it the live field on activation and every rebuild.</summary>
    /// <param name="field">The world contact field.</param>
    public void SetContactField(IContactField? field) {
        m_contactField = field;
    }
    /// <summary>Sets the contact field together with the body-frame policy compiled from the same live definition.
    /// This is the population's activation/rebuild seam; keeping the policy out of <see cref="IContactField"/> leaves
    /// that public physics abstraction provider-neutral.</summary>
    /// <param name="field">The world contact field.</param>
    /// <param name="upPolicy">The body-frame policy compiled from the live world's contact requirements.</param>
    /// <param name="walkableThreshold">The compiled <c>cos(collision.maxSlopeDegrees)</c> a surface normal's
    /// alignment with the body's up axis must clear to read as ground.</param>
    internal void SetContactConfiguration(IContactField? field, WorldBodyUpPolicy upPolicy, FixedQ4816 walkableThreshold) {
        SetContactField(field: field);
        SetWalkableThreshold(threshold: walkableThreshold);

        if (m_upPolicy != upPolicy) {
            // A policy transition invalidates the authority that produced the held axis. Snap to the new ambient
            // authority on the next resolve, and discard fractional turn budgets accumulated under the old one.
            m_upNeedsReseat = true;
            m_upTurnAccumulator.Reset();
            m_contactUpTurnAccumulator.Reset();
        }

        m_upPolicy = upPolicy;
    }
    /// <summary>Sets (or clears) the world gravity field this body reads its solved acceleration and ambient up axis
    /// from — the population hands it the live field on activation and every rebuild.</summary>
    /// <param name="field">The world gravity field, or <see langword="null"/> for a world authoring none.</param>
    public void SetGravityField(WorldGravityField? field) {
        m_gravityField = field;
    }
    /// <summary>Sets the capture latch — a PROJECTION of the owning principal's control-application set, written by
    /// <c>Server.WorldEngagement.SyncLatch</c> alone and never independently: engaged means that set omits this
    /// body's own-body application. A transition in either direction drops the staged transient input images and
    /// clears the last routed intent, so a stale image cannot leak as a stuck direction into the target (engaging) or
    /// burst the avatar into motion (disengaging); the client seat drops its own held device state in the same
    /// operation. The tape and any wire-timed lane press are untouched — a scripted tape keeps driving whichever
    /// target now owns the intent. A no-op if the latch is unchanged.</summary>
    /// <param name="engaged">Whether this body's own intent is diverted away from its avatar.</param>
    public void SetEngaged(bool engaged) {
        if (engaged == m_engaged) {
            return;
        }

        m_engaged = engaged;
        m_engagedIntent = default;
        ClearTransientInput();
    }
    /// <summary>Stages this tick's live-held device channel image — the action overlay a held jump button rides onto a
    /// tape-driven runner. Only composition ordinals are meaningful; movement-role ordinals ride the submitted intent
    /// directly. One-tick, consumed by the next <see cref="Advance"/>; the client republishes it each
    /// submission, and the merge admits it only under <see cref="IntentSource.Live"/>.</summary>
    /// <param name="channels">The channel image live-held this tick (composition ordinals only).</param>
    public void SetHeldChannels(in PlayerIntent channels) {
        m_transferHeldChannels = default;
        m_hasTransferHeldChannels = false;
        m_heldChannels = channels;
    }
    /// <summary>Sets the intent-source axis — <c>body.control</c>'s write and the peer sweep's per-entity half. A
    /// transition drops the staged transient input images (the submitted, producer, and held-lane images), so a stale
    /// image cannot leak across the switch and nothing bursts when a source returns; a seat's client half drops its own
    /// held device state in the same command. The tape and any wire-timed lane press are untouched. A no-op if the
    /// source is unchanged.</summary>
    /// <param name="source">The intent source to latch.</param>
    public void SetIntentSource(IntentSource source) {
        if (source == m_source) {
            return;
        }

        m_source = source;
        ClearTransientInput();
    }
    /// <summary>Sets (or clears) the medium free surface this body's medium hold integrates against — sampled fresh
    /// every tick from the population's field lattice at this body's coupled cell, before this body's own Advance
    /// runs. Meaningful only to a kit authoring a medium hold; every other body carries it inertly.</summary>
    /// <param name="surface">The medium surface, or <see langword="null"/> for no medium at this body's
    /// position.</param>
    public void SetMediumSurface(FixedFieldSurface? surface) {
        m_mediumSurface = surface;
    }
    /// <summary>Stages one deterministic producer intent for the next <see cref="Advance"/> — the producer tier below
    /// the submitted stream, used only while <see cref="Source"/> names its producer
    /// by the source's authored producer program. One-tick, consumed like the submitted
    /// image; same clamps.</summary>
    /// <param name="intent">The producer's fixed-point movement and action image.</param>
    public void StageProducerIntent(in PlayerIntent intent) {
        m_producerIntent = Clamped(intent: in intent);
        m_hasProducerIntent = true;
    }
    /// <summary>Clears every intent producer this body owns: drops the whole tape, the staged transient input images,
    /// every in-flight timed press (<c>body.press</c> hold), and any not-yet-materialized argument-less tap staged
    /// by <see cref="PressChannel(int, FixedQ4816)"/> (see <see cref="MaterializeDefaultLanePresses"/>) — on role and
    /// composition ordinals alike, in every one of these three forms. Not an instantaneous halt — an in-flight jump
    /// arc still resolves under gravity and lands, and the ramped planar velocity decays to rest through the
    /// shaping row rather than snapping to zero. This is the <c>body.stop</c> panic verb's server half; the
    /// client seat drops its held device state in the same command. Unlike <see cref="SetIntentSource"/>/
    /// <see cref="SetEngaged"/>'s shared <see cref="ClearTransientInput"/> call, which deliberately leaves a timed
    /// press running across a source/engagement transition (that hold still belongs to whichever target now owns
    /// the intent — see its own remarks), Stop is the panic verb: a 60-second throttle hold left ticking after it
    /// would make "keys released" a lie.</summary>
    /// <returns>How many held channels were released and how many timed presses — materialized or still pending —
    /// were cancelled. The synchronous read-back <c>body.stop</c>'s handler quotes in its echo.</returns>
    public StopOutcome Stop() {
        ClearTape();

        var releasedHeldChannels = 0;

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_heldChannels[ordinal] != FixedQ4816.Zero) {
                releasedHeldChannels++;
            }
        }

        // An in-flight jump arc still resolves under gravity and lands — clearing the images only stops new input.
        ClearTransientInput();

        var clearedTimedPresses = 0;

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_laneTimers[ordinal] > 0) {
                clearedTimedPresses++;
                m_laneTimers[ordinal] = 0;
                m_channelTimerValues[ordinal] = default;
            }

            // A body.press with no holdSeconds hasn't materialized into a lane timer yet (MaterializeDefaultLanePresses
            // only runs at the next Advance) — panic-verb totality means this pending tap is cleared too, and counted
            // the same as an already-materialized one.
            if (m_pendingDefaultChannelPress[ordinal]) {
                clearedTimedPresses++;
                m_pendingDefaultChannelPress[ordinal] = false;
                m_pendingDefaultChannelValue[ordinal] = default;
            }
        }

        return new StopOutcome(
            ClearedTimedPresses: clearedTimedPresses,
            ReleasedHeldChannels: releasedHeldChannels
        );
    }
    /// <summary>Stages one deterministic submitted intent for the next <see cref="Advance"/> — the live-stream tier: a
    /// live tape still wins, <see cref="IntentSource.Idle"/> masks it, and the value is consumed once (a driver
    /// republishes each tick, so a missed producer tick can never leave a stale entity moving forever). All axes are
    /// clamped to a physical stick's <c>[-1, 1]</c> range.</summary>
    /// <param name="intent">The fixed-point movement and action image to consume when no tape owns this step.</param>
    public void SubmitIntent(in PlayerIntent intent) {
        m_submittedIntent = Clamped(intent: in intent);
        m_hasSubmittedIntent = true;
    }
    /// <summary>Consumes this tick's continuity hint for the snapshot: how the pose changed (ordinary integration, a
    /// hard teleport, or a smoothed correction). Resets to <see cref="EntityContinuityKind.Continuous"/>; within a tick
    /// the last authoritative write wins.</summary>
    public EntityContinuity TakeContinuity() {
        var continuity = m_continuity;

        m_continuity = EntityContinuity.Continuous;

        return continuity;
    }
    /// <summary>Attempts to admit an ordinary authority step after both geometric continuation and continuum-time
    /// ownership have settled. A refused step performs no input, action, timer, gravity, or movement work.</summary>
    public bool TryBeginOrdinaryAdvance(ulong stepStartEngineTick) {
        if (
            (m_pendingContinuum is not null) ||
            ((m_continuumConsumedThroughEngineTick is { } consumedThrough) && (stepStartEngineTick < consumedThrough))
        ) {
            m_ordinaryAdvanceAdmitted = false;
            return false;
        }

        m_continuumConsumedThroughEngineTick = null;
        m_ordinaryAdvanceAdmitted = true;
        return true;
    }
    /// <summary>Marks this body as deliberately deferred by its authored autonomous cadence. Late population passes
    /// must not mistake a prior tick's admission latch for an advance on this tick.</summary>
    internal void DeferOrdinaryAdvance() => m_ordinaryAdvanceAdmitted = false;

    /// <summary>Gets whether externally staged work must be consumed on this authority tick rather than waiting for
    /// an autonomous motion cadence. A live submitted image, transferred held image, or command-side channel press
    /// (pending or timed and already in flight) is latency-sensitive even when the body normally runs batched
    /// producer motion.</summary>
    internal bool RequiresFullRateAutonomy {
        get {
            if (m_hasSubmittedIntent || m_hasTransferHeldChannels) {
                return true;
            }

            for (var ordinal = 0; ordinal < m_pendingDefaultChannelPress.Length; ordinal++) {
                if (m_pendingDefaultChannelPress[ordinal] || (m_laneTimers[ordinal] > 0UL)) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Gets the body that applied the latest targeted effect, held for one recipient advance.</summary>
    internal int AffectingSubject => m_affectingSubject;

    /// <summary>Gets a value indicating whether the body's origin is inside the medium's equilibrium band as of
    /// the medium hold's last evaluation — the <c>world.contacts</c> read-back's medium witness. Always
    /// <see langword="false"/> for a kit authoring no medium hold.</summary>
    public bool AtMediumBand => m_atMediumBand;
    /// <summary>Gets the body motion program this player currently executes.</summary>
    public string BodyMotionProgram => m_bodyMotionProgram.Name;
    /// <summary>Gets the last intent after the admitted held overlay composed with the movement tier, retained only for
    /// <c>body.channels</c>.</summary>
    public PlayerIntent ChannelReadComposed => m_channelReadComposed;
    /// <summary>Gets the held-channel overlay admitted by the last <see cref="Advance"/>, retained only for
    /// <c>body.channels</c>.</summary>
    public PlayerIntent ChannelReadHeld => m_channelReadHeld;
    /// <summary>Gets the kit-authored body volumes, or <see langword="null"/> for a volumeless kit.</summary>
    public FixedWorldCollider? Collider => m_collider;
    /// <summary>Gets the last <see cref="Advance"/>'s grounded witness echoed as a count — <c>1</c> when the resolve grounded
    /// this body, <c>0</c> otherwise. This is not a per-surface tally: a body pushed by a wall while airborne (or while
    /// standing on the ground elsewhere) still reads <c>0</c> here — <em>that</em> obstruction is what
    /// <see cref="LastObstructionNormal"/> now surfaces instead. Introspection-only, surfaced by the
    /// <c>world.contacts</c> read-back.</summary>
    public int ContactCount => m_lastContactCount;
    /// <summary>Gets the base move speed the sim integrates under right now: <see cref="Profile"/>'s requested rate
    /// (or the tuning's profileless fallback) after the kit's
    /// <see cref="WorldSpeed.Envelope"/> clamp. A held speed multiplier — a shaping row's boost
    /// included — scales this after the clamp, so the envelope pins the base rate and not the boosted one; a kit
    /// that means to pin its speed against any profile authors <c>min == max</c>. This is the same resolve
    /// <see cref="Advance"/> performs every tick. A read-only echo: querying this never mutates state, and an
    /// unenveloped kit returns the requested/kit rate unchanged.</summary>
    public FixedQ4816 EffectiveMoveSpeed => ResolveMoveSpeed();
    /// <summary>Gets a value indicating whether this body's own-body control application has been dropped. While it
    /// has, the resolved per-frame intent reaches only the set's other targets (read via <see cref="EngagedIntent"/>)
    /// and the avatar stands idle. <see langword="false"/> while the own-body application is held — whether alone or
    /// beside a mirrored target — and the avatar keeps integrating normally.</summary>
    public bool Engaged => m_engaged;
    /// <summary>Gets the intent resolved on the most recent <see cref="Advance"/> — captured every tick regardless of
    /// the latch, so an applied body's channels are available for translation/passthrough whether or not the
    /// avatar itself is idled. The <see cref="PlayerIntent"/> default (all channels zero) before the first advance.</summary>
    public PlayerIntent EngagedIntent => m_engagedIntent;
    /// <summary>Gets the authoritative deterministic orientation.</summary>
    public FixedQuaternion FixedOrientation => m_orientation;
    /// <summary>Gets the authoritative deterministic position.</summary>
    public FixedVector3 FixedPosition => m_position;
    /// <summary>Gets the body's home — the position it was activated at (a seat's spawn point, an inhabitant's
    /// placement plus its own distribution sample). Producers steer relative to this, so a population spread over
    /// several placements keeps to its own ground instead of converging on the world origin. A teleport does not
    /// move it: <see cref="Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/> puts a body somewhere,
    /// <see cref="SetHome"/> says where it belongs.</summary>
    public FixedVector3 FixedHome => m_home;
    /// <summary>Gets the body's up axis — the direction its gravity opposes, its planar move plane is perpendicular
    /// to, and its contact walkable test measures a surface normal against.</summary>
    public FixedVector3 FixedUp => m_up;
    /// <summary>Gets the avatar's position at the top of the most recent <see cref="Advance"/> — the start point of
    /// the swept segment a portal-crossing scan tests against a slab. A hard teleport (<c>Pose</c>,
    /// <see cref="Reconcile"/>) resets this to the landing position, so the segment collapses to a point exactly
    /// where a teleport-into-the-volume must still be detected as a point test.</summary>
    public FixedVector3 FixedPreviousPosition => m_previousPosition;
    /// <summary>Gets the authoritative deterministic heading.</summary>
    public FixedQ4816 FixedYaw => (m_bodyMotionProgram.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)
        ? ExtractYaw(orientation: m_orientation)
        : m_yaw
    );
    /// <summary>Gets a value indicating whether the body is grounded this tick (resting on a walkable contact surface) — the
    /// <c>world.contacts</c> read-back.</summary>
    public bool Grounded => m_grounded;
    /// <summary>Gets this body's publishable fact mask this tick — evaluated through the SAME predicate the kit's
    /// action gates read (<c>FactHolds</c>), so the snapshot, the gates, and the <c>body.where</c> echo can never
    /// disagree. Facts are not mutually exclusive: a body can be grounded and rising in one tick, and a body holding
    /// an unwalkable surface keeps whichever grounded/airborne answer its last contact resolve produced.</summary>
    public BodyFacts Facts {
        get {
            var facts = BodyFacts.None;

            foreach (var fact in BodyFactVocabulary.Publishable) {
                if (FactHolds(fact: fact)) {
                    facts |= BodyFactVocabulary.Bit(fact: fact);
                }
            }

            return facts;
        }
    }
    /// <summary>Gets the latched resolved non-walkable contact normal — <see cref="FixedVector3.Zero"/> when nothing
    /// obstructs the body, a unit surface normal otherwise. A walkable push (the ground, a ramp) never sets this —
    /// only a contact whose alignment fails the grounded test does, which is exactly the witness
    /// <see cref="ContactCount"/> cannot show: a body pushed by a vertical wall reads <see cref="ContactCount"/>
    /// <c>0</c> but a non-zero normal here, even while simultaneously standing (grounded) on the floor. Latched
    /// (see <see cref="UpdateObstructionWitness"/>) rather than a raw per-tick read: it survives a solver tick that
    /// happens not to re-register the push while the body stays driven and hasn't moved, and clears the instant
    /// input goes idle or the body actually gets clear. Read-back only — the raw normal is a solver fact from
    /// <see cref="ContactResolution"/>, surfaced without changing simulation behavior, via the
    /// <c>world.contacts</c> read-back.</summary>
    public FixedVector3 LastObstructionNormal => m_obstructionWitness;
    /// <summary>Gets whether this body entered the current authority step and may participate in its dynamic-contact
    /// solve. A continuum-fenced body is immutable until a non-overlapping ordinary step admits it.</summary>
    public bool OrdinaryAdvanceAdmitted => m_ordinaryAdvanceAdmitted;
    /// <summary>Gets the avatar's full 6DOF attitude — the canonical orientation a camera rig or a dynamic transform rides.
    /// Pure yaw about world up under the grounded program; an arbitrary body attitude under the free program.</summary>
    public Quaternion Orientation => m_orientation.ToQuaternion();
    /// <summary>The already-evaluated source-step trajectory awaiting ownership resolution before this body may
    /// advance normally on its destination authority.</summary>
    public WorldContinuumTrajectory? PendingContinuum => m_pendingContinuum;
    /// <summary>Gets the body's response-shaped planar speed (world units/second) — the coast/momentum witness the
    /// <c>world.contacts</c> read reports.</summary>
    public float PlanarSpeed => ((float)((double)m_planarVelocity.Length));
    /// <summary>Gets the avatar's current world-space position (the ground foot point under the grounded program, where Y is
    /// pinned to the plane; a free craft's position is unconstrained in all three axes).</summary>
    public Vector3 Position => m_position.ToVector3();
    /// <summary>Gets the profile this player is seated on — the live source of its move/turn speeds and look-invert (read
    /// every <see cref="Advance"/>, so an <c>identity.motion</c> edit is real-time) and the color the avatar renders. May be
    /// <see langword="null"/> before a profile is assigned, in which case the tuning's default rates apply.</summary>
    public WorldIdentity? Profile { get; set; }
    /// <summary>Gets what fills this entity's intent gaps between tape segments — the per-entity axis (the
    /// <c>body.control</c> verb's read/write). <see cref="IntentSource.Live"/> by default; see
    /// <see cref="IntentSource"/> for the merge rule.</summary>
    public IntentSource Source => m_source;
    /// <summary>Gets whether a scripted tape currently owns or awaits motion. Tapes retain full authority cadence even
    /// on an autonomously throttled kit because one batched advance consumes only one segment.</summary>
    internal bool HasMotionTape => (m_tapeCount > 0);
    /// <summary>Gets the most recently staged producer image for population-owned cadence reuse.</summary>
    internal PlayerIntent StagedProducerIntent => m_producerIntent;
    /// <summary>Gets a value indicating whether the body's origin sits below the medium's free surface, along
    /// its own resolved gravity-up, as of the medium hold's last evaluation — the <c>world.contacts</c> read-back's
    /// medium witness. Always <see langword="false"/> for a kit authoring no medium hold.</summary>
    public bool InMedium => m_inMedium;
    /// <summary>Gets the avatar's current heading in radians (0 = facing -Z; increases turning left / counter-clockwise).
    /// Under the grounded program this returns the authoritative heading scalar <c>m_yaw</c> directly (the orientation is a
    /// pure yaw rotation built from it, so decomposing it back out would be a redundant round-trip on the hot wander
    /// path). Under the free program, where the full attitude is authoritative and <c>m_yaw</c> is inert, it is the yaw
    /// component of <see cref="Orientation"/>. The <c>body.where</c> read-back and <see cref="DescribePose"/> decompose
    /// the canonical orientation directly, bypassing this property.</summary>
    public float Yaw => ((float)((double)FixedYaw));

    // A tape entry: the intent it holds while live, and the host ticks it has left. A mutable struct stored inline
    // in the ring buffer (no per-segment heap object) — the front segment's RemainingTicks is decremented in place through a
    // `ref` into its slot.
    private struct TapeSegment {
        public PlayerIntent Intent;
        public ulong RemainingTicks;
    }
}
