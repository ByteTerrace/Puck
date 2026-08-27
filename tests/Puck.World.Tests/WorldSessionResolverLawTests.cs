using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// In-process laws for <see cref="WorldSessionResolver"/> — the idempotence
/// and generation-lifecycle rules the resolver's own remarks describe, plus the scope-refusal/cohort-coherence
/// pairing every denial in this suite is proven with a control. The resolver carries no dependency on
/// <c>Puck.World</c> (the composition root, out of scope for this project — see <c>Fixtures</c>'s own remarks), so
/// every law here calls <see cref="WorldSessionResolver.TryResolve"/> directly against a document built in code,
/// never through a running instance.
/// </summary>
public sealed class WorldSessionResolverLawTests {
    private const string DestinationReference = "ref";
    // The referenced-document identity every law in this suite that does NOT itself exercise the identity threads
    // through unchanged (adversarial-review finding 3: the cache key now carries a THIRD component,
    // referencedDocument — see WorldSessionResolver's own m_active remarks). Holding it constant across a law
    // preserves that law's original semantics exactly (reuse, idempotence, refusal) — only the finding-3 laws below
    // vary it deliberately.
    private const string RefDoc = "worlds/fixture.world.json";

    // A minimal groups section: one kind, and rows shaped for each scope-refusal law below. Built once per test via
    // this factory (never shared mutable state) so laws run independently.
    private static WorldDefinition BuildDocumentWithGroups() {
        var kind = new WorldGroupKind(
            Name: "party",
            Roles: [],
            OwnershipPolicy: WorldGroupOwnershipPolicy.None,
            Lifetime: WorldGroupLifetime.Persistent,
            EvictionPolicy: WorldGroupEvictionPolicy.Remove,
            Capacity: 8
        );
        var groups = new WorldGroup[] {
            // "alpha" — the `named` law's member (Seat 1) and non-member (Seat 2) control.
            new(Id: WorldSafeName.Parse(candidate: "alpha"), KindName: "party", Members: [WorldPrincipal.Seat(slot: 1)]),
            // "gamma" — the `tagged` law's UNIQUE match: Seat 3 holds exactly one membership tagged "raiders".
            new(Id: WorldSafeName.Parse(candidate: "gamma"), KindName: "party", Members: [WorldPrincipal.Seat(slot: 3)], Tags: ["raiders"]),
            // "delta"/"epsilon" — the `tagged` law's AMBIGUOUS case: Seat 4 holds two memberships both tagged
            // "explorers", so neither should be picked silently.
            new(Id: WorldSafeName.Parse(candidate: "delta"), KindName: "party", Members: [WorldPrincipal.Seat(slot: 4)], Tags: ["explorers"]),
            new(Id: WorldSafeName.Parse(candidate: "epsilon"), KindName: "party", Members: [WorldPrincipal.Seat(slot: 4)], Tags: ["explorers"]),
        };

        return Fixtures.BuildDocument() with { Groups = new WorldGroupsSection(Groups: groups, Kinds: [kind], Ownership: []) };
    }
    private static WorldDestination GlobalDestination(string name = "camp") =>
        new(Name: WorldSafeName.Parse(candidate: name), Reference: DestinationReference, Durability: WorldDestinationDurability.Ephemeral);
    private static WorldDestination PersistedGlobalDestination(string name = "camp") =>
        new(Name: WorldSafeName.Parse(candidate: name), Reference: DestinationReference, Durability: WorldDestinationDurability.Persisted);
    private static WorldDestination UserDestination(string name = "workshop") =>
        new(Name: WorldSafeName.Parse(candidate: name), Reference: DestinationReference, Durability: WorldDestinationDurability.Ephemeral, Scope: WorldDestinationScope.User);
    private static WorldDestination NamedGroupDestination(string groupId, string name = "hall", WorldDestinationDurability durability = WorldDestinationDurability.Ephemeral) =>
        new(Name: WorldSafeName.Parse(candidate: name), Reference: DestinationReference, Durability: durability, Scope: WorldDestinationScope.Group, Selector: new WorldGroupSelector.Named(Group: groupId));
    private static WorldDestination TaggedGroupDestination(string tag, string name = "lodge") =>
        new(Name: WorldSafeName.Parse(candidate: name), Reference: DestinationReference, Durability: WorldDestinationDurability.Ephemeral, Scope: WorldDestinationScope.Group, Selector: new WorldGroupSelector.Tagged(Tag: tag));
    private static WorldSessionResolver.CohortMember[] Cohort(params (int Slot, string? IdentityId)[] members) {
        var result = new WorldSessionResolver.CohortMember[members.Length];

        for (var index = 0; (index < members.Length); index++) {
            result[index] = new WorldSessionResolver.CohortMember(Principal: WorldPrincipal.Seat(slot: members[index].Slot), IdentityId: members[index].IdentityId);
        }

        return result;
    }

