using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: the catalog compiles a section's lanes, families, enums, and host-owned marks.
/// A descriptor's role is independent of its kind; a lane descriptor covers exactly its own declarations; a family
/// resolves to the contiguous ordinal range of its member rows and refuses every other arrangement by name; a row
/// naming an enum is admitted only on an <see cref="CellKind.Int"/> kind and only against a declared enum; and a
/// host-owned row is admitted only for the two shapes a host can serve.</summary>
public sealed class StateCatalogModelLawTests {
    private static CellName Name(string candidate) => CellName.Parse(candidate: candidate);
    private static StateRow Row(string name, CellKind kind = CellKind.Int, CellName? symbols = null, bool hostOwned = false, StateDomain? domain = null) => new(
        Name: Name(candidate: name),
        Kind: kind,
        Domain: domain,
        Enum: symbols
    ) { HostOwned = hostOwned };

    [Fact]
    public void ALaneDescriptorCoversExactlyItsOwnDeclarations() {
        var catalog = StateCatalog.Compile(section: new StateSection(
            Rows: [Row(name: "score"), Row(name: "round")],
            ParticipantSlots: [new StateSlot(
                    Name: "jumps",
                    Role: StateParticipantRole.Counter
                )],
            IdentitySlots: [new StateSlot(
                    Name: "cooldown",
                    Role: StateParticipantRole.Timer
                )]
        ));

        Assert.Equal(
            expected: new StateLaneDescriptor(
                Count: 2,
                FirstOrdinal: 0,
                Lane: StateLane.Document
            ),
            actual: catalog.Lane(lane: StateLane.Document)
        );
        Assert.Equal(
            expected: new StateLaneDescriptor(
                Count: 1,
                FirstOrdinal: 2,
                Lane: StateLane.Participant
            ),
            actual: catalog.Lane(lane: StateLane.Participant)
        );
        Assert.Equal(
            expected: new StateLaneDescriptor(
                Count: 1,
                FirstOrdinal: 3,
                Lane: StateLane.Identity
            ),
            actual: catalog.Lane(lane: StateLane.Identity)
        );
    }
    [Fact]
    public void AnEmptyLaneReportsNoFirstOrdinal() {
        var catalog = StateCatalog.Compile(section: new StateSection(Rows: [Row(name: "score")]));
        var lane = catalog.Lane(lane: StateLane.Identity);

        Assert.True(condition: lane.IsEmpty);
        Assert.Equal(
            expected: -1,
            actual: lane.FirstOrdinal
        );
    }
    [Fact]
    public void ARolesKindIsTheLanesAndNeverTheOtherWayAround() {
        var catalog = StateCatalog.Compile(section: new StateSection(
            Rows: [
                Row(
                    kind: CellKind.Fixed,
                    name: "heat"
                ),
                Row(
                    kind: CellKind.Int,
                    name: "round"
                ),
            ],
            ParticipantSlots: [
                new StateSlot(
                    Name: "jumps",
                    Role: StateParticipantRole.Counter
                ),
                new StateSlot(
                    Name: "dash",
                    Role: StateParticipantRole.Timer
                ),
            ]
        ));

        // Two document rows stored in the kinds the two roles use, both carrying no role at all.
        Assert.Equal(
            expected: StateParticipantRole.None,
            actual: catalog.Descriptors[0].Role
        );
        Assert.Equal(
            expected: CellKind.Fixed,
            actual: catalog.Descriptors[0].Kind
        );
        Assert.Equal(
            expected: StateParticipantRole.None,
            actual: catalog.Descriptors[1].Role
        );
        Assert.Equal(
            expected: CellKind.Int,
            actual: catalog.Descriptors[1].Kind
        );
        Assert.Equal(
            expected: (StateParticipantRole.Counter, CellKind.Fixed),
            actual: (catalog.Descriptors[2].Role, catalog.Descriptors[2].Kind)
        );
        Assert.Equal(
            expected: (StateParticipantRole.Timer, CellKind.Int),
            actual: (catalog.Descriptors[3].Role, catalog.Descriptors[3].Kind)
        );
    }
    [Fact]
    public void AFamilyResolvesToTheContiguousRangeOfItsMemberRows() {
        var section = new StateSection(
            Rows: [
                Row(name: "turn"),
                Row(name: "tableau0"),
                Row(name: "tableau1"),
                Row(name: "tableau2"),
            ],
            Families: [new StateFamily(
                    Name: Name(candidate: "tableau"),
                    Size: 3
                )]
        );
        var catalog = StateCatalog.Compile(section: section);

        Assert.True(condition: catalog.TryGetFamily(
            family: out var family,
            name: Name(candidate: "tableau")
        ));
        Assert.Equal(
            expected: 1,
            actual: family.FirstOrdinal
        );
        Assert.Equal(
            expected: 3,
            actual: family.Count
        );
        Assert.Equal(
            expected: 4,
            actual: family.EndOrdinal
        );
        Assert.True(condition: family.Contains(ordinal: 3));
        Assert.False(condition: family.Contains(ordinal: 0));
        Assert.False(condition: family.Contains(ordinal: 4));
        Assert.True(condition: family.TryGetOrdinal(
            index: 2,
            ordinal: out var last
        ));
        Assert.Equal(
            actual: last,
            expected: 3
        );
        Assert.False(condition: family.TryGetOrdinal(
            index: 3,
            ordinal: out var beyond
        ));
        Assert.Equal(
            actual: beyond,
            expected: -1
        );
        Assert.True(condition: catalog.MatchesShape(section: section));
    }
    [Fact]
    public void AFamilyWithAMissingMemberRefusesByName() {
        var exception = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(
            Rows: [Row(name: "tableau0"), Row(name: "tableau1")],
            Families: [new StateFamily(
                    Name: Name(candidate: "tableau"),
                    Size: 3
                )]
        )));

        Assert.Contains(
            expectedSubstring: "tableau2",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void AFamilyWhoseMembersAreNotContiguousRefusesByName() {
        var exception = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(
            Rows: [Row(name: "tableau0"), Row(name: "turn"), Row(name: "tableau1")],
            Families: [new StateFamily(
                    Name: Name(candidate: "tableau"),
                    Size: 2
                )]
        )));

        Assert.Contains(
            expectedSubstring: "contiguous",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void AFamilyOfMixedKindsRefusesByName() {
        var exception = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(
            Rows: [
                Row(name: "tableau0"),
                Row(
                    kind: CellKind.Bool,
                    name: "tableau1"
                ),
            ],
            Families: [new StateFamily(
                    Name: Name(candidate: "tableau"),
                    Size: 2
                )]
        )));

        Assert.Contains(
            expectedSubstring: "tableau1",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void ARowResolvesTheEnumItNames() {
        var suit = new StateEnum(
            Name: Name(candidate: "Suit"),
            Members: [Name(candidate: "Clubs"), Name(candidate: "Hearts")]
        );
        var catalog = StateCatalog.Compile(section: new StateSection(
            Rows: [Row(
                    name: "trump",
                    symbols: Name(candidate: "Suit")
                ), Row(name: "turn")],
            Enums: [suit]
        ));

        Assert.True(condition: catalog.TryResolve(
            handle: out var trump,
            lane: StateLane.Document,
            name: "trump"
        ));
        Assert.True(condition: catalog.TryGetEnum(
            handle: trump,
            symbols: out var resolved
        ));
        Assert.Equal(
            actual: resolved,
            expected: suit
        );
        Assert.True(condition: catalog.TryResolve(
            handle: out var turn,
            lane: StateLane.Document,
            name: "turn"
        ));
        Assert.False(condition: catalog.TryGetEnum(
            handle: turn,
            symbols: out var none
        ));
        Assert.Null(@object: none);
    }
    // A row's enum is whole-document validation's to refuse, once; the catalog draws a row from the enum it names
    // only when that is a declared enum on an Int row, and from no symbolic domain otherwise.
    [Fact]
    public void ARowNamingAnEnumTheCatalogCannotDrawFromHasNoSymbolicDomain() {
        var catalog = StateCatalog.Compile(section: new StateSection(
            Rows: [
                Row(
                    name: "trump",
                    symbols: Name(candidate: "Missing")
                ),
                Row(
                    kind: CellKind.Text,
                    name: "label",
                    symbols: Name(candidate: "Suit")
                ),
            ],
            Enums: [new StateEnum(
                    Name: Name(candidate: "Suit"),
                    Members: [Name(candidate: "Clubs")]
                )]
        ));

        foreach (var name in ((string[])["trump", "label"])) {
            Assert.True(condition: catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: name
            ));
            Assert.False(condition: catalog.TryGetEnum(
                handle: handle,
                symbols: out _
            ));
        }
    }
    [Fact]
    public void AnEnumAdmitsItsOwnRangeAndRendersItsMembers() {
        var suit = new StateEnum(
            Name: Name(candidate: "Suit"),
            Members: [Name(candidate: "Clubs"), Name(candidate: "Hearts")]
        );

        Assert.True(condition: suit.TryValidate(reason: out _));
        Assert.Equal(
            expected: 2,
            actual: suit.Count
        );
        Assert.True(condition: suit.TryAdmit(
            reason: out _,
            value: 1L
        ));
        Assert.False(condition: suit.TryAdmit(
            reason: out var reason,
            value: 2L
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Suit"
        );
        Assert.False(condition: suit.TryAdmit(
            reason: out _,
            value: -1L
        ));
        Assert.True(condition: suit.TryGetMember(
            member: out var member,
            value: 1L
        ));
        Assert.Equal(
            expected: "Hearts",
            actual: member.Value
        );
        Assert.True(condition: suit.TryGetValue(
            member: Name(candidate: "Clubs"),
            value: out var value
        ));
        Assert.Equal(
            actual: value,
            expected: 0L
        );
    }
    [Fact]
    public void AnEnumWithNoMembersOrADuplicateMemberRefusesByName() {
        Assert.False(condition: new StateEnum(
            Name: Name(candidate: "Empty"),
            Members: []
        ).TryValidate(reason: out var empty));
        Assert.Contains(
            actualString: empty,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Empty"
        );
        Assert.False(condition: new StateEnum(
            Name: Name(candidate: "Twice"),
            Members: [Name(candidate: "a"), Name(candidate: "a")]
        ).TryValidate(reason: out var duplicate));
        Assert.Contains(
            actualString: duplicate,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "duplicate member 'a'"
        );
    }
    [Fact]
    public void TheWriteDoorRefusesAValueOutsideTheRowsEnum() {
        var suit = new StateEnum(
            Name: Name(candidate: "Suit"),
            Members: [Name(candidate: "Clubs"), Name(candidate: "Hearts")]
        );
        var row = Row(name: "trump");

        Assert.True(condition: row.TryAdmitWrite(
            current: 0L,
            operand: 1L,
            reason: out _,
            stored: out var stored,
            symbols: suit,
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            actual: stored,
            expected: 1L
        );
        Assert.False(condition: row.TryAdmitWrite(
            current: 0L,
            operand: 2L,
            reason: out var reason,
            stored: out var refused,
            symbols: suit,
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            actual: refused,
            expected: 0L
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Suit"
        );
        // The same write with no enum named is an ordinary admitted write.
        Assert.True(condition: row.TryAdmitWrite(
            current: 0L,
            operand: 2L,
            write: StateWriteKind.Set,
            stored: out _,
            reason: out _
        ));
    }
    [Fact]
    public void ASaturatedWriteOutsideTheEnumIsRefusedRatherThanStored() {
        var suit = new StateEnum(
            Name: Name(candidate: "Suit"),
            Members: [Name(candidate: "Clubs"), Name(candidate: "Hearts")]
        );
        var row = new StateRow(
            Name: Name(candidate: "trump"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 5L,
            Overflow: StateOverflow.Saturate
        );

        Assert.False(condition: row.TryAdmitWrite(
            current: 0L,
            operand: 9L,
            reason: out _,
            stored: out var stored,
            symbols: suit,
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            actual: stored,
            expected: 0L
        );
    }
    [Fact]
    public void AHostOwnedRowIsAdmittedOnlyForSlotAndLatticeShapes() {
        var catalog = StateCatalog.Compile(section: new StateSection(Rows: [
            Row(
                hostOwned: true,
                name: "gravity"
            ),
            Row(
                domain: new StateDomain.CellsOf(Topology: "ground"),
                hostOwned: true,
                kind: CellKind.Fixed,
                name: "heat"
            ),
        ]));

        Assert.True(condition: catalog.Descriptors[0].HostOwned);
        Assert.Equal(
            expected: RowShape.Slot,
            actual: catalog.Descriptors[0].Shape
        );
        Assert.True(condition: catalog.Descriptors[1].HostOwned);
        Assert.Equal(
            expected: RowShape.Lattice,
            actual: catalog.Descriptors[1].Shape
        );

        var exception = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: new StateSection(Rows: [
            new StateRow(
                Name: Name(candidate: "pile"),
                Kind: CellKind.Int,
                Domain: new StateDomain.Ring(Capacity: 4)
            ) { HostOwned = true },
        ])));

        Assert.Contains(
            expectedSubstring: "host-owned",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void AnAuthoredRowCannotClaimTheRuntimeGeneratedMark() {
        var section = new StateSection(Rows: [
            new StateRow(
                Name: Name(candidate: "authored"),
                Kind: CellKind.Int
            ),
            new StateRow(
                Name: Name(candidate: "lowered"),
                Kind: CellKind.Int
            ) { Generated = true },
        ]);

        var error = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: section));

        Assert.Contains(expectedSubstring: "cannot carry", actualString: error.Message);
    }
}
