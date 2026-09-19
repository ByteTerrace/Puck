using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    // The query form of Server.WorldGrants.Allows — Text carries the same shape world.why prints, so a caller
    // reading the wire's Completion lane (which drops Payload) still gets an answer.
    private QueryAnswer AnswerGrantAllows(WorldQuery.GrantAllows query) {
        var verdict = Host.GrantTable.Allows(
            capability: query.Capability,
            principal: query.Principal,
            subject: query.Subject
        );

        return new QueryAnswer(
            Text: $"[grant.allows: {query.Principal.Describe()} {query.Capability.ToString().ToLowerInvariant()} {query.Subject.Describe()} = {(verdict.IsAllowed
            ? "allowed"
            : "denied")} ({verdict.Describe()})]",
            Payload: verdict
        );
    }
    // Mints a WorldHandle for the query's (principal, capability, index), the query form of Server.WorldHandleTable.
    // TryMint — preserves the constructor's own trust-boundary check (throws for a Console/Seat principal), matching
    // the direct in-process call this replaces.
    private QueryAnswer AnswerGrantHandleMint(WorldQuery.GrantHandleMint query) {
        var table = Host.GrantTable.HandleTable(
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
            : $"[grant.handle.mint: index:{query.Index} — no live slot]"),
            Payload: minted
        );
    }
    // Resolves a previously minted WorldHandle against its own (TablePrincipal, TableCapability) table — the query
    // form of Server.WorldHandleTable.TryResolve.
    private QueryAnswer AnswerGrantHandleResolve(WorldQuery.GrantHandleResolve query) {
        var table = Host.GrantTable.HandleTable(
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
            : $"[grant.handle.resolve: handle:{query.Handle.Index}/{query.Handle.Generation} — no longer resolves]"),
            Payload: resolved
        );
    }
    // The owned-world identity catalog, projected — the shape a query answer may cross a wire with (never the owned
    // document a raw WorldIdentity carries).
    private WorldIdentityProjection[] ProjectProfileCatalog() {
        var all = Host.Profiles.All;
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
        var found = Host.Profiles.Find(name: query.Name)?.Project();

        return new QueryAnswer(
            Text: ((found is { } value)
            ? $"[identity.find: '{query.Name}' -> {value.Id}]"
            : $"[identity.find: '{query.Name}' — no match]"),
            Payload: found
        );
    }
    private QueryAnswer AnswerPreferredControllerProfile(WorldQuery.PreferredControllerProfile query) {
        var preferred = Host.Profiles.PreferredProfile(device: query.Device)?.Project();

        return new QueryAnswer(
            Text: ((preferred is { } value)
            ? $"[player.preferred: {query.Device} -> {value.Name}]"
            : $"[player.preferred: {query.Device} — no preference]"),
            Payload: preferred
        );
    }
    // The public Answer surface remains the trusted in-process read-back composer. An envelope, however, may have
    // arrived over WorldPeerHost, so it crosses Observe before reaching that composer. Loopback queries are stamped as
    // Console and pass through the same check using the permissive local seed rather than a separate bypass.
    private QueryAnswer AnswerStateObservations(WorldPrincipal? principal, string? row = null) {
        var time = Time;
        var rows = (WorldStateDisclosure.Compose(
            arena: Host.Arena,
            definition: Host.Definition,
            recipient: principal,
            time: in time
        ) ?? []).Where(predicate: r => ((row is null) || (r.Name == row))).ToArray();

        return new QueryAnswer(
            System.Text.Json.JsonSerializer.Serialize(
                rows,
                WorldJsonContext.Default.WorldObservedRowArray
            ),
            Payload: rows
        );
    }
    /// <summary>Answers one submitted query under the caller's own Observe verdict.</summary>
    /// <param name="query">The query.</param>
    /// <param name="principal">The querying principal.</param>
    /// <returns>The answer, or its refusal.</returns>
    public QueryAnswer AnswerSubmittedQuery(WorldQuery query, WorldPrincipal principal) {
        var subject = query.ObservationSubject();
        var verdict = Host.GrantTable.Allows(
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

        if (query is WorldQuery.StateObservations observation) {
            return AnswerStateObservations(
            principal: principal,
            row: observation.Row
        );
        }
        if (query is WorldQuery.ReflowPreview or WorldQuery.ReflowStatus or WorldQuery.ReflowCancel) {
            return Host.AnswerReflowQuery(
                principal: principal,
                query: query
            );
        }
        return Answer(query: query);
    }
    // Either side resolving to no body reads false — no sight line to nothing.
    private bool ReadBodyLineOfSight(int indexA, int indexB) => (
        (indexA >= 0) &&
        (indexB >= 0) &&
        Host.Population.HasLineOfSightBetween(
        bodyA: indexA,
        bodyB: indexB
    )
    );
    // A body's local +Y rotated by its live orientation, dotted against the up axis its gravity opposes. An absent
    // body reads one (perfectly upright) — never "knocked over" for a body that does not exist.
    private FixedQ4816 ReadBodyUpright(int index) => ((Host.Body(index: index) is { } body)
        ? FixedVector3.Dot(
            left: body.FixedOrientation.Rotate(vector: LocalUp),
            right: body.FixedUp
        )
        : FixedQ4816.One
    );
    // The nearest active body to the origin (itself excluded) whose cell in the keyed tag row reads nonzero, or -1.
    private int ResolveNearestBody(int origin, int tagRowOrdinal) {
        if (Host.Body(index: origin) is not { } from) {
            return -1;
        }

        var best = -1;
        var bestDistance = FixedQ4816.Zero;

        for (var index = 0; (index < Host.Population.Capacity); index++) {
            if (
                (index == origin) ||
                (Host.Body(index: index) is not { } candidate) ||
                !RuleReads.TryIndexKey(
                catalog: Host.Arena.Catalog,
                index: index,
                key: out var key
            ) ||
                (Host.ReadArenaCell(
                key: key,
                rowOrdinal: tagRowOrdinal
            ) == FixedQ4816.Zero)
            ) {
                continue;
            }

            var distance = (candidate.FixedPosition - from.FixedPosition).Length;

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
    private FixedQ4816 ReadBodyDistance(int bodyA, int bodyB) => (
        ((Host.Body(index: bodyA) is { } a) && (Host.Body(index: bodyB) is { } b))
        ? (b.FixedPosition - a.FixedPosition).Length
        : NoBodyDistance
    );
    // The squared sibling for a range test that never needs the root; the same NoBodyDistance sentinel for a
    // missing side, which a caller must test for before comparing against an unbounded range.
    private FixedQ4816 ReadBodyDistanceSquared(int bodyA, int bodyB) => (
        ((Host.Body(index: bodyA) is { } a) && (Host.Body(index: bodyB) is { } b))
        ? (b.FixedPosition - a.FixedPosition).LengthSquared
        : NoBodyDistance
    );
    // $channel: — the 1-based local seat's channel value as its body integrates it that tick: the drained
    // CommandSnapshot's direct read folded with co-driving contributions and the admitted held overlay (a probe axis
    // or any other held sample reaches a channel only through that overlay), in the channel's own FixedQ4816 domain.
    // Compile time already bounded seat to 0..LocalSeatCount-1 and channelOrdinal to a declared channel; an
    // out-of-range seat, or one no local seat currently occupies, reads Zero — the convention $parked:/$machine:/
    // $region: already set.
    private FixedQ4816 ReadChannelValue(int seat, int ordinal) {
        if (
            (((uint)seat) >= ((uint)Host.Population.LocalSeatCount)) ||
            !Host.Population.IsHumanOccupied(bodyIndex: seat)
        ) {
            return FixedQ4816.Zero;
        }

        return (Host.Body(index: seat)?.ChannelReadComposed[ordinal] ?? FixedQ4816.Zero);
    }
    // Reads a declared cell as fixed point off the LIVE definition (Install swaps it on every apply, so this is
    // always this tick's settled document), through the ONE shared (row, key) resolver — which computes an advancing
    // row's LIVE value rather than its stored base, so a rule composes with the trait instead of duplicating it. A
    // row or cell the document no longer declares reads as zero rather than throwing — a mid-tick RemoveStateRow is
    // the only way to get there, and the next Install's recompile refuses the rule outright if it can no longer
    // resolve.
    private FixedQ4816 ReadStateCell(string row, string key, ulong tick) {
        if (
            !WorldStateReader.TryRead(
            definition: Host.Definition,
            key: key,
            rawValue: out var rawValue,
            row: out var declared,
            rowName: row,
            text: out _,
            tick: tick,
            engineTick: Host.CompletedEngineTicks
        ) ||
            (rawValue is not { } raw)
        ) {
            return FixedQ4816.Zero;
        }

        return ((declared.Kind == CellKind.Fixed)
            ? FixedQ4816.FromRawBits(value: raw)
            : StateReader.LiftSaturating(raw: raw)
        );
    }
    // Resolves ONE body reference to a live 0-based index (or -1 for "no body") — a literal index passes through
    // unchanged (compile time already bounded it against the document's declared capacity), an argmax/argmin
    // resolves through the same ResolveArgBody walk the standalone $argmax:/$argmin: channel uses.
    // A '$cell:' key indirection: the cell's integer value spelled as a key; an absent cell reads 0 like any other.
    // The integer part of a Q48.16 value — the key or index a cell's value names.
    private static long IntegerOf(FixedQ4816 value) => (value.Value >> 16);
    // The static tables the definition references, in tables-row order; a validated document's rows are proven to
    // load, so a failure here is an invariant violation, never a reachable case.
    /// <summary>Recompiles the <c>tables</c> section from a definition — the boot compile the install path
    /// otherwise folds into <c>RecompileRules</c>.</summary>
    /// <param name="definition">The definition to compile from.</param>
    internal void RecompileTables(WorldDefinition definition) => m_tables = CompileTables(definition: definition);
    private static CompiledTable[] CompileTables(WorldDefinition definition) {
        var rows = (definition.Tables ?? []);
        var compiled = new CompiledTable[rows.Count];

        for (var index = 0; (index < compiled.Length); index++) {
            if (!WorldTables.TryCompile(
                row: rows[index],
                table: out var table,
                error: out var error
            )) {
                throw new InvalidOperationException(message: $"tables[{rows[index].Name}]: {error} (a validated document must still resolve at construction)");
            }
            compiled[index] = table!;
        }
        return compiled;
    }

    /// <summary>Describes every static table the definition references: name, kind, entry count.</summary>
    internal string DescribeTables() {
        if (m_tables.Length == 0) {
            return "[world.tables: none]";
        }
        return $"[world.tables: {string.Join(
            separator: " | ",
            values: m_tables.Select(selector: static table => $"{table.Name} kind={StateSpelling.Kind(kind: table.Kind)} entries={table.Count}{((table.ColumnNames.Count > 0)
            ? $" columns=[{string.Join(
                    separator: ",",
                    values: table.ColumnNames
                )}]"
            : string.Empty)}")
        )}]";
    }

    // Canonical "a_b" pair keys (underscore, not colon: CellName reserves ':'), cached per distinct DIRECTED
    // pair once minted so a steady-state rule scan allocates nothing: (a, b) and (b, a) name different cells (an
    // observer's impression of a subject is not the reverse), and the domain (population capacity squared) is too
    // large to precompute the way IndexKeyCache's single-index table is, so this grows lazily instead — the
    // first read of a never-before-seen pair mints its key once, and every later read of that same directed pair is
    // a dictionary hit.
    private readonly Dictionary<long, string> m_pairKeyCache = [];

    private string ResolvePairKey(int a, int b) {
        var packed = (((long)a) << 32) | ((uint)b);

        if (!m_pairKeyCache.TryGetValue(
            key: packed,
            value: out var pairKey
        )) {
            pairKey = $"{a}_{b}";
            m_pairKeyCache[packed] = pairKey;
        }

        return pairKey;
    }
    // The (row, key) PAIR rule at the mutation boundary: a null key means the row's SLOT cell, and a row that is
    // positively keyed (WorldStateRow.IsKeyed) has no single cell for a null key to mean — refused by name rather
    // than silently writing cells[0].
    private static bool TryResolveTargetKey(WorldStateRow row, string? key, out CellName resolved, out string reason) {
        if (key is not null) {
            if (!CellName.TryParse(
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

    /// <summary>Composes the authoritative answer to a read-back query.</summary>
    /// <param name="query">The read-back query.</param>
    /// <returns>The authoritative answer.</returns>
    internal QueryAnswer Answer(WorldQuery query) {
        ArgumentNullException.ThrowIfNull(argument: query);

        return query switch {
            WorldQuery.PlayerWhere where when (Host.Body(index: where.Index) is { } body) => new QueryAnswer(
            Text: body.DescribeWhere(index: where.Index),
            Payload: (Source: body.Source, Pose: body.DescribePose())
        ),
            WorldQuery.PlayerWhere where => new QueryAnswer(
            Text: $"[body.where: body:{where.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.PlayerChannels channels when (Host.Body(index: channels.Index) is { } body) => new QueryAnswer(Text: Host.DescribeChannels(
            bodyIndex: channels.Index,
            body: body
        )),
            WorldQuery.PlayerChannels channels => new QueryAnswer(
            Text: $"[body.channels: body:{channels.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.PlayerState state when (Host.Body(index: state.Index) is { } body) => new QueryAnswer(Text: $"[body.state: body:{state.Index} identity={(body.Profile?.Id ?? "none")} {body.DescribeActionState()} outputs={Host.DescribeDurableOutputs(entityIndex: state.Index)} writeback={Host.DescribeDocumentReceipt(ownerId: body.Profile?.Id)}]"),
            WorldQuery.PlayerState state => new QueryAnswer(
            Text: $"[body.state: body:{state.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.StateObservations observation => AnswerStateObservations(
            principal: null,
            row: observation.Row
        ),
            WorldQuery.InputHolds => new QueryAnswer(Text: Host.InputHold.Describe()),
            WorldQuery.Rules => new QueryAnswer(Text: Host.DescribeRules()),
            WorldQuery.PlayerTargets targets when (Host.Body(index: targets.Index) is not null) => new QueryAnswer(Text: Host.Population.DescribeTargets(bodyIndex: targets.Index)),
            WorldQuery.PlayerTargets targets => new QueryAnswer(
            Text: $"[body.targets: body:{targets.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.Contacts contacts when (Host.Body(index: (contacts.Index - 1)) is { } body) => new QueryAnswer(Text: WorldServer.DescribeContacts(
            index: contacts.Index,
            body: body
        )),
            WorldQuery.Contacts contacts => new QueryAnswer(
            Text: $"[world.contacts: body {contacts.Index} is inactive — see world.population]",
            Refused: true
        ),
            WorldQuery.Properties properties => new QueryAnswer(Text: Host.DescribeProperties(bodyIndex: properties.BodyIndex)),
            WorldQuery.Interactions => new QueryAnswer(Text: Host.DescribeInteractions()),
            WorldQuery.GrantAllows allows => AnswerGrantAllows(query: allows),
            WorldQuery.GrantHandleMint mint => AnswerGrantHandleMint(query: mint),
            WorldQuery.GrantHandleResolve resolve => AnswerGrantHandleResolve(query: resolve),
            WorldQuery.PopulationChannels => new QueryAnswer(
            Text: $"[world.channels: {Host.Population.Channels.ChannelCount} declared]",
            Payload: Host.Population.Channels
        ),
            WorldQuery.ProfileCatalog => AnswerProfileCatalog(),
            WorldQuery.FindProfile find => AnswerFindProfile(query: find),
            WorldQuery.PreferredControllerProfile preferred => AnswerPreferredControllerProfile(query: preferred),
            WorldQuery.MusicState state when (Host.Body(index: (state.Index - 1)) is not null) => new QueryAnswer(Text: Host.DescribeMusicState()),
            WorldQuery.MusicState state => new QueryAnswer(
            Text: $"[music.state: player {state.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            WorldQuery.InstrumentState state when (Host.Body(index: (state.Index - 1)) is not null) => new QueryAnswer(Text: Host.DescribeInstrumentState(seatSlot: (state.Index - 1))),
            WorldQuery.InstrumentState state => new QueryAnswer(
            Text: $"[instrument.state: player {state.Index} is not an active population entry — see world.population]",
            Refused: true
        ),
            _ => new QueryAnswer(Text: string.Empty),
        };
    }

}
