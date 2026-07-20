using Puck.Hosting;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>What kind of edit boundary a <see cref="WorldEditEcho"/> narrates — the class the editor HUD tags.</summary>
internal enum WorldEditEchoKind {
    /// <summary>A world-document mutation that applies LIVE on delivery (cameras included).</summary>
    Mutation,

    /// <summary>A DOCUMENT-DEFAULTS mutation — it changes what the next boot wakes on while the live session levers
    /// keep their values (<c>world.render.defaults</c> / <c>world.population.defaults</c>).</summary>
    DocumentDefaults,

    /// <summary>A grant-table change (<c>world.grant</c>/<c>world.revoke</c>) — runtime capability state, not a
    /// document edit.</summary>
    GrantTable,
}

/// <summary>One edit-boundary outcome echoed beside the loud stderr line — the payload of
/// <see cref="WorldServer.EchoTap"/>, so a UI surface (the overlay toast, the editor HUD's act-class tag, the drag
/// channel's frozen-preview retirement) narrates outcomes without scraping stderr.</summary>
/// <param name="Message">The human-readable outcome line (no brackets).</param>
/// <param name="Rejected">Whether the outcome is a rejection/denial.</param>
/// <param name="Kind">The edit-boundary class the outcome belongs to.</param>
/// <param name="Mutation">The mutation the outcome answers, when the boundary was a mutation — the correlation key a
/// released drag preview retires against (<c>WorldEditorDrag.NoteRejected</c>) and the at-site position source the
/// applied-cue lane derives from; <see langword="null"/> otherwise.</param>
/// <param name="Denied">Whether the rejection was a CAPABILITY denial (a missing mutate grant, a refused grant
/// acquisition) rather than a validator/guard rejection — the discriminator the cue lane's <c>grant.denied</c> vs
/// <c>mutation.rejected</c> tokens ride.</param>
internal readonly record struct WorldEditEcho(string Message, bool Rejected, WorldEditEchoKind Kind, WorldMutation? Mutation = null, bool Denied = false);

/// <summary>
/// The authoritative world server — one logical instance owning the LIVE <see cref="WorldDefinition"/>, the entity
/// table (<see cref="WorldPopulation"/>), the profile catalog, and the mutation journal. Commands, session requests,
/// and queries apply synchronously at submit (the host guarantees submissions arrive inside the command-apply window
/// immediately preceding the tick's <see cref="Step"/>, so every mutation lands before that tick's advance in stdin
/// FIFO order). Live world EDITS — mutations, definition swaps, and journal undo — instead BUFFER and drain at
/// <see cref="Step"/>, before the intent drain, so they are tick-aligned: they buffer like intents,
/// they are not synchronous like commands. Per-tick intents also buffer and drain at <see cref="Step"/>, which then
/// advances every body and pushes the tick's <see cref="WorldSnapshot"/> — plus, in any step that applied at least one
/// edit, the new definition — to the attached <see cref="IClientSink"/>.
/// </summary>
/// <remarks>Single-threaded on the host tick, like every simulation type here: submissions arrive during the command
/// pump's apply window and <see cref="Step"/> runs immediately after, both on the launcher's window-pump thread. The
/// journal is the undo engine: the loaded base definition plus an append-only list of applied <see cref="WorldMutation"/>s;
/// undo restores the base and deterministically replays the journal minus its tail through the same apply path — no
/// per-mutation inverse logic is ever written.</remarks>
internal sealed class WorldServer {
    private readonly WorldPopulation m_population;
    private readonly WorldProfiles m_profiles;
    private readonly WorldRenderEnvelope m_envelope;
    private readonly Queue<IntentSubmission> m_intents = new();
    // The buffered live-edit ops (mutations, whole-document swaps, journal undo), drained FIFO at the step boundary
    // BEFORE intents. New allocation lives here, at the mutation boundary; an idle tick pays one empty-queue check.
    private readonly Queue<PendingOp> m_pending = new();
    // The mutation journal — the undo engine. m_base is the loaded base definition (reset by a swap or world.save
    // compaction); m_journal is the append-only edit history over it. dirty == m_journal.Count.
    private readonly List<JournalEntry> m_journal = new();
    // The ONE capability table — every write boundary checks it. Seeded permissive for local play (see WorldGrants).
    private readonly WorldGrants m_grants = new(seatCount: WorldPopulation.LocalSeatCount, population: WorldPopulation.MaxPopulation);
    // Per-body "an intent was denied last drain" latch, so a revoked driver that keeps submitting logs its loud drop
    // ONCE per denial episode (reset when an allowed intent for that body arrives) rather than once per tick.
    private readonly bool[] m_driveDenied = new bool[WorldPopulation.MaxPopulation];
    private readonly EntitySnapshot[] m_snapshotEntries = new EntitySnapshot[WorldPopulation.MaxPopulation];
    private WorldDefinition m_definition;
    private WorldDefinition m_base;
    private IClientSink? m_sink;
    // The live SDF contact field under the FIELD provider (null under analytic / collision off) — the server OWNS this
    // provider's lifecycle: it is built ONCE at apply time (for its loud excluded-op rejection) and handed to the
    // population's rebuild, so a body's first step after a solid edit already solves against the new field. Adopted at
    // construction from the population's boot build so it is never compiled twice for one boundary.
    private WorldSolidField? m_solids;
    // The solid-field revision — bumped each time m_solids is rebuilt (a solid-affecting edit under the field provider),
    // the world.collision.status read-back. Starts at 1 when the boot world uses the field provider, else 0.
    private int m_solidRevision;

    /// <summary>Initializes a new instance of the <see cref="WorldServer"/> class over the world it authoritatively owns.</summary>
    /// <param name="definition">The loaded world definition (the initial live definition and journal base).</param>
    /// <param name="population">The entity table (all bodies, seats included).</param>
    /// <param name="profiles">The profile catalog.</param>
    /// <param name="envelope">The render-capacity oracle a scene/screen mutation is checked against at apply time.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldServer(WorldDefinition definition, WorldPopulation population, WorldProfiles profiles, WorldRenderEnvelope envelope) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: envelope);

