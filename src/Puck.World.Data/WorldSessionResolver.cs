using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The transport-neutral local resolver docs/world-model.md's "Resolve once; consume with separate lifetimes" and
/// "Durability, scope and generation" sections describe — the one place a <see cref="WorldDestination"/> row plus a
/// traveling cohort's claims become a scoped identity: a target-issued generation, a scope key, and the
/// process-local instance name that generation resolves to. <c>Puck.World.WorldInstanceHost</c>'s portal-scan path
/// (<c>ResolveAndEnqueueCoalescedTransfers</c>/<c>EnqueueCoalescedGroup</c>, and <c>ApplyTransfer</c>'s own
/// drain-time re-verification via <see cref="TryDeriveScopeKey"/>) is today's only caller (see those methods' own
/// remarks on the console <c>world.transfer</c> verb's raw <c>ephemeral</c>/<c>persisted</c> forms, which name no
/// destinations row and so have nothing for this type to resolve). Public rather than internal: this type carries
/// no dependency on the composition root, since every type it touches (<see cref="WorldDefinition"/>,
/// <see cref="WorldDestination"/>, <see cref="WorldGroupSelector"/>, <see cref="WorldGroup"/>,
/// <see cref="WorldPrincipal"/>) already lives in this assembly, so it is declared here rather than in
/// <c>Puck.World</c> behind an <c>InternalsVisibleTo</c> grant. This also lets an in-process law in
/// <c>tests/Puck.World.Tests</c> (which references this assembly but deliberately never <c>Puck.World</c> — see
/// that project's own README) exercise the idempotence and generation-lifecycle rules directly.
/// </summary>
/// <remarks>
/// <para><b>Idempotent by construction.</b> Resolving the same (destination, durability, scope key, referenced
/// document) while a generation is still active returns the identical generation id and instance name — never a
/// fresh mint — so two travelers crossing the same scoped door in the same tick, or a later display/entry consumer
/// resolving the same door again, land on one generation (docs/world-model.md "Idempotent resolution"). A
/// generation is minted exactly once, from <see cref="m_nextGenerationId"/> — an ordered counter this resolver
/// alone owns, advancing only when a caller actually asks (the same "moves only when asked, never with time" shape
/// <c>Puck.World.Server.WorldHandleTable.m_nextGeneration</c> already follows) — and the mint is recorded in
/// <see cref="m_active"/> before the caller ever sees it. The key carries two components beyond destination name
/// and scope key: <c>referencedDocument</c>, the destination row's own resolved canonical referenced document
/// identity (the caller canonicalizes; this resolver stays I/O-free), and <c>durability</c> — because a destination
/// name is document-local (two different documents can author a row spelled identically that names two entirely
/// unrelated referenced documents) and durability is not implied by name+scope+document either (an ephemeral and a
/// persisted row can otherwise share all three and still mean two different retention contracts) — see
/// <see cref="m_active"/>'s own remarks for both.</para>
/// <para><b>Generation lifecycle.</b> Ephemeral: the first scoped resolution mints a generation and its instance
/// name; every later resolution against the same key while that instance is still running reuses it;
/// <see cref="NotifyInstanceRetired"/> — called from <c>Puck.World.WorldInstanceHost.TryStop</c>, which is also
/// <c>Puck.World.WorldInstanceHost.ReapIfEmpty</c>'s own apply path — clears the cache entry the moment the
/// instance actually goes away, so the next scoped resolution mints a genuinely new generation rather than reusing
/// a name nothing answers to. Persisted: the same cache carries a stable entry that is never cleared by
/// <c>ReapIfEmpty</c> (a persisted-lifetime resolution is retained — see
/// <c>Puck.World.WorldInstanceHost.TransferDestination</c>'s own remarks) — only an explicit
/// <c>world.instance.stop</c> clears it, mirroring "an explicit reset ends a generation through the same target
/// decision" (docs/world-model.md). Releasing an observation lease alone never advances anything here, because
/// nothing about observation reaches this type yet — see docs/world-model.md's own "Observation and display"
/// gap.</para>
/// <para><b>Scope keys never collide across kinds:</b> <c>user:&lt;identity-id&gt;</c>, <c>group:&lt;group-id&gt;</c>,
/// and the fixed <see cref="GlobalScopeKey"/> live in one namespace by construction (an identity id and a group id
/// are both opaque strings a bare prefix cannot confuse for the other kind, and neither can ever equal the fixed
/// global sentinel).</para>
/// <para><b>Cohort coherence.</b> One call resolves one scope key for the whole cohort passed in — never the
/// triggering member's key alone and never a silent split. User scope requires every member to share one identity
/// id (an anonymous member, or a cohort naming more than one identity, refuses by name). Group/named requires every
/// member to hold the same named group's membership. Group/tagged requires every member's own unique tagged
/// membership to resolve to the same group id — zero or multiple tagged memberships for any one member refuses by
/// name, naming the candidates when there is more than one (docs/world-model.md "Durability, scope and
/// generation").</para>
/// </remarks>
public sealed class WorldSessionResolver {
    /// <summary>The fixed scope key every <see cref="WorldDestinationScope.Global"/> resolution shares.</summary>
    public const string GlobalScopeKey = "global";

