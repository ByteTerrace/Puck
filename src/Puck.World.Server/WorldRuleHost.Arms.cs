using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The effect arms only the world can fire.</summary>
/// <remarks>
/// <para>Every world arm but the identity-fact write leaves the arena, so it is queued and called with
/// <see cref="EffectFiring.Preflight"/> set before the firing's scope commits, where it validates and does nothing
/// outward. The arms of one firing are preflighted as a sequence: what one would do is what the next is judged
/// against.</para>
/// <para>A document-row arm is <see cref="EffectNeeds.Transactional"/>. The firing's rows compose, in order, into one
/// <see cref="WorldMutation.Batch"/> stamped <see cref="WorldPrincipal.World"/>. Each preflight composes the batch
/// so far against the document the firing proposes, so a row is judged against what the rows before it leave. Once
/// every arm has passed, the whole batch is prepared through the ordinary mutation door: every gate that can refuse
/// it runs while the firing can still rewind. The commit adopts the proposed document and installs the prepared
/// batch, and neither can refuse, so a firing's rows land with its arena writes or not at all.</para>
/// <para>A cue, a body motion, an impulse, a field paint, a pose and a save are delivered after the commit. A
/// delivery that refuses is counted and undoes nothing.</para>
/// </remarks>
public sealed partial class WorldRuleHost {
    // What one firing's preflights carry from each arm to the next: the document rows it has queued so far, which
    // are the unit its commit installs, and the velocity its impulses would leave each rigid body at. Preflighting
    // clears both.
    private readonly List<WorldMutation> m_documentArms = [];
    private readonly Dictionary<int, FixedVector3> m_preflightRigidVelocity = [];
    private WorldArenaPublication? m_proposedPublication;
    // The firing's document rows, past every gate and waiting on the commit.
    private WorldPreparedMutation? m_preparedDocument;

    /// <inheritdoc/>
    public bool Apply(in Mutation mutation, out EffectRefusal refusal) => Host.ArenaHost.Apply(
        mutation: in mutation,
        refusal: out refusal
    );
    /// <inheritdoc/>
    public bool TryTransform(ArenaTransform transform, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) => Host.ArenaHost.TryTransform(
        binding: in binding,
        moved: out moved,
        refusal: out refusal,
        transform: transform
    );
    // Committed, Preflighting and Fire implement IEffectHost members that carry default bodies. Narrowing one compiles
    // clean and rebinds to the interface's own implementation, which flushes no identity fact and refuses every
    // world arm as unbound, so their accessibility is load-bearing.
    /// <inheritdoc/>
    public void Committed(int scope) => FlushIdentityFacts();
    /// <inheritdoc/>
    public void Preflighting() {
        // A unit prepared for a firing that then did not commit is released here.
        m_preparedDocument?.Dispose();
        m_preparedDocument = null;
        m_documentArms.Clear();
        m_preflightRigidVelocity.Clear();
        m_proposedPublication = null;
    }
    /// <inheritdoc/>
    public bool PrepareTransactional(in EffectFiring firing, out EffectRefusal refusal) {
        refusal = EffectRefusal.None;

        if (m_documentArms.Count == 0) {
            return true;
        }
        if (!Host.Document.TryPrepareMutation(
            current: Proposed().Definition,
            denied: out _,
            engineTick: firing.EngineTick,
            mutation: DocumentUnit(),
            preMetered: false,
            prepared: out var prepared,
            reason: out var reason,
            tick: firing.Tick
        )) {
            return Refuse(
                code: RuleEffectRefusal.MutationRejected,
                reason: reason,
                refusal: out refusal
            );
        }

        m_preparedDocument = prepared;

        return true;
    }
    /// <inheritdoc/>
    public void CommitTransactional(in EffectFiring firing) {
        if (m_preparedDocument is not { } prepared) {
            return;
        }

        m_preparedDocument = null;
        m_documentArms.Clear();

        // The unit was composed against the proposed document, and its install re-seeds the arena from what it
        // composed, so that proposal is what is installed under it. The scope has committed with nothing written
        // since, so the arena holds exactly what the proposal read.
        _ = Host.AdoptPublication(
            publication: Proposed(),
            reconcile: false
        );
        m_proposedPublication = null;
        Host.Document.InstallPrepared(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            prepared: prepared
        );
    }

