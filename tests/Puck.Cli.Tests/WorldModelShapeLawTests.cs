using Puck.Cli.Schema;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// The model shape the engine's model walks read (<see cref="WorldModelShape"/>) is the table the reflective walk
/// over <see cref="WorldJsonContext"/>'s description of the model produces: every type, in every field, with nothing
/// missing and nothing extra. The walk (<see cref="WorldModelShapeSource.Reflect"/>) is the oracle; the compiled table
/// is what a process reads instead of describing a type.
/// </summary>
public sealed class WorldModelShapeLawTests {
    private static string Describe(IReadOnlyList<WorldModelMember> members) =>
        string.Join(separator: "; ", values: members.Select(selector: static member => $"{member.Name}:{member.Type}@{member.DeclaringType}.{member.Member}[{member.Access}]"));
    private static string Describe(IReadOnlyList<WorldModelArm> arms) =>
        string.Join(separator: "; ", values: arms.Select(selector: static arm => $"{(arm.Discriminator ?? "<none>")}:{arm.Type}"));

    [Fact]
    public void TheCompiledShapeIsTheReflectiveDescriptionOfTheModel() {
        var expected = WorldModelShapeSource.Reflect();
        var problems = new List<string>();

        foreach (var reflected in expected) {
            if (WorldModelShape.Of(type: reflected.Type) is not { } compiled) {
                problems.Add(item: $"{reflected.Type}: missing");

                continue;
            }
            if (
                (compiled.Described != reflected.Described) ||
                (compiled.Kind != reflected.Kind) ||
                (compiled.ElementType != reflected.ElementType) ||
                !string.Equals(a: Describe(arms: compiled.Arms), b: Describe(arms: reflected.Arms), comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: Describe(members: compiled.Members), b: Describe(members: reflected.Members), comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: Describe(members: compiled.Properties), b: Describe(members: reflected.Properties), comparisonType: StringComparison.Ordinal)
            ) {
                problems.Add(item: $"{reflected.Type}: differs");
            }
        }

        var reflectedTypes = expected.Select(selector: static shape => shape.Type).ToHashSet();

        foreach (var compiled in WorldModelShape.Types) {
            if (!reflectedTypes.Contains(item: compiled.Type)) {
                problems.Add(item: $"{compiled.Type}: not in the model");
            }
        }

        Assert.True(
            condition: (problems.Count == 0),
            userMessage: $"{problems.Count} type(s) disagree with the model — regenerate with 'puck schema':\n{string.Join(separator: "\n", values: problems.Take(count: 20))}"
        );
    }
    [Fact]
    public void TheShapeReachesTheDocumentsArmsAndTheArmsTheVocabularyAdds() {
        var lattices = WorldModelShape.Of(type: typeof(Puck.State.LatticeTopology));

        Assert.NotNull(@object: lattices);
        Assert.Contains(collection: lattices.Arms, filter: static arm => ((arm.Discriminator == "field") && (arm.Type == typeof(WorldFieldTopology))));
        Assert.Contains(collection: WorldModelShape.Of(type: typeof(WorldDefinition))!.Members, filter: static member => ((member.Name == "rules") && ((member.Access & WorldModelAccess.Write) != 0)));
        Assert.Null(@object: WorldModelShape.Of(type: typeof(string)));
    }
}