    // (destination name, durability, scope key, RESOLVED REFERENCED DOCUMENT) -> the generation currently active for
    // that key. A Dictionary rather than a per-destination list: lookups are the hot path (every crossing
    // re-resolves), and a destination scoped by many distinct users/groups at once is exactly the case this type
    // exists to keep apart. ReferencedDocument is part of the key because destination NAMES are document-local — two
    // different documents can author a row spelled identically ('home'/global) that names two unrelated referenced
    // documents, so name+scope alone would collide two provably different destinations. Durability is part of the
    // key because an ephemeral and a persisted row can otherwise share name+scope+document while meaning two
    // different retention contracts (ephemeral mints fresh and reaps when empty; persisted retains until an explicit
    // stop) — sharing one cache entry between them would let whichever row resolves first impose its retention rule
    // on the other's traveler.
    private readonly Dictionary<(string Destination, WorldDestinationDurability Durability, string ScopeKey, string ReferencedDocument), Generation> m_active = new();
    // The reverse index NotifyInstanceRetired needs: a caller reports an instance NAME going away (that is what
    // TryStop/ReapIfEmpty know), never the key(s) that minted it. A HashSet per instance name, not a single key:
    // origin adoption (TryAdopt) can install MORE THAN ONE key against the SAME already-running instance — e.g. two
    // distinct persisted destinations that both resolve to the boot instance's own document — so retirement must
    // clear EVERY key this instance name was ever installed under, not just the last one.
    private readonly Dictionary<string, HashSet<(string Destination, WorldDestinationDurability Durability, string ScopeKey, string ReferencedDocument)>> m_byInstanceName = new(comparer: StringComparer.Ordinal);
    // A PURE function of resolution order — never wall-clock, RNG, or tick-of-entry (docs/world-model.md "Resolution
    // and transfer are ordered authority events"). Advances by exactly one per NEW generation minted, mirroring
    // WorldInstanceHost.m_freshCounters' own determinism shape one level up: within one process run, the Nth
    // generation minted for a given (destination, scope key) is always the SAME generation, because this resolver
    // and its caller are both driven off the SAME single fixed-step thread.
    private ulong m_nextGenerationId;

    private readonly record struct Generation(ulong GenerationId, string InstanceName);

    /// <summary>One resolved scoped session — the identity docs/world-model.md's <c>ResolvedWorldSession</c> names,
    /// narrowed to what a same-process caller needs today (no remote authority/session id/epoch yet).</summary>
    /// <param name="DestinationName">The resolved <see cref="WorldDestination.Name"/>.</param>
    /// <param name="ScopeKey">The resolved scope key — <c>user:&lt;id&gt;</c>, <c>group:&lt;id&gt;</c>, or
    /// <see cref="GlobalScopeKey"/>.</param>
    /// <param name="GenerationId">The target-issued generation id — stable across every resolution that reuses this
    /// generation, advancing only when a NEW one is minted.</param>
    /// <param name="InstanceName">The process-local <c>Puck.World.WorldInstanceHost</c> instance name this
    /// generation resolves to — safe to pass straight to
    /// <c>Puck.World.WorldInstanceHost.TransferDestination.Resolved</c>.</param>
    /// <param name="IsNewGeneration">Whether THIS call minted <paramref name="GenerationId"/> — <see langword="false"/>
    /// when an already-active generation was reused.</param>
    public readonly record struct Resolved(string DestinationName, string ScopeKey, ulong GenerationId, string InstanceName, bool IsNewGeneration);

    /// <summary>One cohort member <see cref="TryResolve"/> resolves a scope key across — the entering seat's own
    /// principal (for group-membership checks) alongside its currently-seated identity id, if any (for user-scope
    /// checks). Neither field is re-derived by this type: the caller (which already holds the population) reads
    /// both once, live, before calling.</summary>
    /// <param name="Principal">The member's own principal — <see cref="WorldPrincipal.Seat"/> for a local seat.</param>
    /// <param name="IdentityId">The member's currently-seated owned-identity id, or <see langword="null"/> for an
    /// anonymous seat.</param>
    public readonly record struct CohortMember(WorldPrincipal Principal, string? IdentityId);