        m_definition = definition;
        m_base = definition;
        m_population = population;
        m_profiles = profiles;
        m_envelope = envelope;
        // Adopt the population's boot-built field (the field provider compiled it once for the bodies it minted at
        // construction) — the server owns it from here without a second build.
        m_solids = population.SolidField;
        m_solidRevision = ((m_solids is null) ? 0 : 1);
        // Join the bodies the boot definition's inhabited placements declare into free peer slots (the population
        // constructor activates nothing — the boot census is zero, the whole peer slice is free). Every later Install
        // re-runs this after Rebuild.
        m_population.ReconcileInhabitants(definition: definition);
    }

    /// <summary>The live world definition this server runs — swapped in place as buffered edits apply.</summary>
    public WorldDefinition Definition => m_definition;

    /// <summary>The entity table this server advances.</summary>
    public WorldPopulation Population => m_population;

    /// <summary>The profile catalog (the routed store persists through it).</summary>
    public WorldProfiles Profiles => m_profiles;

    /// <summary>The capability table — the ONE grant primitive the engagement view, the addon driver, and the grant
    /// verbs read/write. Reads are loopback-local today; a socket transport moves grant changes onto the wire.</summary>
    public WorldGrants Grants => m_grants;

    /// <summary>The journal length — the number of applied mutations over the base (the <c>world.status</c> dirty
    /// count, and the <c>world.undo</c> budget).</summary>
    public int JournalLength => m_journal.Count;

    /// <summary>The live SDF contact field under the FIELD provider, or <see langword="null"/> under the analytic
    /// provider / collision off — the <c>world.collision.probe</c>/<c>world.collision.status</c> reads' window onto the
    /// surface the simulation itself solves against.</summary>
    public WorldSolidField? SolidField => m_solids;

    /// <summary>The solid-field revision — bumped each time the field is rebuilt (a solid-affecting edit under the field
    /// provider). The <c>world.collision.status</c> read-back.</summary>
    public int SolidRevision => m_solidRevision;

    /// <summary>An optional edit-echo tap invoked beside the loud stderr accept/reject lines — mutation outcomes,
    /// grant/revoke outcomes, and their document-only class — so a UI surface (the overlay toast, the editor HUD)
    /// narrates them without scraping stderr. Runs on the server's step thread.</summary>
    public Action<WorldEditEcho>? EchoTap { get; set; }

    /// <summary>Compacts the journal: the live definition becomes the new base and the edit history is cleared (the
    /// <c>world.save</c> half — a saved world is clean). Reads/writes only journal state, so it runs on the Immediate
    /// console path behind the stdin barrier.</summary>
    public void Compact() {
        m_base = m_definition;
        m_journal.Clear();
    }

    /// <summary>Attaches the client sink the per-tick snapshot is delivered to, immediately delivering a primer
    /// snapshot of the current table so the client renders the boot state before the first tick. Loopback wiring; one
    /// sink.</summary>
    /// <param name="sink">The sink to deliver snapshots to.</param>
    public void AttachSink(IClientSink sink) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        m_sink = sink;
        EmitSnapshot(tick: 0UL, stepTicks: 0UL);
    }

    /// <summary>The body at a 0-based entity index, or <see langword="null"/> when the index holds no live body.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public WorldBody? Body(int index) => (((uint)index < WorldPopulation.MaxPopulation) ? m_population.EntryBody(index: index) : null);

    /// <summary>Buffers one entity's submitted intent for the next <see cref="Step"/>.</summary>
    /// <param name="submission">The tick, entity index, and merged intent.</param>
    public void EnqueueIntent(in IntentSubmission submission) {
        m_intents.Enqueue(item: submission);
    }

    /// <summary>Buffers one live world mutation for the next <see cref="Step"/> (drained before intents).</summary>
    /// <param name="mutation">The mutation to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> is <see langword="null"/>.</exception>
    public void EnqueueMutation(WorldMutation mutation) {
        ArgumentNullException.ThrowIfNull(argument: mutation);

        m_pending.Enqueue(item: new PendingOp.Mutate(Mutation: mutation));
    }

    /// <summary>Buffers a whole-document swap for the next <see cref="Step"/> (drained before intents).</summary>
    /// <param name="definition">The definition to install.</param>
    /// <param name="principal">The acting identity the swap is checked against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void EnqueueDefinition(WorldDefinition definition, WorldPrincipal principal) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_pending.Enqueue(item: new PendingOp.Swap(Definition: definition, Principal: principal));
    }

    /// <summary>Buffers a journal undo of the last <paramref name="count"/> mutations for the next <see cref="Step"/>.</summary>
    /// <param name="count">How many trailing mutations to undo (clamped to at least 1 and at most the journal length).</param>
    /// <param name="principal">The acting identity the undo is checked against.</param>
    public void EnqueueUndo(int count, WorldPrincipal principal) {
        m_pending.Enqueue(item: new PendingOp.Undo(Count: count, Principal: principal));
    }

    /// <summary>Adds a grant to the table SYNCHRONOUSLY (the <c>world.grant</c> half; like a command, so the next tick's
    /// checks observe it). A rejected exclusive acquisition prints a loud line and changes nothing.</summary>
    /// <param name="grant">The grant to add.</param>
    public void Grant(WorldGrant grant) {
        var label = $"{grant.Principal.Describe()} {grant.Capability.ToString().ToLowerInvariant()} {grant.Subject.Describe()}";

        if (m_grants.TryGrant(grant: grant, reason: out var reason)) {
            Console.Error.WriteLine(value: $"[world.grant: {label}{(grant.Exclusive ? " exclusive" : string.Empty)}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"grant {label}{(grant.Exclusive ? " exclusive" : string.Empty)}", Rejected: false, Kind: WorldEditEchoKind.GrantTable));
        } else {
            Console.Error.WriteLine(value: $"[world.grant rejected: {label} — {reason}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"grant {label} rejected: {reason}", Rejected: true, Kind: WorldEditEchoKind.GrantTable, Denied: true));
        }
    }

    /// <summary>Removes a grant from the table SYNCHRONOUSLY (the <c>world.revoke</c> half).</summary>
    /// <param name="grant">The grant (capability + subject) to revoke.</param>
    public void Revoke(WorldGrant grant) {
        var removed = m_grants.Revoke(principal: grant.Principal, capability: grant.Capability, subject: grant.Subject);
        var label = $"{grant.Principal.Describe()} {grant.Capability.ToString().ToLowerInvariant()} {grant.Subject.Describe()}";

        Console.Error.WriteLine(value: removed
            ? $"[world.revoke: {label}]"
            : $"[world.revoke: {grant.Principal.Describe()} held no {grant.Capability.ToString().ToLowerInvariant()} over {grant.Subject.Describe()}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: (removed ? $"revoke {label}" : $"revoke {label} — nothing held"), Rejected: !removed, Kind: WorldEditEchoKind.GrantTable));
    }

    /// <summary>Applies a LIVE window-composition override SYNCHRONOUSLY (the <c>view.layout</c>/<c>view.camera</c> path
    /// and Arc 9's milestone camera cut). Checks <see cref="WorldCapability.Control"/> over
    /// <see cref="GrantSubject.Composition"/>; on accept pushes it to the client composer, on denial prints a loud line
    /// and changes nothing. Never durable — no document, no journal.</summary>
    /// <param name="composition">The composition override.</param>
    /// <param name="principal">The acting identity the override is checked against.</param>
    public void ApplyComposition(WorldComposition composition, WorldPrincipal principal) {
        ArgumentNullException.ThrowIfNull(argument: composition);

        if (!m_grants.Allows(principal: principal, capability: WorldCapability.Control, subject: GrantSubject.Composition)) {
            Console.Error.WriteLine(value: $"[world.grant denied: {principal.Describe()} cannot control composition — {composition.GetType().Name} dropped]");

            return;
        }

        m_sink?.DeliverComposition(composition: composition);
    }

    /// <summary>Applies an authority command to its target body. Synchronous at submit (see the class summary), so a
    /// policy read following the command in the same batch observes its effect. A command whose entity is not live
    /// no-ops (validation happened at submit; the miss is benign).</summary>
    /// <param name="command">The command to apply.</param>
    public void ApplyCommand(WorldCommand command) {
        ArgumentNullException.ThrowIfNull(argument: command);

        if (!m_grants.Allows(principal: command.Principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: command.EntityIndex))) {
            Console.Error.WriteLine(value: $"[world.grant denied: {command.Principal.Describe()} cannot drive body:{command.EntityIndex} — {command.GetType().Name} dropped]");

            return;
        }

        if (Body(index: command.EntityIndex) is not { } body) {
            return;
        }

        switch (command) {
            case WorldCommand.Teleport { Kind: TeleportKind.Warp } warp:
                body.Warp(x: warp.Position.X, z: warp.Position.Z);

                break;
            case WorldCommand.Teleport pose:
                body.Pose(x: pose.Position.X, y: pose.Position.Y, z: pose.Position.Z, yawRadians: pose.YawRadians, pitchRadians: pose.PitchRadians, rollRadians: pose.RollRadians);

                break;
            case WorldCommand.Face face:
                body.Face(yawRadians: face.YawRadians);

                break;
            case WorldCommand.EnqueueSegment segment:
                body.EnqueueRun(intent: segment.Intent, seconds: segment.Seconds);

                break;
            case WorldCommand.PressLane press:
                if (press.HoldSeconds is { } holdSeconds) {
                    body.PressLane(lane: press.Lane, holdSeconds: holdSeconds);
                } else {
                    body.PressLane(lane: press.Lane);
                }

                break;
            case WorldCommand.SetMotion motion:
                body.SetModel(model: motion.Model);

                break;
            case WorldCommand.SetControl control:
                body.SetIntentSource(source: control.Source);

                break;
            case WorldCommand.Reconcile reconcile:
                body.Reconcile(x: reconcile.X, z: reconcile.Z, yawRadians: reconcile.YawRadians, seconds: reconcile.Seconds);

                break;
            case WorldCommand.Stop:
                body.Stop();

                break;
        }
    }

    /// <summary>Applies a session request synchronously and returns the reply. The protocol handshake is checked here: a
    /// <see cref="SessionRequest.Join"/> whose <see cref="SessionRequest.Join.ProtocolVersion"/> mismatches
    /// <see cref="WorldProtocol.Version"/> is rejected with a distinct reason. Seat allocation is likewise validated: an
    /// out-of-range slot or an unknown profile name is rejected.</summary>
    /// <param name="request">The session request.</param>
    public SessionReply ApplySession(SessionRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);

        switch (request) {
            case SessionRequest.Join join: {
                if (join.ProtocolVersion != WorldProtocol.Version) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"protocol version {join.ProtocolVersion} != server {WorldProtocol.Version}");
                }

                if ((uint)join.Slot >= WorldPopulation.LocalSeatCount) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"slot {join.Slot} out of range");
                }

                var profile = ((join.ProfileName is { } name) ? m_profiles.Find(name: name) : null);

                m_population.ActivateSeat(slot: join.Slot, profile: profile);

                return new SessionReply(Accepted: true, AssignedIndex: (join.Slot + 1), RosterEcho: string.Empty, Reason: string.Empty);
            }
            case SessionRequest.Leave leave:
                if ((uint)leave.Slot >= WorldPopulation.LocalSeatCount) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"slot {leave.Slot} out of range");
                }

                m_population.DeactivateSeat(slot: leave.Slot);

                return new SessionReply(Accepted: true, AssignedIndex: (leave.Slot + 1), RosterEcho: string.Empty, Reason: string.Empty);
            case SessionRequest.SetProfile setProfile: {
                if (((uint)setProfile.Slot >= WorldPopulation.LocalSeatCount) || (m_profiles.Find(name: setProfile.ProfileName) is not { } profile)) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: "slot or profile not found");
                }

                m_population.SetSeatProfile(slot: setProfile.Slot, profile: profile);

                return new SessionReply(Accepted: true, AssignedIndex: (setProfile.Slot + 1), RosterEcho: string.Empty, Reason: string.Empty);
            }
            case SessionRequest.SetPopulation setPopulation: {
                var applied = m_population.SetSimulatedCount(count: setPopulation.Count);

                return new SessionReply(Accepted: true, AssignedIndex: applied, RosterEcho: string.Empty, Reason: string.Empty);
            }
            case SessionRequest.SetPeerSource setPeerSource:
                m_population.SetPeerSource(source: setPeerSource.Source);

                return new SessionReply(Accepted: true, AssignedIndex: -1, RosterEcho: string.Empty, Reason: string.Empty);
            case SessionRequest.SetPlayerSection setSection: {
                // The server owns the durable player document: gate on Edit over the CONCRETE profile subject (the
                // permissive Edit/all seed passes via the wildcard path, so local play is unchanged; a profile:<id>
                // grant narrows trust to named profiles), apply/validate the section, bump the revision, and persist.
                if (!m_grants.Allows(principal: setSection.Principal, capability: WorldCapability.Edit, subject: GrantSubject.Profile(id: setSection.ProfileId))) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"{setSection.Principal.Describe()} cannot edit profile:{setSection.ProfileId}");
                }

                if (!m_profiles.ApplySection(id: setSection.ProfileId, section: setSection.Section, payload: setSection.Payload, reason: out var sectionReason)) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: sectionReason);
                }

                // An identity edit changed the shared handle's color; refresh the population's CACHED seat body color so
                // the snapshot carries the new color (a seat renders its live handle color client-side, but the server's
                // per-entry cache — the snapshot source — must not lie). Name/motion/bindings/preferences need no cache
                // refresh (read live off the handle).
                if ((setSection.Section == WorldPlayerSection.Identity) && (m_profiles.FindById(id: setSection.ProfileId) is { } edited)) {
                    m_population.RefreshSeatColor(profile: edited);
                }

                return new SessionReply(Accepted: true, AssignedIndex: -1, RosterEcho: string.Empty, Reason: string.Empty);
            }
            default:
                return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: "unknown session request");
        }
    }

    /// <summary>Composes the authoritative answer to a read-back query.</summary>
    /// <param name="query">The read-back query.</param>
    public QueryAnswer Answer(WorldQuery query) {
        ArgumentNullException.ThrowIfNull(argument: query);

        return query switch {
            WorldQuery.PlayerWhere where when (Body(index: (where.Index - 1)) is { } body) => new QueryAnswer(Text: body.DescribeWhere(index: where.Index)),
            WorldQuery.PlayerWhere where => new QueryAnswer(Text: $"[player.where: player {where.Index} is not an active population entry — see world.population]"),
            WorldQuery.PlayerDocument => new QueryAnswer(Text: WorldPlayerJson.Serialize(document: m_profiles.ToDocument())),
            _ => new QueryAnswer(Text: string.Empty),
        };
    }

    /// <summary>Advances the authoritative world by one exact host tick: drain the buffered live edits (mutations,
    /// swaps, undo) FIRST — applying each at the tick boundary and delivering the new definition once if any applied →
    /// drain the tick's submitted intents → advance every body (peers, then seats) → deliver the tick's
    /// <see cref="WorldSnapshot"/>.</summary>
    /// <param name="context">The launcher's fixed-step context for this tick.</param>
    public void Step(in FixedStepContext context) {
        _ = DrainPendingOps(tick: context.Tick);

        while (m_intents.TryDequeue(result: out var submission)) {
            if (Body(index: submission.EntityIndex) is not { } body) {
                continue;
            }

            // Server-side Drive enforcement on the per-tick path: a submission whose principal does
            // not hold Drive over the target body is dropped, loud ONCE per denial episode (a revoked driver keeps
            // submitting; we log its first refused tick, then the body idles until re-granted). Allocation-free O(1).
            if (!m_grants.Allows(principal: submission.Principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: submission.EntityIndex))) {
                if (!m_driveDenied[submission.EntityIndex]) {
                    Console.Error.WriteLine(value: $"[world.grant denied: {submission.Principal.Describe()} cannot drive body:{submission.EntityIndex} — intent dropped, body idle]");
                    m_driveDenied[submission.EntityIndex] = true;
                }

                continue;
            }

            m_driveDenied[submission.EntityIndex] = false;
            body.SubmitIntent(intent: submission.Intent);
            body.SetHeldLanes(lanes: submission.HeldLanes);
        }

        m_population.AdvanceSimulated(stepTicks: context.StepTicks);
        m_population.AdvanceSeats(stepTicks: context.StepTicks);
        EmitSnapshot(tick: (context.Tick + 1UL), stepTicks: context.StepTicks);
    }

    // Drain every buffered live edit in FIFO order, applying it at this tick boundary. Delivers the new definition to
    // the client sink ONCE if at least one edit applied (once per step with >=1 applied edit, not once per edit).
    private bool DrainPendingOps(ulong tick) {
        var applied = false;

        while (m_pending.TryDequeue(result: out var op)) {
            var ok = op switch {
                PendingOp.Mutate mutate => TryApplyMutation(mutation: mutate.Mutation, tick: tick),
                PendingOp.Swap swap => ApplyDefinition(definition: swap.Definition, principal: swap.Principal),
                PendingOp.Undo undo => ApplyUndo(count: undo.Count, principal: undo.Principal),
                _ => false,
            };

            applied |= ok;
        }

        if (applied) {
            m_sink?.DeliverDefinition(definition: m_definition);
        }

        return applied;
    }

    // Apply one mutation at the tick boundary: compose a candidate (with-expression) → revalidate the WHOLE document →
    // capacity-check scene/screen edits against the probed render envelope → on any failure reject loudly (definition
    // unchanged) → on success swap the live definition, rebuild the changed section's derived state, and journal it.
    private bool TryApplyMutation(WorldMutation mutation, ulong tick) {
        // Server-side Mutate enforcement: the principal must hold Mutate over the mutation's section. A denial is
        // data-shaped (a missing grant row), never a new message kind — and it is loud and dropped.
        var section = SectionOf(mutation: mutation);

        if (!m_grants.Allows(principal: mutation.Principal, capability: WorldCapability.Mutate, subject: GrantSubject.Section(section: section))) {
            Console.Error.WriteLine(value: $"[world.grant denied: {mutation.Principal.Describe()} cannot mutate section:{section.ToString().ToLowerInvariant()} — {Describe(mutation: mutation)} dropped]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"{Describe(mutation: mutation)} denied: no mutate grant", Rejected: true, Kind: WorldEditEchoKind.Mutation, Mutation: mutation, Denied: true));

            return false;
        }

        if (!TryCompose(current: m_definition, mutation: mutation, candidate: out var candidate, reason: out var composeReason)) {
            Reject(mutation: mutation, reason: composeReason);

            return false;
        }

        if (!WorldDefinitionValidator.TryValidate(definition: candidate, reason: out var validationReason)) {
            Reject(mutation: mutation, reason: validationReason);

            return false;
        }

        if (AffectsRenderEnvelope(mutation: mutation) && !m_envelope.TryFit(candidate: candidate, reason: out var capacityReason)) {
            Reject(mutation: mutation, reason: capacityReason);

            return false;
        }

        // Step 4b — the SDF contact field, built once here (before install) so the warp-free evaluator's excluded-op
        // ceiling is a LOUD apply-time rejection (the definition and the field both stay byte-identical on failure)
        // rather than a constructor throw at install. Only a solid-affecting mutation rebuilds it; otherwise the live
        // field carries forward untouched.
        var solids = m_solids;
        var solidAffecting = AffectsSolidField(mutation: mutation);

        if (solidAffecting) {
            // A SetCollision edit touches only the collision tuning row — the compiled SDF program (scene rows, screens,
            // placements, ground plane) is byte-identical — so when the live field is already the field provider and the
            // candidate still is, re-wrap the existing evaluator with the new scalars instead of recompiling the program
            // (a slope/skin drag never rebuilds hundreds of instructions). Every other solid-affecting edit, and a
            // provider/enabled flip, rebuilds from scratch.
            if ((mutation is WorldMutation.SetCollision) && (m_solids is { } live) && UsesFieldProvider(definition: candidate)) {
                solids = live.WithTuning(tuning: FixedWorldCollision.Compile(collision: candidate.Collision));
            } else if (!TryBuildSolids(definition: candidate, solids: out solids, reason: out var solidReason)) {
                Reject(mutation: mutation, reason: solidReason);

                return false;
            }
        }

        // Assign the field BEFORE the rebuild so a recompiled body's first step already solves against it. A field change
        // forces a population rebuild (bodies must receive the new field reference) even when the mutation kind is not
        // otherwise population-affecting; the analytic path is untouched (solidAffecting is inert without the field provider).
        if (solidAffecting && !ReferenceEquals(objA: solids, objB: m_solids)) {
            m_solids = solids;
            m_solidRevision++;
        }

        Install(definition: candidate, rebuildPopulation: (AffectsPopulation(mutation: mutation) || (solidAffecting && UsesFieldProvider(definition: candidate))));
        m_journal.Add(item: new JournalEntry(Tick: tick, Mutation: mutation));

        // A defaults-class mutation edits what the NEXT boot wakes on while the live
        // session levers keep their values (world.save folds them); every other mutation applies live on delivery.
        // SetAuthoringDefaults is the honest exception to the binary split: ONE whole-row mutation carries BOTH
        // classes at once (WorldAuthoringDefaults' own remarks name which field is which) — the headroom/repeat-cap
        // fields are boot-consumed by the frozen render-envelope probe, while candidate/layout/preview fields are
        // re-read live at every use site. The narration spells out the split rather than forcing the mutation into
        // either WorldEditEchoKind bucket; Kind stays Mutation because the live-consumed majority applies NOW.
        var documentOnly = IsDocumentDefaults(mutation: mutation);
        var message = mutation switch {
            WorldMutation.SetAuthoringDefaults => $"{Describe(mutation: mutation)} applied — candidate/layout/preview levers live now; headroom + max-repeat-per-segment apply at next boot",
            // SetPopulationDefaults is a THIRD timing class: the census figures are document defaults (next boot), but
            // the SpawnPolicy is LIVE for future activations while INERT for bodies already standing — spell out the split.
            WorldMutation.SetPopulationDefaults => $"{Describe(mutation: mutation)} applied — census figures next boot; spawn policy live for future activations, standing bodies unmoved",
            _ => $"{Describe(mutation: mutation)} applied{(documentOnly ? " — document default (next boot; live levers unchanged)" : string.Empty)}",
        };

        Console.Error.WriteLine(value: $"[world.mutation: {message}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: message, Rejected: false, Kind: (documentOnly ? WorldEditEchoKind.DocumentDefaults : WorldEditEchoKind.Mutation), Mutation: mutation));

        return true;
    }

    // The whole-document swap (SubmitDefinition / world.load): validate → capacity-check → swap → full derived rebuild →
    // journal RESET (the loaded definition becomes the new base). The loader already validated a world.load file; this
    // re-check is the defensive apply-time gate every install passes through.
    private bool ApplyDefinition(WorldDefinition definition, WorldPrincipal principal) {
        // A whole-document swap can touch any section: the principal must hold Mutate over EVERY section.
        if (!m_grants.AllowsAllSections(principal: principal, capability: WorldCapability.Mutate)) {
            Console.Error.WriteLine(value: $"[world.grant denied: {principal.Describe()} cannot mutate every section — world.load dropped]");

            return false;
        }

        if (!WorldDefinitionValidator.TryValidate(definition: definition, reason: out var validationReason)) {
            Console.Error.WriteLine(value: $"[world.definition rejected: {validationReason}]");

            return false;
        }

        if (!m_envelope.TryFit(candidate: definition, reason: out var capacityReason)) {
            Console.Error.WriteLine(value: $"[world.definition rejected: {capacityReason}]");

            return false;
        }

        // A whole-document swap rebuilds the field wholesale (loud rejection on an unsupported solid, definition unchanged).
        if (!TryBuildSolids(definition: definition, solids: out var swapSolids, reason: out var swapSolidReason)) {
            Console.Error.WriteLine(value: $"[world.definition rejected: {swapSolidReason}]");

            return false;
        }

        SwapSolids(solids: swapSolids);
        Install(definition: definition, rebuildPopulation: true);
        m_base = definition;
        m_journal.Clear();
        Console.Error.WriteLine(value: "[world.definition: loaded]");

        return true;
    }

    // Undo the last `count` applied mutations (default clamps to 1): restore the base and deterministically replay the
    // journal minus its tail through the SAME apply path. Replaying previously-valid ops over the same base reproduces
    // the earlier state; a replay that somehow fails stops loudly rather than installing a half-built document.
    private bool ApplyUndo(int count, WorldPrincipal principal) {
        // Journal control is Mutate territory over every section (a replay can rebuild any).
        if (!m_grants.AllowsAllSections(principal: principal, capability: WorldCapability.Mutate)) {
            Console.Error.WriteLine(value: $"[world.grant denied: {principal.Describe()} cannot mutate every section — world.undo dropped]");

            return false;
        }

        if (m_journal.Count == 0) {
            Console.Error.WriteLine(value: "[world.undo: nothing to undo]");

            return false;
        }

        var drop = Math.Clamp(value: count, min: 1, max: m_journal.Count);
        var keep = (m_journal.Count - drop);
        var candidate = m_base;
        var kept = new List<JournalEntry>(capacity: keep);

        for (var index = 0; (index < keep); index++) {
            var entry = m_journal[index];

            if (!TryCompose(current: candidate, mutation: entry.Mutation, candidate: out var next, reason: out var reason) ||
                !WorldDefinitionValidator.TryValidate(definition: next, reason: out reason)) {
                Console.Error.WriteLine(value: $"[world.undo: replay failed at journal entry {index} — {reason}]");

                break;
            }

            candidate = next;
            kept.Add(item: entry);
        }

        // The replayed candidate is a validated document the journal previously produced, so its solids rebuild; a
        // failure stops loudly rather than installing a half-built field.
        if (!TryBuildSolids(definition: candidate, solids: out var undoSolids, reason: out var undoSolidReason)) {
            Console.Error.WriteLine(value: $"[world.undo: solid field rebuild failed — {undoSolidReason}]");

            return false;
        }

        SwapSolids(solids: undoSolids);
        Install(definition: candidate, rebuildPopulation: true);
        m_journal.Clear();
        m_journal.AddRange(collection: kept);
        Console.Error.WriteLine(value: $"[world.undo: dropped {drop}, {m_journal.Count} remaining]");

        return true;
    }

    // Swap the live definition and rebuild the derived state that compiled from it. Sim-affecting sections (kits,
    // assignment, motion, wander, seat kit, spawns) recompile the population's fixed tables and live bodies; the
    // scene/screens rebuild on the client through the delivered definition, and cameras/render/population defaults are
    // document-only.
    private void Install(WorldDefinition definition, bool rebuildPopulation) {
        m_definition = definition;

        if (rebuildPopulation) {
            m_population.Rebuild(definition: definition, solids: m_solids);
            // Reconcile inhabited placements AFTER the census rebuild (a placement/creation/kit edit can add, retire, or
            // re-kit a driven body). Idempotent — a no-op when the inhabited set is unchanged.
            m_population.ReconcileInhabitants(definition: definition);
        }
    }

    private void Reject(WorldMutation mutation, string reason) {
        Console.Error.WriteLine(value: $"[world.mutation rejected: {Describe(mutation: mutation)} — {reason}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"{Describe(mutation: mutation)} rejected: {reason}", Rejected: true, Kind: WorldEditEchoKind.Mutation, Mutation: mutation));
    }

    // Whether a mutation is DOCUMENT-DEFAULTS class (edits the next boot's wake state; live session levers own "now").
    // Everything else, cameras included, applies live on delivery.
    private static bool IsDocumentDefaults(WorldMutation mutation) => mutation is
        WorldMutation.SetRenderDefaults or WorldMutation.SetPopulationDefaults or WorldMutation.SetHostDefaults;

    // Whether a mutation recompiles the population's fixed-point derived state (kit table, kit indices, live bodies'
    // compiled tuning/actions, AND the analytic collider set). A scene-row/screen/collision edit rebuilds the collider
    // set so a live world.scene.solid / world.screen / world.collision takes effect on the next tick with no restart —
    // the analytic WorldColliderSet bakes solid SCREENS too, so a screen edit must rebuild it (the field provider already
    // rebuilds on screens via AffectsSolidField; this closes the same staleness under the analytic provider).
    private static bool AffectsPopulation(WorldMutation mutation) => mutation is
        WorldMutation.UpsertKit or WorldMutation.RemoveKit or WorldMutation.SetDefaultSeatKit or
        WorldMutation.SetKitAssignment or WorldMutation.SetMotion or WorldMutation.SetWander or WorldMutation.SetSpawns or
        WorldMutation.SetScene or WorldMutation.UpsertSceneRow or WorldMutation.RemoveSceneRow or WorldMutation.SetCollision or
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        // The LOOK mutations re-resolve the population's look table (PRESENTATION-ONLY, but Rebuild is the one path that
        // re-runs ResolveLookIndices and bumps the client's program-rebuild revision).
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment or
        // SetPopulationDefaults now carries the SpawnPolicy row: Rebuild is the one path that recompiles the fixed spawn
        // policy so it is LIVE for future activations (the live census count still stays the world.population verb — this
        // Rebuild re-seeds SpawnPosition but never re-activates or teleports a standing body).
        WorldMutation.SetPopulationDefaults or
        // A placement row can change the census (Arc 7's Inhabit facet: a placement contributes driven bodies), and an
        // inhabited row's kit resolution reads the creation's Locomotion, so a creation swap can move a body between
        // kits — all must trigger Rebuild + ReconcileInhabitants. (R13: the third and last edit to this switch.)
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation;

    // Build the SDF contact field for a candidate — null when collision is off or the analytic provider is selected (the
    // analytic set is derived inside the population's compile, not here), the built field under the FIELD provider, or a
    // named failure when a solid names an op the warp-free evaluator cannot interpret.
    private static bool TryBuildSolids(WorldDefinition definition, out WorldSolidField? solids, out string reason) {
        reason = string.Empty;

        if (!UsesFieldProvider(definition: definition)) {
            solids = null;

            return true;
        }

        return WorldSolidField.TryBuild(definition: definition, built: out solids, reason: out reason);
    }

    // Adopt a wholesale-rebuilt field (a swap/undo), bumping the revision when the field actually moved so the status
    // read-back tracks it. A swap into an analytic/off world clears the field.
    private void SwapSolids(WorldSolidField? solids) {
        if (!ReferenceEquals(objA: solids, objB: m_solids)) {
            m_solids = solids;
            m_solidRevision++;
        }
    }

    // Whether the definition selects the SDF field contact provider (collision on, provider field).
    private static bool UsesFieldProvider(WorldDefinition definition) =>
        (definition.Collision is { Enabled: true, Provider: WorldContactProvider.Field });

    // Whether a mutation can change the SDF contact field: the collision tuning (provider/probe/skin/slope), the ground
    // plane (SetMotion positions the ground half-space), and every solid-bearing section (scene rows, screens, creations
    // that reshape a stamp, placements). Coarse by section, matching AffectsPopulation/AffectsRenderEnvelope.
    private static bool AffectsSolidField(WorldMutation mutation) => mutation is
        WorldMutation.SetCollision or WorldMutation.SetMotion or
        WorldMutation.SetScene or WorldMutation.UpsertSceneRow or WorldMutation.RemoveSceneRow or
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation or
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement;

    // Whether a mutation can grow the SDF program past the probed render envelope (scene rows / screen slabs /
    // creation stamps — an UpsertCreation re-shapes every live placement of it, so it measures too).
    private static bool AffectsRenderEnvelope(WorldMutation mutation) => mutation is
        WorldMutation.SetScene or WorldMutation.UpsertSceneRow or WorldMutation.RemoveSceneRow or
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation or
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement or
        // A creation look will change the emitted program word count (a body worn as a stamp) once creation-look
        // rendering lands (Arc 7); catalog looks add zero words today, so this arm is honest groundwork — all three look
        // mutations already ride the envelope gate so the loud capacity rejection will fire at apply time, not at a later
        // GPU allocation, the moment creation stamps render.
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment;

    // The world-document section a mutation targets — the Mutate-capability subject it is checked against. One section
    // per mutation kind (coarse, section-keyed — a genre world adds sections + kinds, never changes this mapping).
    private static WorldSection SectionOf(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertKit or WorldMutation.RemoveKit or WorldMutation.SetDefaultSeatKit or WorldMutation.SetKitAssignment => WorldSection.Kits,
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen => WorldSection.Screens,
        WorldMutation.UpsertCamera or WorldMutation.RemoveCamera => WorldSection.Cameras,
        WorldMutation.SetScene or WorldMutation.UpsertSceneRow or WorldMutation.RemoveSceneRow => WorldSection.Scene,
        WorldMutation.SetSpawns => WorldSection.Spawns,
        WorldMutation.SetMotion => WorldSection.Motion,
        WorldMutation.SetWander => WorldSection.Wander,
        WorldMutation.SetPopulationDefaults => WorldSection.Population,
        WorldMutation.SetRenderDefaults => WorldSection.Render,
        WorldMutation.UpsertAddon or WorldMutation.RemoveAddon => WorldSection.Addons,
        WorldMutation.UpsertBindingOverlay or WorldMutation.RemoveBindingOverlay => WorldSection.Bindings,
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation => WorldSection.Creations,
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement => WorldSection.Placements,
        WorldMutation.SetAuthoringDefaults => WorldSection.Authoring,
        WorldMutation.UpsertSpeaker or WorldMutation.RemoveSpeaker => WorldSection.Speakers,
        WorldMutation.UpsertTune or WorldMutation.RemoveTune => WorldSection.Tunes,
        WorldMutation.UpsertPatch or WorldMutation.RemovePatch => WorldSection.Patches,
        WorldMutation.SetAudioDefaults => WorldSection.Audio,
        WorldMutation.SetCollision => WorldSection.Collision,
        WorldMutation.SetHostDefaults => WorldSection.Host,
        WorldMutation.SetViewDefaults or WorldMutation.UpsertViewLayout or WorldMutation.RemoveViewLayout => WorldSection.Views,
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment => WorldSection.Looks,
        WorldMutation.UpsertScreenLink or WorldMutation.RemoveScreenLink => WorldSection.Links,
        // No silent fallback: a new mutation kind added without its own arm would otherwise inherit Kits authority. A
        // missing arm is a build-time authoring gap, surfaced loudly rather than mis-authorized.
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(mutation), actualValue: mutation, message: $"no WorldSection arm for mutation kind '{mutation.GetType().Name}' — every kind must map to its authorizing section."),
    };

    // The dependents a placement-removal guard names: every speaker anchored to the placement (null = none).
    private static string? DescribeSpeakersAnchoredTo(IReadOnlyList<WorldSpeaker> speakers, string placementId) {
        List<string>? names = null;

        foreach (var speaker in speakers) {
            if ((speaker is WorldSpeaker.Anchored { Anchor: WorldAnchor.Placement anchor }) &&
                string.Equals(a: anchor.PlacementId, b: placementId, comparisonType: StringComparison.Ordinal)) {
                (names ??= new List<string>()).Add(item: $"'{speaker.Name}'");
            }
        }

        return ((names is null) ? null : string.Join(separator: ", ", values: names));
    }

    // The dependents a tune/patch-removal guard names among speaker feeds (null = none).
    private static string? DescribeSpeakersSourcing(IReadOnlyList<WorldSpeaker> speakers, Func<WorldSpeakerSource, bool> matches) {
        List<string>? names = null;

        foreach (var speaker in speakers) {
            if ((speaker.Feed?.Source is { } source) && matches(arg: source)) {
                (names ??= new List<string>()).Add(item: $"'{speaker.Name}'");
            }
        }

        return ((names is null) ? null : string.Join(separator: ", ", values: names));
    }

    // Every dependent a patch-removal guard names: synth-sourced speakers plus scene-row/placement emission facets
    // (creation sounds carry their patches INLINE, so they can never dangle). Null = none.
    private static string? DescribePatchDependents(WorldDefinition current, string patchId) {
        List<string>? dependents = null;

        if (DescribeSpeakersSourcing(speakers: current.Speakers, matches: source => ((source is WorldSpeakerSource.Synth synth) && string.Equals(a: synth.PatchId, b: patchId, comparisonType: StringComparison.Ordinal))) is { } speakers) {
            (dependents ??= new List<string>()).Add(item: $"speaker(s) {speakers}");
        }

        foreach (var row in current.Scene.Rows) {
            if ((row.Emission is { } emission) && string.Equals(a: emission.PatchId, b: patchId, comparisonType: StringComparison.Ordinal)) {
                (dependents ??= new List<string>()).Add(item: $"scene row '{row.Id}'");
            }
        }

        foreach (var placement in current.Placements) {
            if ((placement.Emission is { } emission) && string.Equals(a: emission.PatchId, b: patchId, comparisonType: StringComparison.Ordinal)) {
                (dependents ??= new List<string>()).Add(item: $"placement '{placement.Id}'");
            }
        }

        return ((dependents is null) ? null : string.Join(separator: ", ", values: dependents));
    }

    // A short mutation label for the accept/reject console line — the kind plus its stable-id subject.
    private static string Describe(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertKit m => $"UpsertKit '{m.Kit.Name}'",
        WorldMutation.RemoveKit m => $"RemoveKit '{m.Name}'",
        WorldMutation.SetDefaultSeatKit m => $"SetDefaultSeatKit '{m.Name}'",
        WorldMutation.SetKitAssignment m => $"SetKitAssignment '{m.Assignment.Policy}'",
        WorldMutation.UpsertScreen m => $"UpsertScreen {m.Screen.Index}",
        WorldMutation.RemoveScreen m => $"RemoveScreen {m.Index}",
        WorldMutation.UpsertCamera m => $"UpsertCamera '{m.Camera.Name}'",
        WorldMutation.RemoveCamera m => $"RemoveCamera '{m.Name}'",
        WorldMutation.SetScene => "SetScene",
        WorldMutation.UpsertSceneRow m => $"UpsertSceneRow '{m.Row.Id}'",
        WorldMutation.RemoveSceneRow m => $"RemoveSceneRow '{m.Id}'",
        WorldMutation.SetSpawns => "SetSpawns",
        WorldMutation.SetMotion => "SetMotion",
        WorldMutation.SetWander => "SetWander",
        WorldMutation.SetPopulationDefaults => "SetPopulationDefaults",
        WorldMutation.SetRenderDefaults => "SetRenderDefaults",
        WorldMutation.UpsertAddon m => $"UpsertAddon '{m.Addon.Name}'",
        WorldMutation.RemoveAddon m => $"RemoveAddon '{m.Name}'",
        WorldMutation.UpsertBindingOverlay m => $"UpsertBindingOverlay '{m.Overlay.Id}'",
        WorldMutation.RemoveBindingOverlay m => $"RemoveBindingOverlay '{m.Id}'",
        WorldMutation.UpsertCreation m => $"UpsertCreation '{m.Creation.Id}'",
        WorldMutation.RemoveCreation m => $"RemoveCreation '{m.Id}'",
        WorldMutation.UpsertPlacement m => $"UpsertPlacement '{m.Placement.Id}'",
        WorldMutation.RemovePlacement m => $"RemovePlacement '{m.Id}'",
        WorldMutation.SetAuthoringDefaults => "SetAuthoringDefaults",
        WorldMutation.UpsertSpeaker m => $"UpsertSpeaker '{m.Speaker.Name}'",
        WorldMutation.RemoveSpeaker m => $"RemoveSpeaker '{m.Name}'",
        WorldMutation.UpsertTune m => $"UpsertTune '{m.Tune.Id}'",
        WorldMutation.RemoveTune m => $"RemoveTune '{m.Id}'",
        WorldMutation.UpsertPatch m => $"UpsertPatch '{m.Patch.Id}'",
        WorldMutation.RemovePatch m => $"RemovePatch '{m.Id}'",
        WorldMutation.SetAudioDefaults => "SetAudioDefaults",
        WorldMutation.SetCollision => "SetCollision",
        WorldMutation.SetHostDefaults => "SetHostDefaults",
        WorldMutation.SetViewDefaults => "SetViewDefaults",
        WorldMutation.UpsertViewLayout m => $"UpsertViewLayout '{m.Layout.Name}'",
        WorldMutation.RemoveViewLayout m => $"RemoveViewLayout '{m.Name}'",
        WorldMutation.UpsertLook m => $"UpsertLook '{m.Look.Name}'",
        WorldMutation.RemoveLook m => $"RemoveLook '{m.Name}'",
        WorldMutation.SetLookAssignment m => $"SetLookAssignment '{m.Assignment.Policy}'",
        WorldMutation.UpsertScreenLink m => $"UpsertScreenLink '{m.Link.Name}'",
        WorldMutation.RemoveScreenLink m => $"RemoveScreenLink '{m.Name}'",
        _ => "unknown",
    };

    // Compose a candidate definition from the current one and a mutation — a with-expression over the coarse section,
    // whole-row upsert addressed by stable id. A remove of a missing id fails here (before validation) with a reason.
    private static bool TryCompose(WorldDefinition current, WorldMutation mutation, out WorldDefinition candidate, out string reason) {
        reason = string.Empty;

        switch (mutation) {
            case WorldMutation.UpsertKit m:
                candidate = (current with { Kits = Upsert(list: current.Kits, item: m.Kit, keyOf: static kit => kit.Name) });

                return true;
            case WorldMutation.RemoveKit m:
                if (!Remove(list: current.Kits, key: m.Name, keyOf: static kit => kit.Name, result: out var kits)) {
                    candidate = current;
                    reason = $"no kit row named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Kits = kits });

                return true;
            case WorldMutation.SetDefaultSeatKit m:
                candidate = (current with { DefaultSeatKit = m.Name });

                return true;
            case WorldMutation.SetKitAssignment m:
                candidate = (current with { Assignment = m.Assignment });

                return true;
            case WorldMutation.UpsertScreen m:
                candidate = (current with { Screens = Upsert(list: current.Screens, item: m.Screen, keyOf: static screen => screen.Index) });

                return true;
            case WorldMutation.RemoveScreen m:
                if (!Remove(list: current.Screens, key: m.Index, keyOf: static screen => screen.Index, result: out var screens)) {
                    candidate = current;
                    reason = $"no screen at index {m.Index}";

                    return false;
                }

                candidate = (current with { Screens = screens });

                return true;
            case WorldMutation.UpsertCamera m:
                candidate = (current with { Cameras = Upsert(list: current.Cameras, item: m.Camera, keyOf: static camera => camera.Name) });

                return true;
            case WorldMutation.RemoveCamera m:
                if (!Remove(list: current.Cameras, key: m.Name, keyOf: static camera => camera.Name, result: out var cameras)) {
                    candidate = current;
                    reason = $"no camera named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Cameras = cameras });

                return true;
            case WorldMutation.SetScene m:
                candidate = (current with { Scene = m.Scene });

                return true;
            case WorldMutation.UpsertSceneRow m:
                candidate = (current with { Scene = (current.Scene with { Rows = Upsert(list: current.Scene.Rows, item: m.Row, keyOf: static row => row.Id) }) });

                return true;
            case WorldMutation.RemoveSceneRow m:
                if (!Remove(list: current.Scene.Rows, key: m.Id, keyOf: static row => row.Id, result: out var sceneRows)) {
                    candidate = current;
                    reason = $"no scene row with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Scene = (current.Scene with { Rows = sceneRows }) });

                return true;
            case WorldMutation.SetSpawns m:
                candidate = (current with { SpawnPoints = m.Spawns });

                return true;
            case WorldMutation.SetMotion m:
                candidate = (current with { Motion = m.Motion });

                return true;
            case WorldMutation.SetWander m:
                candidate = (current with { Wander = m.Wander });

                return true;
            case WorldMutation.SetPopulationDefaults m:
                candidate = (current with { Population = m.Population });

                return true;
            case WorldMutation.SetRenderDefaults m:
                candidate = (current with { Render = m.Render });

                return true;
            case WorldMutation.UpsertAddon m:
                candidate = (current with { Addons = Upsert(list: current.Addons, item: m.Addon, keyOf: static addon => addon.Name) });

                return true;
            case WorldMutation.RemoveAddon m:
                if (!Remove(list: current.Addons, key: m.Name, keyOf: static addon => addon.Name, result: out var addons)) {
                    candidate = current;
                    reason = $"no addon named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Addons = addons });

                return true;
            case WorldMutation.UpsertCreation m: {
                // The hash contract at the compose boundary: canonicalize (validate + normalize + hash) the
                // carried document, REJECT a hash the pipeline did not itself compute, and store the canonical pair —
                // so a stored row's doc and hash always come from the SAME canonical result.
                Puck.Authoring.CanonicalDocument<Puck.Authoring.CreationDocument> canonical;

                try {
                    canonical = Puck.Authoring.CreationCanonicalizer.Canonicalize(document: m.Creation.Document, source: m.Creation.Id);
                } catch (Puck.Authoring.DocumentValidationException exception) {
                    candidate = current;
                    reason = exception.Message.ReplaceLineEndings(replacementText: " ");

                    return false;
                }

                if (!string.Equals(a: m.Creation.Hash, b: canonical.Hash, comparisonType: StringComparison.Ordinal)) {
                    candidate = current;
                    reason = $"creation '{m.Creation.Id}' hash '{m.Creation.Hash}' does not match the canonical sha256 '{canonical.Hash}' — a hash must come from the canonicalize pipeline";

                    return false;
                }

                candidate = (current with { Creations = Upsert(list: current.Creations, item: (m.Creation with { Document = canonical.Document }), keyOf: static creation => creation.Id) });

                return true;
            }
            case WorldMutation.RemoveCreation m: {
                // The conservative no-cascade ruling: a creation with live placements rejects loudly rather than
                // silently unstamping the world (remove the placements first; undo replay stays order-honest).
                var referencing = 0;

                foreach (var placement in current.Placements) {
                    if (string.Equals(a: placement.CreationId, b: m.Id, comparisonType: StringComparison.Ordinal)) {
                        referencing++;
                    }
                }

                if (referencing > 0) {
                    candidate = current;
                    reason = $"creation '{m.Id}' has {referencing} live placement(s) — remove them first";

                    return false;
                }

                if (!Remove(list: current.Creations, key: m.Id, keyOf: static creation => creation.Id, result: out var creations)) {
                    candidate = current;
                    reason = $"no creation with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Creations = creations });

                return true;
            }
            case WorldMutation.UpsertPlacement m:
                candidate = (current with { Placements = Upsert(list: current.Placements, item: m.Placement, keyOf: static placement => placement.Id) });

                return true;
            case WorldMutation.RemovePlacement m: {
                // The no-cascade guard: a placement a speaker anchors to rejects loudly naming the dependents, never
                // silently unanchoring the speaker (full-document revalidation would also catch the dangling anchor,
                // but the guard names WHO depends rather than echoing a validator path).
                if (DescribeSpeakersAnchoredTo(speakers: current.Speakers, placementId: m.Id) is { } anchored) {
                    candidate = current;
                    reason = $"placement '{m.Id}' anchors speaker(s) {anchored} — remove or re-anchor them first";

                    return false;
                }

                if (!Remove(list: current.Placements, key: m.Id, keyOf: static placement => placement.Id, result: out var placements)) {
                    candidate = current;
                    reason = $"no placement with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Placements = placements });

                return true;
            }
            case WorldMutation.UpsertSpeaker m:
                candidate = (current with { Speakers = Upsert(list: current.Speakers, item: m.Speaker, keyOf: static speaker => speaker.Name) });

                return true;
            case WorldMutation.RemoveSpeaker m:
                if (!Remove(list: current.Speakers, key: m.Name, keyOf: static speaker => speaker.Name, result: out var speakers)) {
                    candidate = current;
                    reason = $"no speaker named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Speakers = speakers });

                return true;
            case WorldMutation.UpsertTune m: {
                // The hash contract at the compose boundary, identical to UpsertCreation's: canonicalize
                // the carried puck.audio.v1 document, REJECT a hash the pipeline did not itself compute, store the pair.
                Puck.Authoring.CanonicalDocument<Puck.Authoring.AudioDocument> canonical;

                try {
                    canonical = Puck.Authoring.AudioCanonicalizer.Canonicalize(document: m.Tune.Document, source: m.Tune.Id);
                } catch (Puck.Authoring.DocumentValidationException exception) {
                    candidate = current;
                    reason = exception.Message.ReplaceLineEndings(replacementText: " ");

                    return false;
                }

                if (!string.Equals(a: m.Tune.Hash, b: canonical.Hash, comparisonType: StringComparison.Ordinal)) {
                    candidate = current;
                    reason = $"tune '{m.Tune.Id}' hash '{m.Tune.Hash}' does not match the canonical sha256 '{canonical.Hash}' — a hash must come from the canonicalize pipeline";

                    return false;
                }

                candidate = (current with { Tunes = Upsert(list: current.Tunes, item: (m.Tune with { Document = canonical.Document }), keyOf: static tune => tune.Id) });

                return true;
            }
            case WorldMutation.RemoveTune m: {
                if (DescribeSpeakersSourcing(speakers: current.Speakers, matches: source => ((source is WorldSpeakerSource.Tune tune) && string.Equals(a: tune.TuneId, b: m.Id, comparisonType: StringComparison.Ordinal))) is { } dependents) {
                    candidate = current;
                    reason = $"tune '{m.Id}' feeds speaker(s) {dependents} — remove or re-source them first";

                    return false;
                }

                if (!Remove(list: current.Tunes, key: m.Id, keyOf: static tune => tune.Id, result: out var tunes)) {
                    candidate = current;
                    reason = $"no tune with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Tunes = tunes });

                return true;
            }
            case WorldMutation.UpsertPatch m: {
                Puck.Authoring.CanonicalDocument<Puck.Authoring.SynthPatchDocument> canonical;

                try {
                    canonical = Puck.Authoring.SynthPatchCanonicalizer.Canonicalize(document: m.Patch.Document, source: m.Patch.Id);
                } catch (Puck.Authoring.DocumentValidationException exception) {
                    candidate = current;
                    reason = exception.Message.ReplaceLineEndings(replacementText: " ");

                    return false;
                }

                if (!string.Equals(a: m.Patch.Hash, b: canonical.Hash, comparisonType: StringComparison.Ordinal)) {
                    candidate = current;
                    reason = $"patch '{m.Patch.Id}' hash '{m.Patch.Hash}' does not match the canonical sha256 '{canonical.Hash}' — a hash must come from the canonicalize pipeline";

                    return false;
                }

                candidate = (current with { Patches = Upsert(list: current.Patches, item: (m.Patch with { Document = canonical.Document }), keyOf: static patch => patch.Id) });

                return true;
            }
            case WorldMutation.RemovePatch m: {
                if (DescribePatchDependents(current: current, patchId: m.Id) is { } dependents) {
                    candidate = current;
                    reason = $"patch '{m.Id}' is referenced by {dependents} — remove or re-source them first";

                    return false;
                }

                if (!Remove(list: current.Patches, key: m.Id, keyOf: static patch => patch.Id, result: out var patches)) {
                    candidate = current;
                    reason = $"no patch with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Patches = patches });

                return true;
            }
            case WorldMutation.SetAudioDefaults m:
                candidate = (current with { Audio = m.Audio });

                return true;
            case WorldMutation.UpsertBindingOverlay m:
                candidate = (current with { BindingOverlays = Upsert(list: current.BindingOverlays, item: m.Overlay, keyOf: static overlay => overlay.Id) });

                return true;
            case WorldMutation.SetAuthoringDefaults m:
                candidate = (current with { Authoring = m.Authoring });

                return true;
            case WorldMutation.SetCollision m:
                candidate = (current with { Collision = m.Collision });

                return true;
            case WorldMutation.SetHostDefaults m:
                candidate = (current with { Host = m.Host });

                return true;
            case WorldMutation.SetViewDefaults m:
                candidate = (current with { Views = m.Views });

                return true;
            case WorldMutation.UpsertViewLayout m: {
                var views = current.Views;

                candidate = (current with { Views = (views with { Layouts = Upsert(list: (views.Layouts ?? []), item: m.Layout, keyOf: static layout => layout.Name) }) });

                return true;
            }
            case WorldMutation.RemoveViewLayout m: {
                var views = current.Views;

                if (!Remove(list: (views.Layouts ?? []), key: m.Name, keyOf: static layout => layout.Name, result: out var layouts)) {
                    candidate = current;
                    reason = $"no view layout named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Views = (views with { Layouts = layouts }) });

                return true;
            }
            case WorldMutation.RemoveBindingOverlay m:
                if (!Remove(list: current.BindingOverlays, key: m.Id, keyOf: static overlay => overlay.Id, result: out var overlays)) {
                    candidate = current;
                    reason = $"no binding overlay with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { BindingOverlays = overlays });

                return true;
            case WorldMutation.UpsertLook m:
                candidate = (current with { Looks = Upsert(list: current.Looks, item: m.Look, keyOf: static look => look.Name) });

                return true;
            case WorldMutation.RemoveLook m:
                if (!Remove(list: current.Looks, key: m.Name, keyOf: static look => look.Name, result: out var looks)) {
                    candidate = current;
                    reason = $"no look row named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Looks = looks });

                return true;
            case WorldMutation.SetLookAssignment m:
                candidate = (current with { LookAssignment = m.Assignment });

                return true;
            case WorldMutation.UpsertScreenLink m:
                candidate = (current with { Links = Upsert(list: current.Links, item: m.Link, keyOf: static link => link.Name) });

                return true;
            case WorldMutation.RemoveScreenLink m:
                if (!Remove(list: current.Links, key: m.Name, keyOf: static link => link.Name, result: out var links)) {
                    candidate = current;
                    reason = $"no cable link named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Links = links });

                return true;
            default:
                candidate = current;
                reason = "unknown mutation kind";

                return false;
        }
    }

    // Replace the row whose key matches the item's, or append it — the coarse whole-row upsert.
    private static IReadOnlyList<T> Upsert<T, TKey>(IReadOnlyList<T> list, T item, Func<T, TKey> keyOf) {
        var key = keyOf(arg: item);
        var result = new List<T>(capacity: (list.Count + 1));
        var replaced = false;

        foreach (var existing in list) {
            if (!replaced && EqualityComparer<TKey>.Default.Equals(x: keyOf(arg: existing), y: key)) {
                result.Add(item: item);
                replaced = true;
            } else {
                result.Add(item: existing);
            }
        }

        if (!replaced) {
            result.Add(item: item);
        }

        return result;
    }

    // Drop the first row whose key matches — reports whether a row was actually removed.
    private static bool Remove<T, TKey>(IReadOnlyList<T> list, TKey key, Func<T, TKey> keyOf, out IReadOnlyList<T> result) {
        var kept = new List<T>(capacity: list.Count);
        var removed = false;

        foreach (var existing in list) {
            if (!removed && EqualityComparer<TKey>.Default.Equals(x: keyOf(arg: existing), y: key)) {
                removed = true;

                continue;
            }

            kept.Add(item: existing);
        }

        result = kept;

        return removed;
    }

    // Build and deliver the tick's snapshot: every live body's authoritative sim pose, color, archetype, and this
    // tick's continuity hint. Skipped with no sink attached.
    private void EmitSnapshot(ulong tick, ulong stepTicks) {
        if (m_sink is null) {
            return;
        }

        var count = 0;

        for (var index = 0; (index < WorldPopulation.MaxPopulation); index++) {
            if (!m_population.IsActive(index: index) || (m_population.EntryBody(index: index) is not { } body)) {
                continue;
            }

            m_snapshotEntries[count++] = new EntitySnapshot(
                Index: index,
                Position: body.Position,
                Orientation: body.Orientation,
                BodyColor: m_population.BodyColor(index: index),
                Active: true,
                Kit: m_population.KitIndex(index: index),
                Look: m_population.LookIndex(index: index),
                Continuity: body.TakeContinuity(),
                PlacementId: m_population.InhabitantPlacementId(index: index)
            );
        }

        m_sink.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: tick,
            Revision: m_population.Revision,
            StepTicks: stepTicks,
            Entries: m_snapshotEntries.AsMemory(start: 0, length: count)
        ));
    }

    // One journal entry — the tick a mutation applied and the mutation itself (the edit history replay reproduces).
    private readonly record struct JournalEntry(ulong Tick, WorldMutation Mutation);

    // One buffered live-edit op, drained FIFO at the step boundary before intents.
    private abstract record PendingOp {
        public sealed record Mutate(WorldMutation Mutation) : PendingOp;
        public sealed record Swap(WorldDefinition Definition, WorldPrincipal Principal) : PendingOp;
        public sealed record Undo(int Count, WorldPrincipal Principal) : PendingOp;
    }
}
