using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;
using Puck.Physics.Navigation;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>Gets the latest peer-advance cadence work. Counts only bodies whose kit authors a nonzero cadence.</summary>
    public WorldAutonomyStatistics AutonomyStatistics => m_serialWork.Autonomy;

    // The field-input pass (SampleFieldInputs): each region's travel resistance this pass, the pass counter, and the
    // pass each slot last sampled in, so a body the pass skipped samples before it advances. Derived each tick from the
    // regions and state; never simulation state.
    private FixedQ4816[] m_regionResistance = [];
    private ulong m_fieldInputPass;
    private ulong[] m_fieldInputPasses = [];
    // The producer fault last narrated for each body slot, so each transition is narrated once. Diagnostic only.
    private readonly Puck.Abstractions.ReasonLatch<int> m_producerFaults = new();

    private static void ApplyVariation(Entry entry, CompiledBodyProducer producer, FixedQ4816 phase, FixedQ4816 weaveUnit, FixedQ4816 activityUnit, bool resetPhase) {
        entry.ProducerState.WeaveFrequency = (producer.Scalar(parameter: BodyProducerParameter.WeaveFrequencyBase) + (producer.Scalar(parameter: BodyProducerParameter.WeaveFrequencyRange) * weaveUnit));

        if (resetPhase) {
            entry.ProducerState.AcquiredTarget = -1;
            entry.ProducerState.CurveArcRaw = 0L;
            entry.ProducerState.CurveArcRemainder = 0L;
            entry.NavigationState.Clear();
            entry.ProducerState.Phase = phase;
            entry.ProducerState.PhaseRemainder = 0L;
            entry.ProducerState.ActivityPhase = (phase + (FixedQ4816.Tau * activityUnit));
            entry.ProducerState.ActivityPhaseRemainder = 0L;
            entry.ProducerState.ActivityRate = (producer.Scalar(parameter: BodyProducerParameter.ActivityRateBase) + (producer.Scalar(parameter: BodyProducerParameter.ActivityRateRange) * activityUnit));
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

    // The distribution for one inhabited body is anchored at the placement's own COMPOSED (world) frame — see
    // WorldDefinitionRows.ResolvedFrame — never its authored (possibly parent-relative) Position directly.
    private static FixedVector3 InhabitantSpawn(CompiledPlacementFrame frame, WorldDistribution distribution, int ordinal, int count) {
        var position = FixedVector3.FromVector3(value: frame.Position);
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
            ? (producer.Scalar(parameter: BodyProducerParameter.AltitudeBase) + (producer.Scalar(parameter: BodyProducerParameter.AltitudeRange) * altitudeUnit))
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
    // Every producer reads another body through the tick's frozen image (m_perception), never its live pose: a body's
    // view of the others is the same whichever bodies stepped before it, so it does not depend on the order bodies
    // step in or on how many threads step them. A body the freeze did not find reads as absent.
    private BodyProducerSensors ReadProducerSensors(int selfIndex, Entry entry, int currentTarget, in FixedVector3 self, CompiledBodyProducer producer, ProducerWork work, ulong stepTicks) {
        var image = m_perception!;
        var candidate = BodySensorTarget.None;
        var targetSource = producer.Target;
        var flock = (producer.Flock is not null);

        if (targetSource?.Source is BodyTargetSource.Designated) {
            var designated = entry.Designations[targetSource.Value.RegisterIndex];

            if (designated.IsPoint) {
                candidate = BodySensorTarget.Point(
                    position: designated.Point,
                    distanceSquared: (designated.Point - self).LengthSquared
                );
            } else if (designated.HasBody && image.Present(index: designated.Index)) {
                var position = image.Position(index: designated.Index);

                candidate = new BodySensorTarget(
                    Index: designated.Index,
                    Position: position,
                    DistanceSquared: (position - self).LengthSquared
                );
            }
        } else if (
            flock &&
            (targetSource?.Source is BodyTargetSource.Sensed)
        ) {
            candidate = ReadFlockTarget(
                entry: entry,
                self: self
            );
        } else if (targetSource?.Source is BodyTargetSource.Sensed sensed) {
            candidate = SenseNearest(selfIndex: selfIndex, sensed: sensed, source: targetSource.Value, self: in self, work: work);
        } else if (targetSource?.Source is BodyTargetSource.CurveFollow) {
            var fixedSource = targetSource.Value;

            if (((uint)fixedSource.CurveIndex) < ((uint)m_curveRows.Count)) {
                var compiled = m_curveRows[fixedSource.CurveIndex].Compiled;

                entry.ProducerState.CurveArcRaw = AdvanceCurveArc(
                    arcRaw: entry.ProducerState.CurveArcRaw,
                    rate: fixedSource.ArcRate,
                    remainder: ref entry.ProducerState.CurveArcRemainder,
                    stepTicks: stepTicks,
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
                work: work
            );
        }

        BodySensorTarget current;

        if (flock) {
            current = ((candidate.Index == currentTarget) ? candidate : BodySensorTarget.None);
        } else if (image.Present(index: currentTarget)) {
            var held = image.Position(index: currentTarget);

            current = new BodySensorTarget(Index: currentTarget, Position: held, DistanceSquared: (held - self).LengthSquared);
        } else {
            current = BodySensorTarget.None;
        }

        return new BodyProducerSensors(
            Candidate: candidate,
            CurrentTarget: current
        );
    }
    // The goal a navigated target names: its point, or the frozen pose of the body it designates; false when it names
    // neither.
    private bool TryNavigationGoal(Entry entry, in FixedBodyTargetSource target, out FixedVector3 goal, out int targetIndex) {
        var designation = entry.Designations[target.RegisterIndex];

        targetIndex = WorldTargetDesignation.PointIndex;
        if (designation.IsPoint) {
            goal = designation.Point;
            return true;
        }
        if (designation.HasBody && m_perception!.Present(index: designation.Index)) {
            goal = m_perception.Position(index: designation.Index);
            targetIndex = designation.Index;
            return true;
        }
        goal = default;
        return false;
    }
    private BodySensorTarget ReadNavigatedTarget(Entry entry, in FixedVector3 self, in FixedBodyTargetSource target, ProducerWork work) {
        var state = entry.NavigationState;

        state.ExpandedLast = 0;
        if (!TryNavigationGoal(entry: entry, goal: out var goal, target: in target, targetIndex: out var targetIndex)) {
            state.Clear(status: NavigationStatus.NoTarget);
            return BodySensorTarget.None;
        }

        if (((uint)target.NavigationDomainIndex) >= ((uint)m_navigation.Count)) {
            state.Clear(status: NavigationStatus.OutsideDomain);
            return BodySensorTarget.None;
        }

        var domain = m_navigation[target.NavigationDomainIndex];

        if (domain.Tuning.Shared) {
            return ReadGoalFieldTarget(domainIndex: target.NavigationDomainIndex, entry: entry, goal: in goal, profile: NavigationProfile(index: target.NavigationProfileIndex), self: in self, targetIndex: targetIndex, work: work);
        }
        if (
            !domain.CoversRouteHeight(position: in self) ||
            !domain.CoversRouteHeight(position: in goal) ||
            !domain.TryCell(
            node: out var start,
            position: in self
        ) ||
            !domain.TryCell(
            node: out var goalCell,
            position: in goal
        )
        ) {
            state.Clear(status: NavigationStatus.OutsideDomain);
            return BodySensorTarget.None;
        }

        var onCachedRoute = ((state.PathLength != 0) && (state.DomainIndex == target.NavigationDomainIndex) && (state.GoalCell == goalCell));

        if (onCachedRoute) {
            var previous = Math.Max(
                val1: 0,
                val2: (state.Waypoint - 1)
            );

            onCachedRoute = ((state.Path[previous] == start) || ((state.Waypoint < state.PathLength) && (state.Path[state.Waypoint] == start)));
            if (
                onCachedRoute &&
                (state.Waypoint < state.PathLength)
            ) {
                onCachedRoute = domain.IsTraversableEdge(
                    current: state.Path[previous],
                    next: state.Path[state.Waypoint]
                );
            }
        }
    Replan:
        if (!onCachedRoute) {
            state.DomainIndex = target.NavigationDomainIndex;
            state.GoalCell = goalCell;
            state.Waypoint = 1;
            state.Status = domain.FindPath(
                start,
                goalCell,
                state.WritablePath(),
                out state.PathLength,
                out state.ExpandedLast,
                NavigationProfile(index: target.NavigationProfileIndex)
            );
            if (state.PathLength == 0) {
                state.Waypoint = 0;
                return BodySensorTarget.None;
            }
        }

        var arrivalSquared = (domain.Tuning.ArrivalDistance * domain.Tuning.ArrivalDistance);

        while (state.Waypoint < state.PathLength) {
            if (
                (state.Waypoint > 0) &&
                !domain.IsTraversableEdge(
                current: state.Path[(state.Waypoint - 1)],
                next: state.Path[state.Waypoint]
            )
            ) {
                onCachedRoute = false;
                goto Replan;
            }
            var waypoint = domain.Foot(node: state.Path[state.Waypoint]);
            var distanceToWaypoint = (waypoint - self).LengthSquared;

            if (distanceToWaypoint > arrivalSquared) {
                state.Status = NavigationStatus.Active;
                return new BodySensorTarget(
                    Index: targetIndex,
                    Position: waypoint,
                    DistanceSquared: distanceToWaypoint
                );
            }
            state.Waypoint++;
        }

        var distanceSquared = (goal - self).LengthSquared;

        state.Status = ((distanceSquared <= arrivalSquared)
            ? NavigationStatus.Arrived
            : NavigationStatus.Active
        );
        return new BodySensorTarget(
            DistanceSquared: distanceSquared,
            Index: targetIndex,
            Position: goal
        );
    }
    // The most goal-field steps one read follows past cells the body already stands within arrival distance of: a
    // junction between two domains is a step at one place, and arrival distance is about a cell, so a read takes one or
    // two.
    private const int GoalFieldLookahead = 8;

    // Locates a shared-domain follower and its goal in the target domain's network: false when either stands outside it.
    private bool TryLocateGoalRead(int domainIndex, in FixedVector3 self, in FixedVector3 goal, out int domain, out int cell, out int goalDomain, out int goalCell) {
        goalDomain = -1;
        goalCell = -1;
        return (TryLocateInNetwork(cell: out cell, domain: out domain, network: domainIndex, position: in self) &&
            TryLocateInNetwork(cell: out goalCell, domain: out goalDomain, network: domainIndex, position: in goal));
    }
    // A shared domain's follower holds no route. Each read locates the body and its goal in the target domain's network
    // and follows the goal's field from the body's cell, so a body pushed off its line reads the step from where it now
    // stands, and a tick costs a step read per follower; a field is derived only for a goal no follower read before. A
    // kit partition reads what its plan located and resolved (PrepareGoalField) from the same pose, goal and profile, and
    // derives nothing: the cache is one thread's.
    private BodySensorTarget ReadGoalFieldTarget(Entry entry, int domainIndex, in FixedVector3 self, in FixedVector3 goal, NavigationCostProfile profile, int targetIndex, ProducerWork work) {
        var state = entry.NavigationState;
        NavigationGoalField field;
        int cell;
        int domain;

        if (work.Partitioned) {
            if (entry.PreparedGoalField is not { } prepared) {
                state.Clear(status: NavigationStatus.OutsideDomain);
                return BodySensorTarget.None;
            }
            // The plan resolved the field under the producer's profile; one under another would price another route.
            field = (prepared.Profile.Equals(other: profile)
                ? prepared
                : throw new InvalidOperationException(message: $"Body {entry.Index}'s goal field was resolved under another cost profile than its kit partition reads."));
            cell = entry.PreparedGoalFrom.Cell;
            domain = entry.PreparedGoalFrom.Domain;
        } else {
            if (!TryLocateGoalRead(cell: out cell, domain: out domain, domainIndex: domainIndex, goal: in goal, goalCell: out var goalCell, goalDomain: out var goalDomain, self: in self)) {
                state.Clear(status: NavigationStatus.OutsideDomain);
                return BodySensorTarget.None;
            }
            field = m_navigation.GoalField(goalCell: goalCell, goalDomain: goalDomain, profile: profile);
        }
        var arrivalSquared = (m_navigation[domainIndex].Tuning.ArrivalDistance * m_navigation[domainIndex].Tuning.ArrivalDistance);

        state.Clear();
        state.DomainIndex = domainIndex;
        for (var step = 0; (step < GoalFieldLookahead); step++) {
            var status = m_navigation.GoalStep(cell: cell, domain: domain, field: field, nextCell: out var nextCell, nextDomain: out var nextDomain);

            if (status == NavigationStatus.Arrived) {
                break;
            }
            if (status != NavigationStatus.Active) {
                state.Status = status;
                return BodySensorTarget.None;
            }

            var waypoint = m_navigation[nextDomain].Foot(node: nextCell);
            var distanceToWaypoint = (waypoint - self).LengthSquared;

            if ((distanceToWaypoint > arrivalSquared) || (step == (GoalFieldLookahead - 1))) {
                state.Status = NavigationStatus.Active;
                return new BodySensorTarget(Index: targetIndex, Position: waypoint, DistanceSquared: distanceToWaypoint);
            }
            domain = nextDomain;
            cell = nextCell;
        }

        var distanceSquared = (goal - self).LengthSquared;

        state.Status = ((distanceSquared <= arrivalSquared) ? NavigationStatus.Arrived : NavigationStatus.Active);
        return new BodySensorTarget(DistanceSquared: distanceSquared, Index: targetIndex, Position: goal);
    }
    // Locates, serially and before a kit partition steers the body, its shared navigated producer's cell and goal
    // from the pose and goal the partition will read, and resolves the goal field, so the partition reads a resident
    // field and locates nothing. No field means the body or its goal stands outside the network. Locating reads and
    // bakes the network domains the partition's steps will read.
    private void PrepareGoalField(Entry entry, CompiledBodyProducer producer, in FixedVector3 self) {
        entry.PreparedGoalField = null;
        if (
            (producer.Target is not { Source: BodyTargetSource.Navigated } target) ||
            (((uint)target.NavigationDomainIndex) >= ((uint)m_navigation.Count)) ||
            !m_navigation[target.NavigationDomainIndex].Tuning.Shared ||
            !TryNavigationGoal(entry: entry, goal: out var goal, target: in target, targetIndex: out _) ||
            !TryLocateGoalRead(cell: out var cell, domain: out var domain, domainIndex: target.NavigationDomainIndex, goal: in goal, goalCell: out var goalCell, goalDomain: out var goalDomain, self: in self)
        ) {
            return;
        }
        entry.PreparedGoalField = m_navigation.GoalField(goalCell: goalCell, goalDomain: goalDomain, profile: NavigationProfile(index: target.NavigationProfileIndex));
        entry.PreparedGoalFrom = (domain, cell);
    }
    // The walkable cell a position stands on in a domain's network: the domain's own cell first, else the nearest of its
    // network's.
    private bool TryLocateInNetwork(int network, in FixedVector3 position, out int domain, out int cell) {
        var own = m_navigation[network];

        if (own.CoversRouteHeight(position: in position) && own.TryCell(node: out cell, position: in position)) {
            domain = network;
            return true;
        }

        return m_navigation.TryLocate(cell: out cell, domain: out domain, network: network, position: in position);
    }
    // Advances a curve-follow arc position by its rate over the engine ticks a producer step covers (the remainder
    // carried in `remainder`, so a batched steering cadence travels what the same span of single ticks would), then
    // wraps (closed) or clamps (open) it back inside [0, totalLengthRaw] — the persisted state never grows past the
    // curve's own length, so it stays bounded across an arbitrarily long run and CompiledCurvatureSpline.EvaluateRaw's
    // own wrap/clamp is redundant with, never a substitute for, this one (EvaluateRaw wraps its ARGUMENT; this wraps the
    // STORED accumulator). The rate is Q16 and the arc Q32, so the integrated step widens by 16 bits; a step covers at
    // most a cadence of a second at a validated rate, and CurvatureSpline's own MaxCoordinate/MaxTangentChordRatio caps
    // bound totalLengthRaw far below 2^62, so the addition cannot overflow.
    private static long AdvanceCurveArc(long arcRaw, FixedQ4816 rate, ref long remainder, ulong stepTicks, long totalLengthRaw, bool closed) {
        var accumulator = FixedRateAccumulator.FromRemainder(remainder: remainder, ticksPerSecond: ((long)FixedTickConversion.TicksPerSecond));
        var stepRaw = (accumulator.Integrate(elapsedTicks: stepTicks, ratePerSecond: rate).Value << FixedQ4816.FractionBitCount);

        remainder = accumulator.Remainder;
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

        return next.FloorModulo(modulus: totalLengthRaw);
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

            var kitIndex = ResolveKitIndex(index: index);

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

    // The altitude a roaming producer holds: a free kit's authored base plus its per-index range sample; a grounded kit
    // starts at the authored spawn point or the world origin and lets contact geometry settle it.
    private static CompiledBodyProducer? SeedProducer(in FixedWorldKit kit) =>
        kit.Producers.Values.FirstOrDefault(predicate: producer => producer.RoamActive);
    // Seed a seat's steering-producer oscillator and altitude from its slot alone (no RNG) — the parameters
    // body.control producer:<name> steers by, parallel to the independently authored peer variation. A seat has no
    // producer spawn/color seeding — the definition spawns it and its profile colors it.
    private void SeedSeatSteeringProducer(int slot, bool resetPhase = true) {
        // A kit that declares no active roam shape (a bare or approach-only seat kit) has no oscillator/altitude to seed.
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
    // live Rebuild re-derives the kit/producer-dependent statics with resetPhase: false, which keeps the running roam
    // phase/activity so the retune does not jerk the crowd.
    private void SeedSimulated(int index, bool resetPhase = true) {
        var offset = (index - LocalSeatCount);

        var producer = SeedProducer(kit: m_kits[KitIndex(index: index)]);

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

        entry.ProducerState.PreferredAltitude = ((producer is null)
            ? FixedQ4816.Zero
            : PreferredAltitudeFor(
                kit: m_kits[KitIndex(index: index)],
                producer: producer,
                altitudeUnit: altitudeUnit
            )
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
    // The named producer in a kit's table, looked up again only when the table or the name changes.
    private static CompiledBodyProducer? ResolveProducer(Entry entry, string name, IReadOnlyDictionary<string, CompiledBodyProducer> table) {
        if (!ReferenceEquals(objA: entry.ProducerLookupTable, objB: table) || !ReferenceEquals(objA: entry.ProducerLookupName, objB: name)) {
            entry.ProducerLookupTable = table;
            entry.ProducerLookupName = name;
            entry.ProducerLookup = (table.TryGetValue(key: name, value: out var found) ? found : null);
        }
        return entry.ProducerLookup;
    }
    /// <summary>Returns why body <paramref name="index"/>'s producer stands down, the read a rule's
    /// <c>$producer:</c> channel answers.</summary>
    /// <param name="index">The 0-based body index.</param>
    /// <returns>The reasons, or <see cref="BodyProducerStandDown.None"/> for a running producer or an index that holds
    /// no active body.</returns>
    public BodyProducerStandDown ProducerStandDown(int index) => (((((uint)index) < ((uint)Capacity)) && m_entries[index].Active)
        ? m_entries[index].ProducerState.StandDown
        : BodyProducerStandDown.None);
    /// <summary>Sets or clears one reason body <paramref name="index"/>'s producer stands down. The producer stops on
    /// the body's next producer step while any reason holds, and starts again from where the body stands once none
    /// does (see <see cref="BodyProducerStandDown"/>).</summary>
    /// <param name="index">The 0-based body index.</param>
    /// <param name="reason">The reason to set or clear.</param>
    /// <param name="standDown">Whether the reason holds.</param>
    /// <returns><see langword="false"/> when the index holds no active body.</returns>
    /// <exception cref="InvalidOperationException">A kit partition is running: the reasons change only in a serial
    /// phase (a rule's firing, the engagement fold), so a partition's steer never sees them move.</exception>
    public bool SetProducerStandDown(int index, BodyProducerStandDown reason, bool standDown) {
        ThrowIfPartitioned();
        if ((((uint)index) >= ((uint)Capacity)) || !m_entries[index].Active) { return false; }
        ref var state = ref m_entries[index].ProducerState.StandDown;
        var next = (standDown ? (state | reason) : (state & ~reason));

        if (next != state) {
            state = next;
            // A rule's $producer: read is versioned by what it answers, so the slot is one the next derivation compares.
            m_factDirtySlots.Add(index: index);
        }
        return true;
    }
    /// <summary>Makes <paramref name="possessed"/> the bodies that stand down for
    /// <see cref="BodyProducerStandDown.Possessed"/>: each gains the reason, and every body that held it and is not
    /// listed loses it. Called once a tick by <see cref="WorldEngagement.FoldTick"/>, which derives the list from the
    /// grant table.</summary>
    /// <param name="possessed">The possessed body indices; duplicates are allowed.</param>
    internal void SyncPossession(List<int> possessed) {
        ThrowIfPartitioned();
        // Recomputed whole from the grant table each tick, so a restored or scope-replayed flag can never outlive the
        // application that set it.
        for (var index = 0; (index < Capacity); index++) {
            ref var state = ref m_entries[index].ProducerState.StandDown;

            if ((state & BodyProducerStandDown.Possessed) != BodyProducerStandDown.None) {
                state &= ~BodyProducerStandDown.Possessed;
                m_factDirtySlots.Add(index: index);
            }
        }
        foreach (var index in possessed) {
            _ = SetProducerStandDown(index: index, reason: BodyProducerStandDown.Possessed, standDown: true);
        }
    }
    // Run the named producer before motion. Live and Idle name no producer, and a producer that stands down runs as
    // none does. It writes only this body's own entry, so a partition may stage it.
    private void StageProducer(Entry entry, WorldBody body, int index, ulong stepTicks, ProducerWork work) {
        // A kit partition's steer fork is measured whole (RunKitPartitions), not body by body.
        using var measure = (work.Partitioned ? default : Puck.Hosting.HostFrameWork.Measure(Puck.Hosting.HostFrameWorkPart.TickProducers));

        body.SetFlockMovementDomain(domain: null);
        var kitIndex = ResolveKitIndex(index: index);

        if (
            (entry.ProducerState.StandDown != BodyProducerStandDown.None) ||
            (body.Source.ProducerName is not { } name) ||
            (ResolveProducer(entry: entry, name: name, table: m_kits[kitIndex].Producers) is not { } producer)
        ) {
            // Leaving the producer lane is itself a producer transition, so returning to it starts the producer again
            // from where the body stands (below). Do not let a prior route (or curve station) remain observable
            // through rule facts while the body is idle, live-driven, stood down, or names a producer that the
            // current kit does not carry.
            if (entry.ProducerState.ActiveProducerName is not null) {
                entry.ProducerState.ActiveProducerName = null;
                entry.ProducerState.ActiveProducerCurveIndex = -1;
                entry.ProducerState.ActiveProducerNavigationDomainIndex = -1;
                entry.ProducerState.CurveArcRaw = 0L;
                entry.ProducerState.CurveArcRemainder = 0L;
                entry.NavigationState.Clear(status: NavigationStatus.NoTarget);
                entry.ProducerState.FlockSeeded = false;
            }
            body.StageProducerIntent(intent: default);
            return;
        }

        // Selecting a producer starts it: a plain producer switch, or a same-name kit retune onto a different
        // curve row, rejoins the curve rather than resuming a foreign curve's station — see
        // BodyProducerState.ActiveProducerName's remarks.
        var curveIndex = -1;
        var navigationDomainIndex = -1;

        if (producer.Target?.Source is BodyTargetSource.CurveFollow) {
            curveIndex = producer.Target.Value.CurveIndex;
        } else if (producer.Target?.Source is BodyTargetSource.Navigated) {
            navigationDomainIndex = producer.Target.Value.NavigationDomainIndex;
        }

        if (
            !string.Equals(
            a: entry.ProducerState.ActiveProducerName,
            b: name,
            comparisonType: StringComparison.Ordinal
        ) ||
            (entry.ProducerState.ActiveProducerCurveIndex != curveIndex) ||
            (entry.ProducerState.ActiveProducerNavigationDomainIndex != navigationDomainIndex)
        ) {
            entry.ProducerState.ActiveProducerCurveIndex = curveIndex;
            entry.ProducerState.ActiveProducerName = name;
            // A curve follower joins its curve where it stands, so followers placed along one curve keep their spacing.
            entry.ProducerState.CurveArcRaw = ((((uint)curveIndex) < ((uint)m_curveRows.Count))
                ? m_curveRows[curveIndex].Compiled.NearestStationRaw(position: body.FixedPosition)
                : 0L);
            entry.ProducerState.CurveArcRemainder = 0L;
            entry.ProducerState.ActiveProducerNavigationDomainIndex = navigationDomainIndex;
            entry.NavigationState.Clear();
            entry.ProducerState.FlockSeeded = false;
        }

        if (producer.Flock is not null) {
            if (producer.Flock.MovementDomainIndex >= 0) {
                body.SetFlockMovementDomain(domain: m_navigation[producer.Flock.MovementDomainIndex]);
            }
            RefreshFlockPerception(
                entry: entry,
                index: index,
                producer: producer,
                stepTicks: stepTicks,
                work: work
            );
        }
        var sensors = ReadProducerSensors(
            selfIndex: index,
            entry: entry,
            currentTarget: entry.ProducerState.AcquiredTarget,
            self: body.FixedPosition,
            producer: producer,
            work: work,
            stepTicks: stepTicks
        );

        if (producer.Flock is not null) {
            sensors = sensors with {
                FlockDesired = BlendFlockPreference(
                index,
                entry,
                producer.Flock,
                sensors.Candidate
            ),
            };
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

    /// <summary>Samples what each body that can advance this tick reads of the field regions, before
    /// <see cref="AdvanceSimulated"/>/<see cref="AdvanceSeats"/>: its medium free surface at its coupled lattice cell
    /// (the same coupling <see cref="Puck.Physics.Fields.FieldLattice.TryBodyCellOf"/> resolves; <see langword="null"/>
    /// outside every medium or over a zero-value medium cell), so a medium hold's law reads this tick's surface, and
    /// the travel resistance of the first region containing it (zero outside every region). A parked rigid body and an
    /// idle sleeper read neither while they rest and are skipped; one that wakes this tick samples at its pose then,
    /// before its first staging (<see cref="EnsureFieldInputs"/>), so no advance reads an answer from an earlier pass.</summary>
    /// <param name="regionResistance">The travel resistance of each field region this tick, by region index, read once
    /// per region; a region authoring none reads zero.</param>
    /// <exception cref="ArgumentException"><paramref name="regionResistance"/> does not hold one value per field
    /// region.</exception>
    public void SampleFieldInputs(ReadOnlySpan<FixedQ4816> regionResistance) {
        if (regionResistance.Length != m_fieldRegions.Length) {
            throw new ArgumentException(message: "One travel resistance per field region is required.", paramName: nameof(regionResistance));
        }
        if (m_regionResistance.Length != regionResistance.Length) { m_regionResistance = new FixedQ4816[regionResistance.Length]; }
        if (m_fieldInputPasses.Length != m_entries.Length) { m_fieldInputPasses = new ulong[m_entries.Length]; }
        regionResistance.CopyTo(destination: m_regionResistance);
        m_fieldInputPass++;
        foreach (var index in m_awakeBodies) {
            if (m_entries[index].Body is { Asleep: false } body) { SampleBodyFieldInputs(index: index, body: body); }
        }
    }
    // Samples a body this pass skipped (an idle sleeper that woke) before it stages or advances.
    private void EnsureFieldInputs(int index, WorldBody body) {
        if ((m_fieldInputPass != 0UL) && (m_fieldInputPasses[index] != m_fieldInputPass)) { SampleBodyFieldInputs(index: index, body: body); }
    }
    private void SampleBodyFieldInputs(int index, WorldBody body) {
        Puck.Physics.Fields.FixedFieldSurface? surface = null;
        var resistance = FixedQ4816.Zero;
        var contained = false;

        m_fieldInputPasses[index] = m_fieldInputPass;
        for (var regionIndex = 0; (regionIndex < m_fieldRegions.Length); regionIndex++) {
            var lattice = m_fieldRegions[regionIndex].Lattice;

            if ((surface is null) && (lattice.MediumSurface(position: body.FixedPosition) is { } candidate)) { surface = candidate; }
            if (!contained && lattice.TryCellOf(body.FixedPosition, out _)) {
                resistance = m_regionResistance[regionIndex];
                contained = true;
            }
            if (contained && (surface is not null)) { break; }
        }
        body.SetMediumSurface(surface: surface);
        body.SetTravelResistance(resistance: resistance);
    }
    /// <summary>Advances every active seat body by one exact simulation tick: a producer-sourced seat gets this tick's
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
                EnsureFieldInputs(body: body, index: slot);
                if (!body.TryBeginOrdinaryAdvance(stepStartEngineTick: stepStartEngineTick)) {
                    engageEdges[slot] = false;
                    continue;
                }

                StageProducer(
                    body: body,
                    entry: entry,
                    index: slot,
                    stepTicks: stepTicks,
                    work: m_serialWork
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
                    effectTargets: targets,
                    engageProbeOrdinal: probe,
                    entityIndex: slot,
                    outputs: m_outputs,
                    scratch: m_stepScratch,
                    stepTicks: stepTicks,
                    tick: tick
                );
                RecordFlockMotion(body: body, work: m_serialWork);
            }
        }
    }

    // The tick's one gravity solve. Gathered in ENTITY ORDER so the solver's input order is the population's own
    // stable order: an approximate solver's answer depends on the order its tree is built in, so activation history
    // must not reach it.
    private bool SolveGravity() {
        if (m_gravityField is not { IsActive: true } gravity) {
            return false;
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
                Mass: ((((uint)KitIndex(index: index)) < ((uint)m_kits.Length))
                ? m_kits[KitIndex(index: index)].Mass
                : FixedQ4816.Zero),
                Position: body.FixedPosition
            ));
        }

        gravity.Solve(targets: m_gravityTargets);
        var woke = false;
        // The field's contact revision says nothing about solved acceleration. Reuse its already-required,
        // entity-ordered target pass; a static/default-gravity world pays no sleeping-body scan.
        foreach (var target in m_gravityTargets) {
            var body = m_entries[target.EntityIndex].Body!;
            FixedVector3? previous;
            if (body.Asleep) { previous = body.IdleSleepGravity; }
            else if (!m_sleepGravity.TryGetValue(key: target.EntityIndex, value: out previous)) { continue; }

            if (body.SolvedGravity != previous) {
                body.WakeUp();
                woke = true;
            }
        }
        return woke;
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
                val2: (((checked((ulong)(ordinal + 1)) * period) + checked((((ulong)count) - 1UL))) / checked((ulong)count))
            )
        );
    }
    private static bool CadenceDue(ulong period, ulong stepTicks, ref ulong elapsed, ref ulong remaining, out ulong elapsedTicks) {
        if (period == 0UL) {
            elapsedTicks = stepTicks;
            return true;
        }

        elapsed = checked((elapsed + stepTicks));
        if (remaining > stepTicks) {
            remaining -= stepTicks;
            elapsedTicks = 0UL;
            return false;
        }

        // Due on the first authority tick at or past this slot of the body's cadence grid. The next slot stays on that
        // grid rather than restarting from this tick, so a cadence that is no whole number of authority ticks refreshes
        // at its authored rate on average, each refresh covering the whole ticks it actually batched.
        var overshoot = (stepTicks - remaining);

        elapsedTicks = elapsed;
        elapsed = 0UL;
        remaining = (period - (overshoot % period));
        return true;
    }

    /// <summary>Advances every active simulated stand-in by one sub-step: a named producer runs before motion, then
    /// every peer body integrates. A live <c>body.fly</c> tape or
    /// a submitted intent overrides the producer per the merge rule; an <see cref="IntentSource.Idle"/> peer holds
    /// still between tape segments yet its tapes still play. The bodies whose step is local advance first, in kit
    /// partitions forked across <paramref name="jobs"/>, and every other body then advances serially in index order;
    /// the result is the same at any worker count. The local seats are advanced separately by
    /// <see cref="AdvanceSeats"/>.</summary>
    /// <remarks>Runs first, before any body (peer or seat) advances this tick, so an attached solid placement's
    /// colliders (<see cref="WorldColliderSet.RefreshAttached"/>) are refreshed exactly once and every body's push
    /// this tick resolves against the same snapshot. Both contact providers sample size; attached body poses are
    /// admitted by the analytic provider. Scale reads the current authority definition, never the definition
    /// retained when geometry compiled, whose arena may have been replaced.</remarks>
    /// <param name="definition">The authority's current live definition and state binding.</param>
    /// <param name="tick">The authority tick being advanced.</param>
    /// <param name="stepTicks">The sub-step duration in engine ticks.</param>
    /// <param name="stepStartEngineTick">The engine-time boundary at the start of the sub-step.</param>
    /// <param name="jobs">The pool the kit partitions fork across, or <see langword="null"/> to run them on the calling
    /// thread; the result is the same either way.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> is zero.</exception>
    public void AdvanceSimulated(WorldDefinition definition, ulong tick, ulong stepTicks, ulong stepStartEngineTick, Puck.Hosting.Jobs.JobPool? jobs = null) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);
        SimulatedBodyVisits = 0;
        m_rigidTickStepTicks = stepTicks;

        if (MovingSolids is { } attached) { attached.RefreshAttached(definition: definition, engineTick: stepStartEngineTick, population: this, tick: tick); }
        var stepBodies = PrepareBodyStep();

        if (MovingSolids is { } colliders) {
            // Every body standing on a moving attached solid rides it before any body advances, seats included.
            foreach (var index in stepBodies) {
                if (
                    m_entries[index].Active &&
                    (m_entries[index].Body is { } rider)
                ) {
                    rider.RideAttachedSupport(field: colliders);
                }
            }
        }

        // A solved-field change can wake a member after the first snapshot, before integration begins.
        if (SolveGravity()) { stepBodies = PrepareBodyStep(); }
        FreezeFlockImage();
        m_navigation.BeginStep();

        // Read once for the whole tick: every sleeping body below compares against the same value, so a solid/lattice
        // change mid-loop (nothing here writes either) can never wake half a tick's peers and not the other half.
        var contactFieldVersion = ContactFieldVersion;

        // The kit advance split (WorldPopulation.Partitions.cs): the bodies whose step is local advance first, in fixed
        // partitions of slots over the frozen image, and every other body then advances here, serially, in index order.
        PlanKitStep(contactFieldVersion: contactFieldVersion, stepBodies: stepBodies, stepStartEngineTick: stepStartEngineTick, stepTicks: stepTicks);
        RunKitPartitions(contactFieldVersion: contactFieldVersion, jobs: jobs, tick: tick);
        foreach (var index in m_serialKitBodies) {
            AdvanceSerialBody(contactFieldVersion: contactFieldVersion, index: index, stepStartEngineTick: stepStartEngineTick, stepTicks: stepTicks, tick: tick);
        }
    }
    // One body's whole step inside the kit transaction: its cadence, its producer and its advance, as a body whose step
    // may write state, reach other bodies or emit outputs takes it.
    private void AdvanceSerialBody(int index, ulong tick, ulong stepTicks, ulong stepStartEngineTick, ulong contactFieldVersion) {
        var entry = m_entries[index];

        // Network peers AND inhabitants advance here (both own their body and run a deterministic producer until a
        // transport/possession supplies intents). An inactive entry has no body.
        if (
            !entry.Active ||
            (entry.Kind == PopulationKind.LocalSeat) ||
            (entry.Body is not { } player) ||
            !TryPlanAutonomy(contactFieldVersion: contactFieldVersion, entry: entry, index: index, player: player, step: out var step, stepStartEngineTick: stepStartEngineTick, stepTicks: stepTicks)
        ) {
            return;
        }

        if (step.SteeringDue) {
            StageProducer(
                body: player,
                entry: entry,
                index: index,
                stepTicks: step.SteeringTicks,
                work: m_serialWork
            );
            SeedSteering(entry: entry, motionPeriod: step.MotionPeriod, player: player, steeringPeriod: step.SteeringPeriod, work: m_serialWork);
        }
        if (!step.Moves) {
            return;
        }
        if (
            !step.SteeringDue &&
            entry.AutonomyState.SteeringSeeded
        ) {
            player.StageProducerIntent(intent: in entry.AutonomyState.SteeringIntent);
        }
        var targets = ReadEffectTargets(
            selfIndex: index,
            entry: entry,
            self: player.FixedPosition
        );

        player.Advance(
            tick: tick,
            stepTicks: step.MotionTicks,
            entityIndex: index,
            effectTargets: targets,
            outputs: m_outputs,
            scratch: m_stepScratch,
            sleepAfterTicks: m_sleepAfterTicks,
            contactFieldVersion: contactFieldVersion
        );
        WorldBodyWork.Process.Add(amount: 1, kind: WorldBodyWork.Advances);
        RecordFlockMotion(body: player, work: m_serialWork);
    }
    // What a non-seat body does this tick under its autonomy cadence: whether its producer runs and over how many
    // engine ticks, and whether it moves and over how many.
    private readonly record struct AutonomyStep(bool SteeringDue, ulong SteeringTicks, ulong SteeringPeriod, ulong MotionPeriod, bool Moves, ulong MotionTicks);

    // Settles a non-seat body's sleep, field inputs and cadence for the tick, the same for a serial body and a kit
    // partition's: false for a sleeper that nothing wakes. A body whose motion is not due is marked deferred, and one
    // whose ordinary advance is not admitted restarts its motion cadence and does not move.
    private bool TryPlanAutonomy(Entry entry, WorldBody player, int index, ulong stepTicks, ulong stepStartEngineTick, ulong contactFieldVersion, out AutonomyStep step) {
        step = default;
        // An asleep body that wakes for nothing this tick costs nothing beyond this check — no staging, no cadence
        // bookkeeping, no Advance. See WorldBody.Sleep.cs.
        if (player.TryDeferSleepingAdvance(contactFieldVersion: contactFieldVersion)) {
            return false;
        }
        EnsureFieldInputs(body: player, index: index);

        var kit = m_kits[KitIndex(index: index)];
        // A possessed body is driven by a participant, so it moves at the full rate; a producer that stands down has
        // no steering to batch.
        var standDown = entry.ProducerState.StandDown;
        var locallyAutonomous = (
            !entry.IsRemoteHuman &&
            !player.Source.IsLive &&
            !player.HasMotionTape &&
            !player.RequiresFullRateAutonomy &&
            ((standDown & BodyProducerStandDown.Possessed) == 0)
        );
        var motionPeriod = (locallyAutonomous
            ? kit.AutonomousMotionTicks
            : 0UL
        );
        var steeringPeriod = ((!entry.IsRemoteHuman && Producing(entry: entry, player: player))
            ? kit.AutonomousSteeringTicks
            : 0UL
        );
        ref var autonomy = ref entry.AutonomyState;
        var ordinal = (index - LocalSeatCount);

        BindCadence(
            motionPeriod,
            ordinal,
            PeerCapacity,
            ref autonomy.MotionPeriodTicks,
            ref autonomy.MotionElapsedTicks,
            ref autonomy.MotionRemainingTicks
        );
        BindCadence(
            steeringPeriod,
            ordinal,
            PeerCapacity,
            ref autonomy.SteeringPeriodTicks,
            ref autonomy.SteeringElapsedTicks,
            ref autonomy.SteeringRemainingTicks
        );

        var steeringDue = CadenceDue(
            elapsed: ref autonomy.SteeringElapsedTicks,
            elapsedTicks: out var steeringTicks,
            period: steeringPeriod,
            remaining: ref autonomy.SteeringRemainingTicks,
            stepTicks: stepTicks
        );
        var moves = CadenceDue(
            elapsed: ref autonomy.MotionElapsedTicks,
            elapsedTicks: out var motionTicks,
            period: motionPeriod,
            remaining: ref autonomy.MotionRemainingTicks,
            stepTicks: stepTicks
        );

        if (!moves) {
            player.DeferOrdinaryAdvance();
            if (motionPeriod != 0UL) {
                m_serialWork.Autonomy = m_serialWork.Autonomy with { MotionDeferred = (m_serialWork.Autonomy.MotionDeferred + 1) };
            }
        } else {
            if (motionPeriod != 0UL) {
                m_serialWork.Autonomy = m_serialWork.Autonomy with { MotionUpdates = (m_serialWork.Autonomy.MotionUpdates + 1) };
            }
            if (!player.TryBeginOrdinaryAdvance(stepStartEngineTick: checked(((stepStartEngineTick + stepTicks) - motionTicks)))) {
                autonomy.MotionElapsedTicks = 0UL;
                autonomy.MotionRemainingTicks = motionPeriod;
                moves = false;
            }
        }
        step = new AutonomyStep(SteeringDue: steeringDue, SteeringTicks: steeringTicks, SteeringPeriod: steeringPeriod, MotionPeriod: motionPeriod, Moves: moves, MotionTicks: motionTicks);
        return true;
    }
    // Keeps a batched body's staged steering for the motion steps between its steering refreshes.
    // Whether the body's producer runs: its source names one and nothing stands it down.
    private static bool Producing(Entry entry, WorldBody player) => (player.Source.IsProducer && (entry.ProducerState.StandDown == BodyProducerStandDown.None));
    private static void SeedSteering(Entry entry, WorldBody player, ulong steeringPeriod, ulong motionPeriod, ProducerWork work) {
        ref var autonomy = ref entry.AutonomyState;

        if (
            Producing(entry: entry, player: player) &&
            ((steeringPeriod != 0UL) || (motionPeriod != 0UL))
        ) {
            autonomy.SteeringIntent = player.StagedProducerIntent;
            autonomy.SteeringSeeded = true;
        } else {
            autonomy.SteeringIntent = default;
            autonomy.SteeringSeeded = false;
        }
        if (steeringPeriod != 0UL) {
            work.Autonomy = work.Autonomy with { SteeringUpdates = (work.Autonomy.SteeringUpdates + 1) };
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
    /// <param name="actionContinuity">Named action registers carried without assuming matching ordinals. Input resets.
    /// They are the body's own state, so the size, kit and look they select under the installed document are the
    /// body's before its segment is swept and before it first advances here (<see cref="SyncBodyCells"/>).</param>
    /// <param name="continuum">The optional already-evaluated adjacency segment to resolve before this body may
    /// participate in another ordinary simulation step. Portal and spawn arrivals omit it, and so does a traveler whose
    /// pose is another body's. The sweep meets this world's ground and the neighbour's, and none of the neighbour's
    /// bodies (<see cref="LandingArrival"/>).</param>
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

        m_actionStateLane.Input?.ResetBody(body: slot);
        body.ResetArrivalInput();
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
            // The list is allocated only when something can read the line: a register this world's closed set
            // refuses is a silent authored disagreement between two worlds otherwise.
            var settled = ((NarrationHub is { HasNarrationSink: true })
                ? new List<string>()
                : null
            );

            body.ApplyTransferActionContinuity(
                continuity: actionContinuity,
                settledToInitial: settled
            );

            if (settled is { Count: > 0 }) {
                NarrationHub!.Narrate(
                    channel: "body.arrival",
                    text: $"[body.arrival: body {slot} settled {string.Join(separator: ", ", values: settled)} to the authored initial — this world's closed set does not admit the carried value]"
                );
            }
        }
        // The document the population last synced against is the installed one: every install syncs.
        if (m_bodyCellDefinition is { } installed) { _ = SyncBodyCells(definition: installed); }
        if (continuum is { } trajectory) {
            BeginAdjacencyContacts();
            LandingArrival = true;
            try {
                body.ApplyContinuumTrajectory(
                    destinationCompletedEngineTick: destinationCompletedEngineTick,
                    entityIndex: slot,
                    trajectory: in trajectory
                );
            } finally {
                LandingArrival = false;
            }
        }

        return true;
    }
    /// <summary>Sets an already-active seat, freshly embodied by the destination's own join, down at an explicit pose:
    /// the posed half of a transfer that carries a seat to a spawn point (<see cref="WorldTransferArrival.Posed"/>).
    /// The body keeps the destination's own kit, motion program and initial action registers, stands still, and is
    /// hard-teleported (<see cref="WorldBody.Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/>) level at the
    /// pose. A no-op returning <see langword="false"/> for an inactive slot.</summary>
    /// <param name="slot">The seat index (0-based) the destination's join just activated.</param>
    /// <param name="position">The arrival position, fixed point.</param>
    /// <param name="yawRadians">The arrival yaw, fixed-point radians.</param>
    /// <returns><see langword="true"/> when the seat was active and its body was posed.</returns>
    public bool ApplyPosedArrival(int slot, FixedVector3 position, FixedQ4816 yawRadians) {
        var entry = m_entries[slot];

        if (
            !entry.Active ||
            (entry.Body is not { } body)
        ) {
            return false;
        }

        m_actionStateLane.Input?.ResetBody(body: slot);
        body.ResetArrivalInput();
        body.Pose(
            position: position,
            yawRadians: yawRadians,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );

        return true;
    }
    /// <summary>Applies entity-directed effect outputs after every body has advanced, then exposes player-keyed durable
    /// writes for the completed tick.</summary>
    public void CompleteStep(ulong tick) {
        m_adjacencyTick = (tick + 1);
        foreach (var output in m_outputs.Effects) {
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
        m_outputs.Effects.Clear();

        m_durableStateOutputs.Clear();
        for (var index = 0; (index < m_entries.Length); index++) {
            m_entries[index].Body?.TakeDurableStateOutputs(
                entityIndex: index,
                outputs: m_durableStateOutputs,
                tick: tick
            );
        }

        // A body whose sweep is refused stays put; it is never silent. Each transition, refused or recovered, is
        // narrated once, after every body has moved this tick, and body.where carries the refusal while it holds.
        for (var index = 0; (index < m_entries.Length); index++) {
            if (
                (m_entries[index].Body is { } body) &&
                body.TryTakeSweepTransition(refusal: out var refusal) &&
                (NarrationHub is { HasNarrationSink: true })
            ) {
                NarrationHub.Narrate(
                    channel: "body.sweep",
                    text: ((refusal == ContactRefusal.None)
                        ? $"[body.sweep: body {index} recovered at tick {tick}]"
                        : $"[body.sweep: body {index} refused at tick {tick} ({refusal}): the step's sweep could not run, so the body did not move]")
                );
            }

            m_entries[index].Body?.EndSweepTick();

            if (m_entries[index].Body?.ProducerFault is { } fault) {
                if (m_producerFaults.Set(key: index, reason: fault) && (NarrationHub is { HasNarrationSink: true })) {
                    NarrationHub.Narrate(
                        channel: "body.producer",
                        text: $"[body.producer: body {index} faulted at tick {tick}: {fault}; the run was refused and the body holds still until its producer runs clean]"
                    );
                }
            } else if (
                m_producerFaults.Clear(key: index) &&
                (m_entries[index].Body is not null) &&
                (NarrationHub is { HasNarrationSink: true })
            ) {
                NarrationHub.Narrate(
                    channel: "body.producer",
                    text: $"[body.producer: body {index} recovered at tick {tick}]"
                );
            }
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
    /// <summary>Names the action registers of a captured body state so another world can restore them without
    /// assuming the two documents assigned the same ordinals.</summary>
    public WorldTransferActionContinuity NameTransferActionContinuity(int slot, WorldBodyTransferState state) {
        var definitions = m_kits[ResolveKitIndex(index: slot)].ActionState;
        var count = Math.Min(
            val1: definitions.Length,
            val2: state.ActionState.Length
        );
        var registers = new WorldTransferActionRegister[count];

        for (var index = 0; (index < count); index++) {
            var raw = state.ActionState[index];

            registers[index] = new WorldTransferActionRegister(
                Name: definitions[index].Name,
                Kind: definitions[index].Kind,
                Value: ((definitions[index].Kind == ActionStateKind.Counter)
                ? FixedQ4816.FromRawBits(value: raw)
                : FixedQ4816.Zero),
                TimerTicks: ((definitions[index].Kind == ActionStateKind.Timer)
                ? unchecked((ulong)Math.Max(
                    val1: 0L,
                    val2: raw
                ))
                : 0UL)
            );
        }

        return new WorldTransferActionContinuity(
            Registers: registers
        );
    }
}