    /// <summary>Resolves a destination row plus a traveling cohort to a scoped session identity — see this type's own
    /// remarks for the idempotence, lifecycle, and cohort-coherence rules.</summary>
    /// <param name="sourceDefinition">The cohort's own world document — group membership resolves against its
    /// <c>groups</c> section (local seats only; docs/world-model.md names remote/foreign membership proof as an
    /// open gap).</param>
    /// <param name="destination">The destination row being resolved. Its <see cref="WorldDestination.Scope"/>/
    /// <see cref="WorldDestination.Selector"/> pairing is assumed already validated (see
    /// <see cref="WorldDefinitionValidator"/>) — this method still refuses defensively rather than throwing if that
    /// assumption is ever violated.</param>
    /// <param name="referencedDocument">The destination's own resolved referenced document identity — a canonical
    /// local identity the caller resolved (this resolver stays I/O-free by construction: it never touches a
    /// filesystem, so it cannot canonicalize anything itself). Two different spellings of one underlying document
    /// ("dive.world.json" vs "Assets/worlds/dive.world.json") must resolve to the identical string here, or they
    /// mint two cache entries for what the host's own instance-reuse fence (<c>Puck.World.WorldInstanceHost.ResolveByStableName</c>'s
    /// name-collision check, <c>TryFindRunningInstanceByOrigin</c>'s origin scan) already treats as one document —
    /// see this type's own <c>m_active</c> remarks.</param>
    /// <param name="cohort">Every member this one resolution must agree for — a single entry for a <c>body</c>
    /// crossing, the source instance's whole active local-seat set for a <c>party</c> crossing. Never empty.</param>
    /// <param name="resolved">The resolved session identity, on success.</param>
    /// <param name="reason">The refusal reason, naming which rule fired, on failure.</param>
    /// <returns><see langword="true"/> when every cohort member agrees on one scope key and a generation resolved.</returns>
    public bool TryResolve(WorldDefinition sourceDefinition, WorldDestination destination, string referencedDocument, IReadOnlyList<CohortMember> cohort, out Resolved resolved, out string reason) {
        resolved = default;

        if (cohort.Count == 0) {
            reason = "the traveling cohort is empty";

            return false;
        }

        if (!TryResolveScopeKey(sourceDefinition: sourceDefinition, destination: destination, cohort: cohort, scopeKey: out var scopeKey, reason: out reason)) {
            return false;
        }

        var key = (Destination: destination.Name.Value, Durability: destination.Durability, ScopeKey: scopeKey, ReferencedDocument: referencedDocument);

        if (m_active.TryGetValue(key: key, value: out var existing)) {
            resolved = new Resolved(DestinationName: destination.Name.Value, ScopeKey: scopeKey, GenerationId: existing.GenerationId, InstanceName: existing.InstanceName, IsNewGeneration: false);
            reason = string.Empty;

            return true;
        }

        var generationId = m_nextGenerationId++;
        var instanceName = MintInstanceName(destinationName: destination.Name.Value, scopeKey: scopeKey, generationId: generationId, durability: destination.Durability);

        // Defensive, not load-bearing: every component MintInstanceName composed from is ALREADY WorldSafeName-typed
        // or a fixed literal (see that method's own remarks), so this can never actually fire. Refused by name on the
        // impossible case rather than proving it can't happen — the same discipline WorldDestination's own defensive
        // re-check follows for an assumption the validator is supposed to have already enforced.
        if (!WorldSafeName.TryParse(candidate: instanceName, name: out _, reason: out var nameReason)) {
            reason = $"the resolved instance name '{instanceName}' is not a safe name ({nameReason}) — refused rather than cached";

            return false;
        }

        var generation = new Generation(GenerationId: generationId, InstanceName: instanceName);

        InstallGeneration(key: key, generation: generation);

        resolved = new Resolved(DestinationName: destination.Name.Value, ScopeKey: scopeKey, GenerationId: generationId, InstanceName: instanceName, IsNewGeneration: true);
        reason = string.Empty;

        return true;
    }

