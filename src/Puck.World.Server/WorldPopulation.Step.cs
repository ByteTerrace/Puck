using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    private static void ApplyVariation(Entry entry, CompiledBodyProducer producer, FixedQ4816 phase, FixedQ4816 weaveUnit, FixedQ4816 activityUnit, bool resetPhase) {
        entry.ProducerState.WeaveFrequency = (producer.Scalar(name: "weaveFrequencyBase") + (producer.Scalar(name: "weaveFrequencyRange") * weaveUnit));

        if (resetPhase) {
            entry.ProducerState.AcquiredTarget = -1;
            entry.ProducerState.Phase = phase;
            entry.ProducerState.ActivityPhase = (phase + (TwoPi * activityUnit));
            entry.ProducerState.ActivityRate = (producer.Scalar(name: "activityRateBase") + (producer.Scalar(name: "activityRateRange") * activityUnit));
        }
    }
    private bool HasLineOfSight(in FixedVector3 from, in FixedQuaternion fromOrientation, in FixedVector3 to, in FixedQuaternion toOrientation) {
        var start = (from + fromOrientation.Rotate(vector: LocalSightOffset));
        var end = (to + toOrientation.Rotate(vector: LocalSightOffset));

        return (m_targetField?.LineOfSight(
            from: start,
            to: end
        ) ?? false);
    }
    // The distribution for one inhabited body is anchored at the placement root.
    private static FixedVector3 InhabitantSpawn(WorldPlacement placement, WorldDistribution distribution, int ordinal, int count) {
        var position = new FixedVector3(
            X: FixedQ4816.FromDouble(value: placement.Position.X),
            Y: FixedQ4816.FromDouble(value: placement.Position.Y),
            Z: FixedQ4816.FromDouble(value: placement.Position.Z)
        );
        var disc = ((WorldDistributionRegion.Disc)distribution.Region);
        var radius = FixedQ4816.FromDouble(value: disc.Radius);

        if (radius <= FixedQ4816.Zero) {
            return position;
        }

        var sampleCount = (disc.SampleCount ?? count);
        var fraction = (FixedQ4816.FromInteger(value: ((2L * ordinal) + 1L)) / FixedQ4816.FromInteger(value: (2L * sampleCount)));
        var angle = WorldSequenceSampling.FixedAngle(
            sequence: distribution.Fill,
            index: ordinal
        );
        var r = (radius * FixedQ4816.Sqrt(value: fraction));

        var (sin, cos) = FixedQ4816.SinCos(angle: angle);

        return new FixedVector3(
            X: (position.X + (r * cos)),
            Y: position.Y,
            Z: (position.Z + (r * sin))
        );
    }
    private FixedQ4816 PreferredAltitudeFor(in FixedWorldKit kit, CompiledBodyProducer producer, FixedQ4816 altitudeUnit) {
        return (kit.BodyMotionProgram.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)
            ? (producer.Scalar(name: "altitudeBase") + (producer.Scalar(name: "altitudeRange") * altitudeUnit))
            : FixedQ4816.Zero
        );
    }
    private BodyEffectTargets ReadEffectTargets(int selfIndex, Entry entry, in FixedVector3 self) {
        var currentTarget = entry.ProducerState.AcquiredTarget;
        var current = (((currentTarget >= 0) && (currentTarget < Capacity) && m_entries[currentTarget].Active && (m_entries[currentTarget].Body is not null))
            ? currentTarget
            : -1
        );

        return new BodyEffectTargets(
            ProducerTarget: current,
            AffectingSubject: (entry.Body?.AffectingSubject ?? -1)
        );
    }
    private BodyProducerSensors ReadProducerSensors(int selfIndex, Entry entry, int currentTarget, in FixedVector3 self, in FixedVector3 forward, CompiledBodyProducer producer) {
        var candidate = BodySensorTarget.None;
        var targetSource = producer.Target;

        if (targetSource?.Source is BodyTargetSource.Designated) {
            var designated = entry.Designations[targetSource.Value.RegisterIndex];

            if (
                (designated >= 0) &&
                (designated < Capacity) &&
                m_entries[designated].Active &&
                (m_entries[designated].Body is { } designatedBody)
            ) {
                var position = designatedBody.FixedPosition;

                candidate = new BodySensorTarget(
                    Index: designated,
                    Position: position,
                    DistanceSquared: (position - self).LengthSquared
                );
            }
        } else if (targetSource?.Source is BodyTargetSource.Sensed sensed) {
            var fixedSource = targetSource.Value;

            for (var index = 0; (index < Capacity); index++) {
                if (
                    (index == selfIndex) ||
                    !m_entries[index].Active ||
                    (m_entries[index].Body is not { } body) ||
                    ((sensed.Scope == BodyTargetScope.Seats) && (m_entries[index].Kind != PopulationKind.LocalSeat))
                ) {
                    continue;
                }

                var position = body.FixedPosition;

                if (
                    !BodyTargetConeSense.Contains(
                    origin: in self,
                    forward: in forward,
                    candidate: in position,
                    range: fixedSource.Range,
                    minimumDot: fixedSource.MinimumDot,
                    distanceSquared: out var squared
                ) ||
                    (sensed.RequiresLineOfSight && !HasLineOfSight(
                    from: self,
                    fromOrientation: m_entries[selfIndex].Body!.FixedOrientation,
                    to: position,
                    toOrientation: body.FixedOrientation
                ))
                ) {
                    continue;
                }

                if (squared < candidate.DistanceSquared) {
                    candidate = new BodySensorTarget(
                        DistanceSquared: squared,
                        Index: index,
                        Position: position
                    );
                }
            }
        }

        var current = (((currentTarget >= 0) && (currentTarget < Capacity) && m_entries[currentTarget].Active && (m_entries[currentTarget].Body is { } held))
            ? new BodySensorTarget(
                Index: currentTarget,
                Position: held.FixedPosition,
                DistanceSquared: (held.FixedPosition - self).LengthSquared
            )
            : BodySensorTarget.None
        );

        return new BodyProducerSensors(
            Candidate: candidate,
            CurrentTarget: current
        );
    }
    // The altitude a wander entity holds: a free kit's authored base plus its per-index range sample; a grounded kit
    // starts at the authored spawn point or the world origin and lets contact geometry settle it.
    private static CompiledBodyProducer? SeedProducer(in FixedWorldKit kit) =>
        kit.Producers.Values.FirstOrDefault(predicate: producer => producer.Program.Contains(operation: BodyMotionOp.ProduceWanderIntent));
    // Seed a seat's wander-producer dynamics from its slot alone (no RNG) — the parameters player.control producer:<name>
    // steers by, parallel to the independently authored peer variation. A seat has no wander spawn/color seeding — the
    // definition spawns it and its profile colors it.
    private void SeedSeatWander(int slot, bool resetPhase = true) {
        // A kit that declares no wander producer (a bare seat kit) has no wander dynamics to seed.
        if (SeedProducer(kit: m_kits[m_seatKit]) is not { } producer) {
            return;
        }

        var phase = WorldSequenceSampling.FixedAngle(
            sequence: m_seatVariation.Phase,
            index: slot
        );
        var weaveUnit = WorldSequenceSampling.FixedScalar(
            sequence: m_seatVariation.Weave,
            index: slot
        );

        var (activityUnit, altitudeUnit) = WorldSequenceSampling.FixedPair(
            sequence: m_seatVariation.Activity,
            index: slot
        );
        var entry = m_entries[slot];

        entry.ProducerState.PreferredAltitude = PreferredAltitudeFor(
            kit: m_kits[m_seatKit],
            producer: producer,
            altitudeUnit: altitudeUnit
        );
        ApplyVariation(
            activityUnit: activityUnit,
            entry: entry,
            phase: phase,
            producer: producer,
            resetPhase: resetPhase,
            weaveUnit: weaveUnit
        );
    }
    // Seed a simulated entry's static per-index data from the authored distribution and independent sequences. Baked
    // for every entry at construction so the color is valid across all 128 from frame 1. A
    // live Rebuild re-derives the kit/wander-dependent statics with resetPhase: false, which keeps the running wander
    // phase/activity so the retune does not jerk the crowd.
    private void SeedSimulated(int index, bool resetPhase = true) {
        var offset = (index - LocalSeatCount);
        if (SeedProducer(kit: m_kits[m_entries[index].KitIndex]) is not { } producer) {
            return;
        }

        var phase = WorldSequenceSampling.FixedAngle(
            sequence: m_peerVariation.Phase,
            index: index
        );
        var weaveUnit = WorldSequenceSampling.FixedScalar(
            sequence: m_peerVariation.Weave,
            index: index
        );

        var (activityUnit, altitudeUnit) = WorldSequenceSampling.FixedPair(
            sequence: m_peerVariation.Activity,
            index: index
        );
        var hue = WorldColor.SequenceHue(
            index: index,
            sequence: m_peerColors
        );
        var entry = m_entries[index];

        entry.ProducerState.PreferredAltitude = PreferredAltitudeFor(
            kit: m_kits[entry.KitIndex],
            producer: producer,
            altitudeUnit: altitudeUnit
        );
        if (m_distribution.Points is { Length: > 0 } points) {
            var basePoint = points[(offset % points.Length)];

            entry.ProducerState.PreferredAltitude = basePoint.Position.Y;
            entry.SpawnPosition = SpawnAtPoint(
                basePoint: basePoint.Position,
                halfExtent: m_distribution.Radius,
                fill: m_distribution.Fill,
                ordinal: offset
            );
            entry.SpawnYaw = basePoint.YawRadians;
        } else {
            var sampleCount = ((m_distribution.SampleCount > 0)
                ? m_distribution.SampleCount
                : Math.Max(
                    val1: m_remoteCap,
                    val2: 1
                )
            );
            var fraction = (FixedQ4816.FromInteger(value: ((2L * offset) + 1L)) / FixedQ4816.FromInteger(value: (2L * sampleCount)));
            var spawnRadius = (m_distribution.Radius * FixedQ4816.Sqrt(value: fraction));
            var spawnAngle = WorldSequenceSampling.FixedAngle(
                sequence: m_distribution.Fill,
                index: offset
            );

            var (sin, cos) = FixedQ4816.SinCos(angle: spawnAngle);

            entry.SpawnPosition = new FixedVector3(
                X: (spawnRadius * cos),
                Y: entry.ProducerState.PreferredAltitude,
                Z: (spawnRadius * sin)
            );
            entry.SpawnYaw = spawnAngle;
        }
        entry.BodyColor = WorldColor.HsvToRgb(
            h: hue,
            s: m_playerDefaults.Saturation,
            v: m_playerDefaults.Value
        );
        ApplyVariation(
            activityUnit: activityUnit,
            entry: entry,
            phase: phase,
            producer: producer,
            resetPhase: resetPhase,
            weaveUnit: weaveUnit
        );
    }
    private static FixedVector3 SpawnAtPoint(FixedVector3 basePoint, FixedQ4816 halfExtent, WorldSequence fill, int ordinal) {
        var (jitterX, jitterZ) = WorldSequenceSampling.FixedPair(
            index: ordinal,
            sequence: fill
        );
        var scatterX = (halfExtent * ((jitterX * FixedQ4816.FromInteger(value: 2L)) - FixedQ4816.One));
        var scatterZ = (halfExtent * ((jitterZ * FixedQ4816.FromInteger(value: 2L)) - FixedQ4816.One));

        return new FixedVector3(
            X: (basePoint.X + scatterX),
            Y: basePoint.Y,
            Z: (basePoint.Z + scatterZ)
        );
    }
    // Run the named producer before motion. Live and Idle name no producer.
    private void StageProducer(Entry entry, WorldBody body, int index, ulong stepTicks) {
        var kitIndex = ((entry.Kind == PopulationKind.LocalSeat)
            ? m_seatKit
            : entry.KitIndex
        );

        if (
            (body.Source.ProducerName is not { } name) ||
            !m_kits[kitIndex].Producers.TryGetValue(
            key: name,
            value: out var producer
        )
        ) {
            return;
        }

        var sensors = ReadProducerSensors(
            selfIndex: index,
            entry: entry,
            currentTarget: entry.ProducerState.AcquiredTarget,
            self: body.FixedPosition,
            forward: body.FixedOrientation.Rotate(vector: LocalForward),
            producer: producer
        );

        body.ExecuteProducer(
            producer: producer,
            sensors: in sensors,
            state: ref entry.ProducerState,
            stepTicks: stepTicks
        );
    }

    /// <summary>Advances every active seat body by one exact simulation tick: a wander-sourced seat gets this tick's
    /// producer image staged first (the same deterministic path as a peer), then the body integrates its submitted
    /// intent per the merge rule. Runs after <see cref="AdvanceSimulated"/> in the server step, so the
    /// population advances before seats.</summary>
    /// <param name="tick">The explicit simulation tick.</param>
    /// <param name="stepTicks">The exact engine ticks this step advances.</param>
    /// <param name="stepStartEngineTick">The inclusive engine-time boundary at which this authority step begins.</param>
    /// <param name="engageProbeOrdinals">Per-slot channel ordinal to probe for the context-sensitive-button rising
    /// edge, sentinel <c>-1</c> for "no probe", or empty for none
    /// at all — the zero-cost path every world without an <c>engageChannel</c>-bearing screen takes.</param>
    /// <param name="engageEdges">Receives, per slot, whether that slot's probe ordinal fired a rising edge this tick
    /// (the caller — <see cref="Puck.World.Server.WorldServer.Step"/> — routes each into
    /// <see cref="Puck.World.Server.WorldEngagement.Engage"/>). Every entry is written for an active slot; an inactive
    /// slot is left at the caller's own default (callers pass a freshly zeroed span).</param>
    public void AdvanceSeats(ulong tick, ulong stepTicks, ulong stepStartEngineTick, ReadOnlySpan<int> engageProbeOrdinals, Span<bool> engageEdges) {
        for (var slot = 0; (slot < LocalSeatCount); slot++) {
            if (m_entries[slot] is { Active: true, Body: { } body } entry) {
                if (!body.TryBeginOrdinaryAdvance(stepStartEngineTick: stepStartEngineTick)) {
                    engageEdges[slot] = false;
                    continue;
                }

                StageProducer(
                    body: body,
                    entry: entry,
                    index: slot,
                    stepTicks: stepTicks
                );

                var probe = ((!engageProbeOrdinals.IsEmpty && (engageProbeOrdinals[slot] >= 0))
                    ? engageProbeOrdinals[slot]
                    : (int?)null
                );

                var targets = ReadEffectTargets(
                    selfIndex: slot,
                    entry: entry,
                    self: body.FixedPosition
                );

                engageEdges[slot] = body.Advance(
                    designationOutputs: m_designationOutputs,
                    effectOutputs: m_effectOutputs,
                    effectTargets: targets,
                    engageProbeOrdinal: probe,
                    entityIndex: slot,
                    generatorInvocations: m_generatorInvocations,
                    judgeInvocations: m_judgeInvocations,
                    stepTicks: stepTicks,
                    tick: tick
                );
            }
        }
    }
    /// <summary>Advances every active simulated stand-in by one sub-step: a named producer runs before motion, then
    /// every peer body integrates. A live <c>player.fly</c> tape or
    /// a submitted intent overrides the producer per the merge rule; an <see cref="IntentSource.Idle"/> peer holds
    /// still between tape segments yet its tapes still play. The local seats are advanced separately by
    /// <see cref="AdvanceSeats"/>.</summary>
    /// <remarks>Runs first, before any body (peer or seat) advances this tick, so an attached solid placement's
    /// colliders (<see cref="WorldColliderSet.RefreshAttached"/>) are refreshed exactly once and every body's push
    /// this tick resolves against the same snapshot — the analytic contact provider only; the field provider compiles
    /// its whole SDF program once and a bad-op world already fails loudly at boot/apply if it cannot (attach+solid
    /// stays refused there, see the document validator).</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> is zero.</exception>
    public void AdvanceSimulated(ulong tick, ulong stepTicks, ulong stepStartEngineTick) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);

        (m_contactField as WorldColliderSet)?.RefreshAttached(population: this);

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            var entry = m_entries[index];

            // Network peers AND inhabitants advance here (both own their body and run a deterministic producer until a
            // transport/possession supplies intents). An inactive entry has no body.
            if (
                !entry.Active ||
                (entry.Kind == PopulationKind.LocalSeat) ||
                (entry.Body is not { } player)
            ) {
                continue;
            }

            if (!player.TryBeginOrdinaryAdvance(stepStartEngineTick: stepStartEngineTick)) {
                continue;
            }

            StageProducer(
                body: player,
                entry: entry,
                index: index,
                stepTicks: stepTicks
            );
            var targets = ReadEffectTargets(
                selfIndex: index,
                entry: entry,
                self: player.FixedPosition
            );

            player.Advance(
                tick: tick,
                stepTicks: stepTicks,
                entityIndex: index,
                effectTargets: targets,
                effectOutputs: m_effectOutputs,
                designationOutputs: m_designationOutputs,
                generatorInvocations: m_generatorInvocations,
                judgeInvocations: m_judgeInvocations
            );
        }
    }
    /// <summary>Overrides an already-active seat's own pose and velocity — the mapped-arrival half of a portal
    /// transfer (see <c>Puck.World.WorldPlacementPortal.Arrival</c>): called by <c>Puck.World.WorldInstanceHost</c>
    /// after the destination's own ordinary <see cref="ActivateSeat"/> join already embodied the traveler fresh
    /// under its own kit (appearance, grants, action-track state) — this call selects the source's named motion
    /// program from the destination's own declared program table, then carries across the positional-continuity facts
    /// <c>Puck.World.WorldFrameIsometry.MapArrival</c> computed: pose and captured velocity rotated into
    /// the destination's frame. It never imports the source kit, appearance, grants, dash overlay, timers, or tape.
    /// <see cref="WorldBody.Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/>
    /// runs first (the hard-teleport commit), <see cref="WorldBody.SetArrivalVelocity"/> after — the same
    /// "after Pose, never before" ordering <see cref="WorldBody.ApplyTransferState"/> already follows, so the
    /// discontinuity has already reset <see cref="WorldBody.FixedPreviousPosition"/> before velocity is written. A
    /// no-op returning <see langword="false"/> for an inactive slot — nothing to override.</summary>
    /// <param name="slot">The seat index (0-based) — the same slot the destination's own join just activated.</param>
    /// <param name="motionProgramName">The source-selected motion program, which must also be declared by the destination.</param>
    /// <param name="position">The mapped arrival position, fixed point.</param>
    /// <param name="yawRadians">The mapped arrival yaw, fixed-point radians.</param>
    /// <param name="planarVelocity">The mapped (rotated) planar velocity.</param>
    /// <param name="verticalVelocity">The mapped (rotation-invariant) vertical velocity.</param>
    /// <param name="actionContinuity">Named held-edge and action-register state carried without assuming matching ordinals.</param>
    /// <param name="continuum">The optional already-evaluated adjacency segment to resolve before this body may
    /// participate in another ordinary simulation step. Portal and spawn arrivals omit it.</param>
    /// <param name="destinationCompletedEngineTick">The destination authority's completed engine-time boundary at
    /// admission. It raises the traveler's consumed-through fence when transport arrives after the source interval.</param>
    /// <returns><see langword="true"/> when the seat was active and its body was overridden.</returns>
    public bool ApplyMappedArrival(int slot, string motionProgramName, FixedVector3 position, FixedQ4816 yawRadians, FixedVector3 planarVelocity, FixedQ4816 verticalVelocity, ulong destinationCompletedEngineTick, WorldTransferActionContinuity? actionContinuity = null, WorldContinuumTrajectory? continuum = null) {
        var entry = m_entries[slot];

        if (
            !entry.Active ||
            (entry.Body is not { } body)
        ) {
            return false;
        }

        if (!body.SetBodyMotionProgram(programName: motionProgramName)) {
            return false;
        }

        body.Pose(
            position: position,
            yawRadians: yawRadians,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        // AFTER Pose's own CommitTeleport — see this method's own remarks.
        body.SetArrivalVelocity(
            planarVelocity: planarVelocity,
            verticalVelocity: verticalVelocity
        );
        if (actionContinuity is not null) {
            body.ApplyTransferActionContinuity(
                channels: m_channels,
                continuity: actionContinuity
            );
        }
        if (continuum is { } trajectory) {
            body.ApplyContinuumTrajectory(
                destinationCompletedEngineTick: destinationCompletedEngineTick,
                entityIndex: slot,
                trajectory: in trajectory
            );
        }

        return true;
    }
    /// <summary>Applies entity-directed effect outputs after every body has advanced, then exposes player-keyed durable
    /// writes for the completed tick.</summary>
    public void CompleteStep(ulong tick) {
        foreach (var output in m_effectOutputs) {
            if (
                (((uint)output.TargetIndex) < ((uint)m_entries.Length)) &&
                (m_entries[output.TargetIndex].Body is { } target)
            ) {
                _ = target.ApplyTargetedEffect(
                    sourceIndex: output.SourceIndex,
                    instruction: output.Instruction
                );
            }
        }
        m_effectOutputs.Clear();

        m_durableStateOutputs.Clear();
        for (var index = 0; (index < m_entries.Length); index++) {
            m_entries[index].Body?.TakeDurableStateOutputs(
                entityIndex: index,
                outputs: m_durableStateOutputs,
                tick: tick
            );
        }
    }
    /// <summary>Returns a value indicating whether solid world geometry leaves the sight-offset segment between two live bodies unobstructed —
    /// the general body-to-body spatial primitive a world rule's <c>$los:</c> operand rides, reusing the same
    /// contact-field query and local sight-offset a sensed target's own cone-sense check already uses. Either index
    /// out of range or naming an inactive slot reads as <see langword="false"/> (no sight line to nothing) rather
    /// than throwing — the "an ineligible candidate reads as absent" precedent this population's own field reads
    /// already follow.</summary>
    /// <param name="bodyA">The first body's 0-based entity index.</param>
    /// <param name="bodyB">The second body's 0-based entity index.</param>
    public bool HasLineOfSightBetween(int bodyA, int bodyB) {
        if (
            (((uint)bodyA) >= ((uint)Capacity)) ||
            (((uint)bodyB) >= ((uint)Capacity)) ||
            (m_entries[bodyA].Body is not { } a) ||
            (m_entries[bodyB].Body is not { } b)
        ) {
            return false;
        }

        return HasLineOfSight(
            from: a.FixedPosition,
            fromOrientation: a.FixedOrientation,
            to: b.FixedPosition,
            toOrientation: b.FixedOrientation
        );
    }
    /// <summary>Names the edge/action subset of a captured body state so another world can restore it without
    /// assuming the two documents assigned the same ordinals.</summary>
    public WorldTransferActionContinuity NameTransferActionContinuity(int slot, WorldBody.TransferState state) {
        var channels = new List<WorldTransferChannelEdge>();

        for (var ordinal = 0; (ordinal < Math.Min(
            val1: state.PreviousChannelBit.Length,
            val2: m_channels.ChannelCount
        )); ordinal++) {
            if (m_channels.Name(ordinal: ordinal) is { } name) {
                channels.Add(item: new WorldTransferChannelEdge(
                    Name: name,
                    PreviousBit: state.PreviousChannelBit[ordinal],
                    HeldValue: state.HeldChannelImage[ordinal]
                ));
            }
        }

        var definitions = m_kits[ResolveKitIndex(index: slot)].ActionState;
        var count = Math.Min(
            val1: definitions.Length,
            val2: Math.Min(
                val1: state.ActionStateValues.Length,
                val2: state.ActionStateTimers.Length
            )
        );
        var registers = new WorldTransferActionRegister[count];

        for (var index = 0; (index < count); index++) {
            registers[index] = new WorldTransferActionRegister(
                Name: definitions[index].Name,
                Kind: definitions[index].Kind,
                Value: state.ActionStateValues[index],
                TimerTicks: state.ActionStateTimers[index]
            );
        }

        return new WorldTransferActionContinuity(
            Channels: channels,
            Registers: registers
        );
    }
}
