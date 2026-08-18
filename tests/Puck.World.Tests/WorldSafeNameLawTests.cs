using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// H9 — <see cref="WorldSafeName"/> previously carried no length ceiling at all: three individually-valid names could
/// still COMPOSE (<c>WorldSessionResolver.MintInstanceName</c>'s length-prefixed <c>ScopedSegment</c> chain)
/// into a string too long for the filesystem, discovered only when <c>WorldInstanceHost.TryStart</c> finally tried
/// to create the directory — a live boot-time fault for what should have been a construction-time refusal at the
/// earliest door a candidate string crosses. This suite proves the direct boundary on
/// <see cref="WorldSafeName.TryParse"/> itself, then proves the SAME check now gives the resolver's own composed
/// name a REAL refusal where its own remarks used to call the case impossible (adversarial-review H9,
/// <see cref="WorldSessionResolver.TryResolve"/>'s defensive re-check).
/// </summary>
public sealed class WorldSafeNameLawTests {
    [Fact]
    public void TryParse_PastMaxLength_RefusesByName_AtMaxLengthParsesClean() {
        var atLimit = new string(c: 'a', count: WorldSafeName.MaxLength);
        var pastLimit = new string(c: 'a', count: (WorldSafeName.MaxLength + 1));

        Assert.False(condition: WorldSafeName.TryParse(candidate: pastLimit, name: out _, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: $"{WorldSafeName.MaxLength}-character limit");

        Assert.True(condition: WorldSafeName.TryParse(candidate: atLimit, name: out var parsed, reason: out var atLimitReason), userMessage: atLimitReason);
        Assert.Equal(expected: atLimit, actual: parsed.Value);
    }
    [Fact]
    public void TryParse_PastMaxLength_RefusesByName_ControlAtMaxLengthParsesClean() {
        Laws.RefusalWithControl(
            lawId: "world-safe-name.max-length",
            deniedOutcome: static () => WorldSafeName.TryParse(candidate: new string(c: 'z', count: (WorldSafeName.MaxLength + 1)), name: out _, reason: out _),
            controlOutcome: static () => WorldSafeName.TryParse(candidate: new string(c: 'z', count: WorldSafeName.MaxLength), name: out _, reason: out _));
    }
    // The "impossible case" made real: MintInstanceName's own remarks call the WorldSafeName.TryParse re-check
    // "defensive, not load-bearing ... this can never actually fire", because every component it composes from was
    // ALREADY WorldSafeName-typed and WorldSafeName had no length rule to violate. Two long-but-individually-valid
    // components (a destination name and a group id, each far under MaxLength alone) now compose, through the
    // length-prefixed ScopedSegment chain, to something PAST MaxLength — a real, reachable refusal rather than a
    // defensive no-op. PERSISTED durability (no trailing generation-ordinal segment) keeps the composed length a
    // pure function of the two authored components, so the arithmetic above is exact.
    [Fact]
    public void TryResolve_ComposedNameExceedsMaxLength_RefusesByName() {
        var resolver = new WorldSessionResolver();
        var longName = new string(c: 'd', count: 150);
        var longGroupId = new string(c: 'g', count: 150);
        var kind = new WorldGroupKind(Name: "party", Roles: [], OwnershipPolicy: WorldGroupOwnershipPolicy.None, Lifetime: WorldGroupLifetime.Persistent, EvictionPolicy: WorldGroupEvictionPolicy.Remove, Capacity: 8);
        var groups = new WorldGroup[] {
            new(Id: WorldSafeName.Parse(candidate: longGroupId), KindName: "party", Members: [WorldPrincipal.Seat(slot: 1)]),
        };
        var definition = Fixtures.BuildDocument() with { Groups = new WorldGroupsSection(Groups: groups, Kinds: [kind], Ownership: []) };
        var destination = new WorldDestination(
            Name: WorldSafeName.Parse(candidate: longName),
            Reference: "ref",
            Durability: WorldDestinationDurability.Persisted,
            Scope: WorldDestinationScope.Group,
            Selector: new WorldGroupSelector.Named(Group: longGroupId)
        );
        var cohort = new[] { new WorldSessionResolver.CohortMember(Principal: WorldPrincipal.Seat(slot: 1), IdentityId: null) };

        var resolved = resolver.TryResolve(cohort: cohort, destination: destination, reason: out var reason, referencedDocument: "worlds/fixture.world.json", resolved: out _, sourceDefinition: definition);

        Assert.False(condition: resolved);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "is not a safe name");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "character limit");
    }
    [Fact]
    public void TryResolve_ComposedNameExceedsMaxLength_RefusesByName_ControlWithShortNamesResolves() {
        var kind = new WorldGroupKind(Name: "party", Roles: [], OwnershipPolicy: WorldGroupOwnershipPolicy.None, Lifetime: WorldGroupLifetime.Persistent, EvictionPolicy: WorldGroupEvictionPolicy.Remove, Capacity: 8);

        bool Resolve(string name, string groupId) {
            var resolver = new WorldSessionResolver();
            var groups = new WorldGroup[] {
                new(Id: WorldSafeName.Parse(candidate: groupId), KindName: "party", Members: [WorldPrincipal.Seat(slot: 1)]),
            };
            var definition = Fixtures.BuildDocument() with { Groups = new WorldGroupsSection(Groups: groups, Kinds: [kind], Ownership: []) };
            var destination = new WorldDestination(
                Name: WorldSafeName.Parse(candidate: name),
                Reference: "ref",
                Durability: WorldDestinationDurability.Persisted,
                Scope: WorldDestinationScope.Group,
                Selector: new WorldGroupSelector.Named(Group: groupId)
            );
            var cohort = new[] { new WorldSessionResolver.CohortMember(Principal: WorldPrincipal.Seat(slot: 1), IdentityId: null) };

            return resolver.TryResolve(cohort: cohort, destination: destination, reason: out _, referencedDocument: "worlds/fixture.world.json", resolved: out _, sourceDefinition: definition);
        }

        Laws.RefusalWithControl(
            lawId: "resolver.composed-instance-name-too-long-refused",
            deniedOutcome: () => Resolve(name: new string(c: 'd', count: 150), groupId: new string(c: 'g', count: 150)),
            controlOutcome: () => Resolve(groupId: "alpha", name: "hall"));
    }
}