    // The shared cache-install half TryResolve and TryAdopt both need: installs the (destination, scope key,
    // referenced document) key against `generation.InstanceName` in BOTH directions — m_active for the ordinary
    // forward lookup, and m_byInstanceName's per-instance key SET for NotifyInstanceRetired's own reverse walk. A
    // set, not an overwrite, because ONE running instance can be the adoption target of MORE THAN ONE key (two
    // persisted destinations both resolving to the boot instance's own document, say) — overwriting would silently
    // lose every earlier key's reverse mapping.
    private void InstallGeneration((string Destination, WorldDestinationDurability Durability, string ScopeKey, string ReferencedDocument) key, Generation generation) {
        m_active[key] = generation;

        if (!m_byInstanceName.TryGetValue(key: generation.InstanceName, value: out var keys)) {
            keys = new HashSet<(string Destination, WorldDestinationDurability Durability, string ScopeKey, string ReferencedDocument)>();
            m_byInstanceName[generation.InstanceName] = keys;
        }

        keys.Add(item: key);
    }

    /// <summary>Clears the cache entry an instance name resolved to, if any — called from
    /// <c>Puck.World.WorldInstanceHost.TryStop</c> (which is also
    /// <c>Puck.World.WorldInstanceHost.ReapIfEmpty</c>'s own apply path) so the NEXT scoped resolution against the
    /// same (destination, scope key) mints a genuinely new generation rather than reusing a name nothing answers to
    /// any more. A no-op for a name this resolver never minted (an instance started through
    /// <c>world.instance.start</c>/<c>world.transfer</c>'s raw forms, or the boot instance).</summary>
    /// <param name="instanceName">The instance name that just stopped.</param>
    public void NotifyInstanceRetired(string instanceName) {
        if (m_byInstanceName.Remove(key: instanceName, value: out var keys)) {
            foreach (var key in keys) {
                m_active.Remove(key: key);
            }
        }
    }

    /// <summary>Retires a generation whose resolve was never followed by a running instance — the other end of
    /// <see cref="NotifyInstanceRetired"/>'s lifecycle: that method clears a cache entry when a running instance
    /// stops; this one clears a cache entry when the resolve that minted it never got as far as a running instance
    /// in the first place. <see cref="TryResolve"/> installs a generation's cache entry before its caller ever
    /// attempts to start or join the instance it names (idempotence requires that ordering — a second concurrent
    /// resolver reusing the same generation must see it immediately), so every failure path after a resolve that
    /// never reaches a live instance (an unstartable reference document, a stable-named destination's own
    /// name-collision fence) must call this or <c>world.destinations</c> reports a dead active generation forever
    /// and every later resolution against the same (destination, scope key) returns the same dead name rather than
    /// minting a fresh attempt. Delegates to <see cref="NotifyInstanceRetired"/> — the underlying cache removal is
    /// identical either way; only the caller's own reason for invoking it differs, which is why this exists as its
    /// own named entry rather than asking every failure path to call the stop-shaped method by a name that would
    /// misdescribe what actually happened.</summary>
    /// <remarks>This narrows but does not close every race: a refusal that fires before a destination is ever
    /// resolved this drain (the source instance itself vanished, for one) does not abort here, because this
    /// resolver cannot tell from that vantage point whether another pending transfer in the same drain batch,
    /// sharing the identical minted name, is about to legitimately start or join it — aborting on a guess could
    /// retire a generation a sibling transfer is about to make real, which is a worse divergence than the one being
    /// avoided. A generation is only ever aborted here from the one call site that just made a direct, first-hand
    /// attempt to resolve the name and watched it fail.</remarks>
    /// <param name="instanceName">The instance name a resolve minted, whose transfer then failed to ever start or
    /// reuse it.</param>
    public void AbortGeneration(string instanceName) => NotifyInstanceRetired(instanceName: instanceName);

    /// <summary>Whether (<paramref name="destinationName"/>, <paramref name="scopeKey"/>) already has an active
    /// generation — a pure read: no mint, no cache write, no side effect (the same shape as
    /// <see cref="TryDeriveScopeKey"/>). The "return means home" seam (docs/world-model.md, <c>Puck.World.WorldInstanceHost</c>'s
    /// own return-portal resolution) reads this first, before ever attempting an origin match: an already-active
    /// entry always wins, because the resolver's own cache is the authority on which instance a pair's generation
    /// actually names once one exists — an origin match (<see cref="TryAdopt"/>) only ever gets to apply to a pair's
    /// first resolution.</summary>
    /// <param name="destinationName">The destination row's own name.</param>
    /// <param name="durability">The destination row's own durability — part of the key (see this type's own
    /// <c>m_active</c> remarks on why an ephemeral and a persisted row must never share one cache entry).</param>
    /// <param name="scopeKey">The resolved scope key — see <see cref="TryDeriveScopeKey"/>.</param>
    /// <param name="referencedDocument">The destination's own resolved (canonical) referenced document identity —
    /// the same value the caller would thread into <see cref="TryResolve"/>/<see cref="TryAdopt"/> for this pair.</param>
    /// <param name="resolved">The active generation, when one exists.</param>
    /// <returns><see langword="true"/> when an active generation already answers to this key.</returns>
    public bool TryGetActive(string destinationName, WorldDestinationDurability durability, string scopeKey, string referencedDocument, out Resolved resolved) {
        if (m_active.TryGetValue(key: (Destination: destinationName, Durability: durability, ScopeKey: scopeKey, ReferencedDocument: referencedDocument), value: out var existing)) {
            resolved = new Resolved(DestinationName: destinationName, ScopeKey: scopeKey, GenerationId: existing.GenerationId, InstanceName: existing.InstanceName, IsNewGeneration: false);

            return true;
        }

        resolved = default;

        return false;
    }

