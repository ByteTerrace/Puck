using Puck.Physics.Fields;

namespace Puck.Physics.Tests;

/// <summary>Pins the one read/write hazard door both field-program schedulers order their nodes by: each of the six
/// clauses fires on its own, a shared read is not a hazard, and fully disjoint sets carry no edge. The field and
/// state handle types are deliberately distinct here so a clause cannot be satisfied by a value crossing between
/// the field sets and the state sets.</summary>
public sealed class ReadWriteHazardLawTests {
    private static bool Conflicts(
        int[]? earlierFieldReads = null,
        int[]? earlierFieldWrites = null,
        long[]? earlierStateReads = null,
        long[]? earlierStateWrites = null,
        int[]? laterFieldReads = null,
        int[]? laterFieldWrites = null,
        long[]? laterStateReads = null,
        long[]? laterStateWrites = null
    ) => ReadWriteHazard.Conflicts<int, long>(
        earlierFieldReads: (earlierFieldReads ?? []),
        earlierFieldWrites: (earlierFieldWrites ?? []),
        earlierStateReads: (earlierStateReads ?? []),
        earlierStateWrites: (earlierStateWrites ?? []),
        laterFieldReads: (laterFieldReads ?? []),
        laterFieldWrites: (laterFieldWrites ?? []),
        laterStateReads: (laterStateReads ?? []),
        laterStateWrites: (laterStateWrites ?? [])
    );

    [Fact]
    public void AFieldReadAfterAnEarlierFieldWriteIsAHazard() {
        Assert.True(condition: Conflicts(
            earlierFieldWrites: [7],
            laterFieldReads: [7]
        ));
    }
    [Fact]
    public void AFieldWriteAfterAnEarlierFieldWriteIsAHazard() {
        Assert.True(condition: Conflicts(
            earlierFieldWrites: [7],
            laterFieldWrites: [7]
        ));
    }
    [Fact]
    public void AFieldWriteAfterAnEarlierFieldReadIsAHazard() {
        Assert.True(condition: Conflicts(
            earlierFieldReads: [7],
            laterFieldWrites: [7]
        ));
    }
    [Fact]
    public void AStateReadAfterAnEarlierStateWriteIsAHazard() {
        Assert.True(condition: Conflicts(
            earlierStateWrites: [7L],
            laterStateReads: [7L]
        ));
    }
    [Fact]
    public void AStateWriteAfterAnEarlierStateWriteIsAHazard() {
        Assert.True(condition: Conflicts(
            earlierStateWrites: [7L],
            laterStateWrites: [7L]
        ));
    }
    [Fact]
    public void AStateWriteAfterAnEarlierStateReadIsAHazard() {
        Assert.True(condition: Conflicts(
            earlierStateReads: [7L],
            laterStateWrites: [7L]
        ));
    }
    [Fact]
    public void TwoNodesReadingTheSameFieldAndStateCarryNoHazard() {
        Assert.False(condition: Conflicts(
            earlierFieldReads: [7, 8],
            earlierStateReads: [7L, 8L],
            laterFieldReads: [7, 8],
            laterStateReads: [7L, 8L]
        ));
    }
    [Fact]
    public void FullyDisjointSetsCarryNoHazard() {
        Assert.False(condition: Conflicts(
            earlierFieldReads: [1, 2],
            earlierFieldWrites: [3],
            earlierStateReads: [1L, 2L],
            earlierStateWrites: [3L],
            laterFieldReads: [4],
            laterFieldWrites: [5, 6],
            laterStateReads: [4L],
            laterStateWrites: [5L, 6L]
        ));
    }
    [Fact]
    public void EmptySetsCarryNoHazard() {
        Assert.False(condition: Conflicts());
    }
}
