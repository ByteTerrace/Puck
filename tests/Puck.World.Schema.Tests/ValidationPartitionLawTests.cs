using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// THE LAW: document validation is one table of partitions (<see cref="WorldDefinitionValidator.Partitions"/>) that
/// owns every part of a document exactly once, and a change is judged only by the units it reaches
/// (<see cref="WorldDefinitionValidator.TryValidateChange"/>): a creation's palette edit is one unit, and a unit the
/// change does not reach, left invalid beside it, answers nothing to it, while whole-document validation still sees
/// that unit.
/// </summary>
public sealed class ValidationPartitionLawTests {
    private readonly WorldBootWork m_work = new();

    private static WorldPrototype Creation(string id, string color, float scale = 0.5f) => new(
        Document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: [new PaletteEntryDocument(Color: color, Emissive: null, Specular: null, Roughness: null, Glass: null)],
            Shapes: [new ShapeDocument(
                Id: 0,
                Name: "body",
                Type: SdfSolidPrimitive.Box,
                Position: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: new Vector3(x: scale, y: scale, z: scale),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            )],
            Frames: null
        ),
        Id: id
    );
    // A world whose second creation binds its colour to a state row the world does not declare, which only a
    // validation of that creation refuses.
    private static WorldDefinition WithBrokenNeighbour(WorldPrototype first) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        HostRaw: (WorldHostDefaults.Absent with { Authority = "127.0.0.1:7777", Height = 1, Listen = "127.0.0.1:7777", Width = 1 }),
        CreationsRaw: [first, Creation(id: "broken", color: "state.nowhere")]
    );

    private long Units() => m_work.Read(kind: WorldBootWork.Validations);

    [Fact]
    public void EveryPartOfADocumentHasExactlyOneOwningPartition() {
        var owned = WorldDocumentInputs.None;

        foreach (var partition in WorldDefinitionValidator.Partitions) {
            Assert.False(condition: owned.Overlaps(other: partition.Owns), userMessage: $"{partition} owns a part another partition owns");
            Assert.True(condition: (partition.Reads.Intersect(other: partition.Owns) == partition.Owns), userMessage: $"{partition} does not read what it owns");
            owned |= partition.Owns;
        }
        Assert.Equal(expected: WorldDocumentInputs.All, actual: owned);
    }
    [Fact]
    public void APaletteEditIsOneUnitAndAnUnreachedInvalidUnitAnswersNothing() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        var before = WithBrokenNeighbour(first: Creation(id: "lamp", color: "#D8D2C4"));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: before, reason: out var whole));
        Assert.Contains(expectedSubstring: "prototypes[1]", actualString: whole);

        var after = (before with { CreationsRaw = [Creation(id: "lamp", color: "#203040"), before.Creations[1]] });
        var start = Units();

        Assert.True(condition: WorldDefinitionValidator.TryValidateChange(after: after, before: before, compilation: out _, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: 1L, actual: (Units() - start));

        // The edited unit itself is still judged.
        var refused = (before with { CreationsRaw = [Creation(id: "lamp", color: "state.nowhere"), before.Creations[1]] });

        Assert.False(condition: WorldDefinitionValidator.TryValidateChange(after: refused, before: before, compilation: out _, reason: out var refusal));
        Assert.Contains(expectedSubstring: "prototypes[0]", actualString: refusal);
    }
    [Fact]
    public void AShapeEditReachesEveryPartitionThatReadsTheCreations() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        var before = WithBrokenNeighbour(first: Creation(id: "lamp", color: "#D8D2C4"));
        var after = (before with { CreationsRaw = [Creation(id: "lamp", color: "#D8D2C4", scale: 0.75f), before.Creations[1]] });
        var start = Units();

        Assert.True(condition: WorldDefinitionValidator.TryValidateChange(after: after, before: before, compilation: out _, reason: out var reason), userMessage: reason);

        // The edited creation, and every partition reading the creations whole; never the broken neighbour.
        var reached = WorldDefinitionValidator.Partitions.Where(predicate: static partition =>
            !partition.Owns.Contains(input: WorldDocumentInput.Prototypes) && partition.Reads.Contains(input: WorldDocumentInput.Prototypes));

        Assert.Equal(expected: (1L + reached.Sum(selector: partition => partition.UnitsIn(definition: after))), actual: (Units() - start));
    }
}
