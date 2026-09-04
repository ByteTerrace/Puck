using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>Gets the latest peer-advance cadence work. Counts only bodies whose kit authors a nonzero cadence.</summary>
    public WorldAutonomyStatistics AutonomyStatistics { get; private set; }
    private static void ApplyVariation(Entry entry, CompiledBodyProducer producer, FixedQ4816 phase, FixedQ4816 weaveUnit, FixedQ4816 activityUnit, bool resetPhase) {
        entry.ProducerState.WeaveFrequency = (producer.Scalar(name: "weaveFrequencyBase") + (producer.Scalar(name: "weaveFrequencyRange") * weaveUnit));

        if (resetPhase) {
            entry.ProducerState.AcquiredTarget = -1;
            entry.ProducerState.CurveArcRaw = 0L;
            entry.NavigationState.Clear();
            entry.ProducerState.Phase = phase;
            entry.ProducerState.ActivityPhase = (phase + (TwoPi * activityUnit));
            entry.ProducerState.ActivityRate = (producer.Scalar(name: "activityRateBase") + (producer.Scalar(name: "activityRateRange") * activityUnit));
        }
    }
    internal bool HasLineOfSight(in FixedVector3 from, in FixedQuaternion fromOrientation, in FixedVector3 to, in FixedQuaternion toOrientation) {
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
        var frozen = producer.Flock is not null;

        if (targetSource?.Source is BodyTargetSource.Designated) {
            var designated = entry.Designations[targetSource.Value.RegisterIndex];

            if (designated.IsPoint) {
                candidate = BodySensorTarget.Point(
                    position: designated.Point,
                    distanceSquared: (designated.Point - self).LengthSquared
                );
            } else if (
                designated.HasBody &&
                (designated.Index < Capacity) &&
                m_entries[designated.Index].Active &&
                (m_entries[designated.Index].Body is { } designatedBody)
            ) {
                var position = frozen ? m_flockPositions[designated.Index] : designatedBody.FixedPosition;

                candidate = new BodySensorTarget(
                    Index: designated.Index,
                    Position: position,
                    DistanceSquared: (position - self).LengthSquared
                );
            }
        } else if (frozen && targetSource?.Source is BodyTargetSource.Sensed) {
            candidate = ReadFlockTarget(entry, self);
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

                var position = frozen ? m_flockPositions[index] : body.FixedPosition;

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
                    fromOrientation: frozen ? m_flockOrientations[selfIndex] : m_entries[selfIndex].Body!.FixedOrientation,
                    to: position,
                    toOrientation: frozen ? m_flockOrientations[index] : body.FixedOrientation
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
        } else if (targetSource?.Source is BodyTargetSource.CurveFollow) {
            var fixedSource = targetSource.Value;

            if (((uint)fixedSource.CurveIndex) < ((uint)m_curveRows.Count)) {
                var compiled = m_curveRows[fixedSource.CurveIndex].Compiled;

                entry.ProducerState.CurveArcRaw = AdvanceCurveArc(
                    arcRaw: entry.ProducerState.CurveArcRaw,
                    stepRaw: fixedSource.ArcStepRaw,
                    totalLengthRaw: compiled.TotalLengthRaw,
                    closed: compiled.Closed
                );

                var position = compiled.EvaluateRaw(arcRaw: entry.ProducerState.CurveArcRaw).Position;

                candidate = BodySensorTarget.Point(
                    position: position,
                    distanceSquared: (position - self).LengthSquared
                );
            }
        } else if (targetSource?.Source is BodyTargetSource.Navigated) {
            candidate = ReadNavigatedTarget(
                entry: entry,
                self: self,
                target: targetSource.Value,
                frozen: frozen
            );
        }

        var current = frozen ? (candidate.Index == currentTarget ? candidate : BodySensorTarget.None) :
            (((currentTarget >= 0) && (currentTarget < Capacity) && m_entries[currentTarget].Active && (m_entries[currentTarget].Body is { } held))
            ? new BodySensorTarget(
                Index: currentTarget,
                Position: frozen ? m_flockPositions[currentTarget] : held.FixedPosition,
                DistanceSquared: ((frozen ? m_flockPositions[currentTarget] : held.FixedPosition) - self).LengthSquared
            )
            : BodySensorTarget.None
        );

        return new BodyProducerSensors(
            Candidate: candidate,
            CurrentTarget: current
        );
    }
    private BodySensorTarget ReadNavigatedTarget(Entry entry, in FixedVector3 self, in FixedBodyTargetSource target, bool frozen) {
        var state = entry.NavigationState;
        state.ExpandedLast = 0;
        var designation = entry.Designations[target.RegisterIndex];
        FixedVector3 goal;
        var targetIndex = WorldTargetDesignation.PointIndex;

        if (designation.IsPoint) {
            goal = designation.Point;
        } else if (
            designation.HasBody &&
            designation.Index < Capacity &&
            m_entries[designation.Index].Active &&
            m_entries[designation.Index].Body is { } designatedBody
        ) {
            goal = frozen ? m_flockPositions[designation.Index] : designatedBody.FixedPosition;
            targetIndex = designation.Index;
        } else {
            state.Clear(status: WorldNavigationStatus.NoTarget);
            return BodySensorTarget.None;
        }

        if ((uint)target.NavigationDomainIndex >= (uint)m_navigation.Count) {
            state.Clear(status: WorldNavigationStatus.OutsideDomain);
            return BodySensorTarget.None;
        }

        var domain = m_navigation[target.NavigationDomainIndex];
        if (!domain.TryCell(position: in self, node: out var start) || !domain.TryCell(position: in goal, node: out var goalCell)) {
            state.Clear(status: WorldNavigationStatus.OutsideDomain);
            return BodySensorTarget.None;
        }

        var onCachedRoute = state.PathLength != 0 && state.DomainIndex == target.NavigationDomainIndex && state.GoalCell == goalCell;
        if (onCachedRoute) {
            var previous = Math.Max(0, state.Waypoint - 1);
            onCachedRoute = state.Path[previous] == start || (state.Waypoint < state.PathLength && state.Path[state.Waypoint] == start);
            if (onCachedRoute && state.Waypoint < state.PathLength) {
                onCachedRoute = domain.IsTraversableEdge(current: state.Path[previous], next: state.Path[state.Waypoint]);
            }
        }
Replan:
        if (!onCachedRoute) {
            state.DomainIndex = target.NavigationDomainIndex;
            state.GoalCell = goalCell;
            state.Waypoint = 1;
            state.Status = domain.Sharing is not null
                ? domain.RequestShared(start, goalCell, state.WritablePath(), out state.PathLength)
                : domain.FindPath(start, goalCell, state.WritablePath(), out state.PathLength, out state.ExpandedLast);
            if (state.PathLength == 0) {
                state.Waypoint = 0;
                return BodySensorTarget.None;
            }
        }

        var arrivalSquared = (domain.Tuning.ArrivalDistance * domain.Tuning.ArrivalDistance);
        while (state.Waypoint < state.PathLength) {
            if (state.Waypoint > 0 && !domain.IsTraversableEdge(current: state.Path[state.Waypoint - 1], next: state.Path[state.Waypoint])) {
                onCachedRoute = false;
                goto Replan;
            }
            var waypoint = domain.Position(node: state.Path[state.Waypoint]);
            if ((waypoint - self).LengthSquared > arrivalSquared) {
                state.Status = WorldNavigationStatus.Active;
                return new BodySensorTarget(
                    Index: targetIndex,
                    Position: waypoint,
                    DistanceSquared: (waypoint - self).LengthSquared
                );
            }
            state.Waypoint++;
        }

        var distanceSquared = (goal - self).LengthSquared;
        state.Status = (distanceSquared <= arrivalSquared ? WorldNavigationStatus.Arrived : WorldNavigationStatus.Active);
        return new BodySensorTarget(Index: targetIndex, Position: goal, DistanceSquared: distanceSquared);
    }
    // Advances a curve-follow arc position by one compiled step, then wraps (closed) or clamps (open) it back inside
    // [0, totalLengthRaw] — the persisted state never grows past the curve's own length, so it stays bounded across
    // an arbitrarily long run and CompiledCurvatureSpline.EvaluateRaw's own wrap/clamp is redundant with, never a
    // substitute for, this one (EvaluateRaw wraps its ARGUMENT; this wraps the STORED accumulator). Both arcRaw and
    // stepRaw arrive already bounded well under long's range (CurvatureSpline's own MaxCoordinate/
    // MaxTangentChordRatio caps bound totalLengthRaw far below 2^62), so the addition itself cannot overflow.
    private static long AdvanceCurveArc(long arcRaw, long stepRaw, long totalLengthRaw, bool closed) {
        var next = unchecked((arcRaw + stepRaw));

        if (!closed) {
            return Math.Clamp(
                max: totalLengthRaw,
                min: 0L,
                value: next
            );
        }

        if (totalLengthRaw <= 0L) {
            return 0L;
        }

        next %= totalLengthRaw;

        if (next < 0L) {
            next += totalLengthRaw;
        }

        return next;
    }

    /// <summary>Counts active bodies whose currently selected producer follows a <c>curves</c> row — the
    /// <c>world.budget</c> cost sheet's own per-tick price for the feature (one
    /// <see cref="Puck.Maths.CompiledCurvatureSpline.Evaluate"/> per follower, per tick).</summary>
    public int CountCurveFollowers() {
        var count = 0;

        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (
                !entry.Active ||
                (entry.Body is not { } body) ||
                (body.Source.ProducerName is not { } name)
            ) {
                continue;
            }

            var kitIndex = ((entry.Kind == PopulationKind.LocalSeat) ? m_seatKit : entry.KitIndex);

            if (
                m_kits[kitIndex].Producers.TryGetValue(
                key: name,
                value: out var producer
            ) &&
                (producer.Target?.Source is BodyTargetSource.CurveFollow)
            ) {
                count++;
            }
        }

        return count;
    }

    // The altitude a wander entity holds: a free kit's authored base plus its per-index range sample; a grounded kit
    // starts at the authored spawn point or the world origin and lets contact geometry settle it.
    private static CompiledBodyProducer? SeedProducer(in FixedWorldKit kit) =>
        kit.Producers.Values.FirstOrDefault(predicate: producer => producer.Program.Contains(operation: BodyMotionOp.ProduceWanderIntent));
    // Seed a seat's wander-producer dynamics from its slot alone (no RNG) — the parameters body.control producer:<name>
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
    // for every entry at construction so its color and spawn are valid regardless of producer kind. A
    // live Rebuild re-derives the kit/wander-dependent statics with resetPhase: false, which keeps the running wander
    // phase/activity so the retune does not jerk the crowd.
    private void SeedSimulated(int index, bool resetPhase = true) {
        var offset = (index - LocalSeatCount);

        var producer = SeedProducer(kit: m_kits[m_entries[index].KitIndex]);

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

        entry.ProducerState.PreferredAltitude = producer is null ? FixedQ4816.Zero : PreferredAltitudeFor(
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
        if (producer is not null) {
            ApplyVariation(
                activityUnit: activityUnit,
                entry: entry,
                phase: phase,
                producer: producer,
                resetPhase: resetPhase,
                weaveUnit: weaveUnit
            );
        }
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
        body.SetFlockMovementDomain(null);
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
            // Leaving the producer lane is itself a producer transition. Do not let a prior route (or curve
            // station) remain observable through rule facts while the body is idle, live-driven, or names a
            // producer that the current kit does not carry.
            if (entry.ProducerState.ActiveProducerName is not null) {
                entry.ProducerState.ActiveProducerName = null;
                entry.ProducerState.ActiveProducerCurveIndex = -1;
                entry.ProducerState.ActiveProducerNavigationDomainIndex = -1;
                entry.ProducerState.CurveArcRaw = 0L;
                entry.NavigationState.Clear(status: WorldNavigationStatus.NoTarget);
                entry.ProducerState.FlockSeeded = false;
            }
            body.StageProducerIntent(intent: default);
            return;
        }

        // Selecting a producer starts it: a plain producer switch, or a same-name kit retune onto a different
        // curve row, resets the travelled arc rather than resuming a foreign curve's station — see
        // BodyProducerState.ActiveProducerName's remarks.
        var curveIndex = -1;
        var navigationDomainIndex = -1;

        if (producer.Target?.Source is BodyTargetSource.CurveFollow) {
            curveIndex = producer.Target.Value.CurveIndex;
        } else if (producer.Target?.Source is BodyTargetSource.Navigated) {
            navigationDomainIndex = producer.Target.Value.NavigationDomainIndex;
        }

        if (
            !string.Equals(a: entry.ProducerState.ActiveProducerName, b: name, comparisonType: StringComparison.Ordinal) ||
            (entry.ProducerState.ActiveProducerCurveIndex != curveIndex) ||
            (entry.ProducerState.ActiveProducerNavigationDomainIndex != navigationDomainIndex)
        ) {
            entry.ProducerState.ActiveProducerCurveIndex = curveIndex;
            entry.ProducerState.ActiveProducerName = name;
            entry.ProducerState.CurveArcRaw = 0L;
            entry.ProducerState.ActiveProducerNavigationDomainIndex = navigationDomainIndex;
            entry.NavigationState.Clear();
            entry.ProducerState.FlockSeeded = false;
        }

        if (producer.Flock is not null) {
            if (producer.Flock.MovementDomainIndex >= 0) {
                body.SetFlockMovementDomain(m_navigation[producer.Flock.MovementDomainIndex]);
            }
            RefreshFlockPerception(index, entry, producer, stepTicks);
        }
        var sensors = ReadProducerSensors(
            selfIndex: index,
            entry: entry,
            currentTarget: entry.ProducerState.AcquiredTarget,
            self: body.FixedPosition,
            forward: body.FixedOrientation.Rotate(vector: LocalForward),
            producer: producer
        );
        if (producer.Flock is not null) {
            sensors = sensors with { FlockDesired = BlendFlockPreference(index, entry, producer.Flock, sensors.Candidate) };
        } else {
            entry.ProducerState.FlockSeeded = false;
        }

        body.ExecuteProducer(
            producer: producer,
            sensors: in sensors,
            state: ref entry.ProducerState,
            stepTicks: stepTicks
        );
    }

    /// <summary>Samples every active body's medium free surface at its coupled lattice cell (the same coupling
    /// <see cref="WorldFieldLattice.TryBodyCellOf"/> resolves) and pushes it to the body — <see langword="null"/>
    /// for a body outside the lattice or over a zero-value medium cell. Called once per tick, before
    /// <see cref="AdvanceSimulated"/>/<see cref="AdvanceSeats"/>, so a medium hold's phase-4 law reads this
    /// tick's surface rather than a stale one. A no-op world without a <c>fields</c> section costs one null
    /// check.</summary>
    public void SampleMediumSurfaces() {
        if (m_fields is not { } fields) {
            return;
        }

        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (
                !entry.Active ||
                (entry.Body is not { } body)
            ) {
                continue;
            }

            body.SetMediumSurface(surface: fields.MediumSurface(position: body.FixedPosition));
        }
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
    /// <see cref="Puck.World.Server.WorldEngagement.Compose"/>). Every entry is written for an active slot; an inactive
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
                    rigidSubstepCeiling: m_bodyContactPolicy.RigidSubstepCeiling,
                    stepTicks: stepTicks,
                    tick: tick
                );
                RecordFlockMotion(body);
            }
        }
    }

    // The tick's one gravity solve. Gathered in ENTITY ORDER so the solver's input order is the population's own
    // stable order: an approximate solver's answer depends on the order its tree is built in, so activation history
    // must not reach it.
    private void SolveGravity() {
        if (m_gravityField is not { IsActive: true } gravity) {
            return;
        }

        gravity.RefreshAttachedAreas(population: this);
        m_gravityTargets.Clear();

        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (
                !entry.Active ||
                (entry.Body is not { } body)
            ) {
                continue;
            }

            m_gravityTargets.Add(item: new WorldGravityTarget(
                EntityIndex: index,
                Mass: ((((uint)entry.KitIndex) < ((uint)m_kits.Length))
                ? m_kits[entry.KitIndex].Mass
                : FixedQ4816.Zero),
                Position: body.FixedPosition
            ));
        }

        gravity.Solve(targets: m_gravityTargets);
    }

    private static void BindCadence(ulong period, int ordinal, int count, ref ulong boundPeriod, ref ulong elapsed, ref ulong remaining) {
        if (boundPeriod == period) {
            return;
        }

        boundPeriod = period;
        elapsed = 0UL;
        remaining = ((period == 0UL)
            ? 0UL
            : Math.Max(
                val1: 1UL,
                val2: (((checked((ulong)(ordinal + 1)) * period) + checked((ulong)count - 1UL)) / checked((ulong)count))
            )
        );
    }
    private static bool CadenceDue(ulong period, ulong stepTicks, ref ulong elapsed, ref ulong remaining, out ulong elapsedTicks) {
        if (period == 0UL) {
            elapsedTicks = stepTicks;
            return true;
        }

        elapsed = checked(elapsed + stepTicks);
        remaining = ((remaining > stepTicks) ? (remaining - stepTicks) : 0UL);
        if (remaining > 0UL) {
            elapsedTicks = 0UL;
            return false;
        }

        elapsedTicks = elapsed;
        elapsed = 0UL;
        remaining = period;
        return true;
    }

    /// <summary>Advances every active simulated stand-in by one sub-step: a named producer runs before motion, then
    /// every peer body integrates. A live <c>body.fly</c> tape or
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

        AutonomyStatistics = default;

        (m_contactField as WorldColliderSet)?.RefreshAttached(population: this);
        SolveGravity();
        FreezeFlockImage();
        m_navigation.BeginStep();

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

            var kit = m_kits[entry.KitIndex];
            var locallyAutonomous = (
                !entry.IsRemoteHuman &&
                !player.Source.IsLive &&
                !player.HasMotionTape &&
                !player.RequiresFullRateAutonomy
            );
            var motionPeriod = (locallyAutonomous ? kit.AutonomousMotionTicks : 0UL);
            var steeringPeriod = ((!entry.IsRemoteHuman && player.Source.IsProducer) ? kit.AutonomousSteeringTicks : 0UL);
            ref var autonomy = ref entry.AutonomyState;
            var ordinal = (index - LocalSeatCount);
            BindCadence(motionPeriod, ordinal, PeerCapacity, ref autonomy.MotionPeriodTicks, ref autonomy.MotionElapsedTicks, ref autonomy.MotionRemainingTicks);
            BindCadence(steeringPeriod, ordinal, PeerCapacity, ref autonomy.SteeringPeriodTicks, ref autonomy.SteeringElapsedTicks, ref autonomy.SteeringRemainingTicks);

            var steeringDue = CadenceDue(
                period: steeringPeriod,
                stepTicks: stepTicks,
                elapsed: ref autonomy.SteeringElapsedTicks,
                remaining: ref autonomy.SteeringRemainingTicks,
                elapsedTicks: out var steeringTicks
            );
            if (steeringDue) {
                StageProducer(
                    body: player,
                    entry: entry,
                    index: index,
                    stepTicks: steeringTicks
                );
                if (player.Source.IsProducer && ((steeringPeriod != 0UL) || (motionPeriod != 0UL))) {
                    autonomy.SteeringIntent = player.StagedProducerIntent;
                    autonomy.SteeringSeeded = true;
                } else {
                    autonomy.SteeringIntent = default;
                    autonomy.SteeringSeeded = false;
                }
                if (steeringPeriod != 0UL) {
                    AutonomyStatistics = AutonomyStatistics with { SteeringUpdates = AutonomyStatistics.SteeringUpdates + 1 };
                }
            }

            if (!CadenceDue(
                period: motionPeriod,
                stepTicks: stepTicks,
                elapsed: ref autonomy.MotionElapsedTicks,
                remaining: ref autonomy.MotionRemainingTicks,
                elapsedTicks: out var motionTicks
            )) {
                player.DeferOrdinaryAdvance();
                if (motionPeriod != 0UL) {
                    AutonomyStatistics = AutonomyStatistics with { MotionDeferred = AutonomyStatistics.MotionDeferred + 1 };
                }
                continue;
            }

            if (motionPeriod != 0UL) {
                AutonomyStatistics = AutonomyStatistics with { MotionUpdates = AutonomyStatistics.MotionUpdates + 1 };
            }

            var accumulatedStart = checked(stepStartEngineTick + stepTicks - motionTicks);
            if (!player.TryBeginOrdinaryAdvance(stepStartEngineTick: accumulatedStart)) {
                autonomy.MotionElapsedTicks = 0UL;
                autonomy.MotionRemainingTicks = motionPeriod;
                continue;
            }

            if (!steeringDue && autonomy.SteeringSeeded) {
                player.StageProducerIntent(intent: in autonomy.SteeringIntent);
            }
            var targets = ReadEffectTargets(
                selfIndex: index,
                entry: entry,
                self: player.FixedPosition
            );

            player.Advance(
                tick: tick,
                stepTicks: motionTicks,
                entityIndex: index,
                effectTargets: targets,
                effectOutputs: m_effectOutputs,
                designationOutputs: m_designationOutputs,
                generatorInvocations: m_generatorInvocations,
                judgeInvocations: m_judgeInvocations,
                rigidSubstepCeiling: m_bodyContactPolicy.RigidSubstepCeiling
            );
            RecordFlockMotion(player);
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