    /// <summary>Installs <paramref name="instanceName"/> as the active generation for (<paramref name="destination"/>,
    /// <paramref name="scopeKey"/>) without minting a fresh instance or starting anything — the "return means home"
    /// half of docs/world-model.md's destination/session model: a destination whose resolved document is the same
    /// document a running instance was already started from must resolve to that instance, never mint a second one.
    /// This resolver carries no notion of "running instances" or "boot" at all (it is transport-neutral — see this
    /// type's own remarks); the origin comparison that decides an instance qualifies is entirely
    /// <c>Puck.World.WorldInstanceHost</c>'s own job (it alone holds <c>WorldInstance.SourcePath</c> for every
    /// running instance). What this method owns is only the cache-install half, so an adopted pair joins the same
    /// idempotent bookkeeping <see cref="TryResolve"/> already provides — a later resolution against the identical
    /// pair (through either this method again or ordinary <see cref="TryResolve"/>) reuses the installed generation
    /// rather than re-deciding anything.</summary>
    /// <remarks><b>Precedence (stated explicitly per docs/world-model.md's own "Refuse-by-name anything
    /// ambiguous"):</b> the resolver's own cache entry for a pair always wins once one exists
    /// (<see cref="TryGetActive"/> is the gate a caller checks first) — origin-matching applies only to a pair's
    /// first resolution, and only for a running instance this resolver did not itself mint (an instance this
    /// resolver minted is already the pair's own cache entry, so adopting it again would be a no-op at best and a
    /// self-collision at worst). In practice that narrows to the boot instance and any instance started outside
    /// this resolver entirely (<c>world.instance.start</c>, <c>world.transfer</c>'s raw forms) —
    /// <c>WorldInstanceHost</c> is expected to refuse by name, before ever calling this, when its own origin scan
    /// finds more than one running instance matching the destination's document (an ambiguous adoption target),
    /// rather than picking one arbitrarily.</remarks>
    /// <param name="destination">The destination row being resolved.</param>
    /// <param name="scopeKey">The scope key already derived for this cohort (see <see cref="TryDeriveScopeKey"/>).</param>
    /// <param name="referencedDocument">The destination's own resolved (canonical) referenced document identity —
    /// the same value the caller threads into <see cref="TryResolve"/>/<see cref="TryGetActive"/> for this pair.</param>
    /// <param name="instanceName">The running instance's own name, proven by the caller to share the destination's
    /// resolved document.</param>
    /// <param name="resolved">The installed generation, on success.</param>
    /// <param name="reason">The refusal reason, naming which rule fired, on failure.</param>
    /// <returns><see langword="true"/> when the adoption installed (or found already installed) a generation.</returns>
    public bool TryAdopt(WorldDestination destination, string scopeKey, string referencedDocument, string instanceName, out Resolved resolved, out string reason) {
        var key = (Destination: destination.Name.Value, Durability: destination.Durability, ScopeKey: scopeKey, ReferencedDocument: referencedDocument);

        if (m_active.TryGetValue(key: key, value: out var existing)) {
            // The resolver's own cache always wins — see this method's own precedence remarks. Reports the existing
            // generation rather than refusing outright, so a caller that skipped the TryGetActive pre-check (or races
            // against one) still gets an honest answer instead of a spurious failure.
            resolved = new Resolved(DestinationName: destination.Name.Value, ScopeKey: scopeKey, GenerationId: existing.GenerationId, InstanceName: existing.InstanceName, IsNewGeneration: false);
            reason = string.Empty;

            return true;
        }

        if (!WorldSafeName.TryParse(candidate: instanceName, name: out _, reason: out var nameReason)) {
            resolved = default;
            reason = $"'{instanceName}' is not a safe instance name ({nameReason}) — refused rather than adopted";

            return false;
        }

        // A fresh ordered id, exactly like TryResolve's own mint branch — an adoption is a resolution event like any
        // other (docs/world-model.md "Resolution and transfer are ordered authority events"), so it consumes the
        // same counter rather than reusing generation 0 (which would collide with a genuinely-minted first
        // generation for some other pair).
        var generationId = m_nextGenerationId++;
        var generation = new Generation(GenerationId: generationId, InstanceName: instanceName);

        InstallGeneration(key: key, generation: generation);

        resolved = new Resolved(DestinationName: destination.Name.Value, ScopeKey: scopeKey, GenerationId: generationId, InstanceName: instanceName, IsNewGeneration: false);
        reason = string.Empty;

        return true;
    }

