using System.Globalization;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // The query form of Server.WorldGrants.Allows — Text carries the same shape world.why prints, so a caller
    // reading the wire's Completion lane (which drops Payload) still gets an answer.
    private QueryAnswer AnswerGrantAllows(WorldQuery.GrantAllows query) {
        var verdict = m_grants.Allows(
            capability: query.Capability,
            principal: query.Principal,
            subject: query.Subject
        );

        return new QueryAnswer(
            Text: $"[grant.allows: {query.Principal.Describe()} {query.Capability.ToString().ToLowerInvariant()} {query.Subject.Describe()} = {(verdict.IsAllowed ? "allowed" : "denied")} ({verdict.Describe()})]",
            Payload: verdict
        );
    }
    // Mints a WorldHandle for the query's (principal, capability, index), the query form of Server.WorldHandleTable.
    // TryMint — preserves the constructor's own trust-boundary check (throws for a Console/Seat principal), matching
    // the direct in-process call this replaces.
    private QueryAnswer AnswerGrantHandleMint(WorldQuery.GrantHandleMint query) {
        var table = m_grants.HandleTable(
            capability: query.Capability,
            principal: query.Principal
        );
        var minted = (table.TryMint(
            handle: out var handle,
            index: query.Index
        )
            ? handle
            : (WorldHandle?)null
        );

        return new QueryAnswer(
            Text: ((minted is { } value)
                ? $"[grant.handle.mint: index:{query.Index} -> handle:{value.Index}/{value.Generation}]"
                : $"[grant.handle.mint: index:{query.Index} — no live slot]"
            ),
            Payload: minted
        );
    }
    // Resolves a previously minted WorldHandle against its own (TablePrincipal, TableCapability) table — the query
    // form of Server.WorldHandleTable.TryResolve.
    private QueryAnswer AnswerGrantHandleResolve(WorldQuery.GrantHandleResolve query) {
        var table = m_grants.HandleTable(
            capability: query.Handle.TableCapability,
            principal: query.Handle.TablePrincipal
        );
        var resolved = (table.TryResolve(
            handle: query.Handle,
            subject: out var subject
        )
            ? subject
            : (GrantSubject?)null
        );

        return new QueryAnswer(
            Text: ((resolved is { } value)
                ? $"[grant.handle.resolve: handle:{query.Handle.Index}/{query.Handle.Generation} -> {value.Describe()}]"
                : $"[grant.handle.resolve: handle:{query.Handle.Index}/{query.Handle.Generation} — no longer resolves]"
            ),
            Payload: resolved
        );
    }
    // The owned-world identity catalog, projected — the shape a query answer may cross a wire with (never the owned
    // document a raw WorldIdentity carries).
    private WorldIdentityProjection[] ProjectProfileCatalog() {
        var all = m_profiles.All;
        var projected = new WorldIdentityProjection[all.Count];

        for (var index = 0; (index < all.Count); index++) {
            projected[index] = all[index].Project();
        }

        return projected;
    }
    private QueryAnswer AnswerProfileCatalog() {
        var projected = ProjectProfileCatalog();

        return new QueryAnswer(
            Text: $"[world.profiles: {projected.Length} catalog entries]",
            Payload: projected
        );
    }
    private QueryAnswer AnswerFindProfile(WorldQuery.FindProfile query) {
        var found = m_profiles.Find(name: query.Name)?.Project();

        return new QueryAnswer(
            Text: ((found is { } value)
                ? $"[identity.find: '{query.Name}' -> {value.Id}]"
                : $"[identity.find: '{query.Name}' — no match]"
            ),
            Payload: found
        );
    }
    private QueryAnswer AnswerPreferredControllerProfile(WorldQuery.PreferredControllerProfile query) {
        var preferred = m_profiles.PreferredProfile(device: query.Device)?.Project();

        return new QueryAnswer(
            Text: ((preferred is { } value)
                ? $"[player.preferred: {query.Device} -> {value.Name}]"
                : $"[player.preferred: {query.Device} — no preference]"
            ),
            Payload: preferred
        );
    }
    // The public Answer surface remains the trusted in-process read-back composer. An envelope, however, may have
    // arrived over WorldTcpHost, so it crosses Observe before reaching that composer. Loopback queries are stamped as
    // Console and pass through the same check using the permissive local seed rather than a separate bypass.
    private QueryAnswer AnswerSubmittedQuery(WorldQuery query, WorldPrincipal principal) {
        var subject = query.ObservationSubject();
        var verdict = m_grants.Allows(
            capability: WorldCapability.Observe,
            principal: principal,
            subject: subject
        );

        if (!verdict.IsAllowed) {
            return new QueryAnswer(
                Text: $"[query refused: {principal.Describe()} cannot observe {subject.Describe()} ({verdict.DescribeDenial()})]",
                Refused: true
            );
        }

        return Answer(query: query);
    }
    // The inverse of the compiler's own literal-to-raw conversion (WorldRuleCompiler.ResolveWrite), applied to a LIVE
    // FixedQ4816 read instead of an authored constant. Compile-time kind-matching (EffectSourceKindMismatch) already
    // proved 'kind' equals the source operand's own resolved kind, so an Int/Bool source is always an EXACT integer
    // in fixed-point form (ReadWorldFact only ever reaches FromInteger for those two) — recovered by an exact shift,
    // never a float round-trip; a Fixed source's raw bits are copied verbatim, bit-identical to the source cell.
    private static long ConvertWorldFactToRaw(FixedQ4816 value, CellKind kind) => kind switch {
        CellKind.Fixed => value.Value,
        CellKind.Bool => ((value.Value != 0L)
        ? 1L
        : 0L),
        _ => (value.Value >> FixedQ4816.FractionBitCount), // Int.
    };
    // The KEYED counterpart of a slot cell's row-level rebase target: looks up an already-installed cell's own
    // advance trait so a scalar-value UpsertStateCell write preserves it (see the UpsertStateCell compose arm above)
    // rather than a fresh WorldStateCell record silently dropping it.
    private static WorldStateAdvance? FindCellAdvance(IReadOnlyList<WorldStateCell> cells, WorldCellName key) {
        foreach (var cell in cells) {
            if (cell.Key == key) {
                return cell.Advance;
            }
        }

        return null;
    }
    private static WorldFact Finite(FixedQ4816 value) => new(
        IsForever: false,
        Value: value
    );
    // $distance: — the straight-line distance between two named bodies, read through WorldServer.Body(int)'s own
    // bounds check (null for an out-of-range index or an inactive slot). Either side missing reads as
    // s_noBodyDistance rather than zero (see its own remarks).
    private FixedQ4816 ReadBodyDistance(CompiledBodyRef bodyA, CompiledBodyRef bodyB, ulong tick) {
        var a = Body(index: ResolveBodyRef(
            bodyRef: bodyA,
            tick: tick
        ));
        var b = Body(index: ResolveBodyRef(
            bodyRef: bodyB,
            tick: tick
        ));

        if (
            (a is null) ||
            (b is null)
        ) {
            return NoBodyDistance;
        }

        return (b.FixedPosition - a.FixedPosition).Length;
    }
    // $los: — the SAME WorldPopulation.HasLineOfSightBetween a sensed target's own RequiresLineOfSight check rides,
    // called against two RESOLVED body references. Either side resolving to no body (a negative index) reads as
    // false — no sight line to nothing, the ordinary "absent reads as the falsy value" convention.
    private bool ReadBodyLineOfSight(CompiledBodyRef bodyA, CompiledBodyRef bodyB, ulong tick) {
        var indexA = ResolveBodyRef(
            bodyRef: bodyA,
            tick: tick
        );
        var indexB = ResolveBodyRef(
            bodyRef: bodyB,
            tick: tick
        );

        return (
            (indexA >= 0) &&
            (indexB >= 0) &&
            m_population.HasLineOfSightBetween(
            bodyA: indexA,
            bodyB: indexB
        )
        );
    }
    // $parked: — the remaining reconnect-grace ticks for ONE named body, resolved through the SAME ResolveBodyRef
    // walk $distance:/$los: use for each of their two body references. THREE REGIMES, deliberately distinct:
    // ABSENT (a reference resolving to no live body, or an unparked one) reads as 0 through
    // WorldPopulation.ParkedRemainingTicks' own guards — the ordinary "absent reads as the neutral falsy value"
    // convention (see WorldRuleFacts.ParkedPrefix's remarks for why 0 is right for absence); FINITE parks read
    // their real remaining count; FOREVER (a null deadline — parked at rate 0) reads as null here and becomes
    // POSITIVE INFINITY in the fact layer, never a numeric sentinel: it IS parked (remaining > 0 holds, > any
    // finite holds, <= any finite does not), but there is no number to compare with or copy — a copy operand
    // alone cannot fire from it (see ApplyRuleEffect's own forever guard).
    private long? ReadParkedRemaining(CompiledBodyRef bodyRef, ulong tick) =>
        m_population.ParkedRemainingTicks(
            index: ResolveBodyRef(
                bodyRef: bodyRef,
                tick: tick
            ),
            tick: tick
        );
    // The $reduce: aggregate — a thin delegation to WorldStateReader.Reduce, the ONE (row, key) read seam's sibling
    // for a whole-row aggregate: it resolves EACH cell's value through TryRead's own per-key path (not the row's
    // declared cell list raw), so a future per-cell advance widening flows through here for free. Count is always
    // integer regardless of the row's declared kind (a count is never fixed-point); Max/Min/Sum preserve the row's
    // kind, matching the compiler's own ValueKind (WorldRuleCompiler.ResolveOperand's reduce branch). An empty row
    // reads as zero for every op — the SAME "absent reads as zero" precedent ReadStateCell itself follows for a
    // vanished cell.
    private FixedQ4816 ReadReduction(string row, WorldStateReduceOp op, ulong tick) =>
        WorldStateReader.Reduce(
            definition: m_definition,
            op: op,
            rowName: row,
            tick: tick
        );
    // Reads a declared cell as fixed point off the LIVE definition (Install swaps it on every apply, so this is
    // always this tick's settled document), through the ONE shared (row, key) resolver — which computes an advancing
    // row's LIVE value rather than its stored base, so a rule composes with the trait instead of duplicating it. A
    // row or cell the document no longer declares reads as zero rather than throwing — a mid-tick RemoveStateRow is
    // the only way to get there, and the next Install's recompile refuses the rule outright if it can no longer
    // resolve.
    private FixedQ4816 ReadStateCell(string row, string key, ulong tick) {
        if (
            !WorldStateReader.TryRead(
            definition: m_definition,
            key: key,
            rawValue: out var rawValue,
            row: out var declared,
            rowName: row,
            text: out _,
            tick: tick
        ) ||
            (rawValue is not { } raw)
        ) {
            return FixedQ4816.Zero;
        }

        return ((declared.Kind == CellKind.Fixed)
            ? FixedQ4816.FromRawBits(value: raw)
            : FixedQ4816.FromInteger(value: raw)
        );
    }
    // Shared by both sides of a compareState conjunct — the primary operand and, when present, the comparand — so
    // the two reads can never diverge in how a reserved channel or a declared row resolves to a live fact.
    private WorldFact ReadWorldFact(CompiledWorldOperand operand, ulong tick) => operand.Kind switch {
        WorldRuleFactKind.Tick => Finite(value: FixedQ4816.FromInteger(value: unchecked((long)tick))),
        WorldRuleFactKind.Population => Finite(value: FixedQ4816.FromInteger(value: m_population.ActiveCount())),
        WorldRuleFactKind.RegionOccupancy => Finite(value: FixedQ4816.FromInteger(value: m_events.OccupantCount(placementId: operand.Row!))),
        // $link: — the SAME per-tick staleness the link event family's own threshold comparison reads, in SIMULATION
        // ticks. An edge whose livenessGraceSeconds is unauthored is held at 0 by the feed itself, so a staleness
        // gate stays closed rather than opening on a world that never asked for liveness sensing.
        WorldRuleFactKind.LinkStaleness => Finite(value: FixedQ4816.FromInteger(value: m_events.LinkStalenessTicks(adjacencyName: operand.Row!))),
        // The SAME IWorldMachineMemoryPeek.TryPeek primitive WorldAddonRuntime's memory-watch family already rides,
        // called directly instead of accumulated as a change event. No machine booted (or no peek capability) reads
        // as 0 — never a hard refusal, since the machine can boot on a later tick.
        WorldRuleFactKind.MachineMemory => Finite(value: FixedQ4816.FromInteger(value: (Machines.TryPeek(
        screen: operand.Screen,
        address: operand.Address,
        out var raw
    )
        ? raw
        : (byte)0))),
        WorldRuleFactKind.Reduction => Finite(value: ReadReduction(
        row: operand.Row!,
        op: operand.Reduce,
        tick: tick
    )),
        WorldRuleFactKind.ArgBody => Finite(value: FixedQ4816.FromInteger(value: ResolveArgBody(
        row: operand.Row!,
        op: operand.Reduce,
        tick: tick
    ))),
        WorldRuleFactKind.BodyDistance => Finite(value: ReadBodyDistance(
        bodyA: operand.BodyA!.Value,
        bodyB: operand.BodyB!.Value,
        tick: tick
    )),
        WorldRuleFactKind.LineOfSight => Finite(value: FixedQ4816.FromInteger(value: (ReadBodyLineOfSight(
        bodyA: operand.BodyA!.Value,
        bodyB: operand.BodyB!.Value,
        tick: tick
    )
        ? 1
        : 0))),
        // Preserve the reserved channel's authored contract: $parked reports the population deadline's own
        // SIMULATION-tick unit. Engine-tick countdown rows use countdownState instead; changing this unrelated
        // channel's unit would silently retune every existing raw compareState threshold and fromState copy.
        WorldRuleFactKind.Parked => ((ReadParkedRemaining(
        bodyRef: operand.BodyA!.Value,
        tick: tick
    ) is { } remaining)
        ? Finite(value: FixedQ4816.FromInteger(value: remaining))
        : new WorldFact(
            Value: FixedQ4816.Zero,
            IsForever: true
        )),
        _ => Finite(value: ReadStateCell(
        row: operand.Row!,
        key: operand.Key!,
        tick: tick
    )),
    };
    // The $argmax:/$argmin: extremum — a thin delegation to WorldStateReader.ArgExtremum, the SAME per-key read seam
    // ReadReduction's sibling resolves each candidate cell through, filtered here to the body indices the LIVE
    // population actually holds (a cell whose key does not parse as a non-negative index is excluded inside the
    // reader itself; the row can gain a non-numeric-keyed cell after compile via an ordinary world.state.cell.set,
    // and compile-time already proved the row is keyed, not that every future key will parse). Ties resolve to the
    // LOWEST eligible index, deterministically. Returns -1 ("no body") when no cell is eligible.
    private int ResolveArgBody(string row, WorldStateReduceOp op, ulong tick) {
        var winner = WorldStateReader.ArgExtremum(
            definition: m_definition,
            rowName: row,
            op: op,
            tick: tick,
            isCandidateIndex: (index => (index < m_population.Capacity))
        );

        return ((winner is null)
            ? -1
            : int.Parse(
                s: winner,
                style: System.Globalization.NumberStyles.Integer,
                provider: System.Globalization.CultureInfo.InvariantCulture
            )
        );
    }
    // Resolves ONE body reference to a live 0-based index (or -1 for "no body") — a literal index passes through
    // unchanged (compile time already bounded it against the document's declared capacity), an argmax/argmin
    // resolves through the SAME ResolveArgBody walk the standalone $argmax:/$argmin: channel uses.
    private int ResolveBodyRef(CompiledBodyRef bodyRef, ulong tick) => (bodyRef.Kind switch {
        CompiledBodyRefKind.Literal => bodyRef.Index,
        _ => ResolveArgBody(
        row: bodyRef.Row!,
        op: ((bodyRef.Kind == CompiledBodyRefKind.ArgMax)
        ? WorldStateReduceOp.Max
        : WorldStateReduceOp.Min),
        tick: tick
    ),
    });
    // The item/currency fact vocabulary's cell key for a market participant. A seat's index is its own stable
    // identity (generation is always 0), so its key is the plain 0-based entity index — the same addressing
    // WorldRuleFacts.ArgMaxPrefix/ArgMinPrefix already read off an unkeyed row. A peer's index is not stable on its
    // own: WorldPopulationLimits recycles a vacated population slot for a later, unrelated connection, and
    // WorldGrants/the ownership escrow substrate both key a peer's real authority on the full (index, generation)
    // pair (WorldPrincipal's own equality) — so a market cell keys the same pair, or a later occupant of the same
    // slot would silently inherit the departed peer's balance/items/listing proceeds. The compound key never
    // collides with a seat's plain-integer key (it always carries a reserved '_' the kernel would otherwise refuse
    // in an authored key) and reads as a non-candidate to ArgExtremum's int.TryParse scan, exactly like any other
    // non-numeric key already does. Only a real player (seat or peer) may hold a market fact; console/world/addon/
    // document/group principals refuse here rather than minting a cell no player could ever read back.
    private static bool TryPlayerCellKey(WorldPrincipal principal, out string key) {
        switch (principal.Kind) {
            case PrincipalKind.Seat:
                key = principal.Index.ToString(provider: CultureInfo.InvariantCulture);

                return true;
            case PrincipalKind.Peer:
                key = $"{principal.Index.ToString(provider: CultureInfo.InvariantCulture)}_{principal.Generation.ToString(provider: CultureInfo.InvariantCulture)}";

                return true;
            default:
                key = string.Empty;

                return false;
        }
    }
    // The (row, key) PAIR rule at the mutation boundary: a null key means the row's SLOT cell, and a row that is
    // positively keyed (WorldStateRow.IsKeyed) has no single cell for a null key to mean — refused by name rather
    // than silently writing cells[0].
    private static bool TryResolveTargetKey(WorldStateRow row, string? key, out WorldCellName resolved, out string reason) {
        if (key is not null) {
            if (!WorldCellName.TryParse(
                candidate: key,
                name: out resolved,
                reason: out var keyReason
            )) {
                reason = $"cell key '{key}' {keyReason}";

                return false;
            }

            reason = string.Empty;

            return true;
        }

        resolved = WorldStateRow.SlotKey;

        if (row.IsKeyed) {
            reason = $"state row '{row.Name}' is keyed and no cell key was named — a keyed row has no single cell to write";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    // ContainsKey/ApplyEviction moved to Puck.World.Schema's WorldStateCellWriter (public, cross-project)
    // so an owned-identity document write — which has no ordered mutation domain of its own — runs the IDENTICAL
    // pure composition rather than a second reading of it. See WorldStateCellWriter's own remarks.

    /// <summary>Composes the authoritative answer to a read-back query.</summary>
    /// <param name="query">The read-back query.</param>
    /// <returns>The authoritative answer.</returns>
    public QueryAnswer Answer(WorldQuery query) {
        ArgumentNullException.ThrowIfNull(argument: query);

        return query switch {
            WorldQuery.PlayerWhere where when (Body(index: (where.Index - 1)) is { } body) => new QueryAnswer(
            Text: body.DescribeWhere(index: where.Index),
            Payload: (Source: body.Source, Pose: body.DescribePose())
        ),
            WorldQuery.PlayerWhere where => new QueryAnswer(
            Text: $"[player.where: player {where.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.PlayerChannels channels when (Body(index: (channels.Index - 1)) is { } body) => new QueryAnswer(Text: DescribeChannels(
            index: channels.Index,
            bodyIndex: (channels.Index - 1),
            body: body
        )),
            WorldQuery.PlayerChannels channels => new QueryAnswer(
            Text: $"[player.channels: player {channels.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.PlayerState state when (Body(index: (state.Index - 1)) is { } body) => new QueryAnswer(Text: $"[player.state: p{state.Index} identity={(body.Profile?.Id ?? "none")} {body.DescribeActionState()} outputs={DescribeDurableOutputs(entityIndex: (state.Index - 1))} writeback={DescribeDocumentReceipt(ownerId: body.Profile?.Id)}]"),
            WorldQuery.PlayerState state => new QueryAnswer(
            Text: $"[player.state: player {state.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.InputHolds => new QueryAnswer(Text: m_inputHold.Describe()),
            WorldQuery.Rules => new QueryAnswer(Text: DescribeRules()),
            WorldQuery.PlayerTargets targets when (Body(index: (targets.Index - 1)) is not null) => new QueryAnswer(Text: m_population.DescribeTargets(bodyIndex: (targets.Index - 1))),
            WorldQuery.PlayerTargets targets => new QueryAnswer(
            Text: $"[player.targets: player {targets.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.Contacts contacts when (Body(index: (contacts.Index - 1)) is { } body) => new QueryAnswer(Text: DescribeContacts(
            index: contacts.Index,
            body: body
        )),
            WorldQuery.Contacts contacts => new QueryAnswer(
            Text: $"[world.contacts: body {contacts.Index} is inactive — see world.population]",
            Refused: true
        ),
            WorldQuery.Properties properties => new QueryAnswer(Text: DescribeProperties(bodyIndex: properties.BodyIndex)),
            WorldQuery.Interactions => new QueryAnswer(Text: DescribeInteractions()),
            WorldQuery.GrantAllows allows => AnswerGrantAllows(query: allows),
            WorldQuery.GrantHandleMint mint => AnswerGrantHandleMint(query: mint),
            WorldQuery.GrantHandleResolve resolve => AnswerGrantHandleResolve(query: resolve),
            WorldQuery.PopulationChannels => new QueryAnswer(
            Text: $"[world.channels: {m_population.Channels.ChannelCount} declared]",
            Payload: m_population.Channels
        ),
            WorldQuery.ProfileCatalog => AnswerProfileCatalog(),
            WorldQuery.FindProfile find => AnswerFindProfile(query: find),
            WorldQuery.PreferredControllerProfile preferred => AnswerPreferredControllerProfile(query: preferred),
            WorldQuery.MusicState state when (Body(index: (state.Index - 1)) is not null) => new QueryAnswer(Text: DescribeMusicState()),
            WorldQuery.MusicState state => new QueryAnswer(
            Text: $"[music.state: player {state.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.JudgeState state when (Body(index: (state.Index - 1)) is not null) => new QueryAnswer(Text: DescribeJudgeState()),
            WorldQuery.JudgeState state => new QueryAnswer(
            Text: $"[judge.state: player {state.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            _ => new QueryAnswer(Text: string.Empty),
        };
    }

    // One live fact off a rule operand: a fixed-point value, or POSITIVE INFINITY (IsForever) for the one channel
    // whose magnitude can exceed every number — $parked: on a forever-parked body. Infinity participates in
    // comparisons through the ActionStateComparisons overload and is never encoded as a numeric stand-in.
    private readonly record struct WorldFact(FixedQ4816 Value, bool IsForever);
}
