using Puck.Commands;
using Xunit;


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
            Capacity: 8,
            EvictionPolicy: WorldGroupEvictionPolicy.Remove,
            Lifetime: WorldGroupLifetime.Persistent,
            Name: "party",
            Roles: []
        );
        var groups = new WorldGroup[] {
            // "alpha" — the `named` law's member (Seat 1) and non-member (Seat 2) control.
            new(
            Id: SafeName.Parse(candidate: "alpha"),
            KindName: "party",
            Members: [new WorldGroupMember(
                    WorldMemberRef.Local(principal: Principal.Seat(slot: 1)),
                    null,
                    0
                )]
        ),
            // "gamma" — the `tagged` law's UNIQUE match: Seat 3 holds exactly one membership tagged "raiders".
            new(
            Id: SafeName.Parse(candidate: "gamma"),
            KindName: "party",
            Members: [new WorldGroupMember(
                    WorldMemberRef.Local(principal: Principal.Seat(slot: 3)),
                    null,
                    0
                )],
            Tags: ["raiders"]
        ),
            // "delta"/"epsilon" — the `tagged` law's AMBIGUOUS case: Seat 4 holds two memberships both tagged
            // "explorers", so neither should be picked silently.
            new(
            Id: SafeName.Parse(candidate: "delta"),
            KindName: "party",
            Members: [new WorldGroupMember(
                    WorldMemberRef.Local(principal: Principal.Seat(slot: 4)),
                    null,
                    0
                )],
            Tags: ["explorers"]
        ),
            new(
            Id: SafeName.Parse(candidate: "epsilon"),
            KindName: "party",
            Members: [new WorldGroupMember(
                    WorldMemberRef.Local(principal: Principal.Seat(slot: 4)),
                    null,
                    0
                )],
            Tags: ["explorers"]
        ),
        };

        return Fixtures.BuildDocument() with {
            Groups = new WorldGroupsSection(
            Groups: groups,
            Kinds: [kind],
            Ownership: []
        ),
        };
    }
    private static WorldSessionResolver.CohortMember[] Cohort(params (int Slot, string? IdentityId)[] members) {
        var result = new WorldSessionResolver.CohortMember[members.Length];

        for (var index = 0; (index < members.Length); index++) {
            result[index] = new WorldSessionResolver.CohortMember(
                Principal: Principal.Seat(slot: members[index].Slot),
                IdentityId: members[index].IdentityId
            );
        }

        return result;
    }
    private static WorldDestination GlobalDestination(string name = "camp") =>
        new(
            Name: SafeName.Parse(candidate: name),
            Reference: DestinationReference,
            Durability: WorldDestinationDurability.Ephemeral
        );
    private static WorldDestination NamedGroupDestination(string groupId, string name = "hall", WorldDestinationDurability durability = WorldDestinationDurability.Ephemeral) =>
        new(
            Name: SafeName.Parse(candidate: name),
            Reference: DestinationReference,
            Durability: durability,
            Scope: WorldDestinationScope.Group,
            Selector: new WorldGroupSelector.Named(Group: groupId)
        );
    private static WorldDestination PersistedGlobalDestination(string name = "camp") =>
        new(
            Name: SafeName.Parse(candidate: name),
            Reference: DestinationReference,
            Durability: WorldDestinationDurability.Persisted
        );
    private static WorldDestination TaggedGroupDestination(string tag, string name = "lodge") =>
        new(
            Name: SafeName.Parse(candidate: name),
            Reference: DestinationReference,
            Durability: WorldDestinationDurability.Ephemeral,
            Scope: WorldDestinationScope.Group,
            Selector: new WorldGroupSelector.Tagged(Tag: tag)
        );
    private static WorldDestination UserDestination(string name = "workshop") =>
        new(
            Name: SafeName.Parse(candidate: name),
            Reference: DestinationReference,
            Durability: WorldDestinationDurability.Ephemeral,
            Scope: WorldDestinationScope.User
        );

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

        Assert.True(
            condition: resolver.TryResolve(
                cohort: cohort,
                destination: abortedDestination,
                reason: out var abortedReason,
                referencedDocument: RefDoc,
                resolved: out var aborted,
                sourceDefinition: definition
            ),
            userMessage: abortedReason
        );

        resolver.AbortGeneration(instanceName: aborted.InstanceName);

        Assert.True(
            condition: resolver.TryResolve(
                cohort: cohort,
                destination: abortedDestination,
                reason: out var afterAbortReason,
                referencedDocument: RefDoc,
                resolved: out var afterAbort,
                sourceDefinition: definition
            ),
            userMessage: afterAbortReason
        );
        Assert.True(
            condition: afterAbort.IsNewGeneration,
            userMessage: "aborting a generation whose instance never started must let the next resolve mint a genuinely fresh one"
        );
        Assert.NotEqual(
            expected: aborted.GenerationId,
            actual: afterAbort.GenerationId
        );

        // Control: the identical shape, but WITHOUT the abort call — the second resolve must reuse the SAME
        // generation, proving the aborted case's difference is AbortGeneration's own effect and not some other
        // difference between the two resolves.
        var reusedDestination = GlobalDestination(name: "camp-reused");

        Assert.True(
            condition: resolver.TryResolve(
                cohort: cohort,
                destination: reusedDestination,
                reason: out var reusedReason,
                referencedDocument: RefDoc,
                resolved: out var reused,
                sourceDefinition: definition
            ),
            userMessage: reusedReason
        );
        Assert.True(
            condition: resolver.TryResolve(
                cohort: cohort,
                destination: reusedDestination,
                reason: out var stillReusedReason,
                referencedDocument: RefDoc,
                resolved: out var stillReused,
                sourceDefinition: definition
            ),
            userMessage: stillReusedReason
        );
        Assert.False(condition: stillReused.IsNewGeneration);
        Assert.Equal(
            expected: reused.GenerationId,
            actual: stillReused.GenerationId
        );
    }
    [Fact]
    public void GenerationLifecycle_AfterRetirement_NextResolveMintsANewGeneration() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = GlobalDestination();
        var cohort = Cohort((1, null));

        Assert.True(condition: resolver.TryResolve(
            cohort: cohort,
            destination: destination,
            reason: out _,
            referencedDocument: RefDoc,
            resolved: out var first,
            sourceDefinition: definition
        ));

        // The instance the first resolution named just went away (WorldInstanceHost.TryStop/ReapIfEmpty's apply
        // path, mirrored here by calling the notification directly) — the resolver's cache entry for this
        // (destination, scope key) must clear, so the NEXT resolution is a genuinely new generation, never a stale
        // reuse of a name nothing answers to any more.
        resolver.NotifyInstanceRetired(instanceName: first.InstanceName);

        Assert.True(condition: resolver.TryResolve(
            cohort: cohort,
            destination: destination,
            reason: out _,
            referencedDocument: RefDoc,
            resolved: out var second,
            sourceDefinition: definition
        ));

        Assert.True(
            condition: second.IsNewGeneration,
            userMessage: "resolving again after retirement must mint a NEW generation, not reuse the retired one"
        );
        Assert.NotEqual(
            expected: first.GenerationId,
            actual: second.GenerationId
        );
        Assert.NotEqual(
            expected: first.InstanceName,
            actual: second.InstanceName
        );
    }
    [Fact]
    public void GroupNamedScope_NonMemberRefused_MemberResolves() {
        var resolver = new WorldSessionResolver();
        var definition = BuildDocumentWithGroups();
        var destination = NamedGroupDestination(groupId: "alpha");

        Laws.RefusalWithControl(
            lawId: "resolver.group-named-non-member-refused",
            // Seat 2 holds no membership in "alpha" at all.
            deniedOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((2, null)),
                resolved: out _,
                reason: out _
            ),
            // Seat 1 is "alpha"'s one authored member.
            controlOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((1, null)),
                resolved: out _,
                reason: out _
            ));
    }
    [Fact]
    public void GroupTaggedScope_AmbiguousMembershipRefused_UniqueMembershipResolves() {
        var resolver = new WorldSessionResolver();
        var definition = BuildDocumentWithGroups();

        Laws.RefusalWithControl(
            lawId: "resolver.group-tagged-ambiguous-refused",
            // Seat 4 holds TWO memberships ("delta"/"epsilon") both tagged "explorers" — ambiguous, refused by name.
            deniedOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: TaggedGroupDestination(
                    name: "lodge-ambiguous",
                    tag: "explorers"
                ),
                referencedDocument: RefDoc,
                cohort: Cohort((4, null)),
                resolved: out _,
                reason: out _
            ),
            // Seat 3 holds exactly ONE membership ("gamma") tagged "raiders" — unique, resolves.
            controlOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: TaggedGroupDestination(
                    name: "lodge-unique",
                    tag: "raiders"
                ),
                referencedDocument: RefDoc,
                cohort: Cohort((3, null)),
                resolved: out _,
                reason: out _
            ));
    }
    [Fact]
    public void GroupTaggedScope_CohortResolvingDifferentGroupsRefused_SameGroupResolves() {
        var resolver = new WorldSessionResolver();
        // Two independent groups sharing one tag, each with its own single member, so a cohort naming both members
        // resolves to two DIFFERENT group ids under the same tag — the cross-member disagreement this law targets.
        var kind = new WorldGroupKind(
            Capacity: 8,
            EvictionPolicy: WorldGroupEvictionPolicy.Remove,
            Lifetime: WorldGroupLifetime.Persistent,
            Name: "party",
            Roles: []
        );
        var groups = new WorldGroup[] {
            new(
            Id: SafeName.Parse(candidate: "north"),
            KindName: "party",
            Members: [new WorldGroupMember(
                    WorldMemberRef.Local(principal: Principal.Seat(slot: 1)),
                    null,
                    0
                )],
            Tags: ["shared"]
        ),
            new(
            Id: SafeName.Parse(candidate: "south"),
            KindName: "party",
            Members: [new WorldGroupMember(
                    WorldMemberRef.Local(principal: Principal.Seat(slot: 2)),
                    null,
                    0
                )],
            Tags: ["shared"]
        ),
        };
        var definition = Fixtures.BuildDocument() with {
            Groups = new WorldGroupsSection(
            Groups: groups,
            Kinds: [kind],
            Ownership: []
        ),
        };
        var destination = TaggedGroupDestination(
            name: "lodge-cohort",
            tag: "shared"
        );

        Laws.RefusalWithControl(
            lawId: "resolver.group-tagged-cohort-disagreement-refused",
            // Seat 1 resolves "north", Seat 2 resolves "south" under the same tag — disagreement, refused.
            deniedOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort(
                    (1, null),
                    (2, null)
                ),
                resolved: out _,
                reason: out _
            ),
            // The SAME two seats, but only Seat 1 travels (a `body` crossing) — one member, no disagreement possible.
            controlOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((1, null)),
                resolved: out _,
                reason: out _
            ));
    }
    [Fact]
    public void GroupTaggedScope_NoMatchingMembershipRefused_MatchingMembershipResolves() {
        var resolver = new WorldSessionResolver();
        var definition = BuildDocumentWithGroups();
        var destination = TaggedGroupDestination(
            name: "lodge-zero-or-one",
            tag: "raiders"
        );

        Laws.RefusalWithControl(
            lawId: "resolver.group-tagged-zero-refused",
            // Seat 1 holds no membership tagged "raiders" at all (only "alpha", untagged).
            deniedOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((1, null)),
                resolved: out _,
                reason: out _
            ),
            // Seat 3 holds exactly one membership tagged "raiders".
            controlOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((3, null)),
                resolved: out _,
                reason: out _
            ));
    }
    [Fact]
    public void IdempotentResolution_SameCohortTwice_ReturnsSameGenerationAndInstance() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = GlobalDestination();
        var cohort = Cohort((1, null));

        Assert.True(condition: resolver.TryResolve(
            cohort: cohort,
            destination: destination,
            reason: out _,
            referencedDocument: RefDoc,
            resolved: out var first,
            sourceDefinition: definition
        ));
        Assert.True(condition: resolver.TryResolve(
            cohort: cohort,
            destination: destination,
            reason: out _,
            referencedDocument: RefDoc,
            resolved: out var second,
            sourceDefinition: definition
        ));

        Assert.True(
            condition: first.IsNewGeneration,
            userMessage: "the FIRST resolution of a destination must mint a new generation"
        );
        Assert.False(
            condition: second.IsNewGeneration,
            userMessage: "the SECOND resolution against the same still-active generation must reuse it, never mint again"
        );
        Assert.Equal(
            expected: first.GenerationId,
            actual: second.GenerationId
        );
        Assert.Equal(
            expected: first.InstanceName,
            actual: second.InstanceName
        );
    }

    // A destination or group id carrying '~' — the character the resolver's own netstring segments and every
    // file-backed name the engine mints are spelled with — is refused by name at load and at resolution, so no
    // instance name is ever spelled from one. Each crafted name is one that could otherwise impersonate a netstring
    // encoding ('1~a4~user1~b' is ScopedSegment("a") + ScopedSegment("user") + ScopedSegment("b")) or blur where one
    // segment ends ('d~group_a' beside a group 'a~group_b'). The control is each name with '~' spelled '-', which
    // loads and resolves.
    private static WorldDefinition DocumentNaming(string destination, string group) {
        var kind = new WorldGroupKind(
            Capacity: 8,
            EvictionPolicy: WorldGroupEvictionPolicy.Remove,
            Lifetime: WorldGroupLifetime.Persistent,
            Name: "party",
            Roles: []
        );

        return Fixtures.BuildDocument() with {
            Destinations = [
                new WorldDestination(
                    Durability: WorldDestinationDurability.Persisted,
                    Name: SafeName.Parse(candidate: destination),
                    Reference: DestinationReference,
                    Scope: WorldDestinationScope.Group,
                    Selector: new WorldGroupSelector.Named(Group: group)
                ),
            ],
            Groups = new WorldGroupsSection(
                Groups: [new WorldGroup(
                    Id: SafeName.Parse(candidate: group),
                    KindName: "party",
                    Members: [new WorldGroupMember(WorldMemberRef.Local(principal: Principal.Seat(slot: 1)), null, 0)]
                )],
                Kinds: [kind],
                Ownership: []
            ),
            References = [new WorldReference(Document: "fixture", Name: SafeName.Parse(candidate: DestinationReference))],
        };
    }
    private static bool Loads(WorldDefinition definition, out string reason) => WorldDefinitionFileSource.TryParseDocument(
        definition: out _,
        json: System.Text.Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition)),
        reason: out reason,
        sourceName: "crafted.world.json"
    );
    private static bool Resolves(WorldDefinition definition, out string reason) => new WorldSessionResolver().TryResolve(
        cohort: Cohort((1, null)),
        destination: definition.Destinations![0],
        reason: out reason,
        referencedDocument: RefDoc,
        resolved: out _,
        sourceDefinition: definition
    );

    [InlineData("1~a4~user1~b", "b", "1~a4~user1~b")]
    [InlineData("d~group_a", "b", "d~group_a")]
    [InlineData("d", "a~group_b", "a~group_b")]
    [Theory]
    public void MintInstanceName_ADestinationOrGroupCarryingTheFileJoiner_IsRefusedByNameAtLoadAndAtResolution(string destination, string group, string refused) {
        var crafted = DocumentNaming(destination: destination, group: group);

        Assert.False(condition: Loads(definition: crafted, reason: out var loadReason));
        Assert.Contains(actualString: loadReason, expectedSubstring: $"'{refused}' carries '~'");
        Assert.False(condition: Resolves(definition: crafted, reason: out var resolveReason));
        Assert.Contains(actualString: resolveReason, expectedSubstring: $"'{refused}' carries '~'");

        var control = DocumentNaming(
            destination: destination.Replace(newChar: '-', oldChar: '~'),
            group: group.Replace(newChar: '-', oldChar: '~')
        );

        Assert.True(condition: Loads(definition: control, reason: out var controlLoad), userMessage: controlLoad);
        Assert.True(condition: Resolves(definition: control, reason: out var controlResolve), userMessage: controlResolve);
    }
    // An instance name spells its destination and group segments file-backed (GeneratedName.ToFile), so the
    // directory never carries '$' — and it stays injective: admitted names spelled with '$' exactly where the refused
    // ones above carried '~' still mint distinct instances with independent caches, the netstring keeping the segment
    // sequence apart. PERSISTED throughout, so no generation ordinal could keep a pair apart instead.
    [Fact]
    public void MintInstanceName_AdmittedNamesSpelledLikeTheNetstring_MintDistinctInstancesThatCarryNoDollar() {
        var resolver = new WorldSessionResolver();
        var definition = DocumentNaming(destination: "d$group_a", group: "b");
        var second = DocumentNaming(destination: "d", group: "a$group_b");
        var global = new WorldDestination(
            Durability: WorldDestinationDurability.Persisted,
            Name: SafeName.Parse(candidate: "1$a4$user1$b"),
            Reference: DestinationReference
        );
        var scoped = new WorldDestination(
            Durability: WorldDestinationDurability.Persisted,
            Name: SafeName.Parse(candidate: "a"),
            Reference: DestinationReference,
            Scope: WorldDestinationScope.User
        );
        var minted = new List<WorldSessionResolver.Resolved>();

        void Resolve(WorldDefinition source, WorldDestination destination, int seat, string? identity) {
            Assert.True(
                condition: resolver.TryResolve(
                    cohort: Cohort((seat, identity)),
                    destination: destination,
                    reason: out var reason,
                    referencedDocument: RefDoc,
                    resolved: out var resolved,
                    sourceDefinition: source
                ),
                userMessage: reason
            );
            minted.Add(item: resolved);
        }

        Resolve(destination: definition.Destinations![0], identity: null, seat: 1, source: definition);
        Resolve(destination: second.Destinations![0], identity: null, seat: 1, source: second);
        Resolve(destination: global, identity: null, seat: 1, source: definition);
        Resolve(destination: scoped, identity: "b", seat: 2, source: definition);

        Assert.Equal(
            actual: minted.Select(selector: static resolved => resolved.InstanceName).Distinct(comparer: StringComparer.Ordinal).Count(),
            expected: minted.Count
        );
        Assert.All(
            action: static resolved => Assert.DoesNotContain(actualString: resolved.InstanceName, expectedSubstring: "$"),
            collection: minted
        );
        Assert.Equal(actual: minted[2].InstanceName, expected: "6~global12~1~a4~user1~b");

        // Retiring one leaves every other pair's cache entry in place.
        resolver.NotifyInstanceRetired(instanceName: minted[0].InstanceName);
        Assert.True(
            condition: resolver.TryResolve(
                cohort: Cohort((1, null)),
                destination: second.Destinations![0],
                reason: out var againReason,
                referencedDocument: RefDoc,
                resolved: out var again,
                sourceDefinition: second
            ),
            userMessage: againReason
        );
        Assert.False(condition: again.IsNewGeneration);
    }
    // A generated link destination is spelled file-backed in the instance it starts: 'link$west' is 'link~west'.
    [Fact]
    public void MintInstanceName_AGeneratedLinkDestinationStartsAnInstanceSpelledFileBacked() {
        Assert.True(
            condition: new WorldSessionResolver().TryResolve(
                cohort: Cohort((1, null)),
                destination: new WorldDestination(Durability: WorldDestinationDurability.Persisted, Name: SafeName.Parse(candidate: GeneratedName.Join("link", "west")), Reference: DestinationReference),
                reason: out var reason,
                referencedDocument: RefDoc,
                resolved: out var resolved,
                sourceDefinition: Fixtures.BuildDocument()
            ),
            userMessage: reason
        );
        Assert.Equal(actual: resolved.InstanceName, expected: "6~global9~link~west");
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
        const string SharedDocument = "worlds/nexus.world.json";
        var destinationOne = GlobalDestination(name: "home-a");
        var destinationTwo = GlobalDestination(name: "home-b");

        Assert.True(
            condition: resolver.TryAdopt(
                destination: destinationOne,
                instanceName: "shared-instance",
                reason: out var oneReason,
                referencedDocument: SharedDocument,
                resolved: out _,
                scopeKey: WorldSessionResolver.GlobalScopeKey
            ),
            userMessage: oneReason
        );
        Assert.True(
            condition: resolver.TryAdopt(
                destination: destinationTwo,
                instanceName: "shared-instance",
                reason: out var twoReason,
                referencedDocument: SharedDocument,
                resolved: out _,
                scopeKey: WorldSessionResolver.GlobalScopeKey
            ),
            userMessage: twoReason
        );

        Assert.True(condition: resolver.TryGetActive(
            destinationName: "home-a",
            durability: WorldDestinationDurability.Ephemeral,
            referencedDocument: SharedDocument,
            resolved: out _,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.True(condition: resolver.TryGetActive(
            destinationName: "home-b",
            durability: WorldDestinationDurability.Ephemeral,
            referencedDocument: SharedDocument,
            resolved: out _,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));

        resolver.NotifyInstanceRetired(instanceName: "shared-instance");

        Assert.False(
            condition: resolver.TryGetActive(
                destinationName: "home-a",
                durability: WorldDestinationDurability.Ephemeral,
                referencedDocument: SharedDocument,
                resolved: out _,
                scopeKey: WorldSessionResolver.GlobalScopeKey
            ),
            userMessage: "retiring an instance adopted by TWO destinations must clear BOTH keys, not just the last one installed"
        );
        Assert.False(
            condition: resolver.TryGetActive(
                destinationName: "home-b",
                durability: WorldDestinationDurability.Ephemeral,
                referencedDocument: SharedDocument,
                resolved: out _,
                scopeKey: WorldSessionResolver.GlobalScopeKey
            ),
            userMessage: "retiring an instance adopted by TWO destinations must clear BOTH keys, not just the last one installed"
        );
    }
    // RETURN MEANS HOME (docs/architecture/worlds.md): TryAdopt/TryGetActive are the cache-install half of the seam
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

        Assert.False(
            condition: resolver.TryGetActive(
                destinationName: destination.Name.Value,
                durability: WorldDestinationDurability.Ephemeral,
                scopeKey: WorldSessionResolver.GlobalScopeKey,
                referencedDocument: RefDoc,
                resolved: out _
            ),
            userMessage: "a pair nothing has resolved yet must report no active generation"
        );

        Assert.True(
            condition: resolver.TryAdopt(
                destination: destination,
                instanceName: "boot",
                reason: out var adoptReason,
                referencedDocument: RefDoc,
                resolved: out var adopted,
                scopeKey: WorldSessionResolver.GlobalScopeKey
            ),
            userMessage: adoptReason
        );
        Assert.Equal(
            expected: "boot",
            actual: adopted.InstanceName
        );
        Assert.False(
            condition: adopted.IsNewGeneration,
            userMessage: "adopting a RUNNING instance is never a fresh mint"
        );

        // TryGetActive now reports it, and an ORDINARY TryResolve call (the shape every other crossing takes) reuses
        // the adopted instance rather than minting a second one — the whole point of the seam.
        Assert.True(condition: resolver.TryGetActive(
            destinationName: destination.Name.Value,
            durability: WorldDestinationDurability.Ephemeral,
            scopeKey: WorldSessionResolver.GlobalScopeKey,
            referencedDocument: RefDoc,
            resolved: out var active
        ));
        Assert.Equal(
            expected: "boot",
            actual: active.InstanceName
        );

        Assert.True(
            condition: resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((1, null)),
                resolved: out var resolved,
                reason: out var resolveReason
            ),
            userMessage: resolveReason
        );
        Assert.Equal(
            expected: "boot",
            actual: resolved.InstanceName
        );
        Assert.False(condition: resolved.IsNewGeneration);
        Assert.Equal(
            expected: adopted.GenerationId,
            actual: resolved.GenerationId
        );
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

        Assert.True(
            condition: resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((1, null)),
                resolved: out var minted,
                reason: out var mintedReason
            ),
            userMessage: mintedReason
        );
        Assert.True(condition: minted.IsNewGeneration);
        Assert.NotEqual(
            expected: "boot",
            actual: minted.InstanceName
        );

        // An origin scan that (hypothetically) later found "boot" sharing this destination's document must NOT
        // overwrite the generation a genuine mint already installed — the resolver's own cache wins.
        Assert.True(
            condition: resolver.TryAdopt(
                destination: destination,
                instanceName: "boot",
                reason: out var adoptReason,
                referencedDocument: RefDoc,
                resolved: out var adoptAttempt,
                scopeKey: WorldSessionResolver.GlobalScopeKey
            ),
            userMessage: adoptReason
        );
        Assert.Equal(
            expected: minted.InstanceName,
            actual: adoptAttempt.InstanceName
        );
        Assert.Equal(
            expected: minted.GenerationId,
            actual: adoptAttempt.GenerationId
        );
        Assert.NotEqual(
            expected: "boot",
            actual: adoptAttempt.InstanceName
        );

        Assert.True(
            condition: resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((1, null)),
                resolved: out var stillMinted,
                reason: out var stillMintedReason
            ),
            userMessage: stillMintedReason
        );
        Assert.Equal(
            expected: minted.InstanceName,
            actual: stillMinted.InstanceName
        );
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
        Assert.True(
            condition: resolver.TryDeriveScopeKey(
                sourceDefinition: definition,
                destination: destination,
                cohort: Cohort((1, null)),
                scopeKey: out var frozenScopeKey,
                reason: out var frozenReason
            ),
            userMessage: frozenReason
        );

        // CONTROL — re-deriving the SAME still-valid cohort at "drain time" agrees with the frozen key.
        Assert.True(
            condition: resolver.TryDeriveScopeKey(
                sourceDefinition: definition,
                destination: destination,
                cohort: Cohort((1, null)),
                scopeKey: out var unchangedScopeKey,
                reason: out var unchangedReason
            ),
            userMessage: unchangedReason
        );
        Assert.Equal(
            actual: unchangedScopeKey,
            expected: frozenScopeKey
        );

        // DRIFT — re-deriving against a cohort whose membership no longer holds (Seat 2 was never "alpha"'s member)
        // either refuses outright or (if it resolves against some OTHER group) disagrees with the frozen key; either
        // way the frozen proof no longer holds, which is the whole point of the drain-time re-check.
        var driftedResolved = resolver.TryDeriveScopeKey(
            sourceDefinition: definition,
            destination: destination,
            cohort: Cohort((2, null)),
            scopeKey: out var driftedScopeKey,
            reason: out _
        );

        Assert.False(
            condition: (driftedResolved && string.Equals(
                a: driftedScopeKey,
                b: frozenScopeKey,
                comparisonType: StringComparison.Ordinal
            )),
            userMessage: "a cohort that no longer proves the frozen destination's membership must not silently re-agree with the frozen scope key"
        );
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
        const string BootDocument = "worlds/nexus.world.json";
        const string ModifiedCopyDocument = "worlds/play-modified.world.json";

        // "boot" is adopted for destination 'home-origin' against the SHIPPED document — mirrors WorldInstanceHost's
        // own boot-instance registration (TryAdopt called once, up front, for the boot instance's own document).
        Assert.True(
            condition: resolver.TryAdopt(
                destination: destination,
                instanceName: "boot",
                reason: out var adoptReason,
                referencedDocument: BootDocument,
                resolved: out _,
                scopeKey: WorldSessionResolver.GlobalScopeKey
            ),
            userMessage: adoptReason
        );

        // SAME resolved document (the shipped nexus.world.json, exactly like a dungeon's own 'home' row naming it) —
        // sees the adoption and would reuse it via the ordinary ResolveAndEnqueueCoalescedTransfers gate.
        Assert.True(condition: resolver.TryGetActive(
            destinationName: "home-origin",
            durability: WorldDestinationDurability.Ephemeral,
            referencedDocument: BootDocument,
            resolved: out var sameDocActive,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.Equal(
            expected: "boot",
            actual: sameDocActive.InstanceName
        );

        // DIFFERENT resolved document (a modified copy booted from elsewhere, naming the SAME destination name and
        // scope) — must NEVER see "boot" as already active; empirically verified to instead mint a fresh
        // instance rather than adopt.
        Assert.False(
            condition: resolver.TryGetActive(
                destinationName: "home-origin",
                durability: WorldDestinationDurability.Ephemeral,
                referencedDocument: ModifiedCopyDocument,
                resolved: out _,
                scopeKey: WorldSessionResolver.GlobalScopeKey
            ),
            userMessage: "a different referenced document must never see another document's adopted generation as its own"
        );

        Assert.True(
            condition: resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: ModifiedCopyDocument,
                cohort: Cohort((1, null)),
                resolved: out var minted,
                reason: out var mintedReason
            ),
            userMessage: mintedReason
        );
        Assert.True(condition: minted.IsNewGeneration);
        Assert.NotEqual(
            expected: "boot",
            actual: minted.InstanceName
        );
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

        Assert.True(
            condition: resolver.TryResolve(
                cohort: cohort,
                destination: ephemeralDestination,
                reason: out var ephemeralReason,
                referencedDocument: RefDoc,
                resolved: out var ephemeral,
                sourceDefinition: definition
            ),
            userMessage: ephemeralReason
        );
        Assert.True(
            condition: resolver.TryResolve(
                cohort: cohort,
                destination: persistedDestination,
                reason: out var persistedReason,
                referencedDocument: RefDoc,
                resolved: out var persisted,
                sourceDefinition: definition
            ),
            userMessage: persistedReason
        );

        Assert.True(
            condition: persisted.IsNewGeneration,
            userMessage: "a persisted row sharing name+scope+document with an already-resolved ephemeral row must still mint its OWN generation, never reuse the ephemeral one"
        );
        Assert.NotEqual(
            expected: ephemeral.GenerationId,
            actual: persisted.GenerationId
        );
        Assert.NotEqual(
            expected: ephemeral.InstanceName,
            actual: persisted.InstanceName
        );

        // TryGetActive keyed by durability sees only its own row — an ephemeral lookup never finds the persisted
        // generation and vice versa, even though name+scope+document all agree.
        Assert.True(condition: resolver.TryGetActive(
            destinationName: "shared-name",
            durability: WorldDestinationDurability.Ephemeral,
            referencedDocument: RefDoc,
            resolved: out var activeEphemeral,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.Equal(
            expected: ephemeral.InstanceName,
            actual: activeEphemeral.InstanceName
        );
        Assert.True(condition: resolver.TryGetActive(
            destinationName: "shared-name",
            durability: WorldDestinationDurability.Persisted,
            referencedDocument: RefDoc,
            resolved: out var activePersisted,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.Equal(
            expected: persisted.InstanceName,
            actual: activePersisted.InstanceName
        );

        // Retiring the ephemeral instance never touches the persisted row's own independent cache entry.
        resolver.NotifyInstanceRetired(instanceName: ephemeral.InstanceName);
        Assert.False(condition: resolver.TryGetActive(
            destinationName: "shared-name",
            durability: WorldDestinationDurability.Ephemeral,
            referencedDocument: RefDoc,
            resolved: out _,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.True(condition: resolver.TryGetActive(
            destinationName: "shared-name",
            durability: WorldDestinationDurability.Persisted,
            referencedDocument: RefDoc,
            resolved: out var stillActivePersisted,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.Equal(
            expected: persisted.GenerationId,
            actual: stillActivePersisted.GenerationId
        );
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
        const string CanonicalIdentity = "D:/repo/src/Puck.World/Assets/worlds/dive.world.json";

        Assert.True(
            condition: resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: CanonicalIdentity,
                cohort: Cohort((1, null)),
                resolved: out var first,
                reason: out var firstReason
            ),
            userMessage: firstReason
        );
        Assert.True(
            condition: resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: CanonicalIdentity,
                cohort: Cohort((2, null)),
                resolved: out var second,
                reason: out var secondReason
            ),
            userMessage: secondReason
        );

        Assert.False(
            condition: second.IsNewGeneration,
            userMessage: "two rows resolving to the SAME canonical identity must share ONE generation, never mint a second"
        );
        Assert.Equal(
            expected: first.GenerationId,
            actual: second.GenerationId
        );
        Assert.Equal(
            expected: first.InstanceName,
            actual: second.InstanceName
        );
    }
    // FINDING 3 — RESOLVER IDENTITY KEYED TOO NARROWLY (adversarial review). Before this fix the cache key was bare
    // (destination name, scope key): two UNRELATED documents authoring an identically-spelled destination row (both
    // naming a 'home' global row, say) collided in this ONE process-wide resolver, and cache-first precedence (see
    // TryGetActive's own remarks) meant whichever document resolved first silently claimed the name for good — a
    // SECOND document's own row could never mint its own generation; it would keep reusing the FIRST document's
    // instance forever. Live confirmation (independent verification): booting a MODIFIED COPY of
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
        const string DocumentX = "worlds/nexus.world.json";
        const string DocumentY = "worlds/play-modified.world.json";
        var cohort = Cohort((1, null));

        Assert.True(
            condition: resolver.TryResolve(
                cohort: cohort,
                destination: destinationFromDocumentX,
                reason: out var xReason,
                referencedDocument: DocumentX,
                resolved: out var fromX,
                sourceDefinition: definition
            ),
            userMessage: xReason
        );
        Assert.True(
            condition: resolver.TryResolve(
                cohort: cohort,
                destination: destinationFromDocumentY,
                reason: out var yReason,
                referencedDocument: DocumentY,
                resolved: out var fromY,
                sourceDefinition: definition
            ),
            userMessage: yReason
        );

        Assert.True(
            condition: fromY.IsNewGeneration,
            userMessage: "a SECOND document's identically-named row must mint its OWN generation, never reuse the first document's — the bug this law is red against under the old (name, scope) key"
        );
        Assert.NotEqual(
            expected: fromX.InstanceName,
            actual: fromY.InstanceName
        );
        Assert.NotEqual(
            expected: fromX.GenerationId,
            actual: fromY.GenerationId
        );

        // Each document's own TryGetActive sees only ITS OWN generation — the other document's row is invisible at
        // its own referenced-document identity.
        Assert.True(condition: resolver.TryGetActive(
            destinationName: "home",
            durability: WorldDestinationDurability.Ephemeral,
            referencedDocument: DocumentX,
            resolved: out var activeX,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.Equal(
            expected: fromX.InstanceName,
            actual: activeX.InstanceName
        );
        Assert.True(condition: resolver.TryGetActive(
            destinationName: "home",
            durability: WorldDestinationDurability.Ephemeral,
            referencedDocument: DocumentY,
            resolved: out var activeY,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.Equal(
            expected: fromY.InstanceName,
            actual: activeY.InstanceName
        );

        // Retiring one document's instance never touches the other's independent cache entry.
        resolver.NotifyInstanceRetired(instanceName: fromX.InstanceName);
        Assert.False(condition: resolver.TryGetActive(
            destinationName: "home",
            durability: WorldDestinationDurability.Ephemeral,
            referencedDocument: DocumentX,
            resolved: out _,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.True(condition: resolver.TryGetActive(
            destinationName: "home",
            durability: WorldDestinationDurability.Ephemeral,
            referencedDocument: DocumentY,
            resolved: out var stillActiveY,
            scopeKey: WorldSessionResolver.GlobalScopeKey
        ));
        Assert.Equal(
            expected: fromY.GenerationId,
            actual: stillActiveY.GenerationId
        );
    }
    [Fact]
    public void UserScope_AnonymousSeatRefused_IdentifiedSeatResolves() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = UserDestination();

        Laws.RefusalWithControl(
            lawId: "resolver.user-scope-anonymous-refused",
            deniedOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((1, null)),
                resolved: out _,
                reason: out _
            ),
            controlOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort((1, "amber-identity")),
                resolved: out _,
                reason: out _
            )
        );
    }
    [Fact]
    public void UserScope_MultiUserCohortRefused_SingleIdentityResolves() {
        var resolver = new WorldSessionResolver();
        var definition = Fixtures.BuildDocument();
        var destination = UserDestination(name: "workshop2");

        Laws.RefusalWithControl(
            lawId: "resolver.user-scope-cohort-mismatch-refused",
            deniedOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort(
                    (1, "amber-identity"),
                    (2, "ember-identity")
                ),
                resolved: out _,
                reason: out _
            ),
            controlOutcome: () => resolver.TryResolve(
                sourceDefinition: definition,
                destination: destination,
                referencedDocument: RefDoc,
                cohort: Cohort(
                    (1, "amber-identity"),
                    (3, "amber-identity")
                ),
                resolved: out _,
                reason: out _
            )
        );
    }
}