    /// <summary>Re-derives only the scope key a cohort would resolve to against a destination — no cache lookup, no
    /// mint, no side effect. <c>Puck.World.WorldInstanceHost.ApplyTransfer</c>'s own drain-time re-verification
    /// uses this to confirm a frozen resolution's scope key still holds against live membership immediately before
    /// applying it, refusing the whole transfer by name when it no longer matches (membership drift between scan
    /// and drain means the proof <see cref="TryResolve"/> produced at scan time has expired) — never re-resolving
    /// through <see cref="TryResolve"/> itself for this purpose, which would mint a fresh generation as a side
    /// effect of what is meant to be a pure check.</summary>
    /// <param name="sourceDefinition">The cohort's own world document — identical contract to
    /// <see cref="TryResolve"/>'s own parameter.</param>
    /// <param name="destination">The destination row being re-verified.</param>
    /// <param name="cohort">The cohort to re-derive against — every member must agree, exactly like
    /// <see cref="TryResolve"/>. Never empty (an empty cohort refuses by name, like every other rule here).</param>
    /// <param name="scopeKey">The re-derived scope key, on success.</param>
    /// <param name="reason">The refusal reason, naming which rule fired, on failure.</param>
    /// <returns><see langword="true"/> when every cohort member still agrees on one scope key.</returns>
    public bool TryDeriveScopeKey(WorldDefinition sourceDefinition, WorldDestination destination, IReadOnlyList<CohortMember> cohort, out string scopeKey, out string reason) {
        if (cohort.Count == 0) {
            scopeKey = string.Empty;
            reason = "the traveling cohort is empty";

            return false;
        }

        return TryResolveScopeKey(sourceDefinition: sourceDefinition, destination: destination, cohort: cohort, scopeKey: out scopeKey, reason: out reason);
    }

    /// <summary>Every currently active generation resolved for one destination row — <c>world.destinations</c>'s
    /// read-back of resolution state (docs/world-model.md, "a decision nothing can echo can only be asserted").
    /// Ordinal by scope key so the echo is stable across calls.</summary>
    /// <param name="destinationName">The destination row's own name.</param>
    /// <param name="durability">The calling row's own durability — part of the filter, on the same terms as
    /// <paramref name="referencedDocument"/>.</param>
    /// <param name="referencedDocument">The calling document's own row's resolved (canonical) referenced document
    /// identity — filters the echo to generations that answer to this row specifically. Required now that the
    /// cache key carries referenced-document identity (see this type's own <c>m_active</c> remarks): without this
    /// filter, two unrelated documents authoring the identical destination name would each see the other's active
    /// generations in their own <c>world.destinations</c> echo — a decision echoed dishonestly is worse than one
    /// not echoed at all.</param>
    /// <returns>Every active (scope key, generation id, instance name) tuple for that destination row, possibly
    /// empty.</returns>
    public IReadOnlyList<(string ScopeKey, ulong GenerationId, string InstanceName)> DescribeActive(string destinationName, WorldDestinationDurability durability, string referencedDocument) {
        var rows = new List<(string ScopeKey, ulong GenerationId, string InstanceName)>();

        foreach (var pair in m_active) {
            if (string.Equals(a: pair.Key.Destination, b: destinationName, comparisonType: StringComparison.Ordinal) &&
                (pair.Key.Durability == durability) &&
                string.Equals(a: pair.Key.ReferencedDocument, b: referencedDocument, comparisonType: StringComparison.Ordinal)) {
                rows.Add(item: (pair.Key.ScopeKey, pair.Value.GenerationId, pair.Value.InstanceName));
            }
        }

        rows.Sort(comparison: static (a, b) => string.CompareOrdinal(strA: a.ScopeKey, strB: b.ScopeKey));

        return rows;
    }

