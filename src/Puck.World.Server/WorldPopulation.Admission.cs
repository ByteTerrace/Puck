using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // Join one inhabited body at a claimed peer slot: mint its body from the resolved kit spawned at the placement's
    // scatter pose, seat its intent source, and tag the peer with the placement back-reference (the entry stays a
    // NetworkPeer — an inhabitant is a peer, not a separate kind).
    private void ActivateInhabitant(int index, WorldPlacement placement, WorldPlacementInhabit inhabit, byte kitIndex, int ordinal) {
        var entry = m_entries[index];
        var kit = m_kits[kitIndex];
        var body = new WorldBody(
            tuning: kit.Tuning,
            program: kit.BodyMotionProgram,
            programs: m_bodyMotionPrograms,
            actions: kit.Actions,
            actionThresholds: kit.ActionThresholds,
            actionShapes: kit.ActionShapes,
            roleMask: kit.RoleMask,
            roleOrdinals: kit.RoleOrdinals,
            actionState: kit.ActionState,
            collider: kit.Collider,
            rigid: kit.Rigid,
            carry: kit.Carry,
            maxSmoothError: m_fixedMotion.MaxSmoothError,
            holds: kit.Holds
        );

        body.SetContactConfiguration(
            field: m_contactField,
            upPolicy: m_bodyUpPolicy,
                walkableThreshold: m_walkableThreshold
        );
        body.SetGravityField(field: m_gravityField);
        body.SetAttachmentPolicy(policy: m_fixedAttachment);

        var spawn = InhabitantSpawn(
            placement: placement,
            distribution: inhabit.Distribution!,
            ordinal: ordinal,
            count: inhabit.Count
        );
        var altitude = FixedQ4816.FromDouble(value: placement.Position.Y);
        var yaw = FixedQ4816.FromDouble(value: (placement.YawDegrees * (Math.PI / 180.0)));

        body.Pose(
            position: spawn with { Y = altitude },
            yawRadians: yaw,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        // An inhabitant's home is the ground its row activated it on — the placement's position plus its own
        // distribution sample — so its producer wanders THERE rather than at the world origin.
        body.SetHome(home: (spawn with { Y = altitude }));

        body.SetIntentSource(source: inhabit.Source);
        entry.Body = body;
        entry.PlacementId = placement.Id;
        entry.KitIndex = kitIndex;
        entry.LookIndex = ResolveInhabitLook(placement: placement);
        entry.CatalogRig = WorldLookSource.Catalog.DefaultIndex(index);
        entry.ProducerState.PreferredAltitude = altitude;
        entry.ProducerState.AcquiredTarget = -1;
        entry.ProducerState.CurveArcRaw = 0L;
        entry.AutonomyState.Clear();
        entry.NavigationState.Clear();
        ClearDesignations(entry: entry);
        entry.Generation = checked((entry.Generation + 1));
        entry.Active = true;
    }
    // Activate a simulated entry: re-seed its canonical pose/color/wander from its index, then mint its own body from
    // its kit row (tuning + primary-action binding) spawned at that pose with the stored peer-source default. The
    // Warp/Face is a server-authoritative spawn (a one-time write into the sim); from here the pose flows only out.
    private void ActivateSimulated(int index, int? generation = null, IntentSource? source = null) {
        m_entries[index].CatalogRig = WorldLookSource.Catalog.DefaultIndex(index);
        SeedSimulated(index: index);

        var entry = m_entries[index];
        var kit = m_kits[entry.KitIndex];
        // Profileless — advances on the kit row's tuning with the row's lane bindings.
        var player = new WorldBody(
            tuning: kit.Tuning,
            program: kit.BodyMotionProgram,
            programs: m_bodyMotionPrograms,
            actions: kit.Actions,
            actionThresholds: kit.ActionThresholds,
            actionShapes: kit.ActionShapes,
            roleMask: kit.RoleMask,
            roleOrdinals: kit.RoleOrdinals,
            actionState: kit.ActionState,
            collider: kit.Collider,
            rigid: kit.Rigid,
            carry: kit.Carry,
            maxSmoothError: m_fixedMotion.MaxSmoothError,
            holds: kit.Holds
        );

        player.SetContactConfiguration(
            field: m_contactField,
            upPolicy: m_bodyUpPolicy,
                walkableThreshold: m_walkableThreshold
        );
        player.SetGravityField(field: m_gravityField);
        player.SetAttachmentPolicy(policy: m_fixedAttachment);

        player.Pose(
            position: entry.SpawnPosition,
            yawRadians: entry.SpawnYaw,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        player.SetHome(home: entry.SpawnPosition);

        player.SetIntentSource(source: (source ?? m_defaultPeerSource));
        entry.Body = player;
        entry.AutonomyState.Clear();
        ClearDesignations(entry: entry);
        entry.Generation = (generation ?? checked((entry.Generation + 1)));
    }
    private int CountExternalNetworkPlayers() {
        var count = 0;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (
                m_entries[index].Active &&
                (m_entries[index].IsRemoteHuman || m_entries[index].IsAuthorityTransferred)
            ) {
                count++;
            }
        }
        return count;
    }
    private int CountNetworkPlayers() {
        var count = 0;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (
                m_entries[index].Active &&
                (m_entries[index].PlacementId is null)
            ) {
                count++;
            }
        }
        return count;
    }
    private WorldPeerEventEntry PeerEventEntry(int index) {
        var entry = m_entries[index];
        var identity = WorldPrincipal.Peer(
            index: index,
            generation: entry.Generation
        );

        return new WorldPeerEventEntry(
            BodyIndex: index,
            Generation: entry.Generation,
            Source: (entry.Body?.Source ?? m_defaultPeerSource),
            Identity: identity,
            IdentityDomain: entry.IdentityDomain,
            IdentitySubject: entry.IdentitySubject,
            AuthorityTransferred: entry.IsAuthorityTransferred,
            PlacementId: entry.PlacementId,
            CatalogRig: entry.CatalogRig
        );
    }
    // Retire an inhabited peer slot back to an inactive census peer (its body dropped, its placement tag cleared). The
    // slot was already a NetworkPeer; only the placement back-reference and body go.
    private void RetireInhabitant(int index) {
        var entry = m_entries[index];

        entry.Body = null;
        entry.PlacementId = null;
        entry.Active = false;
        entry.ProducerState.AcquiredTarget = -1;
        entry.ProducerState.CurveArcRaw = 0L;
        entry.AutonomyState.Clear();
        entry.NavigationState.Clear();
        ClearDesignations(entry: entry);
    }
    private bool TryAdmitTransferredEntityAtCore(int slot, IntentSource source, bool remoteHuman, bool authorityTransferred, IReadOnlyList<WorldAdmissionGrant> grantTemplates, string identityDomain, string identitySubject, out WorldPeerEventEntry admitted, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);

        if (
            (slot < LocalSeatCount) ||
            (slot >= Capacity) ||
            m_entries[slot].Active
        ) {
            admitted = default;
            refusal = ((slot < 0)
                ? $"the {Capacity}-slot entity table is full"
                : $"reserved peer body:{slot} is no longer free"
            );

            return false;
        }

        if (CountNetworkPlayers() >= m_remoteCap) {
            admitted = default;
            refusal = $"the networkPlayers admission cap ({m_remoteCap}) is already met";

            return false;
        }

        if (!SupportsSource(
            index: slot,
            refusal: out refusal,
            source: source
        )) {
            admitted = default;
            refusal = $"reserved peer body:{slot} {refusal}";
            return false;
        }

        ActivateSimulated(
            index: slot,
            source: source
        );

        var entry = m_entries[slot];

        entry.Active = true;
        entry.IsRemoteHuman = remoteHuman;
        entry.IsAuthorityTransferred = authorityTransferred;
        // The server-authored PeerAdmitted event applies the requested rows through the live grant door immediately
        // after this allocation and then records ONLY the rows that succeeded. Nothing is installed yet at this
        // point, so the revocation baseline must begin empty rather than containing authored attempts.
        entry.AdmissionInstalledGrantTemplates = [];
        entry.AdmissionRevokedKeys.Clear();
        entry.IdentityDomain = (identityDomain ?? string.Empty);
        entry.IdentitySubject = (identitySubject ?? string.Empty);
        m_simulatedCount = CountActiveCensus();
        m_revision++;
        admitted = PeerEventEntry(index: slot);
        refusal = string.Empty;

        return true;
    }

    /// <summary>Activates a local seat (indices <c>0..</c><see cref="LocalSeatCount"/>) — the session join's server
    /// half: mints the seat's body at its full authored spawn pose, seated on <paramref name="profile"/>. A no-op if
    /// the seat is already active. Bumps the revision.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The profile the seat's body reads speeds and color from, or <see langword="null"/>.</param>
    public void ActivateSeat(int slot, WorldIdentity? profile) {
        var entry = m_entries[slot];

        if (entry.Active) {
            return;
        }

        // The seat body constructs from the definition's designated seat kit row (its tuning and lane bindings); the
        // seated profile's speeds still override live.
        var body = new WorldBody(
            tuning: m_kits[m_seatKit].Tuning,
            program: m_kits[m_seatKit].BodyMotionProgram,
            programs: m_bodyMotionPrograms,
            actions: m_kits[m_seatKit].Actions,
            actionThresholds: m_kits[m_seatKit].ActionThresholds,
            actionShapes: m_kits[m_seatKit].ActionShapes,
            roleMask: m_kits[m_seatKit].RoleMask,
            roleOrdinals: m_kits[m_seatKit].RoleOrdinals,
            actionState: m_kits[m_seatKit].ActionState,
            collider: m_kits[m_seatKit].Collider,
            rigid: m_kits[m_seatKit].Rigid,
            carry: m_kits[m_seatKit].Carry,
            maxSmoothError: m_fixedMotion.MaxSmoothError,
            holds: m_kits[m_seatKit].Holds
        ) {
            Profile = profile,
        };

        body.SetContactConfiguration(
            field: m_contactField,
            upPolicy: m_bodyUpPolicy,
                walkableThreshold: m_walkableThreshold
        );
        body.SetGravityField(field: m_gravityField);
        body.SetAttachmentPolicy(policy: m_fixedAttachment);

        var spawnPoint = m_seatSpawns[slot];

        body.Pose(
            position: spawnPoint.Position,
            yawRadians: spawnPoint.YawRadians,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        body.SetHome(home: spawnPoint.Position);
        // Seats default Live and are never touched by population operations; producer state is seeded so a later
        // body.control producer:<name> uses the same deterministic path as a peer.
        ClearDesignations(entry: entry);
        SeedSeatWander(slot: slot);
        entry.Body = body;
        entry.BodyColor = (profile?.Color ?? Vector3.Zero);
        entry.CatalogRig = WorldLookSource.Catalog.DefaultIndex(slot);
        entry.Generation = checked((entry.Generation + 1));
        entry.Active = true;
        m_revision++;
    }
    /// <summary>Re-applies one recorded admission through the population door. A live event reaches this after the
    /// point of effect and is idempotent (<see cref="TryAdmitRemotePeer"/> already set every field this touches);
    /// replay reaches it before the effect and reconstructs the body — including the <see cref="Entry.IsRemoteHuman"/>
    /// marker, inferred from <see cref="WorldPeerEventEntry.Source"/> being <see cref="IntentSource.Live"/> (the
    /// document-authored census/inhabitant defaults are never <see cref="IntentSource.Live"/>).</summary>
    /// <param name="peer">The recorded peer entry.</param>
    /// <param name="grantTemplates">The admission templates reconstructed from this event's concrete minted grant
    /// rows. Empty for a document-authored/simulated peer and for a legitimately zero-grant remote identity.</param>
    public void ApplyPeerAdmitted(in WorldPeerEventEntry peer, IReadOnlyList<WorldAdmissionGrant> grantTemplates) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);

        if (((uint)(peer.BodyIndex - LocalSeatCount)) >= PeerCapacity) {
            return;
        }

        var entry = m_entries[peer.BodyIndex];

        if (!entry.Active) {
            ActivateSimulated(
                index: peer.BodyIndex,
                generation: peer.Generation,
                source: peer.Source
            );
            entry.Active = true;
            entry.IsRemoteHuman = (peer.Source == IntentSource.Live);
            m_simulatedCount = CountActiveCensus();
            m_revision++;
        }
        // A resumed connection rides the same PeerAdmitted event as a fresh one, and a replay reaches it against an
        // entry the recorded disconnect left parked — unpark it here so the re-drive lands where the live resume
        // did. Live, TryResumeParkedPeer already cleared the park (and bumped the revision), so this is idempotent
        // there; the generation guard keeps a hypothetical different-generation admission at a parked index from
        // silently unparking a stranger's body.
        if (
            entry.Parked &&
            (entry.Generation == peer.Generation)
        ) {
            entry.Parked = false;
            entry.ParkedUntilTick = null;
            m_revision++;
        }

        entry.IsAuthorityTransferred = peer.AuthorityTransferred;
        entry.PlacementId = peer.PlacementId;
        entry.CatalogRig = peer.CatalogRig;

        // Live admission already installed these fields before emitting the event, so this is idempotent there.
        // Replay reaches this path with a fresh population and needs the verified identity restored so a later
        // recorded rebuild re-authorizes the peer against the same facts the live rebuild consulted.
        entry.AdmissionInstalledGrantTemplates = grantTemplates;
        entry.AdmissionRevokedKeys.Clear();
        entry.IdentityDomain = peer.IdentityDomain;
        entry.IdentitySubject = peer.IdentitySubject;
    }
    /// <summary>Re-applies one recorded disconnect through the population door. Park-with-grace: on the same terms as
    /// <see cref="DeactivateSeat"/>, this defers the body/occupancy half of the teardown (<see cref="Entry.Body"/>,
    /// <see cref="Entry.Active"/>, <see cref="Entry.IsRemoteHuman"/>) to <see cref="ReclaimExpiredParks"/> when the
    /// compiled grace is positive or <see cref="CompiledTickDuration.IsNever"/> (a positive authored grace at
    /// simulation rate 0 — see <see cref="DeactivateSeat"/>'s own remarks) — the entry marks
    /// <see cref="Entry.Parked"/> instead, and <see cref="IsAdmittedPeer"/> (hence <see cref="IsHumanOccupied"/>)
    /// keeps reading <see langword="true"/> through the grace window since <see cref="Entry.IsRemoteHuman"/> is
    /// untouched. Only the BODY half is deferred: the disconnected generation's grant rows are released
    /// unconditionally by the caller (<c>Server.WorldServer.ApplyServerEvent</c>) — authority follows the
    /// connection, and a verified-identity reconnect that resumes this body
    /// (<see cref="TryResumeParkedPeer"/>) re-mints its admission templates through the ordinary
    /// <c>PeerAdmitted</c> event; see that arm for the argument.</summary>
    /// <param name="peer">The recorded peer entry.</param>
    /// <param name="tick">The current tick — the basis a finite <see cref="Entry.ParkedUntilTick"/> is stamped
    /// from.</param>
    public void ApplyPeerDisconnected(in WorldPeerEventEntry peer, ulong tick) {
        if (((uint)(peer.BodyIndex - LocalSeatCount)) >= PeerCapacity) {
            return;
        }

        var entry = m_entries[peer.BodyIndex];

        if (
            entry.Active &&
            (entry.Generation == peer.Generation)
        ) {
            if (m_reconnectGraceTicks.IsNever) {
                entry.Parked = true;
                entry.ParkedUntilTick = null;
            } else if (m_reconnectGraceTicks.IsZero) {
                entry.Body = null;
                entry.Active = false;
                entry.IsRemoteHuman = false;
                entry.IsAuthorityTransferred = false;
                entry.PlacementId = null;
                entry.AdmissionInstalledGrantTemplates = [];
                entry.AdmissionRevokedKeys.Clear();
                entry.IdentityDomain = string.Empty;
                entry.IdentitySubject = string.Empty;
                entry.Parked = false;
                entry.ParkedUntilTick = null;
            } else {
                entry.Parked = true;
                entry.ParkedUntilTick = unchecked((((long)tick) + m_reconnectGraceTicks.Ticks));
            }

            m_simulatedCount = CountActiveCensus();
            m_revision++;
        }
    }
    /// <summary>Deactivates a local seat — the session leave's server half. A no-op if the seat is not active.
    /// Park-with-grace: when the compiled grace (<see cref="m_reconnectGraceTicks"/>) is positive or
    /// <see cref="CompiledTickDuration.IsNever"/>, this does not drop the body — it marks the entry
    /// <see cref="Entry.Parked"/> and, for a finite grace, stamps <see cref="Entry.ParkedUntilTick"/> (left
    /// <see langword="null"/> for never — a rate-0 world has no tick to stamp a deadline at, so the body parks
    /// forever instead of tearing down), keeping the body (pose, durable state) in the sim/collider set and
    /// <see cref="IsHumanOccupied"/> reading <see langword="true"/> exactly as before the leave. The full teardown
    /// this method used to perform unconditionally now fires from <see cref="ReclaimExpiredParks"/> once a finite
    /// grace window passes with no matching re-Join (see <see cref="TryResumeParkedSeat"/>) — never, for never. An
    /// authored-disabled grace (<see cref="CompiledTickDuration.IsZero"/>, distinct from never: a positive authored
    /// grace at rate 0 is never, an authored zero is disabled at any rate) keeps the immediate-teardown behavior
    /// exactly as authored (the grace window is opt-in, not a forced behavior change for a world that authors none).
    /// Bumps the revision either way.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="tick">The current tick — the basis a finite <see cref="Entry.ParkedUntilTick"/> is stamped from
    /// (<c>tick + reconnectGraceTicks</c>).</param>
    public void DeactivateSeat(int slot, ulong tick) {
        var entry = m_entries[slot];

        if (!entry.Active) {
            return;
        }

        if (m_reconnectGraceTicks.IsNever) {
            entry.Parked = true;
            entry.ParkedUntilTick = null;
            m_revision++;

            return;
        }

        if (m_reconnectGraceTicks.IsZero) {
            entry.Body = null;
            entry.Active = false;
            entry.Parked = false;
            entry.ParkedUntilTick = null;
            m_revision++;

            return;
        }

        entry.Parked = true;
        entry.ParkedUntilTick = unchecked((((long)tick) + m_reconnectGraceTicks.Ticks));
        m_revision++;
    }
    /// <summary>Returns a value indicating whether <paramref name="bodyIndex"/> is bound to a remote-admitted human.
    /// Live for a body a <see cref="TryAdmitRemotePeer"/> call is still holding (see
    /// <see cref="Entry.IsRemoteHuman"/>); a socket door's disconnect clears it through
    /// <see cref="ApplyPeerDisconnected"/> exactly as admission set it.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public bool IsAdmittedPeer(int bodyIndex) => ((((uint)bodyIndex) < Capacity) && m_entries[bodyIndex].IsRemoteHuman);
    /// <summary>Returns a value indicating whether <paramref name="bodyIndex"/> is human-occupied — the co-driving
    /// fold's occupancy discriminator (and the bot-overwrite door in <c>WorldServer.ApplyIntentSubmission</c>): a
    /// body is human-occupied iff a local seat slot is <see cref="IsActive"/> and bound to it, or the body is bound
    /// to an <see cref="IsAdmittedPeer"/> — never <see cref="WorldBody.Source"/> (what fills gaps; its
    /// <see cref="IntentSource.Live"/> value also covers a remote peer) and never engagement (an orthogonal axis).
    /// The pool this gates exists only when this returns <see langword="true"/>: an unoccupied body is a bot at full
    /// authority by construction, not by an undefined ceiling.
    /// <para><b>A parked body (see <see cref="Entry.Parked"/>) still reads <see langword="true"/> here</b> —
    /// <see cref="IsActive"/>/<see cref="IsAdmittedPeer"/> are exactly what a park leaves untouched, by construction,
    /// so no separate parked-aware branch exists in this method. A disconnected-but-parked body stays targetable and
    /// its CC pool keeps running offline through the grace window; only <see cref="ReclaimExpiredParks"/>'s eventual
    /// teardown removes it from the pool.</para></summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <returns><see langword="true"/> when the index is bound to a live local seat or an admitted peer.</returns>
    public bool IsHumanOccupied(int bodyIndex) =>
        (((((uint)bodyIndex) < LocalSeatCount) && IsActive(index: bodyIndex)) || IsAdmittedPeer(bodyIndex: bodyIndex));
    /// <summary>Returns a value indicating whether <paramref name="index"/> currently holds a parked body (see <see cref="Entry.Parked"/>) —
    /// the general form of <see cref="IsSeatParked"/>, valid for a local seat or a peer index alike. The read-back
    /// verb's own enumeration gate.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns><see langword="true"/> when the index holds a parked body.</returns>
    public bool IsParked(int index) => ((((uint)index) < Capacity) && (m_entries[index] is { Active: true, Parked: true }));
    /// <summary>Returns a value indicating whether <paramref name="slot"/> holds a body currently parked (see <see cref="Entry.Parked"/>) —
    /// the resume-eligibility gate a re-Join checks before <see cref="ActivateSeat"/> would mint a fresh body.
    /// <see langword="false"/> for an out-of-range slot, an inactive slot, or an active-but-never-left one.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    public bool IsSeatParked(int slot) => ((((uint)slot) < LocalSeatCount) && (m_entries[slot] is { Active: true, Parked: true }));
    /// <summary>Reads the reconnect-park reserved rule channel's live value for one body — the remaining grace ticks
    /// (<c>ParkedUntilTick - tick</c>, floored at zero) when the body is parked with a finite deadline, <c>0</c> for
    /// an active, unparked, or out-of-range body alike, and <see langword="null"/> for a body parked forever
    /// (<see cref="Entry.ParkedUntilTick"/> is <see langword="null"/> — a positive grace compiled at simulation rate
    /// 0; see <see cref="DeactivateSeat"/>'s own remarks).
    /// <para><b>Forever is not a number, and every consumer says so in its own vocabulary.</b> <c>world.parked</c>
    /// renders <c>never</c> for both fields; the <c>$parked:</c> reserved rule channel
    /// (<see cref="WorldRuleFacts.ParkedPrefix"/>, read through <c>Server.WorldServer.ReadWorldFact</c>'s
    /// <c>WorldRuleFactKind.Parked</c> arm) carries it as positive infinity — <c>remaining &gt; 0</c> holds (the
    /// seat is parked, more so than any other), <c>remaining &gt; any finite</c> holds, <c>remaining &lt;= any
    /// finite</c> does not, and a copy operand alone cannot fire because there is no representable number to store
    /// (the <c>ActionStateComparisons</c> infinity-aware overload owns the comparison semantics). That is exactly
    /// what the expiry sweep already says on the deadline side, where <c>signedTick &gt;= ParkedUntilTick</c> never
    /// fires against a null deadline: the channel repeats what the sweep says rather than inventing a third answer.
    /// Reading forever as no fact was considered and rejected — it would make the most-parked seat of all invisible
    /// to a rule gated on <c>remaining &gt; 0</c>, a lie by omission rather than by sentinel; and a numeric sentinel
    /// was rejected because a rule could not tell it from an authored literal.</para></summary>
    /// <param name="index">The resolved 0-based entity index, or a negative sentinel for "no body".</param>
    /// <param name="tick">The current tick.</param>
    /// <returns>The remaining grace ticks when the body is parked with a finite deadline; <see langword="null"/>
    /// when parked forever (a deadline that will never arrive is not a count — see <c>ParkedUntilTick</c>'s own
    /// remarks for why no numeric sentinel is admissible); <c>0</c> for an active, unparked, or out-of-range
    /// body.</returns>
    public long? ParkedRemainingTicks(int index, ulong tick) {
        if (((uint)index) >= Capacity) {
            return 0L;
        }

        var entry = m_entries[index];

        if (!(entry is { Active: true, Parked: true })) {
            return 0L;
        }

        if (entry.ParkedUntilTick is not { } deadline) {
            return null;
        }

        return Math.Max(
            val1: 0L,
            val2: (deadline - unchecked((long)tick))
        );
    }
    /// <summary>The admission templates that actually reached the live grant table for the connection bound to
    /// <paramref name="bodyIndex"/> (see <see cref="TryAdmitRemotePeer"/>), or empty when the slot is not a
    /// remote-admitted peer. <see cref="Server.WorldServer.RemintPeerAdmissionGrants"/>'s one read.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    public IReadOnlyList<WorldAdmissionGrant> PeerAdmissionInstalledGrantTemplates(int bodyIndex) =>
        ((((uint)bodyIndex) < Capacity)
            ? m_entries[bodyIndex].AdmissionInstalledGrantTemplates
            : []
        );
    /// <summary>Gets admission-grant keys explicitly revoked during this connection. Unlike the current policy
    /// baseline, these survive a policy generation that temporarily removes the row, and are cleared when the live
    /// table shows the row was explicitly granted back.</summary>
    /// <param name="bodyIndex">The 0-based remote-peer body index.</param>
    public IReadOnlySet<(WorldCapability Capability, GrantSubject Subject)> PeerAdmissionRevokedKeys(int bodyIndex) =>
        ((((uint)bodyIndex) < Capacity)
            ? m_entries[bodyIndex].AdmissionRevokedKeys
            : EmptyAdmissionRevokedKeys
        );
    /// <summary>Gets a value indicating whether the body at <paramref name="bodyIndex"/> arrived through an authority
    /// transfer rather than a connection handshake, which decides which admission-door arm re-authorizes it.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    public bool PeerAuthorityTransferred(int bodyIndex) =>
        ((((uint)bodyIndex) < Capacity) && m_entries[bodyIndex].IsAuthorityTransferred);
    /// <summary>The verified admission identity's own (Domain, Subject) for the connection currently bound to
    /// <paramref name="bodyIndex"/> (see <see cref="TryAdmitRemotePeer"/>), or two empty strings when the slot is
    /// not a remote-admitted peer. <see cref="Server.WorldServer.RemintPeerAdmissionGrants"/>'s re-authorization
    /// key — it re-matches this pair against the rebuild candidate's own admission entries
    /// (<see cref="WorldAdmissionDoor.TryMatchEntry"/>) rather than trusting <see cref="PeerAdmissionInstalledGrantTemplates"/>
    /// is still what the current document would mint.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    public (string Domain, string Subject) PeerIdentity(int bodyIndex) =>
        ((((uint)bodyIndex) < Capacity)
            ? (m_entries[bodyIndex].IdentityDomain, m_entries[bodyIndex].IdentitySubject)
            : (string.Empty, string.Empty)
        );
    /// <summary>Gets the current generation-bearing peer identity for a peer slot.</summary>
    /// <param name="index">The peer body index.</param>
    /// <returns>The current peer principal.</returns>
    public WorldPrincipal PeerPrincipal(int index) => WorldPrincipal.Peer(
        index: index,
        generation: m_entries[index].Generation
    );
    /// <summary>Tears down every entry parked past its grace deadline — the deferred half of
    /// <see cref="DeactivateSeat"/>/<see cref="ApplyPeerDisconnected"/>'s teardown (see <see cref="Entry.Parked"/>'s
    /// own remarks): drops the body, clears <see cref="Entry.Active"/> and (for a peer) <see cref="Entry.IsRemoteHuman"/>,
    /// exactly as an immediate disconnect already did before park-with-grace existed. Covers both local seats and
    /// peers in one pass — the same <c>Active &amp;&amp; Parked</c> gate discriminates a park regardless of
    /// <see cref="PopulationKind"/>, so there is no separate seat/peer sweep. Grant rows are never this sweep's to
    /// release: a peer generation's rows go at its <c>PeerDisconnected</c> event, and a restored parked generation's
    /// go at <c>Server.WorldServer.RestoreCheckpoint</c> — by the time a park expires here, its principal holds
    /// nothing. Driven purely by <paramref name="tick"/> — no wall clock, no
    /// randomness — so it is exactly as replay-deterministic as <c>Server.WorldServer.ReclaimExpiredEscrows</c>,
    /// which this mirrors and is swept beside every tick.
    /// <para><b>Revival re-stamp.</b> This method is per-tick and so never runs for a rate-0 world (the step loop
    /// that calls it is itself skipped — see <c>WorldInstanceHost</c>'s stepping gate); a seat that parked with
    /// <see cref="Entry.ParkedUntilTick"/> <see langword="null"/> (a positive reconnect grace compiled against rate
    /// 0 — <see cref="CompiledTickDuration.IsNever"/>) therefore stays exactly as parked, untouched, until the world
    /// steps again. <see cref="Rebuild"/> recompiles <see cref="m_reconnectGraceTicks"/> against whatever rate a
    /// reload delivers, but it only ever touches the compiled tables — it does not walk live entries — so the first
    /// sweep after a revival to a positive rate is exactly the moment a null-forever deadline is resolved against
    /// the now-finite grace: it is dropped and re-derived, never left stranded. A null deadline with a still-never
    /// compiled grace (the world reloaded but is still rate 0, or reloaded at a positive rate with the grace itself
    /// re-authored as never — not possible today, since never only arises at rate 0, but the branch reads correctly
    /// either way) is left null, exactly as before. A freshly-stamped entry is deliberately not evaluated for
    /// teardown in the same pass — the visitor's window restarts at the revival tick, so it must survive at least
    /// one full sweep before it can expire.</para></summary>
    /// <param name="tick">The current (just-completed) simulation tick.</param>
    public void ReclaimExpiredParks(ulong tick) {
        var signedTick = unchecked((long)tick);
        var changed = false;

        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (!(entry is { Active: true, Parked: true })) {
                continue;
            }

            if (entry.ParkedUntilTick is not { } deadline) {
                // A NEVER park (see this method's own "Revival re-stamp" remarks). Only re-derivable once the
                // compiled grace itself is no longer NEVER — a rate-0 world never reaches this method at all, so
                // reading m_reconnectGraceTicks.IsNever here is exactly "has this world been revived to a positive
                // rate since the park happened".
                if (m_reconnectGraceTicks.IsNever) {
                    continue;
                }

                entry.ParkedUntilTick = (signedTick + m_reconnectGraceTicks.Ticks);
                changed = true;

                continue;
            }

            if (signedTick >= deadline) {
                entry.Body = null;
                entry.Active = false;
                entry.Parked = false;
                entry.ParkedUntilTick = null;
                entry.IsAuthorityTransferred = false;
                entry.PlacementId = null;

                if (entry.IsRemoteHuman) {
                    entry.IsRemoteHuman = false;
                    entry.AdmissionInstalledGrantTemplates = [];
                    entry.AdmissionRevokedKeys.Clear();
                    entry.IdentityDomain = string.Empty;
                    entry.IdentitySubject = string.Empty;
                }

                changed = true;
            }
        }

        if (changed) {
            m_simulatedCount = CountActiveCensus();
            m_revision++;
        }
    }
    /// <summary>Reconciles the inhabited-body registrations against the delivered definition (called from the server's
    /// Install after <see cref="Rebuild(WorldDefinition, WorldSolidField?)"/>): a placement's inhabit facet joins bodies
    /// into the peer slice over the loopback link — an inhabitant is a <see cref="PopulationKind.NetworkPeer"/> whose entry
    /// carries a placement back-reference, holding a normal <see cref="WorldBody"/> under the resolved kit and driven by
    /// its kit's attend producer. Bodies claim the highest free slots (capacity minus one downward) so an existing inhabitant never
    /// renumbers; admission is bounded only by the table itself and rejects loudly when it is genuinely full — there is no
    /// census-fit reservation. Diff-by-placement: retire an entry whose row vanished, lost its facet, or changed
    /// creation/kit; keep a matching one (its pose survives an unrelated placement edit); admit new bodies at the highest
    /// free slots. The census ceiling (<see cref="MaxSimulated"/>) follows all non-census physical occupancy, and the
    /// census is re-clamped without renumbering an inhabitant or transferred entity.</summary>
    /// <param name="definition">The delivered definition (its placements, creations, kits, and look table).</param>
    /// <param name="admitted">Optional sink for the peer generations admitted by the reconciliation.</param>
    /// <param name="disconnected">Optional sink for the peer generations disconnected by the reconciliation.</param>
    public void ReconcileInhabitants(WorldDefinition definition, List<WorldPeerEventEntry>? admitted = null, List<WorldPeerEventEntry>? disconnected = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        // Pass 1 — retire inhabited slots whose placement/facet/creation-kit binding no longer holds. A surviving slot
        // keeps its body (pose preserved); a kit change recompiles in place. An inhabited entry is a peer carrying a
        // placement back-reference; a plain census peer (no PlacementId) is left untouched.
        for (var index = (Capacity - 1); (index >= LocalSeatCount); index--) {
            var entry = m_entries[index];

            if (entry.PlacementId is null) {
                continue;
            }

            if (
                (entry.PlacementId is not { } placementId) ||
                (FindInhabited(
                definition: definition,
                placementId: placementId
            ) is not { } placement) ||
                (ResolveInhabitKit(
                definition: definition,
                placement: placement
            ) is not { } kitName) ||
                (ResolveKitOrNull(name: kitName) is not { } kitIndex)
            ) {
                disconnected?.Add(item: PeerEventEntry(index: index));
                RetireInhabitant(index: index);

                continue;
            }

            entry.KitIndex = kitIndex;
            entry.LookIndex = ResolveInhabitLook(placement: placement);
            entry.Body?.SetIntentSource(source: placement.Inhabit!.Source);
            entry.Body?.RecompileKit(
                tuning: m_kits[kitIndex].Tuning,
                actions: m_kits[kitIndex].Actions,
                actionThresholds: m_kits[kitIndex].ActionThresholds,
                actionShapes: m_kits[kitIndex].ActionShapes,
                roleMask: m_kits[kitIndex].RoleMask,
                roleOrdinals: m_kits[kitIndex].RoleOrdinals,
                actionState: m_kits[kitIndex].ActionState,
                program: m_kits[kitIndex].BodyMotionProgram,
                programs: m_bodyMotionPrograms,
                collider: m_kits[kitIndex].Collider,
                rigid: m_kits[kitIndex].Rigid,
                carry: m_kits[kitIndex].Carry,
                maxSmoothError: m_fixedMotion.MaxSmoothError,
                holds: m_kits[kitIndex].Holds
            );
        }

        // Pass 2 — grow/shrink each inhabited placement to its declared count, at the highest free slots (document order).
        foreach (var placement in definition.Placements) {
            if (
                (placement.Inhabit is not { } inhabit) ||
                (ResolveInhabitKit(
                definition: definition,
                placement: placement
            ) is not { } kitName) ||
                (ResolveKitOrNull(name: kitName) is not { } kitIndex)
            ) {
                continue;
            }

            var desired = Math.Clamp(
                value: inhabit.Count,
                min: 0,
                max: PeerCapacity
            );
            var live = CountInhabitants(placementId: placement.Id);

            for (var ordinal = live; (ordinal < desired); ordinal++) {
                var slot = HighestFreeSlot();

                if (slot < 0) {
                    Console.Error.WriteLine(value: $"[world.placement: inhabited '{placement.Id}' has no free entity slot — the {Capacity}-slot table is full]");

                    break;
                }

                ActivateInhabitant(
                    index: slot,
                    inhabit: inhabit,
                    kitIndex: kitIndex,
                    ordinal: ordinal,
                    placement: placement
                );
                admitted?.Add(item: PeerEventEntry(index: slot));
            }

            for (var extra = desired; (extra < live); extra++) {
                var slot = LowestInhabitant(placementId: placement.Id);

                if (slot >= 0) {
                    disconnected?.Add(item: PeerEventEntry(index: slot));
                    RetireInhabitant(index: slot);
                }
            }
        }

        // Re-clamp the census against every entity-table slot now owned by an inhabitant or transferred authority.
        _ = SetSimulatedCount(count: m_simulatedCount);
        m_revision++;
    }
    /// <summary>Restores a just-detached federated peer after an aborted transfer, preserving its generation,
    /// admission facts, pose, dynamic state, and designation registers.</summary>
    public bool RestoreDetachedPeer(in WorldPeerEventEntry peer, IReadOnlyList<WorldAdmissionGrant> grantTemplates, WorldIdentity? profile, FixedVector3 position, FixedQ4816 yawRadians, WorldBody.TransferState dynamicState, IReadOnlyList<WorldTargetDesignation>? designations = null) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);
        if (
            (((uint)(peer.BodyIndex - LocalSeatCount)) >= PeerCapacity) ||
            m_entries[peer.BodyIndex].Active
        ) {
            return false;
        }

        ApplyPeerAdmitted(
            grantTemplates: grantTemplates,
            peer: in peer
        );
        var entry = m_entries[peer.BodyIndex];

        if (entry.Body is not { } body) {
            return false;
        }

        body.Profile = profile;
        entry.BodyColor = (profile?.Color ?? Vector3.Zero);
        body.Pose(
            position: position,
            yawRadians: yawRadians,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        body.ApplyTransferState(state: dynamicState);
        ClearDesignations(entry: entry);
        if (designations is not null) {
            var count = Math.Min(
                val1: designations.Count,
                val2: entry.Designations.Length
            );

            for (var index = 0; (index < count); index++) {
                entry.Designations[index] = designations[index];
            }
        }

        return true;
    }
    /// <summary>Restores a body <see cref="TryDetachSeatForTransfer"/> just detached back onto its original seat at
    /// the exact pose it held at detach — the abort half of a same-process transfer's atomic move. Unlike
    /// <see cref="ActivateSeat"/>'s fresh-spawn path, the body is posed at <paramref name="position"/>/<paramref name="yawRadians"/>
    /// instead of the seat's authored spawn point, so a transfer that must abort after this seat already departed
    /// restores play exactly where it left off rather than teleporting it home. The seat kit every local seat
    /// constructs today authors no <c>drive</c> row, so <see cref="WorldBody.FixedOrientation"/> is
    /// always a pure yaw rotation (pitch = roll = 0) for a seat body — capturing position and yaw alone therefore
    /// reconstructs the departed body's orientation bit-for-bit, the identical construction <see cref="ActivateSeat"/>'s
    /// own spawn already relies on. A seat kit that someday adopts a genuine free or driven attitude for a local seat
    /// would need this method (or a sibling) to accept the full orientation instead.
    /// <para><b>Dynamic state.</b>
    /// <paramref name="dynamicState"/> carries the perceivable subset <see cref="WorldBody.CaptureTransferState"/>
    /// read off the departed body before <see cref="TryDetachSeatForTransfer"/> discarded it — velocity, a live dash
    /// overlay, and in-flight timed-press state (see that struct's own remarks for exactly what and why). It is
    /// applied via <see cref="WorldBody.ApplyTransferState"/> after <see cref="WorldBody.Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/>
    /// below — the abort-refire invariant's own ordering: <c>Pose</c> is the same hard-teleport commit
    /// <see cref="WorldBody.Reconcile"/> and every other discontinuity in this engine routes through
    /// (<see cref="WorldBody.FixedPreviousPosition"/> collapses to the landing point, so the restored body's own
    /// swept portal-crossing segment starts exactly here rather than ghosting back through the volume it just left —
    /// this is what stops an aborted transfer's stale pre-detach origin from re-firing the door it was just refused
    /// by), and velocity/overlay/timer state is only meaningful once that discontinuity has already run.</para>
    /// <para><b>Park stays derived.</b> This method never writes <see cref="Entry.Parked"/>
    /// or <see cref="Entry.ParkedUntilTick"/> — <see cref="TryDetachSeatForTransfer"/> already cleared both at detach
    /// time and nothing here reinstates them from <paramref name="dynamicState"/> or any other capture, because park
    /// is a live-compiled-grace fact the next <see cref="DeactivateSeat"/> re-derives, never a snapshot to replay.</para>
    /// A no-op returning <see langword="false"/> when the slot is already active — nothing to restore onto; the
    /// caller's own bookkeeping (never restoring the same detach twice) is what keeps this from firing over a live
    /// occupant.</summary>
    /// <param name="slot">The seat index (0-based) — the same slot the detach came from.</param>
    /// <param name="profile">The detached body's own retained identity, exactly as <see cref="TryDetachSeatForTransfer"/>
    /// returned it.</param>
    /// <param name="position">The captured pre-detach position.</param>
    /// <param name="yawRadians">The captured pre-detach yaw.</param>
    /// <param name="dynamicState">The captured pre-detach dynamic state (velocity, overlay, action-track) — see
    /// <see cref="WorldBody.TransferState"/>.</param>
    /// <param name="designations">The seat's own pre-detach <see cref="Entry.Designations"/> register, from
    /// <see cref="CaptureDesignations"/>, or <see langword="null"/> to leave the register at its cleared default (a
    /// non-abort restore caller has nothing to pass — every actual caller today is abort-only, so this defaults to
    /// <see langword="null"/> only for a hypothetical future caller, never today's).</param>
    /// <returns><see langword="true"/> when the seat was restored.</returns>
    public bool RestoreDetachedSeat(int slot, WorldIdentity? profile, FixedVector3 position, FixedQ4816 yawRadians, WorldBody.TransferState dynamicState, IReadOnlyList<WorldTargetDesignation>? designations = null) {
        var entry = m_entries[slot];

        if (entry.Active) {
            return false;
        }

        var body = new WorldBody(
            tuning: m_kits[m_seatKit].Tuning,
            program: m_kits[m_seatKit].BodyMotionProgram,
            programs: m_bodyMotionPrograms,
            actions: m_kits[m_seatKit].Actions,
            actionThresholds: m_kits[m_seatKit].ActionThresholds,
            actionShapes: m_kits[m_seatKit].ActionShapes,
            roleMask: m_kits[m_seatKit].RoleMask,
            roleOrdinals: m_kits[m_seatKit].RoleOrdinals,
            actionState: m_kits[m_seatKit].ActionState,
            collider: m_kits[m_seatKit].Collider,
            rigid: m_kits[m_seatKit].Rigid,
            carry: m_kits[m_seatKit].Carry,
            maxSmoothError: m_fixedMotion.MaxSmoothError,
            holds: m_kits[m_seatKit].Holds
        ) {
            Profile = profile,
        };

        body.SetContactConfiguration(
            field: m_contactField,
            upPolicy: m_bodyUpPolicy,
                walkableThreshold: m_walkableThreshold
        );
        body.SetGravityField(field: m_gravityField);
        body.SetAttachmentPolicy(policy: m_fixedAttachment);
        body.Pose(
            position: position,
            yawRadians: yawRadians,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        // AFTER Pose's own CommitTeleport — see this method's own "Dynamic state" remarks above.
        body.ApplyTransferState(state: dynamicState);
        ClearDesignations(entry: entry);

        // Reapply the CAPTURED pre-detach register on top of the defensive clear above — the same
        // "restore on top of the reset" ordering ApplyTransferState's own fields already follow.
        // Absent (null) means the caller captured nothing to restore (never today's abort-only caller, which always
        // reads CaptureDesignations before detaching) — leaves the cleared default alone rather than throwing.
        if (designations is not null) {
            var count = Math.Min(
                val1: designations.Count,
                val2: entry.Designations.Length
            );

            for (var index = 0; (index < count); index++) {
                entry.Designations[index] = designations[index];
            }
        }

        // resetPhase:false: entry.ProducerState is NEVER cleared by TryDetachSeatForTransfer (it only clears
        // Body/Active/Parked/Designations — see that method's own remarks), so the pre-detach wander
        // phase/activity/acquired-target are still sitting right here, untouched, the moment this runs — reseeding
        // them would needlessly discard state that was never actually lost, only about to be overwritten.
        // WeaveFrequency/PreferredAltitude are still recomputed either way (a pure function of slot+kit,
        // safe/idempotent to redo), matching SeedSeatWander's other resetPhase:false caller (the ApplyPeerAdmitted-adjacent path).
        SeedSeatWander(
            resetPhase: false,
            slot: slot
        );
        entry.Body = body;
        entry.BodyColor = (profile?.Color ?? Vector3.Zero);
        entry.Active = true;
        m_revision++;

        return true;
    }
    /// <summary>Updates the successfully-installed admission baseline for one connected peer after authorization.
    /// A later rebuild compares only these rows with the then-live table: an authored attempt rejected by the grant
    /// door was never present and therefore cannot be inferred as an explicit runtime revoke.</summary>
    /// <param name="bodyIndex">The 0-based remote-peer body index.</param>
    /// <param name="grantTemplates">The templates successfully installed from the current matched policy.</param>
    public void SetPeerAdmissionInstalledGrantTemplates(int bodyIndex, IReadOnlyList<WorldAdmissionGrant> grantTemplates) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);

        if (((uint)bodyIndex) < Capacity) {
            m_entries[bodyIndex].AdmissionInstalledGrantTemplates = grantTemplates;
        }
    }
    /// <summary>Replaces one connected peer's persistent explicit-revocation set after rebuild re-authorization.</summary>
    /// <param name="bodyIndex">The 0-based remote-peer body index.</param>
    /// <param name="revokedKeys">The currently remembered revoked keys.</param>
    public void SetPeerAdmissionRevokedKeys(int bodyIndex, IReadOnlySet<(WorldCapability Capability, GrantSubject Subject)> revokedKeys) {
        ArgumentNullException.ThrowIfNull(argument: revokedKeys);

        if (((uint)bodyIndex) < Capacity) {
            m_entries[bodyIndex].AdmissionRevokedKeys = new HashSet<(WorldCapability Capability, GrantSubject Subject)>(collection: revokedKeys);
        }
    }
    /// <summary>Checks whether the kit selected for one body declares a named producer source.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <param name="source">The requested source.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the source is live, idle, or declared by the selected kit.</returns>
    public bool SupportsSource(int index, IntentSource source, out string refusal) {
        if (
            source.IsLive ||
            source.IsIdle
        ) {
            refusal = string.Empty;

            return true;
        }
        if (
            !source.IsProducer ||
            (source.ProducerName is not { } producerName)
        ) {
            refusal = $"intent source '{source}' is not defined";

            return false;
        }

        var kitIndex = ResolveKitIndex(index: index);

        if (!m_kits[kitIndex].Producers.ContainsKey(key: producerName)) {
            refusal = $"kit '{m_kitRows[kitIndex].Name}' declares no parameters for producer '{producerName}'";

            return false;
        }

        refusal = string.Empty;

        return true;
    }
    /// <summary>Admits one remote-human peer body at the point of effect — the P7 socket door's own primitive,
    /// parallel to <see cref="ReconcileInhabitants"/>'s inhabited-body admission: the body claims the highest free
    /// slot (capacity minus one downward, via <see cref="HighestFreeSlot"/>) so it never renumbers an existing peer and never
    /// collides with the census's own upward allocation (<see cref="SetSimulatedCount"/> now skips any slot this
    /// method marks <see cref="Entry.IsRemoteHuman"/>). Refused by name on whichever bound fails: no free slot in the
    /// 128-body table, or the document's <c>networkPlayers</c> admission cap already met (census bots and admitted
    /// remote humans share that one cap — see <see cref="CountActiveCensus"/>).</summary>
    /// <param name="source">The intent source the body starts with (<see cref="IntentSource.Live"/> for a genuine
    /// remote human — a submitted intent/command fills its gaps, never a wander/attend producer).</param>
    /// <param name="grantTemplates">The verified admission entry's own grant templates for this connection (see
    /// <see cref="WorldAdmissionDoor"/>) — stored on the activated slot so a later whole-document rebuild can
    /// compare the then-live rows with the policy baseline before re-authorizing
    /// (<see cref="Server.WorldServer.RemintPeerAdmissionGrants"/>). Empty (never null) for the identical reason a
    /// verified-but-granted-nothing identity is a legitimate outcome — see <see cref="WorldAdmissionEntry.Grants"/>.</param>
    /// <param name="identityDomain">The verified admission identity's own domain (see
    /// <see cref="WorldAdmissionDoor"/>) — stored alongside <paramref name="grantTemplates"/> so a later rebuild can
    /// re-match this identity against the current admission policy instead of trusting the connection-time
    /// verdict still holds (<see cref="Server.WorldServer.RemintPeerAdmissionGrants"/>).</param>
    /// <param name="identitySubject">The verified admission identity's own subject (empty for a Vouches root's
    /// chain-resolved subject).</param>
    /// <param name="admitted">The admitted peer entry on success.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public bool TryAdmitRemotePeer(IntentSource source, IReadOnlyList<WorldAdmissionGrant> grantTemplates, string identityDomain, string identitySubject, out WorldPeerEventEntry admitted, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);

        var slot = HighestFreeSlot();

        return TryAdmitRemotePeerAt(
            slot: slot,
            source: source,
            grantTemplates: grantTemplates,
            identityDomain: identityDomain,
            identitySubject: identitySubject,
            admitted: out admitted,
            refusal: out refusal
        );
    }
    /// <summary>Admits a remote peer at a body index already bound by a transfer reservation. This is the
    /// commit-side companion to <see cref="TryAdmitRemotePeer"/>; callers must reserve the exact index first.</summary>
    /// <param name="slot">The reserved peer body index.</param>
    /// <param name="source">The body's live or simulated intent source.</param>
    /// <param name="grantTemplates">Admission grant templates to install.</param>
    /// <param name="identityDomain">The verified identity domain.</param>
    /// <param name="identitySubject">The verified identity subject.</param>
    /// <param name="admitted">The admitted peer entry on success.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <param name="authorityTransferred">Whether the peer arrived through authority transfer and is therefore not
    /// eligible for destination census reconciliation.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public bool TryAdmitRemotePeerAt(int slot, IntentSource source, IReadOnlyList<WorldAdmissionGrant> grantTemplates, string identityDomain, string identitySubject, out WorldPeerEventEntry admitted, out string refusal, bool authorityTransferred = false) {
        return TryAdmitTransferredEntityAtCore(
            admitted: out admitted,
            authorityTransferred: authorityTransferred,
            grantTemplates: grantTemplates,
            identityDomain: identityDomain,
            identitySubject: identitySubject,
            refusal: out refusal,
            remoteHuman: true,
            slot: slot,
            source: source
        );
    }
    /// <summary>Admits an autonomous traveler at the peer body index already bound by a transfer reservation.</summary>
    public bool TryAdmitTransferredEntityAt(int slot, IntentSource source, out WorldPeerEventEntry admitted, out string refusal) =>
        TryAdmitTransferredEntityAtCore(
            admitted: out admitted,
            authorityTransferred: true,
            grantTemplates: [],
            identityDomain: string.Empty,
            identitySubject: string.Empty,
            refusal: out refusal,
            remoteHuman: false,
            slot: slot,
            source: source
        );
    /// <summary>Captures the generation-bearing row for any active entity-table peer before authority transfer.
    /// Unlike <see cref="TryCaptureTransferredPeer"/>, this includes autonomous census and inhabitant bodies.</summary>
    public bool TryCaptureTransferredEntity(int index, out WorldPeerEventEntry peer) {
        if (
            (((uint)(index - LocalSeatCount)) < PeerCapacity) &&
            m_entries[index].Active
        ) {
            peer = PeerEventEntry(index: index);
            return true;
        }

        peer = default;
        return false;
    }
    /// <summary>Captures the generation-bearing admission row for a live transferred peer before detachment.</summary>
    public bool TryCaptureTransferredPeer(int index, out WorldPeerEventEntry peer) {
        if (
            (((uint)(index - LocalSeatCount)) < PeerCapacity) &&
            m_entries[index].Active &&
            m_entries[index].IsRemoteHuman
        ) {
            peer = PeerEventEntry(index: index);
            return true;
        }

        peer = default;
        return false;
    }
    /// <summary>Detaches an authoritative body's embodiment for an atomic transfer to another world authority —
    /// the leave half of atomic body transfer (the composition root's per-host pending-transfer drain). Unlike
    /// <see cref="DeactivateSeat"/>, this never parks and never consults <c>reconnectGraceTicks</c>: it unconditionally
    /// clears <see cref="Entry.Body"/> and <see cref="Entry.Active"/> so the body stops being advanced (or counted
    /// active) in this instance from the moment it returns — a park would leave <see cref="Entry.Active"/> true and
    /// <see cref="AdvanceSeats"/> would keep integrating it here, which is exactly the double-embodiment a transfer
    /// must not allow once the same identity is about to be re-activated in another instance's population. Only the
    /// seat binding (the caller already holds the slot) and the body's own <see cref="WorldBody.Profile"/> survive
    /// this call — pose, velocity, action-track state, and tape are discarded here by design (the destination world
    /// re-embodies the identity through its own normal join/kit-assignment; none of that state is meaningful under a
    /// different kit). A caller preparing for a possible abort reads <see cref="WorldBody.CaptureTransferState"/>
    /// (and the body's own pose) off the still-active body before calling this — this method itself does not do so,
    /// since a committed transfer never needs it and this stays the single unconditional "leave" primitive either way
    /// (see <see cref="RestoreDetachedSeat"/> for where a captured state re-enters). This method also clears
    /// <see cref="Entry.Designations"/> (via <see cref="ClearDesignations"/>) unconditionally, before the caller
    /// knows whether the transfer will abort — an abort-preparing caller that wants designations to survive an abort
    /// must read <see cref="CaptureDesignations"/> before calling this, exactly like it already does for
    /// <see cref="WorldBody.CaptureTransferState"/>. <see cref="Entry.Designations"/> and
    /// <see cref="Entry.ProducerState"/> live on this class's own <see cref="Entry"/>, entirely outside
    /// <see cref="WorldBody"/>'s own reach, which is why they are addressed here rather than in
    /// <see cref="WorldBody.TransferState"/>. A no-op returning <see langword="false"/> when the seat holds no active
    /// body — nothing captured, nothing changed.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The detached body's own retained identity, or <see langword="null"/> for an anonymous
    /// seat.</param>
    /// <returns><see langword="true"/> when an active body was detached.</returns>
    public bool TryDetachSeatForTransfer(int slot, out WorldIdentity? profile) {
        var entry = m_entries[slot];

        if (
            !entry.Active ||
            (entry.Body is not { } body)
        ) {
            profile = null;

            return false;
        }

        profile = body.Profile;
        entry.Body = null;
        entry.Active = false;
        entry.Parked = false;
        entry.ParkedUntilTick = null;
        ClearDesignations(entry: entry);
        entry.PlacementId = null;
        entry.IsAuthorityTransferred = false;
        if (entry.IsRemoteHuman) {
            entry.IsRemoteHuman = false;
            entry.AdmissionInstalledGrantTemplates = [];
            entry.AdmissionRevokedKeys.Clear();
            entry.IdentityDomain = string.Empty;
            entry.IdentitySubject = string.Empty;
            m_simulatedCount = CountActiveCensus();
        }
        m_revision++;

        return true;
    }
    /// <summary>Attempts to resume a parked seat's retained body for a re-Join — body-resume, the reconnect
    /// primitive's third half. The match rule is deliberately narrow and precise: the incoming
    /// <paramref name="profile"/>'s <see cref="WorldIdentity.Id"/> must equal the parked body's own retained
    /// <see cref="WorldBody.Profile"/>.<see cref="WorldIdentity.Id"/> — read directly off the body the park never
    /// dropped, so no separate "remembered identity" field is needed. Both <see langword="null"/> (an anonymous seat
    /// reconnecting anonymously) counts as a match too. On a match: clears <see cref="Entry.Parked"/> and returns
    /// <see langword="true"/>, leaving pose/durable state exactly as parked (no fresh spawn, no
    /// <c>ResetDurableState</c> — that reset is keyed on an actual id change, and this is the same id). On a
    /// mismatch, the parked body is left untouched (so a later, correctly-identified re-Join can still recover it
    /// before grace expires) and <paramref name="mismatch"/> is set, letting the caller report a distinct refusal
    /// from "nothing to resume". <see langword="false"/> for a slot that is not parked at all — the caller falls
    /// back to <see cref="ActivateSeat"/>.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The re-Join's resolved identity, or <see langword="null"/> for an anonymous seat.</param>
    /// <param name="mismatch">Set <see langword="true"/> when the slot is parked but the identity does not match.</param>
    /// <returns><see langword="true"/> when the parked body was resumed.</returns>
    public bool TryResumeParkedSeat(int slot, WorldIdentity? profile, out bool mismatch) {
        mismatch = false;

        if (!IsSeatParked(slot: slot)) {
            return false;
        }

        var entry = m_entries[slot];

        if (!string.Equals(
            a: entry.Body?.Profile?.Id,
            b: profile?.Id,
            comparisonType: StringComparison.Ordinal
        )) {
            mismatch = true;

            return false;
        }

        entry.Parked = false;
        entry.ParkedUntilTick = null;

        // The retained body already carries this identity (that is what the match just proved) — a re-seat only
        // matters when the caller resolved a DIFFERENT WorldIdentity instance for the same id (a profile edit
        // reloaded between park and resume), so the cached color follows without disturbing durable state.
        if (
            (entry.Body is { } body) &&
            (profile is not null) &&
            !ReferenceEquals(
            objA: body.Profile,
            objB: profile
        )
        ) {
            body.Profile = profile;
            entry.BodyColor = profile.Color;
        }

        m_revision++;

        return true;
    }
    /// <summary>The peer-range counterpart of <see cref="TryResumeParkedSeat"/>: a reconnecting peer's own verified
    /// (<paramref name="identityDomain"/>, <paramref name="identitySubject"/>) pair names which parked body is
    /// "the identity that disconnected" — there is no seat slot a peer's own re-Join names, so the match happens by
    /// searching rather than by an explicit index. Resumes the FIRST parked peer whose identity matches in place, at
    /// its own retained index, rather than leaving it parked while a fresh admission mints a new body elsewhere
    /// (<see cref="TryAdmitRemotePeer"/>'s own <c>HighestFreeSlot</c> never revisits an active — including
    /// parked — slot, so without this a reconnect always orphans the parked body until its grace expires).</summary>
    /// <param name="identityDomain">The reconnecting peer's verified admission identity domain. Empty never
    /// matches — an empty domain cannot distinguish one anonymous reconnect from another parked stranger's, and
    /// resuming on so ambiguous a key would hand a reconnecting peer whichever parked body happened to sort
    /// first.</param>
    /// <param name="identitySubject">The reconnecting peer's verified admission identity subject.</param>
    /// <param name="admitted">The resumed peer's own admission row on success.</param>
    /// <returns><see langword="true"/> when a matching parked peer was found and resumed.</returns>
    public bool TryResumeParkedPeer(string identityDomain, string identitySubject, out WorldPeerEventEntry admitted) {
        admitted = default;

        if (identityDomain.Length == 0) {
            return false;
        }

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (
                !IsParked(index: index) ||
                !string.Equals(a: m_entries[index].IdentityDomain, b: identityDomain, comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: m_entries[index].IdentitySubject, b: identitySubject, comparisonType: StringComparison.Ordinal)
            ) {
                continue;
            }

            var entry = m_entries[index];

            entry.Parked = false;
            entry.ParkedUntilTick = null;
            m_revision++;
            admitted = PeerEventEntry(index: index);

            return true;
        }

        return false;
    }
    /// <summary>Sets the peer intent-source default and sweeps every peer (indices 4 through capacity minus one) to it — last-writer-wins, so a
    /// per-entity source (a possession, an earlier flip) does not survive the global. Seats are never touched.
    /// Render-inert: it reshapes only the intent producers, so it does not bump the revision. A live
    /// <c>body.fly</c> tape still drives regardless.</summary>
    /// <param name="source">The intent source to store and sweep.</param>
    /// <param name="refusal">The named refusal when an assigned kit does not declare the producer.</param>
    /// <returns><see langword="true"/> when every peer kit admits the source.</returns>
    public bool TrySetPeerSource(IntentSource source, out string refusal) {
        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (!SupportsSource(
                index: index,
                refusal: out refusal,
                source: source
            )) {
                return false;
            }
        }

        m_defaultPeerSource = source;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            m_entries[index].Body?.SetIntentSource(source: source);
        }

        refusal = string.Empty;

        return true;
    }
}
