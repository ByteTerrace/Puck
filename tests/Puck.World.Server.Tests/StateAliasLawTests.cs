using System.Text.Json;
using Puck.Networking;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// A state declaration owns its lists. An enum's members and a row's cells are copied as the record is built, so a
/// caller that keeps the collection it passed in cannot change the record afterwards, and every cache keyed by the
/// record (the identity projection wire's encoded records and its validated facts rows) stays true for the record's
/// lifetime. Each law mutates the alias after the record was first encoded and asserts the record, its encoding and
/// its validation are what they were. Red legs: a record that keeps the caller's collection reads the mutation, so
/// the cached encoding no longer matches the record and a facts row past its capacity still encodes.
/// </summary>
public sealed class StateAliasLawTests {
    private static byte[] Encode(WorldIdentityProjection projection) {
        var writer = new WireWriter();

        WorldIdentityProjectionWire.Write(
            projection: projection,
            writer: writer
        );

        return writer.WrittenSpan.ToArray();
    }
    private static WorldIdentityProjection Projection() {
        using var fixture = Fixtures.FreshServer();

        return fixture.Server.Profiles.BootProfile.Project();
    }

    [Fact]
    public void AnEnumsMembersAliasCannotChangeItsEncodedRecords() {
        var first = CellName.Parse(candidate: "first");
        var members = new[] { first, CellName.Parse(candidate: "second") };
        var records = new WorldStateSection(Enums: [new StateEnum(Name: CellName.Parse(candidate: "rank"), Members: members)]);
        var projection = (Projection() with { Records = records });
        var encoded = Encode(projection: projection);

        members[0] = CellName.Parse(candidate: "renamed");

        // The section reads what it was built with, and its encoding is what a fresh copy of it encodes.
        var fresh = JsonSerializer.Deserialize(
            jsonTypeInfo: WorldJsonContext.Default.WorldStateSection,
            utf8Json: JsonSerializer.SerializeToUtf8Bytes(jsonTypeInfo: WorldJsonContext.Default.WorldStateSection, value: records)
        );

        Assert.Equal(expected: first, actual: records.Enums![0].Members[0]);
        Assert.Equal(expected: encoded, actual: Encode(projection: (projection with { Records = fresh })));
        Assert.Equal(expected: encoded, actual: Encode(projection: projection));
    }
    [Fact]
    public void AFactsRowsCellsAliasCannotPushItPastItsCapacity() {
        var template = Projection();
        var cells = new List<StateCell> {
            new(Key: CellName.Parse(candidate: "score"), Value: CellValue.Int(value: 1L)),
        };
        var facts = new WorldStateRow(Name: template.Facts!.Name, Kind: CellKind.Int, Capacity: 1, Cells: cells);
        var projection = (template with { Facts = facts });
        var encoded = Encode(projection: projection);

        cells.Add(item: new StateCell(Key: CellName.Parse(candidate: "extra"), Value: CellValue.Int(value: 2L)));

        // The control: a row built from the grown list is refused.
        var refused = Assert.Throws<InvalidOperationException>(testCode: () => WorldIdentityFacts.Validate(row: (facts with { Cells = cells })));

        Assert.Contains(actualString: refused.Message, expectedSubstring: "past its capacity");
        Assert.Single(collection: facts.Cells!);
        Assert.Equal(expected: encoded, actual: Encode(projection: projection));
    }
}