    // The instance name a generation resolves to. Built by construction to be INJECTIVE: two different (destination,
    // scope key) pairs must never mint the identical instance name, or the reverse index (m_byInstanceName) would
    // silently overwrite one entry with the other and NotifyInstanceRetired would clear only one of the two logical
    // cache entries when the shared instance stopped. Every piece this composes from (destination name, scope kind,
    // scope id) goes through ScopedSegment below, which is LENGTH-PREFIXED and therefore self-delimiting — no
    // separator character to pick, so no separator character a value could also contain, and nothing to escape. Two
    // distinct segment SEQUENCES can never encode to the same string regardless of what any one segment contains.
    //
    // Ephemeral carries the generation ordinal as its own trailing ScopedSegment — appended directly, since the
    // segment's own decimal length prefix keeps it unambiguous against whatever precedes it. Successive generations
    // at one (destination, scope key) are never live at the same time (the cache entry for the old one is gone
    // before a new one mints — see NotifyInstanceRetired), but the ordinal keeps every name this resolver ever
    // minted distinct for the lifetime of the process, which is what makes a stale echo or log line unambiguous.
    // Persisted carries none: a persisted destination's identity is the (destination, scope key) pair itself,
    // durable for as long as the row is authored, so its name never needs to change generations at all.
    //
    // Injectivity holds ACROSS the global and scoped arms too: each arm opens with its own fixed, netstring-wrapped
    // KIND segment ("global" or "scoped") before any caller-supplied piece — two different fixed literals can never
    // agree byte-for-byte, so the two arms' whole segment sequences can never collide regardless of what a later
    // segment (including a raw destinationName) contains. Every piece of the output is a netstring segment from its
    // first byte; nothing raw or unwrapped appears anywhere in either arm.
    private static string MintInstanceName(string destinationName, string scopeKey, ulong generationId, WorldDestinationDurability durability) {
        string scoped;

        if (string.Equals(a: scopeKey, b: GlobalScopeKey, comparisonType: StringComparison.Ordinal)) {
            scoped = $"{ScopedSegment(value: "global")}{ScopedSegment(value: destinationName)}";
        } else {
            // scopeKey is always exactly "user:<id>" or "group:<id>" here (TryResolveScopeKey's other two cases) —
            // decomposed rather than embedded whole because its own ':' is a character no instance name may ever
            // carry (WorldSafeName's reserved set); the id half is WorldSafeName-sourced, so it is always
            // colon-free, which is what makes finding the one separating colon unambiguous.
            var colon = scopeKey.IndexOf(value: ':');
            var scopeKind = ((colon < 0) ? scopeKey : scopeKey[..colon]);
            var scopeId = ((colon < 0) ? string.Empty : scopeKey[(colon + 1)..]);

            scoped = $"{ScopedSegment(value: "scoped")}{ScopedSegment(value: destinationName)}{ScopedSegment(value: scopeKind)}{ScopedSegment(value: scopeId)}";
        }

        return ((durability == WorldDestinationDurability.Persisted) ? scoped : $"{scoped}{ScopedSegment(value: generationId.ToString())}");
    }

    // One length-prefixed, self-delimiting segment for MintInstanceName's scoped branch: its own decimal length, a
    // single '~' (never ambiguous with the length digits themselves — a digit and '~' can never be confused), then
    // exactly that many characters verbatim. No character this composes from is ever escaped or folded, which is
    // what makes the WHOLE composed name injective in the segment SEQUENCE regardless of what any one segment
    // contains — the classic netstring argument. Every value this wraps (a WorldSafeName's own Value, the fixed
    // "user"/"group" literal, or decimal generation digits) is already free of every character WorldSafeName's own
    // reserved set forbids, so wrapping never introduces one either.
    private static string ScopedSegment(string value) => $"{value.Length}~{value}";

    private static bool TryResolveScopeKey(WorldDefinition sourceDefinition, WorldDestination destination, IReadOnlyList<CohortMember> cohort, out string scopeKey, out string reason) {
        switch (destination.Scope) {
            case WorldDestinationScope.User:
                return TryResolveUserScopeKey(cohort: cohort, scopeKey: out scopeKey, reason: out reason);

            case WorldDestinationScope.Group:
                return TryResolveGroupScopeKey(sourceDefinition: sourceDefinition, destination: destination, cohort: cohort, scopeKey: out scopeKey, reason: out reason);

            default:
                scopeKey = GlobalScopeKey;
                reason = string.Empty;

                return true;
        }
    }