    [Fact]
    public void IdempotentResolution_SameCohortTwice_ReturnsSameGenerationAndInstance() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = GlobalDestination();
        var cohort = Cohort((1, null));

        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: destination, reason: out _, referencedDocument: RefDoc, resolved: out var first, sourceDefinition: definition));
        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: destination, reason: out _, referencedDocument: RefDoc, resolved: out var second, sourceDefinition: definition));

        Assert.True(condition: first.IsNewGeneration, userMessage: "the FIRST resolution of a destination must mint a new generation");
        Assert.False(condition: second.IsNewGeneration, userMessage: "the SECOND resolution against the same still-active generation must reuse it, never mint again");
        Assert.Equal(expected: first.GenerationId, actual: second.GenerationId);
        Assert.Equal(expected: first.InstanceName, actual: second.InstanceName);
    }
    [Fact]
    public void GenerationLifecycle_AfterRetirement_NextResolveMintsANewGeneration() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = GlobalDestination();
        var cohort = Cohort((1, null));

        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: destination, reason: out _, referencedDocument: RefDoc, resolved: out var first, sourceDefinition: definition));

        // The instance the first resolution named just went away (WorldInstanceHost.TryStop/ReapIfEmpty's apply
        // path, mirrored here by calling the notification directly) — the resolver's cache entry for this
        // (destination, scope key) must clear, so the NEXT resolution is a genuinely new generation, never a stale
        // reuse of a name nothing answers to any more.
        resolver.NotifyInstanceRetired(instanceName: first.InstanceName);

        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: destination, reason: out _, referencedDocument: RefDoc, resolved: out var second, sourceDefinition: definition));

        Assert.True(condition: second.IsNewGeneration, userMessage: "resolving again after retirement must mint a NEW generation, not reuse the retired one");
        Assert.NotEqual(expected: first.GenerationId, actual: second.GenerationId);
        Assert.NotEqual(expected: first.InstanceName, actual: second.InstanceName);
    }
    [Fact]
    public void UserScope_AnonymousSeatRefused_IdentifiedSeatResolves() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = UserDestination();

        Laws.RefusalWithControl(
            lawId: "resolver.user-scope-anonymous-refused",
            deniedOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out _, reason: out _),
            controlOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, "amber-identity")), resolved: out _, reason: out _));
    }
    [Fact]
    public void UserScope_MultiUserCohortRefused_SingleIdentityResolves() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = UserDestination(name: "workshop2");

        Laws.RefusalWithControl(
            lawId: "resolver.user-scope-cohort-mismatch-refused",
            deniedOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, "amber-identity"), (2, "ember-identity")), resolved: out _, reason: out _),
            controlOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, "amber-identity"), (3, "amber-identity")), resolved: out _, reason: out _));
    }
    [Fact]
    public void GroupNamedScope_NonMemberRefused_MemberResolves() {
        var resolver = new WorldSessionResolver();
        var definition = BuildDocumentWithGroups();
        var destination = NamedGroupDestination(groupId: "alpha");

        Laws.RefusalWithControl(
            lawId: "resolver.group-named-non-member-refused",
            // Seat 2 holds no membership in "alpha" at all.
            deniedOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((2, null)), resolved: out _, reason: out _),
            // Seat 1 is "alpha"'s one authored member.
            controlOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out _, reason: out _));
    }
    [Fact]
    public void GroupTaggedScope_AmbiguousMembershipRefused_UniqueMembershipResolves() {
        var resolver = new WorldSessionResolver();
        var definition = BuildDocumentWithGroups();

        Laws.RefusalWithControl(
            lawId: "resolver.group-tagged-ambiguous-refused",
            // Seat 4 holds TWO memberships ("delta"/"epsilon") both tagged "explorers" — ambiguous, refused by name.
            deniedOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: TaggedGroupDestination(name: "lodge-ambiguous", tag: "explorers"), referencedDocument: RefDoc, cohort: Cohort((4, null)), resolved: out _, reason: out _),
            // Seat 3 holds exactly ONE membership ("gamma") tagged "raiders" — unique, resolves.
            controlOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: TaggedGroupDestination(name: "lodge-unique", tag: "raiders"), referencedDocument: RefDoc, cohort: Cohort((3, null)), resolved: out _, reason: out _));
    }
    [Fact]
    public void GroupTaggedScope_NoMatchingMembershipRefused_MatchingMembershipResolves() {
        var resolver = new WorldSessionResolver();
        var definition = BuildDocumentWithGroups();
        var destination = TaggedGroupDestination(name: "lodge-zero-or-one", tag: "raiders");

        Laws.RefusalWithControl(
            lawId: "resolver.group-tagged-zero-refused",
            // Seat 1 holds no membership tagged "raiders" at all (only "alpha", untagged).
            deniedOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out _, reason: out _),
            // Seat 3 holds exactly one membership tagged "raiders".
            controlOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((3, null)), resolved: out _, reason: out _));
    }
    [Fact]
    public void GroupTaggedScope_CohortResolvingDifferentGroupsRefused_SameGroupResolves() {
        var resolver = new WorldSessionResolver();
        // Two independent groups sharing one tag, each with its own single member, so a cohort naming both members
        // resolves to two DIFFERENT group ids under the same tag — the cross-member disagreement this law targets.
        var kind = new WorldGroupKind(Name: "party", Roles: [], OwnershipPolicy: WorldGroupOwnershipPolicy.None, Lifetime: WorldGroupLifetime.Persistent, EvictionPolicy: WorldGroupEvictionPolicy.Remove, Capacity: 8);
        var groups = new WorldGroup[] {
            new(Id: WorldSafeName.Parse(candidate: "north"), KindName: "party", Members: [WorldPrincipal.Seat(slot: 1)], Tags: ["shared"]),
            new(Id: WorldSafeName.Parse(candidate: "south"), KindName: "party", Members: [WorldPrincipal.Seat(slot: 2)], Tags: ["shared"]),
        };
        var definition = Fixtures.BuildDocument() with { Groups = new WorldGroupsSection(Groups: groups, Kinds: [kind], Ownership: []) };
        var destination = TaggedGroupDestination(name: "lodge-cohort", tag: "shared");

        Laws.RefusalWithControl(
            lawId: "resolver.group-tagged-cohort-disagreement-refused",
            // Seat 1 resolves "north", Seat 2 resolves "south" under the same tag — disagreement, refused.
            deniedOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, null), (2, null)), resolved: out _, reason: out _),
            // The SAME two seats, but only Seat 1 travels (a `body` crossing) — one member, no disagreement possible.
            controlOutcome: () => resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out _, reason: out _));
    }
    // G1 — SCOPED INSTANCE NAMES ARE INJECTIVE BY CONSTRUCTION. Before the fix, MintInstanceName joined
    // destinationName + '~' + a hand-sanitized scope key: the sanitizer only folded WorldSafeName's OWN reserved
    // characters (quote/angle-brackets/pipe/colon/asterisk/question-mark/both-slashes), never '~' itself, so a
    // destination name and a group id that DIFFER only in where a '~' falls could still mint the IDENTICAL instance
    // name. Concretely, under the old scheme: destination "d~group_a" + group "b" and destination "d" + group
    // "a~group_b" both minted "d~group_a~group_b" — the second destination's raw group id "a~group_b" was legal
    // under the OLD plain-`string` WorldGroup.Id (and is legal under the NEW WorldSafeName-typed one too, since '~'
    // is not a reserved character either way; the two destinations that collide are chosen from OPPOSITE ends of the
    // '~' rather than from a character WorldSafeName has ever forbidden). This law proves the two now resolve to
    // DISTINCT instance names and that retiring one never touches the other's cache entry — see
    // WorldSessionResolver.MintInstanceName's own remarks for the length-prefixed ("netstring") construction that
    // makes this a proof rather than a hope.
    [Fact]
    public void MintInstanceName_NamesThatWouldHaveCollidedUnderTheOldSanitizer_ResolveToDistinctInstancesWithIndependentCaches() {
        var resolver = new WorldSessionResolver();
        var kind = new WorldGroupKind(Name: "party", Roles: [], OwnershipPolicy: WorldGroupOwnershipPolicy.None, Lifetime: WorldGroupLifetime.Persistent, EvictionPolicy: WorldGroupEvictionPolicy.Remove, Capacity: 8);
        var groups = new WorldGroup[] {
            new(Id: WorldSafeName.Parse(candidate: "b"), KindName: "party", Members: [WorldPrincipal.Seat(slot: 1)]),
            new(Id: WorldSafeName.Parse(candidate: "a~group_b"), KindName: "party", Members: [WorldPrincipal.Seat(slot: 2)]),
        };
        var definition = Fixtures.BuildDocument() with { Groups = new WorldGroupsSection(Groups: groups, Kinds: [kind], Ownership: []) };
        // PERSISTED — no generation-ordinal suffix, so nothing but the composition itself could keep these apart
        // (an Ephemeral pair would coincidentally disambiguate via the resolver's own global generation counter,
        // which defeats the point of this law).
        var destinationOne = NamedGroupDestination(durability: WorldDestinationDurability.Persisted, groupId: "b", name: "d~group_a");
        var destinationTwo = NamedGroupDestination(durability: WorldDestinationDurability.Persisted, groupId: "a~group_b", name: "d");

        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destinationOne, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out var first, reason: out var firstReason), userMessage: firstReason);
        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destinationTwo, referencedDocument: RefDoc, cohort: Cohort((2, null)), resolved: out var second, reason: out var secondReason), userMessage: secondReason);

        Assert.NotEqual(expected: first.InstanceName, actual: second.InstanceName);

        // The reverse index proof: retiring the FIRST instance must clear ONLY its own cache entry — the historical
        // bug was the two entries sharing one instance name, so retiring one silently dropped BOTH. Re-resolving the
        // SECOND destination's SAME cohort afterward must still reuse its own untouched generation.
        resolver.NotifyInstanceRetired(instanceName: first.InstanceName);

        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destinationTwo, referencedDocument: RefDoc, cohort: Cohort((2, null)), resolved: out var secondAgain, reason: out var secondAgainReason), userMessage: secondAgainReason);
        Assert.False(condition: secondAgain.IsNewGeneration, userMessage: "retiring destinationOne's instance must never have touched destinationTwo's independent cache entry");
        Assert.Equal(expected: second.GenerationId, actual: secondAgain.GenerationId);

        // And the FIRST destination's own (destination, scope key) pair is genuinely gone — a fresh resolve for it
        // mints a NEW generation rather than finding a cache entry the collision would have left behind.
        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destinationOne, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out var firstAgain, reason: out var firstAgainReason), userMessage: firstAgainReason);
        Assert.True(condition: firstAgain.IsNewGeneration);
        Assert.NotEqual(expected: first.GenerationId, actual: firstAgain.GenerationId);
    }
    // P0 — CROSS-ARM NAME COLLISION (adversarial review, post-G1). G1 above made the SCOPED branch injective within
    // itself, but the GLOBAL branch used to emit `destinationName` RAW, unwrapped, into the SAME name space the
    // scoped branch's netstring OUTPUT lives in. Destination 'a' + scope key 'user:b' netstring-encodes to
    // "1~a4~user1~b" (ScopedSegment("a") + ScopedSegment("user") + ScopedSegment("b")) — a GLOBAL destination
    // literally spelled that way used to mint the IDENTICAL instance name as the unrelated scoped pair, silently
    // overwriting the reverse index (WorldSessionResolver.MintInstanceName's own remarks). This law is RED against
    // the pre-fix scheme and proves the crafted pair now resolves to DISTINCT instances with independent caches,
    // mirroring G1's own proof shape one level up (both arms now open with their own netstring-wrapped KIND
    // segment).
    [Fact]
    public void MintInstanceName_CraftedGlobalNameMatchingScopedEncoding_ResolvesToDistinctInstanceFromTheScopedPairItWouldHaveCollidedWith() {
        var resolver = new WorldSessionResolver();
        const string collidingGlobalName = "1~a4~user1~b";
        // PERSISTED both — no generation-ordinal suffix, so nothing but the KIND-segment fix itself could keep these
        // apart (an Ephemeral pair would coincidentally disambiguate via the resolver's own generation counter).
        var globalDestination = new WorldDestination(Name: WorldSafeName.Parse(candidate: collidingGlobalName), Reference: DestinationReference, Durability: WorldDestinationDurability.Persisted);
        var scopedDestination = new WorldDestination(Name: WorldSafeName.Parse(candidate: "a"), Reference: DestinationReference, Durability: WorldDestinationDurability.Persisted, Scope: WorldDestinationScope.User);
        var definition = Fixtures.BuildDocument();

        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: globalDestination, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out var global, reason: out var globalReason), userMessage: globalReason);
        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: scopedDestination, referencedDocument: RefDoc, cohort: Cohort((2, "b")), resolved: out var scoped, reason: out var scopedReason), userMessage: scopedReason);

        Assert.NotEqual(expected: global.InstanceName, actual: scoped.InstanceName);

        // The reverse-index proof, same shape as G1: retiring one must never touch the other's independent cache
        // entry — the historical bug was the two entries sharing one instance name, so retiring one silently
        // dropped both.
        resolver.NotifyInstanceRetired(instanceName: global.InstanceName);

        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: scopedDestination, referencedDocument: RefDoc, cohort: Cohort((2, "b")), resolved: out var scopedAgain, reason: out var scopedAgainReason), userMessage: scopedAgainReason);
        Assert.False(condition: scopedAgain.IsNewGeneration, userMessage: "retiring the crafted global instance must never have touched the scoped pair's independent cache entry");
        Assert.Equal(expected: scoped.GenerationId, actual: scopedAgain.GenerationId);
    }
    // G2 — COHORT TOCTOU: TryDeriveScopeKey is the primitive Puck.World.WorldInstanceHost.ApplyTransfer uses at
    // drain time to re-verify a FROZEN resolution's scope key against LIVE membership before applying it (this
    // project never references Puck.World — see this file's own class remarks — so the resolver-level primitive is
    // what a law here can exercise directly; the end-to-end drain-time refusal is proven by running the app, per the
    // task's VERIFY section). A membership row mutated between scan and drain (here: the group's own roster changes)
    // must make a re-derivation DISAGREE with the frozen scope key, while an UNCHANGED cohort re-derives the
    // IDENTICAL scope key every time — the refusal/control pairing this suite's other laws already follow.
    [Fact]
    public void TryDeriveScopeKey_MembershipDriftedSinceFirstDerivation_DisagreesWithTheFrozenKey_UnchangedCohortAgrees() {
        var resolver = new WorldSessionResolver();
        var definition = BuildDocumentWithGroups();
        var destination = NamedGroupDestination(groupId: "alpha");

        // Frozen at "scan time": Seat 1, "alpha"'s one authored member.
        Assert.True(condition: resolver.TryDeriveScopeKey(sourceDefinition: definition, destination: destination, cohort: Cohort((1, null)), scopeKey: out var frozenScopeKey, reason: out var frozenReason), userMessage: frozenReason);

        // CONTROL — re-deriving the SAME still-valid cohort at "drain time" agrees with the frozen key.
        Assert.True(condition: resolver.TryDeriveScopeKey(sourceDefinition: definition, destination: destination, cohort: Cohort((1, null)), scopeKey: out var unchangedScopeKey, reason: out var unchangedReason), userMessage: unchangedReason);
        Assert.Equal(actual: unchangedScopeKey, expected: frozenScopeKey);

        // DRIFT — re-deriving against a cohort whose membership no longer holds (Seat 2 was never "alpha"'s member)
        // either refuses outright or (if it resolves against some OTHER group) disagrees with the frozen key; either
        // way the frozen proof no longer holds, which is the whole point of the drain-time re-check.
        var driftedResolved = resolver.TryDeriveScopeKey(sourceDefinition: definition, destination: destination, cohort: Cohort((2, null)), scopeKey: out var driftedScopeKey, reason: out _);

        Assert.False(condition: (driftedResolved && string.Equals(a: driftedScopeKey, b: frozenScopeKey, comparisonType: StringComparison.Ordinal)), userMessage: "a cohort that no longer proves the frozen destination's membership must not silently re-agree with the frozen scope key");
    }
    // G3 — GENERATIONS ACTIVATE BEFORE THEIR INSTANCE EXISTS: AbortGeneration is the primitive
    // Puck.World.WorldInstanceHost.ApplyTransfer calls on every drain-time failure after a resolve that never
    // reaches a running instance (see that method's own remarks) — retiring a generation exactly like
    // NotifyInstanceRetired does for a RUNNING instance that stopped, so a resolve that never started one does not
    // leave world.destinations reporting a dead generation forever. Refusal/control: aborting clears the cache entry
    // (the next resolve mints fresh), while NOT aborting leaves the SAME generation cached (the next resolve reuses
    // it) — proving AbortGeneration's own effect rather than assuming it.
    [Fact]
    public void AbortGeneration_AfterAFailedStart_NextResolveMintsFresh_WithoutAbortItReuses() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var cohort = Cohort((1, null));

        // Aborted case: the resolve that "never reached a running instance" (TriggerPortal's own drain-time
        // TryResolveDestination failure, simulated here by calling AbortGeneration directly against the minted
        // name, exactly as ApplyTransfer's own failure path does).
        var abortedDestination = GlobalDestination(name: "camp-aborted");

        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: abortedDestination, reason: out var abortedReason, referencedDocument: RefDoc, resolved: out var aborted, sourceDefinition: definition), userMessage: abortedReason);

        resolver.AbortGeneration(instanceName: aborted.InstanceName);

        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: abortedDestination, reason: out var afterAbortReason, referencedDocument: RefDoc, resolved: out var afterAbort, sourceDefinition: definition), userMessage: afterAbortReason);
        Assert.True(condition: afterAbort.IsNewGeneration, userMessage: "aborting a generation whose instance never started must let the next resolve mint a genuinely fresh one");
        Assert.NotEqual(expected: aborted.GenerationId, actual: afterAbort.GenerationId);

        // Control: the identical shape, but WITHOUT the abort call — the second resolve must reuse the SAME
        // generation, proving the aborted case's difference is AbortGeneration's own effect and not some other
        // difference between the two resolves.
        var reusedDestination = GlobalDestination(name: "camp-reused");

        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: reusedDestination, reason: out var reusedReason, referencedDocument: RefDoc, resolved: out var reused, sourceDefinition: definition), userMessage: reusedReason);
        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: reusedDestination, reason: out var stillReusedReason, referencedDocument: RefDoc, resolved: out var stillReused, sourceDefinition: definition), userMessage: stillReusedReason);
        Assert.False(condition: stillReused.IsNewGeneration);
        Assert.Equal(expected: reused.GenerationId, actual: stillReused.GenerationId);
    }
    // RETURN MEANS HOME (docs/vision.md): TryAdopt/TryGetActive are the cache-install half of the seam
    // Puck.World.WorldInstanceHost's own origin scan drives — this resolver carries no notion of "running instances"
    // at all (see this file's own class remarks), so what a law here can prove is exactly TryAdopt's OWN documented
    // contract: a pair with no active generation adopts the named instance, and ordinary TryResolve afterward reuses
    // it rather than minting a second one — the "never mint a second one" half of the invariant, proven at the layer
    // this project can reach.
    [Fact]
    public void TryAdopt_FirstResolution_InstallsNamedInstance_OrdinaryResolveThenReusesIt() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = GlobalDestination(name: "home");

        Assert.False(condition: resolver.TryGetActive(destinationName: destination.Name.Value, durability: WorldDestinationDurability.Ephemeral, scopeKey: WorldSessionResolver.GlobalScopeKey, referencedDocument: RefDoc, resolved: out _), userMessage: "a pair nothing has resolved yet must report no active generation");

        Assert.True(condition: resolver.TryAdopt(destination: destination, instanceName: "boot", reason: out var adoptReason, referencedDocument: RefDoc, resolved: out var adopted, scopeKey: WorldSessionResolver.GlobalScopeKey), userMessage: adoptReason);
        Assert.Equal(expected: "boot", actual: adopted.InstanceName);
        Assert.False(condition: adopted.IsNewGeneration, userMessage: "adopting a RUNNING instance is never a fresh mint");

        // TryGetActive now reports it, and an ORDINARY TryResolve call (the shape every other crossing takes) reuses
        // the adopted instance rather than minting a second one — the whole point of the seam.
        Assert.True(condition: resolver.TryGetActive(destinationName: destination.Name.Value, durability: WorldDestinationDurability.Ephemeral, scopeKey: WorldSessionResolver.GlobalScopeKey, referencedDocument: RefDoc, resolved: out var active));
        Assert.Equal(expected: "boot", actual: active.InstanceName);

        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out var resolved, reason: out var resolveReason), userMessage: resolveReason);
        Assert.Equal(expected: "boot", actual: resolved.InstanceName);
        Assert.False(condition: resolved.IsNewGeneration);
        Assert.Equal(expected: adopted.GenerationId, actual: resolved.GenerationId);
    }
    // The precedence rule stated in TryAdopt's own remarks: the resolver's cache ALWAYS wins once a generation is
    // active — an origin match discovered AFTER a genuine mint must never silently displace it. Refusal/control
    // shape adapted: "refusal" here is TryAdopt reporting the EXISTING (already-minted) generation rather than
    // installing the caller's differently-named instance; the control is the ordinary first-resolution adopt path
    // (the law just above) actually installing what it was given.
    [Fact]
    public void TryAdopt_WhenAGenerationIsAlreadyActive_ReportsTheExistingGeneration_NeverOverwritesWithTheAdoptedName() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = GlobalDestination(name: "home-already-active");

        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out var minted, reason: out var mintedReason), userMessage: mintedReason);
        Assert.True(condition: minted.IsNewGeneration);
        Assert.NotEqual(expected: "boot", actual: minted.InstanceName);

        // An origin scan that (hypothetically) later found "boot" sharing this destination's document must NOT
        // overwrite the generation a genuine mint already installed — the resolver's own cache wins.
        Assert.True(condition: resolver.TryAdopt(destination: destination, instanceName: "boot", reason: out var adoptReason, referencedDocument: RefDoc, resolved: out var adoptAttempt, scopeKey: WorldSessionResolver.GlobalScopeKey), userMessage: adoptReason);
        Assert.Equal(expected: minted.InstanceName, actual: adoptAttempt.InstanceName);
        Assert.Equal(expected: minted.GenerationId, actual: adoptAttempt.GenerationId);
        Assert.NotEqual(expected: "boot", actual: adoptAttempt.InstanceName);

        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: RefDoc, cohort: Cohort((1, null)), resolved: out var stillMinted, reason: out var stillMintedReason), userMessage: stillMintedReason);
        Assert.Equal(expected: minted.InstanceName, actual: stillMinted.InstanceName);
    }
    // FINDING 3 — RESOLVER IDENTITY KEYED TOO NARROWLY (adversarial review). Before this fix the cache key was bare
    // (destination name, scope key): two UNRELATED documents authoring an identically-spelled destination row (both
    // naming a 'home' global row, say) collided in this ONE process-wide resolver, and cache-first precedence (see
    // TryGetActive's own remarks) meant whichever document resolved first silently claimed the name for good — a
    // SECOND document's own row could never mint its own generation; it would keep reusing the FIRST document's
    // instance forever. Live confirmation (independent verification, 2026-08-09): booting a MODIFIED COPY of
    // nexus.world.json from a different path, with a dungeon whose 'home' destination references the SHIPPED
    // nexus.world.json, the return crossing correctly minted a NEW instance rather than adopting boot — proving the
    // fix must hold BOTH directions: identical resolved document -> adopt/reuse (the shipped boot case, verified
    // landing in instance=boot); different resolved document under the SAME destination name and scope -> mint
    // fresh, never adopt (the modified-copy case just observed live). This law proves the fresh-mint direction at
    // the TryResolve layer; the two laws below prove the origin-adoption gate's own same-document/different-document
    // split and the reverse index's N:1 capability.
    [Fact]
    public void TryResolve_TwoDocumentsAuthorIdenticalDestinationName_DifferentReferencedDocuments_ResolveToDistinctGenerationsWithIndependentCaches() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        // Two documents' own 'home' rows, spelled IDENTICALLY (same name, same Global scope) — the exact shape the
        // old bare (name, scope) key could not tell apart.
        var destinationFromDocumentX = GlobalDestination(name: "home");
        var destinationFromDocumentY = GlobalDestination(name: "home");
        const string documentX = "worlds/nexus.world.json";
        const string documentY = "worlds/play-modified.world.json";
        var cohort = Cohort((1, null));

        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: destinationFromDocumentX, reason: out var xReason, referencedDocument: documentX, resolved: out var fromX, sourceDefinition: definition), userMessage: xReason);
        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: destinationFromDocumentY, reason: out var yReason, referencedDocument: documentY, resolved: out var fromY, sourceDefinition: definition), userMessage: yReason);

        Assert.True(condition: fromY.IsNewGeneration, userMessage: "a SECOND document's identically-named row must mint its OWN generation, never reuse the first document's — the bug this law is red against under the old (name, scope) key");
        Assert.NotEqual(expected: fromX.InstanceName, actual: fromY.InstanceName);
        Assert.NotEqual(expected: fromX.GenerationId, actual: fromY.GenerationId);

        // Each document's own TryGetActive sees only ITS OWN generation — the other document's row is invisible at
        // its own referenced-document identity.
        Assert.True(condition: resolver.TryGetActive(destinationName: "home", durability: WorldDestinationDurability.Ephemeral, referencedDocument: documentX, resolved: out var activeX, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.Equal(expected: fromX.InstanceName, actual: activeX.InstanceName);
        Assert.True(condition: resolver.TryGetActive(destinationName: "home", durability: WorldDestinationDurability.Ephemeral, referencedDocument: documentY, resolved: out var activeY, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.Equal(expected: fromY.InstanceName, actual: activeY.InstanceName);

        // Retiring one document's instance never touches the other's independent cache entry.
        resolver.NotifyInstanceRetired(instanceName: fromX.InstanceName);
        Assert.False(condition: resolver.TryGetActive(destinationName: "home", durability: WorldDestinationDurability.Ephemeral, referencedDocument: documentX, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.True(condition: resolver.TryGetActive(destinationName: "home", durability: WorldDestinationDurability.Ephemeral, referencedDocument: documentY, resolved: out var stillActiveY, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.Equal(expected: fromY.GenerationId, actual: stillActiveY.GenerationId);
    }
    // The "return means home" seam's OWN mechanism, proven at the layer this project can reach: TryGetActive gates
    // origin-adoption BEFORE TryAdopt ever runs (WorldInstanceHost.EnqueueCoalescedGroup's own call order) — a
    // referenced document identical to an already-adopted generation's own sees it and reuses; a DIFFERENT
    // referenced document under the identical destination name and scope never does, so WorldInstanceHost's own
    // TryFindRunningInstanceByOrigin scan is never even reached for it — it falls straight to the ordinary
    // TryResolve mint path instead. This is the resolver-level half of the live-verified split (see this method's
    // own class-level remarks above); the DURABILITY narrowing (ephemeral destinations never attempt origin-adoption
    // at all, since an ephemeral destination's generations are resolver-minted by definition) lives in
    // WorldInstanceHost.EnqueueCoalescedGroup, outside this project's reach (that project deliberately never
    // references Puck.World — see this file's own class remarks) — verified by running the app (see the task's own
    // VERIFY section).
    [Fact]
    public void TryGetActive_SameReferencedDocumentSeesAnAlreadyAdoptedInstance_DifferentReferencedDocumentNeverDoes() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = GlobalDestination(name: "home-origin");
        const string bootDocument = "worlds/nexus.world.json";
        const string modifiedCopyDocument = "worlds/play-modified.world.json";

        // "boot" is adopted for destination 'home-origin' against the SHIPPED document — mirrors WorldInstanceHost's
        // own boot-instance registration (TryAdopt called once, up front, for the boot instance's own document).
        Assert.True(condition: resolver.TryAdopt(destination: destination, instanceName: "boot", reason: out var adoptReason, referencedDocument: bootDocument, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey), userMessage: adoptReason);

        // SAME resolved document (the shipped nexus.world.json, exactly like a dungeon's own 'home' row naming it) —
        // sees the adoption and would reuse it via the ordinary ResolveAndEnqueueCoalescedTransfers gate.
        Assert.True(condition: resolver.TryGetActive(destinationName: "home-origin", durability: WorldDestinationDurability.Ephemeral, referencedDocument: bootDocument, resolved: out var sameDocActive, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.Equal(expected: "boot", actual: sameDocActive.InstanceName);

        // DIFFERENT resolved document (a modified copy booted from elsewhere, naming the SAME destination name and
        // scope) — must NEVER see "boot" as already active; live-verified (2026-08-09) to instead mint a fresh
        // instance rather than adopt.
        Assert.False(condition: resolver.TryGetActive(destinationName: "home-origin", durability: WorldDestinationDurability.Ephemeral, referencedDocument: modifiedCopyDocument, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey), userMessage: "a different referenced document must never see another document's adopted generation as its own");

        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: modifiedCopyDocument, cohort: Cohort((1, null)), resolved: out var minted, reason: out var mintedReason), userMessage: mintedReason);
        Assert.True(condition: minted.IsNewGeneration);
        Assert.NotEqual(expected: "boot", actual: minted.InstanceName);
    }
    // FINDING 3's N:1 REVERSE INDEX: origin adoption can install MORE THAN ONE (destination, scope key, referenced
    // document) key against the SAME already-running instance (two persisted destinations that both happen to
    // resolve to the boot instance's own document, say) — the old one-to-one reverse index could represent only the
    // LAST adoption, so retiring the instance cleared only one of the two logically-adopted keys, leaving
    // world.destinations reporting a dead active generation for the other forever. This law installs two DIFFERENT
    // destinations against the identical instance name via TryAdopt, then proves ONE retirement clears BOTH.
    [Fact]
    public void NotifyInstanceRetired_InstanceAdoptedByMultipleDestinations_ClearsEveryKey() {
        var resolver = new WorldSessionResolver();
        const string sharedDocument = "worlds/nexus.world.json";
        var destinationOne = GlobalDestination(name: "home-a");
        var destinationTwo = GlobalDestination(name: "home-b");

        Assert.True(condition: resolver.TryAdopt(destination: destinationOne, instanceName: "shared-instance", reason: out var oneReason, referencedDocument: sharedDocument, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey), userMessage: oneReason);
        Assert.True(condition: resolver.TryAdopt(destination: destinationTwo, instanceName: "shared-instance", reason: out var twoReason, referencedDocument: sharedDocument, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey), userMessage: twoReason);

        Assert.True(condition: resolver.TryGetActive(destinationName: "home-a", durability: WorldDestinationDurability.Ephemeral, referencedDocument: sharedDocument, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.True(condition: resolver.TryGetActive(destinationName: "home-b", durability: WorldDestinationDurability.Ephemeral, referencedDocument: sharedDocument, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey));

        resolver.NotifyInstanceRetired(instanceName: "shared-instance");

        Assert.False(condition: resolver.TryGetActive(destinationName: "home-a", durability: WorldDestinationDurability.Ephemeral, referencedDocument: sharedDocument, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey), userMessage: "retiring an instance adopted by TWO destinations must clear BOTH keys, not just the last one installed");
        Assert.False(condition: resolver.TryGetActive(destinationName: "home-b", durability: WorldDestinationDurability.Ephemeral, referencedDocument: sharedDocument, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey), userMessage: "retiring an instance adopted by TWO destinations must clear BOTH keys, not just the last one installed");
    }
    // CANONICAL IDENTITY, NOT THE VERBATIM LOCATOR (Codex follow-up to adversarial-review finding 3). The resolver
    // itself is I/O-free by construction (this type's own class remarks: "no dependency on the composition root at
    // all") — resolving "dive.world.json" and "Assets/worlds/dive.world.json" to the SAME underlying file is
    // necessarily a HOST-side act (Puck.World.WorldInstanceHost.CanonicalDocumentIdentity, which this project
    // cannot reach or unit-test directly — see this file's own class remarks), proven live instead (VERIFY section):
    // a diver's "home" destination — authored against nexus.world.json under whatever spelling dive.world.json's own
    // references section uses — correctly adopted the ALREADY-RUNNING boot instance (generation 0, instance=boot)
    // despite boot's own resolved SourcePath almost certainly being a different string. What THIS law proves is the
    // half the resolver DOES own: once the host folds two alias spellings to one canonical string (exactly what
    // CanonicalDocumentIdentity's contract promises its callers), feeding that SAME string in for two otherwise
    // identical rows must resolve to ONE generation, never two independent ones — the resolver-side half of the
    // fix, without which a correct host-side canonicalization would still be wasted on a resolver that re-split it.
    [Fact]
    public void TryResolve_TwoAliasSpellingsCanonicalizedToTheSameHostIdentity_ResolveToOneGeneration() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = GlobalDestination(name: "home");
        // Stands in for WorldInstanceHost.CanonicalDocumentIdentity's own output — the ONE string two raw spellings
        // ("dive.world.json" and "Assets/worlds/dive.world.json", say) fold to once the host resolves both through
        // WorldFileOrigin.TryResolveCanonicalPath's probes. This law's whole point is that the RESOLVER never sees the raw spellings
        // at all, only this already-folded identity — proving that IS enough to dedupe.
        const string canonicalIdentity = "D:/repo/src/Puck.World/Assets/worlds/dive.world.json";

        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: canonicalIdentity, cohort: Cohort((1, null)), resolved: out var first, reason: out var firstReason), userMessage: firstReason);
        Assert.True(condition: resolver.TryResolve(sourceDefinition: definition, destination: destination, referencedDocument: canonicalIdentity, cohort: Cohort((2, null)), resolved: out var second, reason: out var secondReason), userMessage: secondReason);

        Assert.False(condition: second.IsNewGeneration, userMessage: "two rows resolving to the SAME canonical identity must share ONE generation, never mint a second");
        Assert.Equal(expected: first.GenerationId, actual: second.GenerationId);
        Assert.Equal(expected: first.InstanceName, actual: second.InstanceName);
    }
    // DURABILITY IS PART OF THE IDENTITY (Codex follow-up to adversarial-review finding 3). An ephemeral and a
    // persisted destination row sharing name+scope+document are NOT the same identity: ephemeral's contract is
    // "mint fresh, reap when empty" while persisted's is "retained until an explicit world.instance.stop" — sharing
    // one cache entry would let whichever row resolved FIRST silently impose its own retention rule on travelers
    // through the OTHER row. Refusal/control shape: the SAME (name, scope, document) triple, durability the only
    // difference, must mint TWO independent generations — the discriminating case the coordinator's course
    // correction named explicitly.
    [Fact]
    public void TryResolve_EphemeralAndPersistedRowsOtherwiseIdentical_ResolveToTwoIndependentGenerations() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var ephemeralDestination = GlobalDestination(name: "shared-name");
        var persistedDestination = PersistedGlobalDestination(name: "shared-name");
        var cohort = Cohort((1, null));

        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: ephemeralDestination, reason: out var ephemeralReason, referencedDocument: RefDoc, resolved: out var ephemeral, sourceDefinition: definition), userMessage: ephemeralReason);
        Assert.True(condition: resolver.TryResolve(cohort: cohort, destination: persistedDestination, reason: out var persistedReason, referencedDocument: RefDoc, resolved: out var persisted, sourceDefinition: definition), userMessage: persistedReason);

        Assert.True(condition: persisted.IsNewGeneration, userMessage: "a persisted row sharing name+scope+document with an already-resolved ephemeral row must still mint its OWN generation, never reuse the ephemeral one");
        Assert.NotEqual(expected: ephemeral.GenerationId, actual: persisted.GenerationId);
        Assert.NotEqual(expected: ephemeral.InstanceName, actual: persisted.InstanceName);

        // TryGetActive keyed by durability sees only its own row — an ephemeral lookup never finds the persisted
        // generation and vice versa, even though name+scope+document all agree.
        Assert.True(condition: resolver.TryGetActive(destinationName: "shared-name", durability: WorldDestinationDurability.Ephemeral, referencedDocument: RefDoc, resolved: out var activeEphemeral, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.Equal(expected: ephemeral.InstanceName, actual: activeEphemeral.InstanceName);
        Assert.True(condition: resolver.TryGetActive(destinationName: "shared-name", durability: WorldDestinationDurability.Persisted, referencedDocument: RefDoc, resolved: out var activePersisted, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.Equal(expected: persisted.InstanceName, actual: activePersisted.InstanceName);

        // Retiring the ephemeral instance never touches the persisted row's own independent cache entry.
        resolver.NotifyInstanceRetired(instanceName: ephemeral.InstanceName);
        Assert.False(condition: resolver.TryGetActive(destinationName: "shared-name", durability: WorldDestinationDurability.Ephemeral, referencedDocument: RefDoc, resolved: out _, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.True(condition: resolver.TryGetActive(destinationName: "shared-name", durability: WorldDestinationDurability.Persisted, referencedDocument: RefDoc, resolved: out var stillActivePersisted, scopeKey: WorldSessionResolver.GlobalScopeKey));
        Assert.Equal(expected: persisted.GenerationId, actual: stillActivePersisted.GenerationId);
    }
}
