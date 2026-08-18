using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    private void ReconcileInDoubtTransfers() {
        for (var index = 0; (index < m_inDoubtTransfers.Count);) {
            var pending = m_inDoubtTransfers[index];
            var transfer = pending.Transfer;

            try {
                // Every step below may answer "not yet". Leaving the entry exactly where it is and re-asking at the
                // next drain is the whole reconciliation loop already does for an unresolved status.
                if (!pending.TargetAuthority.TryStatus(
                    sourceAuthority: pending.SourceAuthority,
                    transferId: pending.Transfer.TransferId,
                    status: out var status
                )) {
                    index++;
                    continue;
                }

                if (status == WorldTransferStatus.Reserved) {
                    if (
                        m_instances.TryGetValue(
                        key: pending.Transfer.SourceInstance,
                        value: out var source
                    ) &&
                        ((source.Server.NextInputTick - 1UL) >= pending.SourceDeadlineTick)
                    ) {
                        pending.TargetAuthority.Abort(
                            sourceAuthority: pending.SourceAuthority,
                            transferId: pending.Transfer.TransferId
                        );

                        if (!pending.TargetAuthority.TryStatus(
                            sourceAuthority: pending.SourceAuthority,
                            transferId: pending.Transfer.TransferId,
                            status: out status
                        )) {
                            index++;
                            continue;
                        }
                    } else {
                        var step = pending.TargetAuthority.Commit(
                            sourceAuthority: pending.SourceAuthority,
                            transferId: pending.Transfer.TransferId,
                            members: pending.CommitMembers,
                            accepted: out var committed,
                            reason: out _
                        );

                        if (
                            (step == WorldTransferStep.Answered) &&
                            committed
                        ) {
                            status = WorldTransferStatus.Committed;
                        } else if (!pending.TargetAuthority.TryStatus(
                            sourceAuthority: pending.SourceAuthority,
                            transferId: pending.Transfer.TransferId,
                            status: out status
                        )) {
                            index++;
                            continue;
                        }
                    }
                }

                if (status == WorldTransferStatus.Committed) {
                    m_inDoubtTransfers.RemoveAt(index: index);
                    Console.Error.WriteLine(value: $"[world.transfer: transfer={pending.Transfer.TransferId} RESOLVED committed at '{pending.TargetName}' after an ambiguous acknowledgement]");
                    FinalizeCommittedTransfer(
                        transfer: in transfer,
                        targetAuthority: pending.TargetAuthority,
                        targetName: pending.TargetName,
                        spawned: pending.Spawned,
                        landed: pending.Landed,
                        memberCount: pending.MemberCount
                    );
                    continue;
                }

                if (status == WorldTransferStatus.Missing) {
                    if (!m_instances.TryGetValue(
                        key: pending.Transfer.SourceInstance,
                        value: out var source
                    )) {
                        index++;
                        continue;
                    }

                    foreach (var member in pending.Landed) {
                        RestoreDetachedMember(
                            member: member,
                            source: source
                        );
                    }

                    m_inDoubtTransfers.RemoveAt(index: index);
                    Console.Error.WriteLine(value: $"[world.transfer: transfer={pending.Transfer.TransferId} RESOLVED absent at '{pending.TargetName}' — every member restored to '{pending.Transfer.SourceInstance}' from retained recovery state]");
                    if (pending.Spawned) { ReapIfEmpty(name: pending.TargetName); }
                    NoteResolvedTransferOutcome(
                        transfer: in transfer,
                        sourceName: pending.Transfer.SourceInstance,
                        targetName: pending.TargetName,
                        outcome: "aborted:in-doubt-resolved-missing"
                    );
                    CloseAdjacencyAfterRefusal(
                        transfer: in transfer,
                        reason: $"'{pending.TargetName}' has no record of the reservation this crossing was committed against"
                    );
                    continue;
                }
            } catch (Exception exception) when ((exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)) {
                // Still ambiguous. Keep the exact recovery and commit records; the next fixed-point drain retries.
            }

            index++;
        }
    }
    // Resolves this scan's hits, coalesces identical mappings, and enqueues one transfer per group. The order list
    // preserves first-seen scan order, keeping transfer and generation ids independent of dictionary enumeration.
    private void ResolveAndEnqueueCoalescedTransfers(WorldInstance instance, List<PortalEdgeHit> hits) {
        var groups = new Dictionary<CoalescedPortalGroupKey, CoalescedPortalGroup>();
        var order = new List<CoalescedPortalGroupKey>();

        foreach (var hit in hits) {
            if (WorldDefinitionRows.FindDestination(
                destinations: instance.Server.Definition.Destinations,
                name: hit.Portal.Destination
            ) is not { } destination) {
                Console.Error.WriteLine(value: $"[world.portal: '{instance.Name}'/{hit.Placement.Id}/{hit.Face.Face} refused (destination '{hit.Portal.Destination}' names no destinations row)]");

                continue;
            }

            if (WorldDefinitionRows.FindReference(
                references: instance.Server.Definition.References,
                name: destination.Reference
            ) is not { } reference) {
                Console.Error.WriteLine(value: $"[world.portal: '{instance.Name}'/{hit.Placement.Id}/{hit.Face.Face} refused (destination '{hit.Portal.Destination}' names references row '{destination.Reference}', which does not exist)]");

                continue;
            }

            // Mirrors WorldPlacementCommandModule.DescribePortals' own resolution order exactly: the facet's own
            // travel, else the document's portals.portalDefaults.travel, else 'body' when the world declares no
            // portals section.
            var defaultTravel = (instance.Server.Definition.Portals?.PortalDefaults.Travel ?? WorldPortalTravel.Body);
            var travel = (hit.Portal.Travel ?? defaultTravel);
            var scope = ((travel == WorldPortalTravel.Party)
                ? TransferScope.Party
                : TransferScope.Body
            );
            var portalDefaults = (instance.Server.Definition.Portals?.PortalDefaults ?? new WorldPortalDefaults(Travel: WorldPortalTravel.Body));

            // This hit's own candidate cohort — the source instance's whole active local-seat set for a
            // `party` door, or just the entering seat for `body`. Read live, not cached.
            var cohortSlots = ((scope == TransferScope.Party)
                ? ActiveLocalSeats(server: instance.Server)
                : [hit.Seat]
            );
            var cohort = BuildCohort(
                server: instance.Server,
                slots: cohortSlots
            );

            if (!m_resolver.TryDeriveScopeKey(
                sourceDefinition: instance.Server.Definition,
                destination: destination,
                cohort: cohort,
                scopeKey: out var scopeKey,
                reason: out var scopeReason
            )) {
                Console.Error.WriteLine(value: $"[world.portal: '{instance.Name}'/{hit.Placement.Id}/{hit.Face.Face} refused (destination '{hit.Portal.Destination}' — {scopeReason})]");

                continue;
            }

            var key = new CoalescedPortalGroupKey(
                DestinationName: destination.Name.Value,
                ScopeKey: scopeKey,
                SourcePlacementId: hit.Placement.Id,
                SourceFace: hit.Face.Face,
                Arrival: hit.Portal.Arrival,
                Counterpart: hit.Portal.Counterpart
            );

            if (!groups.TryGetValue(
                key: key,
                value: out var group
            )) {
                group = new CoalescedPortalGroup {
                    Arrival = hit.Portal.Arrival,
                    Border = $"{hit.Placement.Id}/{hit.Face.Face}",
                    BorderCapacity = hit.Portal.Capacity,
                    Counterpart = hit.Portal.Counterpart,
                    Destination = destination,
                    FullPolicy = portalDefaults.Full,
                    HoldSeconds = portalDefaults.HoldSeconds,
                    PartyAllOrNothing = portalDefaults.PartyAllOrNothing,
                    ReferenceDocument = reference.NeighbourKey,
                    Scope = scope,
                    SourceFrame = hit.Frame,
                    Travel = travel,
                };
                groups[key] = group;
                order.Add(item: key);
            } else if (
                (scope == TransferScope.Party) &&
                (group.Scope != TransferScope.Party)
            ) {
                // A party-travel hit widens an already-open body-travel group's own reported scope — the merged
                // cohort below is what ApplyTransfer actually moves either way (it prefers FrozenCohortSlots over
                // Scope whenever both are present), so this only affects what the enqueue echo/verb narrates.
                group.Scope = TransferScope.Party;
            }

            foreach (var slot in cohortSlots) {
                group.Slots.Add(item: slot);
            }

            group.Descriptions.Add(item: $"{hit.Placement.Id}/{hit.Face.Face} seat {(hit.Seat + 1)}");
        }

        foreach (var key in order) {
            EnqueueCoalescedGroup(
                instance: instance,
                group: groups[key]
            );
        }
    }
    // The shared "reuse-if-running, else start" resolution both TransferLifetime.Persistent and .Resolved use — the
    // ONLY difference between them is whether the retention rule is fixed (Persistent, always retained) or carried
    // per-call (Resolved, from the resolver's own destination durability). Extracted so the name-collision fence
    // below is written, and kept correct, exactly once.
    private bool ResolveByStableName(string name, string documentPath, bool retain, out WorldInstance? resolved, out string resolvedName, out bool spawned, out string reason) {
        resolvedName = name;

        if (m_instances.TryGetValue(
            key: resolvedName,
            value: out resolved
        )) {
            spawned = false;

            // A name-collision fence: a stable-named destination reuses an already-running instance by name
            // alone, so this verifies it was started from the same document — two doors authoring the
            // identical name against different reference documents would otherwise silently route a
            // traveler into whichever world happened to claim the name first. Resolve both sides through
            // the same probe TryStart itself uses (rooted/relative/base-directory/shipped-worlds), so a
            // spelling difference alone never false-refuses.
            if (
                !TryResolveDocumentPath(
                path: documentPath,
                resolved: out var expectedPath
            ) ||
                !TryResolveDocumentPath(
                path: resolved.SourcePath,
                resolved: out var actualPath
            ) ||
                !string.Equals(
                a: expectedPath,
                b: actualPath,
                comparisonType: PathComparison
            )
            ) {
                reason = $"'{resolvedName}' is already running from '{resolved.SourcePath}', not the document this destination names ('{documentPath}') — a stable-named destination must resolve the same document everywhere it is authored";
                resolved = null;

                return false;
            }

            // Reached by name — from this point on it is retained (if `retain`) even if it happens to be empty right
            // now (e.g. it was only ever started, never yet joined).
            if (retain) {
                m_retainedInstances.Add(item: resolvedName);
            }

            reason = string.Empty;

            return true;
        }

        if (!TryStart(
            instance: out resolved,
            name: resolvedName,
            path: documentPath,
            reason: out reason
        )) {
            spawned = false;

            return false;
        }

        spawned = true;

        if (retain) {
            m_retainedInstances.Add(item: resolvedName);
        }

        return true;
    }
    // Reuse-if-running exactly like Persistent (only the retention rule differs — see
    // TransferDestination.Resolved) unless the resolver-minted name is no longer running. Reaching TryStart
    // under that stale name directly, the way the Persistent path always has, would restart an instance
    // behind the resolver's own back: whatever TryStop call retired the original already cleared
    // WorldSessionResolver's cache entry via NotifyInstanceRetired, so a blind restart would make one live
    // again with the resolver never told, and the next traveler's resolve would mint a second, different
    // generation for what should be one scoped session. Re-resolving through the resolver instead — using
    // the frozen cohort's still-active members against the frozen destination row — keeps cache and reality
    // from diverging.
    private bool ResolveTransferDestination(in PendingTransfer transfer, WorldInstance source, out WorldInstance? resolved, out string resolvedName, out bool spawned, out string reason) {
        var destination = transfer.Destination;

        if (m_instances.ContainsKey(key: destination.Name!)) {
            return ResolveByStableName(
                name: destination.Name!,
                documentPath: destination.DocumentPath!,
                retain: destination.Retain,
                resolved: out resolved,
                resolvedName: out resolvedName,
                spawned: out spawned,
                reason: out reason
            );
        }

        if (
            (transfer.ResolvedDestinationRow is not { } destinationRow) ||
            (transfer.FrozenCohortSlots is not { } frozenSlots)
        ) {
            // Unreachable for a genuine Resolved-lifetime transfer — EnqueueCoalescedGroup, the only minter of this
            // lifetime, always populates both. Falls back to the ordinary reuse-if-running/else-start path rather
            // than throwing if that invariant is ever violated.
            resolved = null;
            resolvedName = string.Empty;
            spawned = false;
            reason = $"'{destination.Name}' carries no frozen resolution context to re-resolve through — refused rather than restarting it blind";

            return false;
        }

        var liveCohort = LiveCohortForFrozenSlots(
            server: source.Server,
            frozenSlots: frozenSlots
        );

        if (liveCohort.Count == 0) {
            resolved = null;
            resolvedName = string.Empty;
            spawned = false;
            reason = $"'{destination.Name}' is no longer running, and no frozen cohort member is still active in '{source.Name}' to re-resolve it through";

            return false;
        }

        if (!m_resolver.TryResolve(
            sourceDefinition: source.Server.Definition,
            destination: destinationRow,
            referencedDocument: CanonicalDocumentIdentity(documentPath: destination.DocumentPath!),
            cohort: liveCohort,
            resolved: out var reResolved,
            reason: out var resolveReason
        )) {
            resolved = null;
            resolvedName = string.Empty;
            spawned = false;
            reason = $"'{destination.Name}' is no longer running, and re-resolving it failed ({resolveReason})";

            return false;
        }

        return ResolveByStableName(
            name: reResolved.InstanceName,
            documentPath: destination.DocumentPath!,
            retain: destination.Retain,
            resolved: out resolved,
            resolvedName: out resolvedName,
            spawned: out spawned,
            reason: out reason
        );
    }
    // The "return means home" origin scan: every running instance's own resolved document path against the
    // destination's, through the same TryResolveDocumentPath probes ResolveByStableName's name-collision
    // fence already uses, so a spelling difference alone never false-refuses or false-matches. Names order
    // (ordinal) for determinism; a stopped instance is invisible by construction (removed from m_instances
    // by TryStop already). Two or more matches is reported ambiguous rather than adopting one arbitrarily.
    private bool TryFindRunningInstanceByOrigin(string documentPath, out string matchedName, out IReadOnlyList<string>? ambiguous) {
        matchedName = string.Empty;
        ambiguous = null;

        if (!TryResolveDocumentPath(
            path: documentPath,
            resolved: out var targetPath
        )) {
            return false;
        }

        List<string>? matches = null;

        foreach (var name in Names) {
            if (
                !TryResolveDocumentPath(
                path: m_instances[name].SourcePath,
                resolved: out var candidatePath
            ) ||
                !string.Equals(
                a: targetPath,
                b: candidatePath,
                comparisonType: PathComparison
            )
            ) {
                continue;
            }

            (matches ??= new List<string>()).Add(item: name);
        }

        if (matches is not { Count: > 0 }) {
            return false;
        }

        if (matches.Count > 1) {
            ambiguous = matches;

            return false;
        }

        matchedName = matches[0];

        return true;
    }
    // Resolves (spawning or starting as needed) a queued transfer's destination — the one place a
    // TransferDestination becomes a live WorldInstance, so a party's whole member set shares this single
    // resolution (a Fresh destination mints its name once here, not once per member). `spawned` is true
    // only when this call started a brand-new instance (Fresh always; Persistent only when it was not
    // already running) — ApplyTransfer reads it to decide whether an empty destination is worth reaping
    // when every member's join fails. `source` is read only by the Resolved case; every other lifetime
    // resolves from `transfer.Destination` alone.
    private bool TryResolveDestination(in PendingTransfer transfer, WorldInstance source, out WorldInstance? resolved, out string resolvedName, out bool spawned, out string reason) {
        var destination = transfer.Destination;

        switch (destination.Lifetime) {
            case TransferLifetime.Existing:
                resolvedName = destination.Name!;
                spawned = false;

                if (!m_instances.TryGetValue(
                    key: resolvedName,
                    value: out resolved
                )) {
                    reason = $"no instance named '{resolvedName}'";

                    return false;
                }

                reason = string.Empty;

                return true;

            case TransferLifetime.Persistent:
                return ResolveByStableName(
                    name: destination.Name!,
                    documentPath: destination.DocumentPath!,
                    retain: true,
                    resolved: out resolved,
                    resolvedName: out resolvedName,
                    spawned: out spawned,
                    reason: out reason
                );

            case TransferLifetime.Resolved:
                return ResolveTransferDestination(
                    reason: out reason,
                    resolved: out resolved,
                    resolvedName: out resolvedName,
                    source: source,
                    spawned: out spawned,
                    transfer: in transfer
                );

            case TransferLifetime.Fresh:
                resolvedName = MintFreshInstanceName(site: destination.Site!);

                if (!TryStart(
                    name: resolvedName,
                    path: destination.DocumentPath!,
                    instance: out resolved,
                    reason: out reason
                )) {
                    spawned = false;

                    return false;
                }

                spawned = true;

                return true;

            default:
                resolved = null;
                resolvedName = string.Empty;
                spawned = false;
                reason = $"unrecognized transfer lifetime '{destination.Lifetime}'";

                return false;
        }
    }
    private static bool TryResolveDocumentPath(string path, out string resolved) {
        try {
            var direct = Path.GetFullPath(path: path);

            if (File.Exists(path: direct)) {
                resolved = direct;

                return true;
            }

            var fallback = Path.GetFullPath(path: Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: path
            ));

            if (File.Exists(path: fallback)) {
                resolved = fallback;

                return true;
            }

            var shippedWorlds = Path.GetFullPath(path: Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: "Assets",
                path3: "worlds",
                path4: path
            ));

            if (File.Exists(path: shippedWorlds)) {
                resolved = shippedWorlds;

                return true;
            }
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            // A path the OS cannot even form is a path with no file at it, which is exactly what the caller refuses
            // by name — swallowing here keeps one refusal sentence instead of two spellings of "not found".
        }

        resolved = string.Empty;

        return false;
    }
    private bool TryResolveWorldPeerCall(in PendingTransfer transfer, WorldInstance source, out WorldPeerCall authority, out string resolvedName, out bool spawned, out string reason) {
        if (
            (transfer.Destination.DocumentPath is { } documentPath) &&
            TryResolveDocumentPath(
            path: documentPath,
            resolved: out var resolvedPath
        )
        ) {
            var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => ((Path.GetDirectoryName(path: resolvedPath) is { Length: > 0 } directory)
                ? directory
                : AppContext.BaseDirectory));

            if (
                WorldDefinitionLoader.TryLoadFile(
                path: resolvedPath,
                definition: out var definition,
                reason: out var loadReason,
                instanceIdentity: (transfer.Destination.Name ?? (transfer.Destination.Site ?? "remote")),
                neighbours: neighbours
            ) &&
                (definition is not null) &&
                ((transfer.Destination.Authority ?? definition.Host.Authority) is { Length: > 0 } endpoint)
            ) {
                resolvedName = (transfer.Destination.Name ?? (transfer.Destination.Site ?? endpoint));

                try {
                    if (
                        !m_remoteAuthorities.TryGetValue(
                        key: resolvedName,
                        value: out var remote
                    ) ||
                        !string.Equals(
                        a: remote.Endpoint,
                        b: endpoint,
                        comparisonType: StringComparison.Ordinal
                    )
                    ) {
                        remote?.Dispose();
                        remote = new WorldRemoteAuthority(
                            endpoint: endpoint,
                            placeholder: definition,
                            security: source.Federation.Authenticator,
                            observerAuthority: source.Federation.Subject,
                            applicationStopping: m_applicationStopping
                        );
                        m_remoteAuthorities[resolvedName] = remote;
                    }

                    authority = new WorldPeerCall(
                        Local: null,
                        Remote: remote
                    );
                    spawned = false;
                    reason = string.Empty;
                    return true;
                } catch (FormatException exception) {
                    authority = default;
                    spawned = false;
                    reason = exception.Message;
                    return false;
                }
            } else if (
                (definition is null) &&
                (loadReason.Length > 0)
            ) {
                authority = default;
                resolvedName = string.Empty;
                spawned = false;
                reason = loadReason;
                return false;
            }
        }

        // A windowed source may already hold a colocated projection cache for a portal screen. That cache is not
        // transfer authority: an authored remote endpoint still wins above, and only a document with no remote
        // authority may short-circuit through an existing colocated instance here.
        if (
            (transfer.Destination.Name is { } existingName) &&
            m_instances.TryGetValue(
            key: existingName,
            value: out var existing
        )
        ) {
            authority = LocalPeerCall(local: existing);
            resolvedName = existingName;
            spawned = false;
            reason = string.Empty;
            return true;
        }

        if (TryResolveDestination(
            reason: out reason,
            resolved: out var local,
            resolvedName: out resolvedName,
            source: source,
            spawned: out spawned,
            transfer: in transfer
        )) {
            authority = LocalPeerCall(local: local!);
            return true;
        }

        authority = default;
        return false;
    }

    /// <summary>Resolves a shared presentation consumer from one running source instance through the same scoped
    /// session identity portal entry uses. Presentation currently has no viewer identity, so only global
    /// destinations are admissible. Persisted destinations adopt an unambiguous running instance with the same
    /// document origin before minting, preserving the "return means home" rule for a view as well as a traveler.</summary>
    public bool TryResolveObservedDestination(WorldInstance source, string destinationName, out WorldInstance? target, out WorldSessionResolver.Resolved resolved, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: destinationName);

        target = null;
        resolved = default;

        if (WorldDefinitionRows.FindDestination(
            destinations: source.Server.Definition.Destinations,
            name: destinationName
        ) is not { } destination) {
            reason = $"destination '{destinationName}' names no destinations row";

            return false;
        }

        if (destination.Scope != WorldDestinationScope.Global) {
            reason = "viewer-scoped destination on a shared screen surface awaits per-viewport binding work";

            return false;
        }

        if (WorldDefinitionRows.FindReference(
            references: source.Server.Definition.References,
            name: destination.Reference
        ) is not { } reference) {
            reason = $"destination '{destinationName}' names references row '{destination.Reference}', which does not exist";

            return false;
        }

        var cohort = new[] { new WorldSessionResolver.CohortMember(
            Principal: WorldPrincipal.Seat(slot: 0),
            IdentityId: null
        ) };
        var referencedDocument = ResolveReferenceDocument(
            source: source,
            documentPath: reference.NeighbourKey
        );
        var canonicalDocument = CanonicalDocumentIdentity(documentPath: referencedDocument);

        if (
            (destination.Durability == WorldDestinationDurability.Persisted) &&
            !m_resolver.TryGetActive(
            destinationName: destination.Name.Value,
            durability: destination.Durability,
            scopeKey: WorldSessionResolver.GlobalScopeKey,
            referencedDocument: canonicalDocument,
            resolved: out _
        )
        ) {
            if (TryFindRunningInstanceByOrigin(
                ambiguous: out var ambiguousNames,
                documentPath: referencedDocument,
                matchedName: out var matchedName
            )) {
                if (!m_resolver.TryAdopt(
                    destination: destination,
                    instanceName: matchedName,
                    reason: out reason,
                    referencedDocument: canonicalDocument,
                    resolved: out _,
                    scopeKey: WorldSessionResolver.GlobalScopeKey
                )) {
                    return false;
                }
            } else if (ambiguousNames is { Count: > 1 }) {
                reason = $"destination '{destinationName}' resolves document '{reference.NeighbourKey}', matching {ambiguousNames.Count} running instances [{string.Join(
                    separator: ",",
                    values: ambiguousNames
                )}] by origin — ambiguous, refused rather than adopting one arbitrarily";

                return false;
            }
        }

        if (!m_resolver.TryResolve(
            sourceDefinition: source.Server.Definition,
            destination: destination,
            referencedDocument: canonicalDocument,
            cohort: cohort,
            resolved: out resolved,
            reason: out reason
        )) {
            return false;
        }

        if (m_instances.TryGetValue(
            key: resolved.InstanceName,
            value: out target
        )) {
            reason = string.Empty;

            return true;
        }

        if (ResolveByStableName(
            name: resolved.InstanceName,
            documentPath: referencedDocument,
            retain: (destination.Durability == WorldDestinationDurability.Persisted),
            resolved: out target,
            resolvedName: out _,
            spawned: out _,
            reason: out reason
        )) {
            return true;
        }

        m_resolver.AbortGeneration(instanceName: resolved.InstanceName);

        return false;
    }
    /// <summary>Remote-capable form of <see cref="TryResolveObservedDestination"/>. It resolves the same global
    /// session but returns its projection contract instead of requiring a local <see cref="WorldInstance"/>.</summary>
    public bool TryResolveObservedProjection(WorldInstance source, string destinationName, out string instanceName, out ulong generationId, out WorldDefinition? definition, out Func<IClientSink, IDisposable>? attach, out string reason) {
        instanceName = string.Empty;
        generationId = 0;
        definition = null;
        attach = null;

        if (
            (WorldDefinitionRows.FindDestination(
            destinations: source.Server.Definition.Destinations,
            name: destinationName
        ) is not { } destination) ||
            (destination.Scope != WorldDestinationScope.Global) ||
            (WorldDefinitionRows.FindReference(
            references: source.Server.Definition.References,
            name: destination.Reference
        ) is not { } reference)
        ) {
            reason = $"destination '{destinationName}' is absent, non-global, or names no reference";
            return false;
        }

        var cohort = new[] { new WorldSessionResolver.CohortMember(
            Principal: WorldPrincipal.Seat(slot: 0),
            IdentityId: null
        ) };
        var referencedDocument = ResolveReferenceDocument(
            source: source,
            documentPath: reference.NeighbourKey
        );
        var canonicalDocument = CanonicalDocumentIdentity(documentPath: referencedDocument);

        // Observation is a first caller of the same persisted global session handoff uses. Adopt a matching
        // already-running authority BEFORE TryResolve can mint a parallel generation—especially the boot authority
        // reached on a quilt's closing edge. Terrain, ghosts, and later transfer must all converge on one identity.
        if (
            (destination.Durability == WorldDestinationDurability.Persisted) &&
            m_resolver.TryDeriveScopeKey(
            sourceDefinition: source.Server.Definition,
            destination: destination,
            cohort: cohort,
            scopeKey: out var scopeKey,
            reason: out _
        ) &&
            !m_resolver.TryGetActive(
            destinationName: destination.Name.Value,
            durability: destination.Durability,
            scopeKey: scopeKey,
            referencedDocument: canonicalDocument,
            resolved: out _
        )
        ) {
            if (TryFindRunningInstanceByOrigin(
                ambiguous: out var ambiguousNames,
                documentPath: referencedDocument,
                matchedName: out var matchedName
            )) {
                m_resolver.TryAdopt(
                    destination: destination,
                    instanceName: matchedName,
                    reason: out _,
                    referencedDocument: canonicalDocument,
                    resolved: out _,
                    scopeKey: scopeKey
                );
            } else if (ambiguousNames is { Count: > 1 }) {
                reason = $"destination '{destinationName}' resolves document '{reference.NeighbourKey}', matching several running authorities [{string.Join(
                    separator: ",",
                    values: ambiguousNames
                )}]";
                return false;
            }
        }

        if (!m_resolver.TryResolve(
            sourceDefinition: source.Server.Definition,
            destination: destination,
            referencedDocument: canonicalDocument,
            cohort: cohort,
            resolved: out var resolved,
            reason: out reason
        )) {
            return false;
        }

        instanceName = resolved.InstanceName;
        generationId = resolved.GenerationId;

        if (TryGetProjection(
            adjacencies: out _,
            attach: out attach,
            definition: out definition,
            envelope: out _,
            name: instanceName
        )) {
            reason = string.Empty;
            return true;
        }

        if (!TryResolveDocumentPath(
            path: referencedDocument,
            resolved: out var resolvedPath
        )) {
            reason = $"no referenced world document at '{referencedDocument}'";
            return false;
        }

        var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => ((Path.GetDirectoryName(path: resolvedPath) is { Length: > 0 } directory)
            ? directory
            : AppContext.BaseDirectory));

        if (
            !WorldDefinitionLoader.TryLoadFile(
            definition: out var loaded,
            instanceIdentity: instanceName,
            neighbours: neighbours,
            path: resolvedPath,
            reason: out reason
        ) ||
            (loaded is null)
        ) {
            return false;
        }

        if (loaded.Host.Authority is { Length: > 0 } endpoint) {
            try {
                var remote = new WorldRemoteAuthority(
                    endpoint: endpoint,
                    placeholder: loaded,
                    security: source.Federation.Authenticator,
                    observerAuthority: source.Federation.Subject,
                    applicationStopping: m_applicationStopping
                );

                m_remoteAuthorities[instanceName] = remote;
                definition = loaded;
                attach = remote.AttachSink;
                reason = string.Empty;
                return true;
            } catch (FormatException exception) {
                reason = exception.Message;
                return false;
            }
        }

        if (
            ResolveByStableName(
            documentPath: referencedDocument,
            name: instanceName,
            reason: out reason,
            resolved: out var local,
            resolvedName: out _,
            retain: (destination.Durability == WorldDestinationDurability.Persisted),
            spawned: out _
        ) &&
            (local is not null)
        ) {
            definition = local.Server.Definition;
            attach = local.Server.AttachSink;
            return true;
        }

        return false;
    }

    // The transfer contract is authority-shaped. A local row invokes the same server escrow directly; a remote row
    // serializes that contract over TCP. No transfer logic branches on colocation below this adapter. Fault
    // substitutes every interface member below for a row SetPeerCallFault named — see that method's own remarks;
    // Local/Remote/Definition/IsRemote stay pointed at the real destination either way, since narration and
    // post-commit address resolution must still read the row a fault decorates, not the decorator.
    private readonly record struct WorldPeerCall(WorldInstance? Local, WorldRemoteAuthority? Remote, IWorldPeerCall? Fault = null) : IWorldPeerCall {
        public WorldDefinition Definition => (Local?.Server.Definition ?? Remote!.Definition);
        public bool IsRemote => (Remote is not null);

        public void Abort(string sourceAuthority, ulong transferId) {
            if (Fault is not null) {
                Fault.Abort(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            } else if (Local is not null) {
                Local.Server.AbortTransfer(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            } else {
                Remote!.Abort(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            }
        }
        public void Acknowledge(string sourceAuthority, ulong transferId) {
            if (Fault is not null) {
                Fault.Acknowledge(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            } else if (Local is not null) {
                Local.Server.AcknowledgeTransfer(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            } else {
                Remote!.Acknowledge(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            }
        }
        public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out bool accepted, out string reason) {
            if (Fault is not null) {
                return Fault.Commit(
                    accepted: out accepted,
                    members: members,
                    reason: out reason,
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            }
            if (Local is not null) {
                accepted = Local.Server.CommitTransfer(
                    members: members,
                    reason: out reason,
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );

                return WorldTransferStep.Answered;
            }

            return Remote!.Commit(
                accepted: out accepted,
                members: members,
                reason: out reason,
                sourceAuthority: sourceAuthority,
                transferId: transferId
            );
        }
        // A colocated row answers inline; a remote row answers over its persistent lane, and a lane that could not
        // deliver the step answers a named refusal rather than nothing. Every step here always answers: a caller
        // told "not yet" would leave this transfer queued while the adjacency scan minted a second crossing for the
        // same seat.
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => ((Fault is not null)
            ? Fault.Reserve(request: request)
            : ((Local is not null)
                ? Local.Server.ReserveTransfer(request: request)
                : Remote!.Reserve(request: request with { PeerAdmission = true })
            ));
        public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
            if (Fault is not null) {
                return Fault.TryStatus(
                    sourceAuthority: sourceAuthority,
                    status: out status,
                    transferId: transferId
                );
            }
            if (Local is not null) {
                status = Local.Server.TransferStatus(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );

                return true;
            }

            return Remote!.TryStatus(
                sourceAuthority: sourceAuthority,
                status: out status,
                transferId: transferId
            );
        }
    }
}