    // User scope: the entering seat's OWN owned-identity world IS the identity (docs/world-model.md "User resolves
    // locally to the entering seat's owned-identity world"). An anonymous member refuses by name rather than minting
    // one; a cohort spanning more than one identity refuses as a named scope mismatch rather than picking either.
    private static bool TryResolveUserScopeKey(IReadOnlyList<CohortMember> cohort, out string scopeKey, out string reason) {
        scopeKey = string.Empty;

        string? identityId = null;

        foreach (var member in cohort) {
            if (member.IdentityId is not { Length: > 0 } id) {
                reason = $"{member.Principal.Describe()} has no identity — an anonymous seat cannot resolve a user-scoped destination";

                return false;
            }

            if (identityId is null) {
                identityId = id;
            } else if (!string.Equals(a: identityId, b: id, comparisonType: StringComparison.Ordinal)) {
                reason = $"the cohort resolves more than one identity ('{identityId}' and '{id}') for one user-scoped destination — a multi-user party into a user-scoped destination is refused rather than picking one";

                return false;
            }
        }

        scopeKey = $"user:{identityId}";
        reason = string.Empty;

        return true;
    }

    // Group scope: `named` binds every member to ONE authored group id; `tagged` resolves each member's own UNIQUE
    // membership carrying the tag, then requires the whole cohort to land on the SAME group id (docs/world-model.md
    // "Group / tagged"). Membership itself reads the world's own `groups` section — LOCAL seats only, per this
    // resolver's own remarks; a remote/foreign membership claim is the federated campaign's job.
    private static bool TryResolveGroupScopeKey(WorldDefinition sourceDefinition, WorldDestination destination, IReadOnlyList<CohortMember> cohort, out string scopeKey, out string reason) {
        scopeKey = string.Empty;

        if (destination.Selector is not { } selector) {
            reason = $"destination '{destination.Name}' declares scope 'group' with no selector — refused at validation, but re-checked here defensively";

            return false;
        }

        var groups = (sourceDefinition.Groups?.Groups ?? []);

        switch (selector) {
            case WorldGroupSelector.Named named: {
                foreach (var member in cohort) {
                    if (!GroupContains(groups: groups, groupId: named.Group, principal: member.Principal)) {
                        reason = $"{member.Principal.Describe()} is not a member of group '{named.Group}'";

                        return false;
                    }
                }

                scopeKey = $"group:{named.Group}";
                reason = string.Empty;

                return true;
            }

            case WorldGroupSelector.Tagged tagged: {
                string? resolvedGroupId = null;

                foreach (var member in cohort) {
                    var candidates = FindTaggedMemberships(groups: groups, tag: tagged.Tag, principal: member.Principal);

                    if (candidates.Count == 0) {
                        reason = $"{member.Principal.Describe()} holds no membership tagged '{tagged.Tag}'";

                        return false;
                    }

                    if (candidates.Count > 1) {
                        reason = $"{member.Principal.Describe()} holds {candidates.Count} memberships tagged '{tagged.Tag}' ({string.Join(separator: ", ", values: candidates)}) — ambiguous";

                        return false;
                    }

                    var candidateGroupId = candidates[0];

                    if (resolvedGroupId is null) {
                        resolvedGroupId = candidateGroupId;
                    } else if (!string.Equals(a: resolvedGroupId, b: candidateGroupId, comparisonType: StringComparison.Ordinal)) {
                        reason = $"the cohort's tag '{tagged.Tag}' resolves more than one group ('{resolvedGroupId}' and '{candidateGroupId}') for one destination";

                        return false;
                    }
                }

                scopeKey = $"group:{resolvedGroupId}";
                reason = string.Empty;

                return true;
            }

            default:
                reason = $"destination '{destination.Name}' carries an unrecognized selector kind";

                return false;
        }
    }

    private static bool GroupContains(IReadOnlyList<WorldGroup> groups, string groupId, WorldPrincipal principal) {
        foreach (var group in groups) {
            if ((group is not null) && string.Equals(a: group.Id, b: groupId, comparisonType: StringComparison.Ordinal)) {
                return group.Members.Contains(value: principal);
            }
        }

        return false;
    }

    private static List<string> FindTaggedMemberships(IReadOnlyList<WorldGroup> groups, string tag, WorldPrincipal principal) {
        var matches = new List<string>();

        foreach (var group in groups) {
            if ((group is null) || (group.Tags is not { Count: > 0 } tags)) {
                continue;
            }

            var carriesTag = false;

            foreach (var candidate in tags) {
                if (string.Equals(a: candidate, b: tag, comparisonType: StringComparison.Ordinal)) {
                    carriesTag = true;

                    break;
                }
            }

            if (carriesTag && group.Members.Contains(value: principal)) {
                matches.Add(item: group.Id);
            }
        }

        return matches;
    }
}
