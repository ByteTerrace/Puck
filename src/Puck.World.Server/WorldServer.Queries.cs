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
                Text: $"[query refused: {verdict.DescribeRefusal(
                    actor: principal,
                    subject: subject.Describe(),
                    verb: "observe"
                )}]",
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
    private static WorldFact Finite(FixedQ4816 value) => new(
        IsForever: false,
        Value: value
    );
    // $distance: — the straight-line distance between two named bodies, read through WorldServer.Body(int)'s own
    // bounds check (null for an out-of-range index or an inactive slot). Either side missing reads as
    // s_noBodyDistance rather than zero (see its own remarks).
    private FixedQ4816 ReadBodyDistance(CompiledBodyRef bodyA, CompiledBodyRef bodyB, ulong tick) =>
        ReadBodyDistance(
            bodyA: ResolveBodyRef(
                bodyRef: bodyA,
                tick: tick
            ),
            bodyB: ResolveBodyRef(
                bodyRef: bodyB,
                tick: tick
            )
        );
    private FixedQ4816 ReadBodyDistance(int bodyA, int bodyB) => (
        ((Body(index: bodyA) is { } a) && (Body(index: bodyB) is { } b))
            ? (b.FixedPosition - a.FixedPosition).Length
            : NoBodyDistance
    );
    // The squared sibling for a range test that never needs the root; the same NoBodyDistance sentinel for a
    // missing side, which a caller must test for before comparing against an unbounded range.
    private FixedQ4816 ReadBodyDistanceSquared(int bodyA, int bodyB) => (
        ((Body(index: bodyA) is { } a) && (Body(index: bodyB) is { } b))
            ? (b.FixedPosition - a.FixedPosition).LengthSquared
            : NoBodyDistance
    );
    // $los: — the same WorldPopulation.HasLineOfSightBetween a sensed target's own RequiresLineOfSight check rides,
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
    // $parked: — the remaining reconnect-grace ticks for ONE named body, resolved through the same ResolveBodyRef
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
    // $channel: — the 1-based local seat's channel value as its body integrates it that tick: the drained
    // CommandSnapshot's direct read folded with co-driving contributions and the admitted held overlay (a probe axis
    // or any other held sample reaches a channel only through that overlay), in the channel's own FixedQ4816 domain.
    // Compile time already bounded seat to 0..LocalSeatCount-1 and channelOrdinal to a declared channel; an
    // out-of-range seat, or one no local seat currently occupies, reads Zero — the convention $parked:/$machine:/
    // $region: already set.
    private FixedQ4816 ReadChannelValue(int seat, int ordinal) {
        if (
            (((uint)seat) >= ((uint)m_population.LocalSeatCount)) ||
            !m_population.IsHumanOccupied(bodyIndex: seat)
        ) {
            return FixedQ4816.Zero;
        }

        return (Body(index: seat)?.ChannelReadComposed[ordinal] ?? FixedQ4816.Zero);
    }
    // The $reduce: aggregate — a thin delegation to WorldStateReader.Reduce, the ONE (row, key) read seam's sibling
    // for a whole-row aggregate: it resolves EACH cell's value through TryRead's own per-key path (not the row's
    // declared cell list raw), so a future per-cell advance widening flows through here for free. Count is always
    // integer regardless of the row's declared kind (a count is never fixed-point); Max/Min/Sum preserve the row's
    // kind, matching the compiler's own ValueKind (WorldRuleCompiler.ResolveOperand's reduce branch). An empty row
    // reads as zero for every op — the same "absent reads as zero" precedent ReadStateCell itself follows for a
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
        // $link: — the same per-tick staleness the link event family's own threshold comparison reads, in SIMULATION
        // ticks. An edge whose livenessGraceSeconds is unauthored is held at 0 by the feed itself, so a staleness
        // gate stays closed rather than opening on a world that never asked for liveness sensing.
        WorldRuleFactKind.LinkStaleness => Finite(value: FixedQ4816.FromInteger(value: m_events.LinkStalenessTicks(adjacencyName: operand.Row!))),
        // The same IWorldMachineMemoryPeek.TryPeek primitive WorldAddonRuntime's memory-watch family already rides,
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
        WorldRuleFactKind.Channel => Finite(value: ReadChannelValue(
        seat: operand.Seat,
        ordinal: operand.ChannelOrdinal
    )),
        WorldRuleFactKind.Nearest => Finite(value: FixedQ4816.FromInteger(value: ResolveNearestBody(
        from: operand.BodyA!.Value,
        row: operand.Row!,
        tick: tick
    ))),
        _ => Finite(value: ReadStateCell(
        row: operand.Row!,
        key: ResolveOperandKey(
        key: operand.Key,
        keyFrom: operand.KeyFrom,
        tick: tick
    ),
        tick: tick
    )),
    };
    // The $argmax:/$argmin: extremum — a thin delegation to WorldStateReader.ArgExtremum, the same per-key read seam
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
    // resolves through the same ResolveArgBody walk the standalone $argmax:/$argmin: channel uses.
    // A '$cell:' key indirection: the cell's integer value spelled as a key; an absent cell reads 0 like any other.
    // The integer part of a Q48.16 value — the key or index a cell's value names.
    private static long IntegerOf(FixedQ4816 value) => (value.Value >> 16);
    // The bodies bound for the evaluation in progress — set by the rule/interaction evaluator before a gate or
    // effect is read, -1 when a binding is not in play.
    private int m_boundEach = -1;
    private int m_boundLeft = -1;
    private int m_boundRight = -1;

    private int BoundBody(RuleBinding binding) => binding switch {
        RuleBinding.Each => m_boundEach,
        RuleBinding.Left => m_boundLeft,
        RuleBinding.Right => m_boundRight,
        _ => -1,
    };
    // A '$cell:' key indirection reads the cell's integer value as a key; a binding token reads the bound body.
    private string ResolveOperandKey(string? key, CompiledCellRef? keyFrom, ulong tick) {
        if (keyFrom is not { } indirection) {
            return key!;
        }

        var index = ((indirection.Binding != RuleBinding.None)
            ? BoundBody(binding: indirection.Binding)
            : IntegerOf(value: ReadStateCell(
                row: indirection.Row,
                key: indirection.Key,
                tick: tick
            ))
        );

        return index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
    }
    // The nearest active body to 'from' (itself excluded) whose cell in the keyed tag row reads nonzero, or -1.
    private int ResolveNearestBody(CompiledBodyRef from, string row, ulong tick) {
        var origin = Body(index: ResolveBodyRef(
            bodyRef: from,
            tick: tick
        ));

        if (origin is null) {
            return -1;
        }

        var originIndex = ResolveBodyRef(
            bodyRef: from,
            tick: tick
        );
        var best = -1;
        var bestDistance = FixedQ4816.Zero;

        for (var index = 0; (index < m_population.Capacity); index++) {
            if (
                (index == originIndex) ||
                (Body(index: index) is not { } candidate) ||
                (ReadStateCell(
                row: row,
                key: index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                tick: tick
            ) == FixedQ4816.Zero)
            ) {
                continue;
            }

            var distance = (candidate.FixedPosition - origin.FixedPosition).Length;

            if (
                (best < 0) ||
                (distance < bestDistance)
            ) {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }
    private int ResolveBodyRef(CompiledBodyRef bodyRef, ulong tick) => (bodyRef.Kind switch {
        CompiledBodyRefKind.Literal => bodyRef.Index,
        CompiledBodyRefKind.Binding => BoundBody(binding: (RuleBinding)bodyRef.Index),
        CompiledBodyRefKind.Cell => ((IntegerOf(value: ReadStateCell(
        row: bodyRef.Row!,
        key: bodyRef.Key!,
        tick: tick
    )) is var cellIndex) && (cellIndex >= 0) && (cellIndex < m_population.Capacity)
        ? ((int)cellIndex)
        : -1),
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
    // own: WorldBodiesLimits recycles a vacated population slot for a later, unrelated connection, and
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
            WorldQuery.PlayerWhere where when (Body(index: where.Index) is { } body) => new QueryAnswer(
            Text: body.DescribeWhere(index: where.Index),
            Payload: (Source: body.Source, Pose: body.DescribePose())
        ),
            WorldQuery.PlayerWhere where => new QueryAnswer(
            Text: $"[body.where: body:{where.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.PlayerChannels channels when (Body(index: channels.Index) is { } body) => new QueryAnswer(Text: DescribeChannels(
            bodyIndex: channels.Index,
            body: body
        )),
            WorldQuery.PlayerChannels channels => new QueryAnswer(
            Text: $"[body.channels: body:{channels.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.PlayerState state when (Body(index: state.Index) is { } body) => new QueryAnswer(Text: $"[body.state: body:{state.Index} identity={(body.Profile?.Id ?? "none")} {body.DescribeActionState()} outputs={DescribeDurableOutputs(entityIndex: state.Index)} writeback={DescribeDocumentReceipt(ownerId: body.Profile?.Id)}]"),
            WorldQuery.PlayerState state => new QueryAnswer(
            Text: $"[body.state: body:{state.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.InputHolds => new QueryAnswer(Text: m_inputHold.Describe()),
            WorldQuery.Rules => new QueryAnswer(Text: DescribeRules()),
            WorldQuery.PlayerTargets targets when (Body(index: targets.Index) is not null) => new QueryAnswer(Text: m_population.DescribeTargets(bodyIndex: targets.Index)),
            WorldQuery.PlayerTargets targets => new QueryAnswer(
            Text: $"[body.targets: body:{targets.Index} is not an active population entry — see world.population]",
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