    // The proposal is taken once per firing: its preflights run after the firing's last arena write.
    private WorldArenaPublication Proposed() => (m_proposedPublication ??= Host.ProposePublication());
    // One row travels as itself, so its journal entry is the mutation it always was; several travel as one batch.
    private WorldMutation DocumentUnit() => ((m_documentArms.Count == 1)
        ? m_documentArms[0]
        : new WorldMutation.Batch(
            Mutations: [.. m_documentArms],
            Principal: WorldPrincipal.World
        )
    );
    /// <inheritdoc/>
    public bool Fire(ICompiledFact effect, in EffectFiring firing, out EffectRefusal refusal) {
        switch (effect) {
            case WorldCueEffect cue:
                if (!firing.Preflight) {
                    FireGameplayCue(
                        effect: cue,
                        tick: firing.Tick
                    );
                }

                refusal = EffectRefusal.None;

                return true;
            case WorldBodyMotionEffect body:
                return FireBodyMotion(
                    effect: body,
                    preflight: firing.Preflight,
                    refusal: out refusal
                );
            case WorldRigidImpulseEffect impulse:
                return FireRigidImpulse(
                    effect: impulse,
                    preflight: firing.Preflight,
                    refusal: out refusal
                );
            case WorldPaintFieldEffect paint:
                return FireFieldPaint(
                    effect: paint,
                    preflight: firing.Preflight,
                    refusal: out refusal
                );
            case WorldPoseCellEffect cellPose:
                return FirePoseCell(cellPose, firing.Preflight, out refusal);
            case WorldPoseEffect pose:
                return FirePose(
                    effect: pose,
                    preflight: firing.Preflight,
                    refusal: out refusal
                );
            case WorldIdentityFactEffect fact:
                return FireIdentityFact(
                    effect: fact,
                    refusal: out refusal
                );
            case WorldDocumentEffect document:
                return FireDocument(
                    effect: document,
                    firing: in firing,
                    refusal: out refusal
                );
            default:
                refusal = IEffectHost.Unbound(effect: effect);

                return false;
        }
    }

