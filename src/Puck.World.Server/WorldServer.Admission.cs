using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Commits an autonomous traveler into the entity-table index reserved by destination escrow. Its
    /// authored intent source survives the crossing; unlike a live peer, it receives no connection route or Drive
    /// grant and remains server-authored.</summary>
    internal SessionReply AdmitTransferredEntity(int slot, IntentSource source, WorldIdentity? identity) {
        if (source.IsLive) {
            return new SessionReply(
                Accepted: false,
                AssignedIndex: -1,
                Reason: "an autonomous transfer cannot carry the Live intent source",
                RosterEcho: string.Empty
            );
        }
        if (!m_population.TryAdmitTransferredEntityAt(
            admitted: out var admitted,
            refusal: out var refusal,
            slot: slot,
            source: source
        )) {
            return new SessionReply(
                Accepted: false,
                AssignedIndex: -1,
                Reason: refusal,
                RosterEcho: string.Empty
            );
        }

        ApplyLifecycleEvents(
            admitted: [admitted],
            disconnected: [],
            ordered: true
        );
        if (identity is not null) {
            m_population.SetSeatProfile(
                profile: identity,
                slot: slot
            );
        }
        return new SessionReply(
            Accepted: true,
            AssignedIndex: (slot + 1),
            Reason: string.Empty,
            RosterEcho: string.Empty
        );
    }
    /// <summary>Commits a federated transfer into the peer body index destination escrow reserved. Admission assigns
    /// the ordinary <see cref="PrincipalKind.Peer"/> principal and body together; no transfer-only principal exists.</summary>
    /// <param name="slot">The reserved destination body index.</param>
    /// <param name="verdict">The arrival verdict the reservation's own admission decision produced. The traveler's
    /// wire-supplied profile does not reach the identity columns: they name the authenticated authority the verdict
    /// was decided against.</param>
    /// <returns>The admission verdict.</returns>
    internal SessionReply AdmitTransferredPeer(int slot, WorldAdmissionVerdict? verdict) {
        if (!TryAdmitVerifiedParticipant(
            verdict: verdict,
            reservedSlot: slot,
            source: IntentSource.Live,
            authorityTransferred: true,
            admitted: out _,
            refusal: out var refusal
        )) {
            return new SessionReply(
                Accepted: false,
                AssignedIndex: -1,
                Reason: refusal,
                RosterEcho: string.Empty
            );
        }

        return new SessionReply(
            Accepted: true,
            AssignedIndex: (slot + 1),
            Reason: string.Empty,
            RosterEcho: string.Empty
        );
    }
    /// <summary>Disconnects one remote-human peer connection: revokes every grant that generation held and drops the
    /// body, through the same <see cref="WorldServerEvent.PeerDisconnected"/> ordered-domain path a census shrink
    /// uses. <c>Server.WorldTcpHost</c> calls this from the tick thread on socket teardown (graceful or dead).</summary>
    /// <param name="peer">The peer entry <see cref="TryAdmitPeerConnection"/> returned at admission.</param>
    internal void DisconnectPeerConnection(WorldPeerEventEntry peer) {
        ApplyLifecycleEvents(
            admitted: [],
            disconnected: [peer],
            ordered: true
        );
    }
    /// <summary>Removes a peer admitted by a transfer whose multi-member commit is rolling back, including every
    /// generation-scoped grant minted with it.</summary>
    internal void RollbackTransferredEntity(int slot) {
        if (m_population.TryCaptureTransferredEntity(
            index: slot,
            peer: out var peer
        )) {
            foreach (var grant in m_grants.Rows(principal: peer.Identity)) {
                Revoke(
                    grant: grant,
                    actor: WorldPrincipal.Console
                );
            }
        }
        _ = m_population.TryDetachSeatForTransfer(
            profile: out _,
            slot: slot
        );
    }
    /// <summary>Admits one remote-human peer connection through the population door and dispatches the
    /// <see cref="WorldServerEvent.PeerAdmitted"/> event through the same ordered domain every other lifecycle event
    /// drains through — <c>Server.WorldTcpHost</c>'s Hello door is the one caller, and it calls this only from the
    /// tick thread (the population/grant tables carry no lock), only after <see cref="Protocol.WorldAdmissionDoor"/>
    /// has already verified the connecting peer's identity off the tick thread. Refused by name on whichever
    /// capacity bound <see cref="WorldPopulation.TryAdmitRemotePeer"/> names.</summary>
    /// <param name="verdict">What <see cref="Protocol.WorldAdmissionDoor"/> decided this identity is authorized —
    /// the only shape this method accepts, so no ingress can hand it grant rows of its own. Empty templates mint
    /// nothing, which is a legitimate authored outcome (see <see cref="Protocol.WorldAdmissionEntry.Grants"/>).</param>
    /// <param name="expectedAdmissionEntries">The <c>admission</c> section <c>Protocol.WorldAdmissionDoor.TryAdmit</c>
    /// actually consulted to decide <paramref name="verdict"/>, captured by the caller before crossing onto
    /// the tick thread. Identity verification runs off the tick thread against a snapshot of the document, but this
    /// method is where the decision commits, on the tick thread, single-threaded with every mutation and rebuild —
    /// the one place that can prove the policy has not moved in between. Compared by reference against the live
    /// <see cref="Definition"/>'s own <c>Admission</c> list: <c>WorldDefinition</c>'s sections are immutable
    /// records, so an unrelated mutation or rebuild that never touches <c>Admission</c> leaves this exact reference
    /// standing, while one that does (a concurrent <c>world.reset</c>/<c>load</c>/<c>reload</c>, or a live edit to
    /// the section) mints a new list, which this method treats as the policy having moved and asks the peer to
    /// reconnect.</param>
    /// <param name="admitted">The admitted peer entry on success.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    internal bool TryAdmitPeerConnection(WorldAdmissionVerdict? verdict, IReadOnlyList<WorldAdmissionEntry>? expectedAdmissionEntries, out WorldPeerEventEntry admitted, out string refusal) {
        if (!ReferenceEquals(
            objA: m_definition.Admission,
            objB: expectedAdmissionEntries
        )) {
            admitted = default;
            refusal = "the world's admission policy changed while this connection was verifying its identity — reconnect to be re-evaluated against the current policy";

            return false;
        }

        return TryAdmitVerifiedParticipant(
            verdict: verdict,
            reservedSlot: null,
            source: IntentSource.Live,
            authorityTransferred: false,
            admitted: out admitted,
            refusal: out refusal
        );
    }
    /// <summary>Admits one verified participant onto a population body and mints exactly what the admission door's
    /// verdict authorizes — the single entry every authority-materializing ingress crosses.</summary>
    /// <remarks>There is no arm that accepts grant rows. A caller with no verdict is refused by name rather than
    /// admitted with a default seed: an ingress that cannot say who it admitted has nothing to authorize.</remarks>
    /// <param name="verdict">The door's decision.</param>
    /// <param name="reservedSlot">The body index a destination escrow already reserved, or <see langword="null"/> to
    /// take the lowest free peer index.</param>
    /// <param name="source">The admitted body's intent source.</param>
    /// <param name="authorityTransferred">Whether this admission commits a transfer rather than opening a
    /// connection.</param>
    /// <param name="admitted">The admitted peer entry on success.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    internal bool TryAdmitVerifiedParticipant(WorldAdmissionVerdict? verdict, int? reservedSlot, IntentSource source, bool authorityTransferred, out WorldPeerEventEntry admitted, out string refusal) {
        if (verdict is not { } decision) {
            admitted = default;
            refusal = "admission carries no door verdict — nothing authorizes this ingress";

            return false;
        }

        // BODY-RESUME (peer range): an ordinary connect (never a transfer commit — that always reserves a specific
        // slot) whose verified identity matches a body still parked from an earlier disconnect resumes that SAME
        // retained body in place, mirroring the local-seat Join resume (WorldPopulation.TryResumeParkedSeat's own
        // caller). A resumed body mints NOTHING through the ordinary lifecycle door below: the grant table survives
        // a park untouched (WorldGrants has no checkpoint-excluded half), so re-running BuildAdmissionGrants here
        // would double-grant a principal whose rows already stand.
        if (
            (reservedSlot is null) &&
            m_population.TryResumeParkedPeer(
                identityDomain: decision.IdentityDomain,
                identitySubject: decision.IdentitySubject,
                admitted: out admitted
            )
        ) {
            refusal = string.Empty;

            return true;
        }

        var admittedOk = ((reservedSlot is { } slot)
            ? m_population.TryAdmitRemotePeerAt(
                slot: slot,
                source: source,
                grantTemplates: decision.Templates,
                identityDomain: decision.IdentityDomain,
                identitySubject: decision.IdentitySubject,
                admitted: out admitted,
                refusal: out refusal,
                authorityTransferred: authorityTransferred
            )
            : m_population.TryAdmitRemotePeer(
                source: source,
                grantTemplates: decision.Templates,
                identityDomain: decision.IdentityDomain,
                identitySubject: decision.IdentitySubject,
                admitted: out admitted,
                refusal: out refusal
            )
        );

        if (!admittedOk) {
            return false;
        }

        ApplyLifecycleEvents(
            admitted: [admitted],
            disconnected: [],
            ordered: true,
            mintedGrants: BuildAdmissionGrants(
                principal: admitted.Identity,
                bodyIndex: admitted.BodyIndex,
                templates: decision.Templates
            )
        );

        return true;
    }

    // PeerAdmitted already records concrete minted rows. Stripping their generated principal reconstructs the exact
    // admission templates without duplicating those rows in the tape; the verified domain/subject on the peer entry
    // distinguishes a genuine zero-grant remote admission from ordinary simulated-population lifecycle events.
    private static IReadOnlyList<WorldAdmissionGrant> AdmissionTemplatesFor(WorldPeerEventEntry peer, IReadOnlyList<WorldGrant> mintedGrants) {
        if (string.IsNullOrEmpty(value: peer.IdentityDomain)) {
            return [];
        }

        var templates = new List<WorldAdmissionGrant>();

        foreach (var grant in mintedGrants) {
            if (grant.Principal != peer.Identity) {
                continue;
            }

            templates.Add(item: new WorldAdmissionGrant(
                Capability: grant.Capability,
                Subject: grant.Subject,
                Exclusive: grant.Exclusive,
                Budget: grant.Budget,
                EventBudget: grant.EventBudget,
                KindMask: grant.KindMask
            ));
        }

        return templates;
    }
    private void ApplyLifecycleEvents(IReadOnlyList<WorldPeerEventEntry> admitted, IReadOnlyList<WorldPeerEventEntry> disconnected, bool ordered, IReadOnlyList<WorldGrant>? mintedGrants = null) {
        if (disconnected.Count > 0) {
            var revoked = new List<WorldGrant>();

            foreach (var peer in disconnected) {
                revoked.AddRange(collection: m_grants.Rows(principal: peer.Identity));
            }

            DispatchServerEvent(
                serverEvent: new WorldServerEvent.PeerDisconnected(
                    Entries: [.. disconnected],
                    RevokedGrants: revoked
                ),
                ordered: ordered
            );
        }

        if (admitted.Count > 0) {
            // mintedGrants is supplied only by TryAdmitVerifiedParticipant, built from the door's verdict. Every
            // other admitted-list caller (boot inhabitant reconciliation, world.population's SetSimulatedCount, a
            // definition swap's post-Rebuild reconciliation) activates a locally-simulated body with no connecting
            // identity to verify, and mints the census Control/all seed instead.
            var minted = (mintedGrants ?? BuildDefaultPeerControlGrants(admitted: admitted));

            DispatchServerEvent(
                serverEvent: new WorldServerEvent.PeerAdmitted(
                    Entries: [.. admitted],
                    MintedGrants: minted
                ),
                ordered: ordered
            );
        }
    }
    // Builds the concrete minted grant rows for one just-admitted peer from its verified admission templates. A
    // template can carry neither the Principal nor a body subject — both are unknowable until admission assigns an
    // index and generation — so those are the only fields this fills in; every other field passes through unchanged.
    private static List<WorldGrant> BuildAdmissionGrants(WorldPrincipal principal, int bodyIndex, IReadOnlyList<WorldAdmissionGrant> templates) {
        var minted = new List<WorldGrant>(capacity: templates.Count);

        foreach (var template in templates) {
            minted.Add(item: new WorldGrant(
                Principal: principal,
                Capability: template.Capability,
                Subject: template.SubjectFor(bodyIndex: bodyIndex),
                Exclusive: template.Exclusive,
                Budget: template.Budget,
                EventBudget: template.EventBudget,
                KindMask: template.KindMask
            ));
        }

        return minted;
    }
    private static List<WorldGrant> BuildDefaultPeerControlGrants(IReadOnlyList<WorldPeerEventEntry> admitted) {
        var minted = new List<WorldGrant>(capacity: admitted.Count);

        foreach (var peer in admitted) {
            minted.Add(item: new WorldGrant(
                Principal: peer.Identity,
                Capability: WorldCapability.Control,
                Subject: GrantSubject.All,
                Exclusive: false
            ));
        }

        return minted;
    }
    // Re-establishes admission grants for every peer connection ApplyRebuild's snapshot pass captured (admitted,
    // NOT parked — see that pass's own remarks) — after WorldGrants.Reset wiped the whole runtime grant table. A
    // peer is a CONNECTION, not a document row or a boot-time seat, so nothing in WorldGrants.Reset or the
    // document-Grants replay re-establishes it — this is the one thing that must run AFTER both.
    //
    // Re-authorizes each peer rather than replaying its stored, connection-time templates, so a world.revoke
    // against a peer, or an operator narrowing/removing its admission entry, is honored across a
    // world.reset/load/reload rather than silently undone:
    //
    //  1. Re-match the peer's verified (Domain, Subject) — WorldPopulation.PeerIdentity, stored at
    //     TryAdmitRemotePeer, never recomputed here — against the CANDIDATE document's OWN admission entries,
    //     through WorldAdmissionDoor.TryMatchEntry: the SAME (domain, subject, mode) rule a fresh connection would
    //     be judged by. No match at all (the identity's entry was removed, or never existed in this candidate)
    //     mints nothing — "an identity no longer trusted... gets the current verdict, not the boot-time one".
    //  2. A match's CURRENT Grants list governs, not the stored connection-time templates — narrower or wider than
    //     what was minted at connection, exactly as if this peer connected fresh right now.
    //  3. Any row that WAS successfully installed in the peer's prior authorization, but is missing from the caller's preRebuildPeerRows
    //     snapshot (taken an instant before the wipe), was explicitly revoked live — that omission is preserved
    //     rather than re-derived, because live revocation is runtime state a document can never express. The baseline
    //     advances to the successfully-installed rows after every re-authorization, so a policy row rejected by the
    //     grant door is retried later rather than misremembered as revoked.
    //
    // A re-grant of an already-held row is a no-op acceptance, not a duplicate (WorldGrants keys on the (principal,
    // capability, subject) triple).
    private void RemintPeerAdmissionGrants(WorldDefinition candidate, IReadOnlyDictionary<int, IReadOnlyList<WorldGrant>> preRebuildPeerRows) {
        foreach (var (index, priorRows) in preRebuildPeerRows) {
            var principal = m_population.PeerPrincipal(index: index);
            var baselineTemplates = m_population.PeerAdmissionInstalledGrantTemplates(bodyIndex: index);

            var (domain, subject) = m_population.PeerIdentity(bodyIndex: index);

            // Everything in baselineTemplates that is NOT still present in priorRows (the live snapshot taken right
            // before the wipe) was revoked at runtime since connection — never resurrect it. Anything never in
            // the baseline is not a revocation candidate (there was nothing to revoke under that policy generation).
            var revokedKeys = new HashSet<(WorldCapability Capability, GrantSubject Subject)>(collection: m_population.PeerAdmissionRevokedKeys(bodyIndex: index));

            foreach (var template in baselineTemplates) {
                revokedKeys.Add(item: (template.Capability, template.SubjectFor(bodyIndex: index)));
            }

            foreach (var row in priorRows) {
                // A live re-grant is just as explicit as a live revoke: if the row is held again when the next
                // rebuild snapshots it, forget any older remembered revocation for this key.
                revokedKeys.Remove(item: (row.Capability, row.Subject));
            }

            m_population.SetPeerAdmissionRevokedKeys(
                bodyIndex: index,
                revokedKeys: revokedKeys
            );

            // An arrival is re-authorized against its own authority row, a connection against its identity row: the
            // same door decides both, from the candidate document rather than the connection-time policy.
            var stillTrusted = (m_population.PeerAuthorityTransferred(bodyIndex: index)
                ? ((Protocol.WorldAdmissionDoor.TryAdmitArrival(
                    entries: candidate.Admission,
                    sourceAuthority: domain,
                    verdict: out var arrivalVerdict
                ) is null)
                    ? arrivalVerdict
                    : null)
                : (Protocol.WorldAdmissionDoor.TryMatchEntry(
                    entries: candidate.Admission,
                    domain: domain,
                    subject: subject,
                    verdict: out var matchedVerdict
                )
                    ? matchedVerdict
                    : null
            ));

            if (stillTrusted is not { } current) {
                m_population.SetPeerAdmissionInstalledGrantTemplates(
                    bodyIndex: index,
                    grantTemplates: []
                );

                continue;
            }

            var installedTemplates = new List<WorldAdmissionGrant>();

            foreach (var template in current.Templates) {
                if (revokedKeys.Contains(item: (template.Capability, template.SubjectFor(bodyIndex: index)))) {
                    continue;
                }

                if (TryApplyGrant(
                    grant: new WorldGrant(
                        Principal: principal,
                        Capability: template.Capability,
                        Subject: template.SubjectFor(bodyIndex: index),
                        Exclusive: template.Exclusive,
                        Budget: template.Budget,
                        EventBudget: template.EventBudget,
                        KindMask: template.KindMask
                    ),
                    actor: WorldPrincipal.Console
                )) {
                    installedTemplates.Add(item: template);
                }
            }

            // The next absence comparison may only contain rows that ACTUALLY reached the live table. An authored
            // row rejected by exclusivity or another grant-door rule was never present to revoke; recording it here
            // would turn that refusal into a permanent remembered revoke and prevent a later conflict-free rebuild
            // from retrying the current policy. Explicitly revoked rows need no baseline entry — revokedKeys already
            // carries them independently until a live re-grant clears them.
            m_population.SetPeerAdmissionInstalledGrantTemplates(
                bodyIndex: index,
                grantTemplates: installedTemplates
            );
        }
    }
    private void StageOwnedState(int slot, WorldIdentity? profile) {
        if (
            (profile is null) ||
            (Body(index: slot) is not { } body)
        ) {
            return;
        }
        var declarations = new List<(string Name, ActionStateKind Kind)>();
        var values = new List<DurableStateValue>();

        body.AppendDurableStateDeclarations(declarations: declarations);
        foreach (var declaration in declarations) {
            if (m_profiles.TryReadDurableState(
                ownerId: profile.Id,
                sourceDocumentId: (m_definition.DocumentId ?? string.Empty),
                slot: declaration.Name,
                kind: declaration.Kind,
                value: out var value,
                reason: out _
            )) {
                values.Add(item: value);
            }
        }
        if (values.Count > 0) {
            _ = body.TryStageDurableState(
                tick: NextInputTick,
                values: values,
                requirePlayerWritable: false,
                writer: $"world:{profile.Id}",
                reason: out _
            );
        }
    }

    /// <summary>Applies a session request synchronously and returns the reply. The protocol handshake is checked here: a
    /// <see cref="SessionRequest.Join"/> whose <see cref="SessionRequest.Join.WireProtocolKey"/> mismatches
    /// <see cref="WorldProtocol.WireProtocolKey"/> is rejected with a distinct reason. Seat allocation is likewise validated: an
    /// out-of-range slot is rejected, as is an unknown profile name on a <see cref="SessionRequest.SetIdentity"/> — a
    /// <see cref="SessionRequest.Join"/> naming an unresolved profile seats with no identity rather than refusing.</summary>
    /// <param name="request">The session request.</param>
    /// <returns>The session reply.</returns>
    public SessionReply ApplySession(SessionRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);

        switch (request) {
            case SessionRequest.Join join: {
                    // LOOPBACK STAYS CREDENTIAL-FREE BY CONSTRUCTION. This is the in-process Session.Join path — the
                    // boot client, the console, and every local seat all reach it through LoopbackTransport, never a
                    // socket — so it checks WorldHelloDoor's protocol-version compatibility and STOPS there; it never
                    // calls Protocol.WorldAdmissionDoor. The reason is the BOUNDARY, not the code path: an identity
                    // check exists to answer "is the party on the other side of this wire who they claim to be", and
                    // there is no wire here — the caller is this same process, already running as whichever principal
                    // the OS session grants it. The trust boundary this door polices is the process boundary itself;
                    // requiring a signed claim from your own process to talk to your own process would authenticate
                    // nothing real while adding a key-management burden with no attacker on the other side of it. A
                    // REMOTE connection (Server.WorldTcpHost) crosses a real wire and passes through WorldAdmissionDoor
                    // in addition to this check, once this one succeeds.
                    if (!WorldHelloDoor.TryAccept(
                        offeredKey: join.WireProtocolKey,
                        refusal: out var helloRefusal
                    )) {
                        return new SessionReply(
                            Accepted: false,
                            AssignedIndex: -1,
                            RosterEcho: string.Empty,
                            Reason: $"{helloRefusal}: wire key 0x{join.WireProtocolKey:x16} != server 0x{WorldProtocol.WireProtocolKey:x16}"
                        );
                    }

                    if (((uint)join.Slot) >= Population.LocalSeatCount) {
                        return new SessionReply(
                            Accepted: false,
                            AssignedIndex: -1,
                            RosterEcho: string.Empty,
                            Reason: $"slot {join.Slot} out of range"
                        );
                    }

                    // A seat's own Drive/body:slot grant (seeded at construction) is the "this principal legitimately IS
                    // this seat" check for the whole session-lifecycle family: a principal with no drive claim on the slot
                    // (an addon, which is seeded nothing) can never mint or reseat its participant.
                    if (m_grants.Allows(
                        principal: join.Principal,
                        capability: WorldCapability.Drive,
                        subject: GrantSubject.Body(index: join.Slot)
                    ) is { IsAllowed: false } joinVerdict) {
                        return new SessionReply(
                            Accepted: false,
                            AssignedIndex: -1,
                            RosterEcho: string.Empty,
                            Reason: $"{join.Principal.Describe()} cannot join slot {join.Slot} ({joinVerdict.DescribeDenial()})"
                        );
                    }

                    var profile = ((join.IdentityName is { } name)
                        ? m_profiles.Find(name: name)
                        : null
                    );

                    // BODY-RESUME: a re-Join against a slot still PARKED from an earlier leave tries to recover that
                    // retained body first — see TryResumeParkedSeat's own remarks for the identity match rule. Only a
                    // slot that is not parked at all falls through to ActivateSeat's fresh-spawn path (its own no-op
                    // guard against an already-active, never-parked slot is unaffected).
                    if (m_population.IsSeatParked(slot: join.Slot)) {
                        if (!m_population.TryResumeParkedSeat(
                            slot: join.Slot,
                            profile: profile,
                            mismatch: out _
                        )) {
                            return new SessionReply(
                                Accepted: false,
                                AssignedIndex: -1,
                                RosterEcho: string.Empty,
                                Reason: $"slot {join.Slot} is parked by a different identity — it can only resume for the identity that disconnected, or reactivate once its grace window ends"
                            );
                        }
                    } else {
                        m_population.ActivateSeat(
                            slot: join.Slot,
                            profile: profile
                        );
                    }

                    StageOwnedState(
                        slot: join.Slot,
                        profile: profile
                    );

                    return new SessionReply(
                        Accepted: true,
                        AssignedIndex: (join.Slot + 1),
                        RosterEcho: string.Empty,
                        Reason: string.Empty
                    );
                }
            case SessionRequest.Leave leave:
                if (((uint)leave.Slot) >= Population.LocalSeatCount) {
                    return new SessionReply(
                        Accepted: false,
                        AssignedIndex: -1,
                        RosterEcho: string.Empty,
                        Reason: $"slot {leave.Slot} out of range"
                    );
                }

                if (m_grants.Allows(
                    principal: leave.Principal,
                    capability: WorldCapability.Drive,
                    subject: GrantSubject.Body(index: leave.Slot)
                ) is { IsAllowed: false } leaveVerdict) {
                    return new SessionReply(
                        Accepted: false,
                        AssignedIndex: -1,
                        RosterEcho: string.Empty,
                        Reason: $"{leave.Principal.Describe()} cannot leave slot {leave.Slot} ({leaveVerdict.DescribeDenial()})"
                    );
                }

                m_population.DeactivateSeat(
                    slot: leave.Slot,
                    tick: NextInputTick
                );

                return new SessionReply(
                    Accepted: true,
                    AssignedIndex: (leave.Slot + 1),
                    RosterEcho: string.Empty,
                    Reason: string.Empty
                );
            case SessionRequest.SetIdentity setProfile: {
                    if (
                        (((uint)setProfile.Slot) >= Population.LocalSeatCount) ||
                        (m_profiles.Find(name: setProfile.IdentityName) is not { } profile)
                    ) {
                        return new SessionReply(
                            Accepted: false,
                            AssignedIndex: -1,
                            Reason: "slot or identity not found",
                            RosterEcho: string.Empty
                        );
                    }

                    if (m_grants.Allows(
                        principal: setProfile.Principal,
                        capability: WorldCapability.Drive,
                        subject: GrantSubject.Body(index: setProfile.Slot)
                    ) is { IsAllowed: false } profileVerdict) {
                        return new SessionReply(
                            Accepted: false,
                            AssignedIndex: -1,
                            RosterEcho: string.Empty,
                            Reason: $"{setProfile.Principal.Describe()} cannot set the profile of slot {setProfile.Slot} ({profileVerdict.DescribeDenial()})"
                        );
                    }

                    m_population.SetSeatProfile(
                        slot: setProfile.Slot,
                        profile: profile
                    );
                    StageOwnedState(
                        slot: setProfile.Slot,
                        profile: profile
                    );

                    return new SessionReply(
                        Accepted: true,
                        AssignedIndex: (setProfile.Slot + 1),
                        RosterEcho: string.Empty,
                        Reason: string.Empty
                    );
                }
            case SessionRequest.SetPopulation setPopulation: {
                    // A global census lever, not a per-slot one: gated the same way SetPopulationDefaults' document edit
                    // is (Mutate over the Population section) rather than a per-body Drive check.
                    if (m_grants.Allows(
                        principal: setPopulation.Principal,
                        capability: WorldCapability.Mutate,
                        subject: GrantSubject.Section(section: WorldSection.Population)
                    ) is { IsAllowed: false } populationVerdict) {
                        return new SessionReply(
                            Accepted: false,
                            AssignedIndex: -1,
                            RosterEcho: string.Empty,
                            Reason: $"{setPopulation.Principal.Describe()} cannot mutate section:population ({populationVerdict.DescribeDenial()})"
                        );
                    }

                    var admitted = new List<WorldPeerEventEntry>();
                    var disconnected = new List<WorldPeerEventEntry>();
                    var applied = m_population.SetSimulatedCount(
                        count: setPopulation.Count,
                        admitted: admitted,
                        disconnected: disconnected
                    );

                    ApplyLifecycleEvents(
                        admitted: admitted,
                        disconnected: disconnected,
                        ordered: true
                    );

                    return new SessionReply(
                        Accepted: true,
                        AssignedIndex: applied,
                        Reason: string.Empty,
                        RosterEcho: string.Empty
                    );
                }
            case SessionRequest.SetPeerSource setPeerSource:
                if (m_grants.Allows(
                    principal: setPeerSource.Principal,
                    capability: WorldCapability.Mutate,
                    subject: GrantSubject.Section(section: WorldSection.Population)
                ) is { IsAllowed: false } peerSourceVerdict) {
                    return new SessionReply(
                        Accepted: false,
                        AssignedIndex: -1,
                        RosterEcho: string.Empty,
                        Reason: $"{setPeerSource.Principal.Describe()} cannot mutate section:population ({peerSourceVerdict.DescribeDenial()})"
                    );
                }

                if (!m_population.TrySetPeerSource(
                    source: setPeerSource.Source,
                    refusal: out var peerSourceRefusal
                )) {
                    return new SessionReply(
                        Accepted: false,
                        AssignedIndex: -1,
                        Reason: peerSourceRefusal,
                        RosterEcho: string.Empty
                    );
                }

                return new SessionReply(
                    Accepted: true,
                    AssignedIndex: -1,
                    Reason: string.Empty,
                    RosterEcho: string.Empty
                );
            case SessionRequest.RememberPreferredController rememberPreferred:
                if (m_profiles.Find(name: rememberPreferred.IdentityName) is not { } rememberedIdentity) {
                    return new SessionReply(
                        Accepted: false,
                        AssignedIndex: -1,
                        RosterEcho: string.Empty,
                        Reason: $"identity '{rememberPreferred.IdentityName}' not found"
                    );
                }

                m_profiles.RememberPreferredController(
                    device: rememberPreferred.Device,
                    profile: rememberedIdentity
                );

                return new SessionReply(
                    Accepted: true,
                    AssignedIndex: -1,
                    Reason: string.Empty,
                    RosterEcho: string.Empty
                );
            default:
                return new SessionReply(
                    Accepted: false,
                    AssignedIndex: -1,
                    Reason: "unknown session request",
                    RosterEcho: string.Empty
                );
        }
    }
    /// <summary>Applies a live session lever — the same shape as <see cref="ApplyComposition"/> one section over:
    /// checked against <see cref="WorldCapability.Mutate"/> over the section the lever folds into, then pushed to the
    /// client to write onto its presentation service. Synchronous at submit (like a command), never journaled, and
    /// never a <see cref="WorldMutation"/> — a slider must not mint an undo entry, and "live now, document owns boot"
    /// is the asymmetry a lever exists for.</summary>
    /// <remarks>Writing the injected presentation service directly, bypassing this method, skips the grant check
    /// below and lets an ungranted caller move — and persist through <c>world.save</c> — a knob in a section it
    /// holds no grant over.</remarks>
    /// <param name="lever">The lever write.</param>
    /// <param name="principal">The acting identity the lever is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    public void ApplySessionLever(WorldSessionLever lever, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        if (m_grants.Allows(
            principal: principal,
            capability: WorldCapability.Mutate,
            subject: GrantSubject.Section(section: lever.Section)
        ) is { IsAllowed: false } verdict) {
            var denial = $"{principal.Describe()} cannot mutate section:{lever.Section.ToString().ToLowerInvariant()} ({verdict.DescribeDenial()}) — {lever.Kind} lever dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: denial,
                Rejected: true,
                Kind: WorldEditEchoKind.GrantTable,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return;
        }

        m_output.DeliverSessionLever(lever: lever);
    }
}