    private static bool Refuse<TRefusal>(TRefusal code, string reason, out EffectRefusal refusal) where TRefusal : unmanaged, Enum {
        refusal = EffectRefusal.Of(
            code: code,
            reason: reason
        );

        return false;
    }
    // A live body key resolves through the one cell-reference carrier every dynamic key travels in; an indirection
    // naming no cell reads the invalid key, which names no body.
    private int ResolveBodyKeyIndex(CellKey key, CompiledCellRef? keyFrom) {
        var resolved = RuleReads.ResolveKey(
            keyFrom: keyFrom,
            literal: key,
            reader: this
        );

        return ((RuleReads.TryKeyIndex(
            catalog: Host.Arena.Catalog,
            index: out var index,
            key: resolved
        ) && (index >= 0L) && (index <= int.MaxValue))
            ? ((int)index)
            : -1
        );
    }
    private void FireGameplayCue(WorldCueEffect effect, ulong tick) {
        int? body = null;

        if (effect.Key != default) {
            var index = ResolveBodyKeyIndex(
                key: effect.Key,
                keyFrom: effect.KeyFrom
            );

            if (Host.Body(index: index) is not null) {
                body = index;
            }
        }

        var cue = new WorldGameplayCue(
            Body: body,
            Name: effect.Cue,
            Payload: effect.Payload,
            Tick: tick
        );

        Host.GameplayCueTap?.Invoke(obj: cue);
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(
                channel: "world.cue",
                text: $"[world.cue: {cue.Name} tick={tick}{((body is { } index)
                ? $" body:{index}"
                : string.Empty)}]"
            );
        }
    }
    private bool FireBodyMotion(WorldBodyMotionEffect effect, bool preflight, out EffectRefusal refusal) {
        var bodyIndex = ResolveBodyKeyIndex(
            key: effect.Key,
            keyFrom: effect.KeyFrom
        );

        if (Host.Body(index: bodyIndex) is not { } body) {
            return Refuse(
                code: WorldRuleEffectRefusal.BodyInactive,
                reason: $"body '{bodyIndex}' is inactive",
                refusal: out refusal
            );
        }

        refusal = EffectRefusal.None;

        var operation = effect.Action;

        if (operation.Operation == BodyMotionOp.Designate) {
            if (!Host.Population.TryResolveTargetRegister(
                index: out var registerIndex,
                name: operation.Register!
            )) {
                return Refuse(
                    code: WorldRuleEffectRefusal.BodyTargetInvalid,
                    reason: $"target register '{operation.Register}' is unavailable",
                    refusal: out refusal
                );
            }
            if (operation.Designation == WorldBodyDesignationKind.Clear) {
                if (!preflight) {
                    Host.Population.SetDesignation(
                        bodyIndex: bodyIndex,
                        registerIndex: registerIndex,
                        target: WorldTargetDesignation.None
                    );
                }

                return true;
            }

            var targetIndex = ResolveBodyKeyIndex(
                key: operation.TargetKey,
                keyFrom: operation.TargetKeyFrom
            );

            if (
                (targetIndex == bodyIndex) ||
                (Host.Body(index: targetIndex) is null)
            ) {
                return Refuse(
                    code: WorldRuleEffectRefusal.BodyTargetInvalid,
                    reason: ((targetIndex == bodyIndex)
                        ? $"body:{bodyIndex} cannot designate itself"
                        : $"target body:{targetIndex} is inactive"),
                    refusal: out refusal
                );
            }
            if (preflight) {
                return true;
            }

            _ = Host.Document.ApplyDesignationCore(
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0,
                designation: new WorldDesignation(
                    EntityIndex: bodyIndex,
                    Register: operation.Register!,
                    Subject: GrantSubject.Body(index: targetIndex)
                ),
                knownSubject: true,
                principal: WorldPrincipal.World
            );

            return true;
        }
        if (preflight) {
            return true;
        }

        // A world-authored kinematic effect has no affecting body: passing the recipient would mint a false
        // Affected fact on its next action pass.
        _ = body.ApplyTargetedEffect(
            instruction: new CompiledBodyInstruction(
                Direction: operation.Direction,
                DurationTicks: operation.DurationTicks,
                Operation: operation.Operation,
                StateSlot: -1,
                Value: operation.Value
            ),
            sourceIndex: -1
        );

        return true;
    }
    private bool FireRigidImpulse(WorldRigidImpulseEffect effect, bool preflight, out EffectRefusal refusal) {
        var targetIndex = ResolveWorldBodyRef(bodyRef: effect.Target);

        if (Host.Body(index: targetIndex) is not { } target) {
            return Refuse(
                code: WorldRuleEffectRefusal.BodyInactive,
                reason: $"applyRigidImpulse key resolves to no active body (index {targetIndex})",
                refusal: out refusal
            );
        }
        if (!target.IsRigid) {
            return Refuse(
                code: WorldRuleEffectRefusal.RigidBodyRequired,
                reason: $"body:{targetIndex} carries no rigid kit facet — see world.rigid",
                refusal: out refusal
            );
        }

        var headingIndex = ResolveWorldBodyRef(bodyRef: effect.Heading);

        if (Host.Body(index: headingIndex) is not { } heading) {
            return Refuse(
                code: WorldRuleEffectRefusal.BodyInactive,
                reason: $"applyRigidImpulse headingKey resolves to no active body (index {headingIndex})",
                refusal: out refusal
            );
        }

        var magnitudeFact = effect.Magnitude.Read(reader: this);

        if (
            magnitudeFact.IsAbsent ||
            magnitudeFact.IsForever
        ) {
            return Refuse(
                code: WorldRuleEffectRefusal.RigidImpulseOutOfRange,
                reason: "applyRigidImpulse magnitude cell is absent",
                refusal: out refusal
            );
        }

        refusal = EffectRefusal.None;

        var magnitude = FixedQ4816.FromRawBits(value: magnitudeFact.ToRaw(kind: CellKind.Fixed));
        var impulse = (heading.FixedOrientation.Rotate(vector: RigidImpulseLocalForward) * magnitude);
        var accepted = true;

        // A preflighted impulse lands on the velocity the firing's earlier impulses would leave the body at, so two
        // that each fit and together pass the ceiling are refused before the commit, where the firing still rewinds.
        if (preflight) {
            accepted = target.TryProjectRigidImpulse(
                impulse: impulse,
                projected: out var projected,
                velocity: (m_preflightRigidVelocity.TryGetValue(
                    key: targetIndex,
                    value: out var pending
                )
                    ? pending
                    : target.RigidVelocity),
                velocityCeiling: Host.Population.RigidVelocityCeiling
            );

            if (accepted) {
                m_preflightRigidVelocity[targetIndex] = projected;

                return true;
            }
        } else {
            accepted = target.TryApplyRigidImpulse(
                impulse: impulse,
                velocityCeiling: Host.Population.RigidVelocityCeiling
            );
        }
        if (!accepted) {
            return Refuse(
                code: WorldRuleEffectRefusal.RigidImpulseOutOfRange,
                reason: $"body:{targetIndex} impulse is not representable or would exceed the world's declared speed ceiling ({((double)Host.Population.RigidVelocityCeiling):0.###})",
                refusal: out refusal
            );
        }

        return true;
    }
    private bool FireFieldPaint(WorldPaintFieldEffect effect, bool preflight, out EffectRefusal refusal) {
        var paint = effect.Paint;

        if (Host.Population.Fields is not { } lattice) {
            return Refuse(
                code: WorldRuleEffectRefusal.FieldUnavailable,
                reason: "no live field lattice is installed",
                refusal: out refusal
            );
        }
        if (!lattice.TryFieldIndex(
            field: out _,
            name: paint.Field
        )) {
            return Refuse(
                code: WorldRuleEffectRefusal.FieldUnavailable,
                reason: $"live field '{paint.Field}' is unavailable",
                refusal: out refusal
            );
        }

        refusal = EffectRefusal.None;

        if (preflight) {
            return true;
        }

        _ = lattice.PaintSphere(
            centerX: paint.X,
            centerY: paint.Y,
            centerZ: paint.Z,
            fieldName: paint.Field,
            operation: (paint.Operation switch {
                WorldFieldWriteOp.Set => Puck.Physics.Fields.FieldWriteOp.Set,
                WorldFieldWriteOp.Add => Puck.Physics.Fields.FieldWriteOp.Add,
                _ => throw new ArgumentOutOfRangeException(
                actualValue: paint.Operation,
                message: null,
                paramName: nameof(effect)
            ),
            }),
            radius: paint.Radius,
            value: paint.Value
        );

        return true;
    }
    // Body state, not document state: the same WorldBody.Pose door body.pose uses, as the world's own act — no
    // drive-gate or grant check, since a gated body is one a rule still needs to move.
    private bool FirePoseCell(WorldPoseCellEffect effect, bool preflight, out EffectRefusal refusal) {
        var index = ResolveWorldBodyRef(effect.Body);
        var topology = WorldTopologyCompilation.Find(Host.Definition, effect.Topology);
        if (Host.Body(index: index) is not { } body) {
            return Refuse(code: WorldRuleEffectRefusal.BodyInactive, reason: $"body:{index} is inactive", refusal: out refusal);
        }
        if (topology is null || !RuleExpressions.TryEvaluate(fault: out _, kind: CellKind.Int, program: effect.Expression, reader: this, value: out var cell) || cell < 0 || cell >= topology.CellCount) {
            return Refuse(code: WorldRuleEffectRefusal.BodyTargetInvalid, reason: "poseCell target is outside its topology", refusal: out refusal);
        }
        refusal = EffectRefusal.None;
        if (!preflight) {
            var centre = topology.CellCentre((int)cell);
            body.Pose(position: new FixedVector3(centre.X + effect.Offset.X, centre.Y + effect.Offset.Y, centre.Z + effect.Offset.Z),
                yawRadians: body.FixedYaw, pitchRadians: FixedQ4816.Zero, rollRadians: FixedQ4816.Zero);
        }
        return true;
    }
    private bool FirePose(WorldPoseEffect effect, bool preflight, out EffectRefusal refusal) {
        var bodyIndex = ((effect.KeyFrom is { } keyFrom)
            ? ResolveBodyKeyIndex(
                key: default,
                keyFrom: keyFrom
            )
            : effect.Index
        );

        if (Host.Body(index: bodyIndex) is not { } body) {
            return Refuse(
                code: WorldRuleEffectRefusal.BodyInactive,
                reason: $"body:{bodyIndex} is inactive",
                refusal: out refusal
            );
        }

        refusal = EffectRefusal.None;

        CompiledWorldPose pose;

        if (effect.Pose is { } literal) {
            pose = literal;
        } else if (WorldDefinitionRows.FindSpawnPoint(
            id: effect.SpawnPoint,
            spawnPoints: Host.Definition.SpawnPoints
        ) is { } point) {
            var spawn = FixedSpawnPoint.Compile(point: in point);

            pose = new CompiledWorldPose(
                PitchRadians: FixedQ4816.Zero,
                Position: spawn.Position,
                RollRadians: FixedQ4816.Zero,
                YawRadians: spawn.YawRadians
            );
        } else {
            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(
                    channel: "world.rule",
                    text: $"[world.rule: pose skipped — spawnPoint '{effect.SpawnPoint}' is no longer declared]"
                );
            }

            return true;
        }
        if (preflight) {
            return true;
        }

        body.Pose(
            pitchRadians: pose.PitchRadians,
            position: pose.Position,
            rollRadians: pose.RollRadians,
            yawRadians: pose.YawRadians
        );
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(
                channel: "world.rule",
                text: $"[world.rule: pose body:{bodyIndex} -> ({pose.Position.X}, {pose.Position.Y}, {pose.Position.Z})]"
            );
        }

        return true;
    }
    // The lane cell is an arena row, so this arm is rewound by the firing's own scope; only the persisted identity
    // document write waits for the commit.
    private bool FireIdentityFact(WorldIdentityFactEffect effect, out EffectRefusal refusal) {
        var bodyIndex = ResolveBodyKeyIndex(
            key: effect.Key,
            keyFrom: effect.KeyFrom
        );

        if (Host.Body(index: bodyIndex) is not { } body) {
            return Refuse(
                code: WorldRuleEffectRefusal.BodyInactive,
                reason: $"body:{bodyIndex} is inactive",
                refusal: out refusal
            );
        }
        if (body.Profile is not { Document: not null } identity) {
            return Refuse(
                code: WorldRuleEffectRefusal.IdentityUnbound,
                reason: $"body:{bodyIndex} drives under no owned identity — a fact is refused, never minted for an anonymous seat",
                refusal: out refusal
            );
        }

        var laneOrdinal = effect.LaneOrdinal;

        if (((uint)laneOrdinal) >= ((uint)Host.Arena.Layout.RowCount)) {
            return Refuse(
                code: WorldRuleEffectRefusal.IdentityFactUnwritable,
                reason: $"the installed document declares no '{WorldIdentityFactLane.RowName}' lane row",
                refusal: out refusal
            );
        }

        long value;

        if (effect.Expression is { } program) {
            if (!RuleExpressions.TryEvaluate(
                fault: out _,
                kind: CellKind.Int,
                program: program,
                reader: this,
                value: out value
            )) {
                return Refuse(
                    code: WorldRuleEffectRefusal.IdentityFactUnwritable,
                    reason: "the value expression faulted",
                    refusal: out refusal
                );
            }
        } else {
            value = effect.Source.RawValue;
        }

        var key = IdentityLaneKey(
            bodyIndex: bodyIndex,
            fact: effect.Fact.Value
        );

        if (!Host.Arena.TryWrite(
            key: key,
            operand: value,
            reason: out var reason,
            rowOrdinal: laneOrdinal,
            write: StateWriteKind.Set
        )) {
            if (!Host.Arena.TryMint(
                key: out _,
                name: Host.Arena.Catalog.Keys[key],
                reason: out reason,
                rowOrdinal: laneOrdinal,
                value: CellValue.Int(value: value)
            )) {
                return Refuse(
                    code: WorldRuleEffectRefusal.IdentityFactUnwritable,
                    reason: reason,
                    refusal: out refusal
                );
            }
        }

        m_pendingIdentityFacts.Add(item: new PendingIdentityFact(
            Identity: identity,
            Key: effect.Fact,
            LaneKey: key,
            LaneOrdinal: laneOrdinal,
            Value: value
        ));
        refusal = EffectRefusal.None;

        return true;
    }
    // A pending fact is persisted only while the arena still holds the value the firing wrote: a firing the
    // evaluator rewound restored the lane cell, so its entry is dropped rather than written outward.
    private void FlushIdentityFacts() {
        if (m_pendingIdentityFacts.Count == 0) {
            return;
        }

        foreach (var pending in m_pendingIdentityFacts) {
            if (
                Host.Arena.TryRead(
                key: pending.LaneKey,
                rowOrdinal: pending.LaneOrdinal,
                value: out var stored
            ) &&
                (stored.Kind == CellKind.Int) &&
                (stored.AsInt == pending.Value)
            ) {
                PersistIdentityFact(
                    identity: pending.Identity,
                    key: pending.Key,
                    value: pending.Value
                );
            }
        }

        m_pendingIdentityFacts.Clear();
    }
    private bool FireDocument(WorldDocumentEffect effect, in EffectFiring firing, out EffectRefusal refusal) {
        refusal = EffectRefusal.None;

        if (effect.Write == WorldDocumentWrite.Save) {
            if (firing.Preflight) {
                return true;
            }
            if (Host.SaveEffectTap is { } save) {
                save(firing.Tick);

                return true;
            }

            return Refuse(
                code: WorldRuleEffectRefusal.SaveUnavailable,
                reason: "no save-effect host is attached",
                refusal: out refusal
            );
        }

        // A removePlacement targeting a placement whose inhabit facet is bound to a POSSESSED body would destroy an
        // explicit possession grant's binding out from under it: refused, never orphaned to escrow.
        if (
            (effect.Write == WorldDocumentWrite.RemovePlacement) &&
            Host.TryFindPossessedInhabitant(
            bodyIndex: out var possessedBody,
            holder: out var possessor,
            placementId: effect.Id
        )
        ) {
            return Refuse(
                code: WorldRuleEffectRefusal.CarrierPossessed,
                reason: $"placement '{effect.Id}' carries inhabitant body:{possessedBody}, possessed by {possessor.Describe()}",
                refusal: out refusal
            );
        }

        WorldMutation mutation = (effect.Write switch {
            WorldDocumentWrite.UpsertHudPanel => new WorldMutation.UpsertHudPanel(
            Panel: effect.HudPanel!,
            Principal: WorldPrincipal.World
        ),
            WorldDocumentWrite.RemoveHudPanel => new WorldMutation.RemoveHudPanel(
            Id: effect.Id,
            Principal: WorldPrincipal.World
        ),
            WorldDocumentWrite.UpsertPlacement => new WorldMutation.UpsertPlacement(
            Placement: effect.Placement!,
            Principal: WorldPrincipal.World
        ),
            _ => new WorldMutation.RemovePlacement(
            Id: effect.Id,
            Principal: WorldPrincipal.World
        ),
        });

        // A document row is never fired on its own: the evaluator preflights it, the firing's rows are prepared as one
        // unit (PrepareTransactional), and the commit installs that unit (CommitTransactional).
        if (!firing.Preflight) {
            return Refuse(
                code: RuleEffectRefusal.MutationRejected,
                reason: "a document row is installed with its firing's commit, never delivered after it",
                refusal: out refusal
            );
        }

        // The batch so far is composed against the document the firing proposes, so each row is judged against what
        // the rows before it leave and a row that cannot follow them is refused by name. Every other gate runs once,
        // over the whole unit, in PrepareTransactional. Nothing is installed here: the firing's scope is still open,
        // and a document carrying writes that may yet rewind would keep them.
        m_documentArms.Add(item: mutation);

        return (WorldDocument.TryCompose(
            candidate: out _,
            current: Proposed().Definition,
            engineTick: firing.EngineTick,
            evictedKey: out _,
            instanceIdentity: Host.InstanceIdentity,
            mutation: DocumentUnit(),
            reason: out var composeReason,
            tick: firing.Tick
        ) || Refuse(
            code: RuleEffectRefusal.MutationRejected,
            reason: composeReason,
            refusal: out refusal
        ));
    }
    private int ResolveWorldBodyRef(WorldBodyRef bodyRef) => (bodyRef.Kind switch {
        CompiledBodyRefKind.Literal => bodyRef.Index,
        CompiledBodyRefKind.Binding => BoundIndex(key: ((BoundKey)bodyRef.Index)),
        CompiledBodyRefKind.Cell => (((IntegerOf(value: Host.ReadArenaCell(
        key: bodyRef.Key,
        rowOrdinal: bodyRef.RowOrdinal
    )) is var cellIndex) && (cellIndex >= 0L) && (cellIndex < Host.Population.Capacity))
        ? ((int)cellIndex)
        : -1),
        CompiledBodyRefKind.Placement => ((bodyRef.PlacementOrdinals is not null)
        ? Host.Population.BodyForPlacementOrdinal(ordinal: Host.PlacementOrdinalOf(id: (Host.Arena.Catalog.Keys.TryGetName(
            key: BoundEachKey,
            name: out var placementName
        )
            ? placementName.Value
            : string.Empty)))
        : Host.Population.BodyForPlacementOrdinal(ordinal: bodyRef.Index)),
        _ => Host.ResolveArgBodyOrdinal(
        filterRowOrdinal: -1,
        op: ((bodyRef.Kind == CompiledBodyRefKind.ArgMax)
        ? StateReduceOp.Max
        : StateReduceOp.Min),
        rowOrdinal: bodyRef.RowOrdinal
    ),
    });
}
