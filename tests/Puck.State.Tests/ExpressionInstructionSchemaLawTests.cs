using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.State.Tests;

/// <summary>The generated schema of one expression instruction names the members its operation carries, so a
/// mis-cased or foreign member is refused by the schema and by the converter alike rather than by the converter
/// alone.</summary>
public class ExpressionInstructionSchemaLawTests {
    private static JsonObject ArmFor(ExpressionOp operation) {
        var arms = Assert.IsType<JsonArray>(@object: InstructionSchema()["anyOf"]);

        foreach (var candidate in arms) {
            var arm = Assert.IsType<JsonObject>(@object: candidate);
            var operations = Assert.IsType<JsonArray>(@object: arm["properties"]!["op"]!["enum"]);

            if (operations.Any(predicate: name => (name!.GetValue<string>() == operation.ToString()))) {
                return arm;
            }
        }
        throw new InvalidOperationException(message: $"no schema arm admits operation '{operation}'");
    }
    private static JsonObject InstructionSchema() {
        var schema = new ExpressionProgramJsonConverter().BuildSchema(exportType: static _ => new JsonObject());

        return Assert.IsType<JsonObject>(@object: schema["properties"]!["instructions"]!["items"]);
    }
    private static string[] MemberNames(JsonObject arm) => [.. Assert
        .IsType<JsonObject>(@object: arm["properties"])
        .Select(selector: static member => member.Key)
        .Where(predicate: static name => (name != "op"))
        .Order(comparer: StringComparer.Ordinal)
    ];

    // The schema closes a vector operand and a subprogram with additionalProperties false, and the converter refuses
    // the same documents: a member neither names is refused by both, never loaded by one and failed by the other.
    [Theory]
    [InlineData("""{"instructions":[{"op":"Dot","left":{"$type":"cell","name":"a"},"right":{"$type":"literal","value":"1","bogus":"x"}}]}""", "bogus")]
    [InlineData("""{"instructions":[{"op":"Dot","left":{"$type":"cell","name":"a","value":"1"},"right":{"$type":"cell","name":"b"}}]}""", "value")]
    [InlineData("""{"instructions":[{"op":"Dot","left":{"$type":"embed","text":"a","name":"x"},"right":{"$type":"cell","name":"b"}}]}""", "name")]
    [InlineData("""{"instructions":[],"subprograms":[{"name":"f","arity":1,"instructions":[],"extra":1}]}""", "extra")]
    public void TheConverterRefusesAMemberTheClosedSchemaArmsRefuse(string json, string member) {
        var refused = Assert.Throws<JsonException>(testCode: () => ExpressionProgramJsonConverter.FromNode(node: JsonNode.Parse(json: json)));

        Assert.Contains(
            actualString: refused.Message,
            expectedSubstring: $"'{member}'"
        );
    }
    [Fact]
    public void AVectorOperandAndASubprogramCarryingOnlyTheirOwnMembersLoad() {
        var program = ExpressionProgramJsonConverter.FromNode(node: JsonNode.Parse(json: """{"instructions":[{"op":"Dot","left":{"$type":"cell","name":"a","key":"k"},"right":{"$type":"embed","text":"t","space":"lore"}}],"subprograms":[{"name":"f","arity":1,"instructions":[]}]}"""));

        Assert.Single(collection: program.Instructions);
        Assert.Single(collection: program.Subprograms);
    }
    [Fact]
    public void EveryOperationIsAdmittedByExactlyOneArmNamingItsOwnMembers() {
        foreach (var operation in Enum.GetValues<ExpressionOp>()) {
            var arm = ArmFor(operation: operation);
            var expected = ExpressionProgramJsonConverter
                .PayloadMembers(shape: ExpressionOperators.PayloadOf(operation: operation))
                .Order(comparer: StringComparer.Ordinal);

            Assert.Equal(
                expected,
                MemberNames(arm: arm)
            );
            Assert.False(condition: arm["additionalProperties"]!.GetValue<bool>());
        }
    }
    [Fact]
    public void AForeignMemberIsAdmittedByNoArm() {
        var arms = Assert.IsType<JsonArray>(@object: InstructionSchema()["anyOf"]);

        Assert.NotEmpty(collection: arms);
        foreach (var candidate in arms) {
            var arm = Assert.IsType<JsonObject>(@object: candidate);

            Assert.DoesNotContain(
                collection: MemberNames(arm: arm),
                filter: static name => (name == "row")
            );
            Assert.False(condition: arm["additionalProperties"]!.GetValue<bool>());
        }
    }
    [Fact]
    public void AMisCasedMemberIsNamedByNoArmAndRefusedByTheConverter() {
        var arm = ArmFor(operation: ExpressionOp.Operand);

        Assert.Contains(
            collection: MemberNames(arm: arm),
            filter: static name => (name == "name")
        );
        Assert.DoesNotContain(
            collection: MemberNames(arm: arm),
            filter: static name => (name == "Name")
        );
        Assert.Throws<JsonException>(testCode: static () => ExpressionProgramJsonConverter.FromNode(node: JsonNode.Parse("""
            { "instructions": [ { "op": "Operand", "Name": "hp" } ] }
            """)));
    }
    [Fact]
    public void AnArgumentIndexIsAnIntegerAndABoardIndexIsADirectionName() {
        Assert.Equal(
            "integer",
            ArmFor(operation: ExpressionOp.Argument)["properties"]!["index"]!["type"]!.GetValue<string>()
        );
        Assert.Equal(
            "string",
            ArmFor(operation: ExpressionOp.BoardShift)["properties"]!["index"]!["type"]!.GetValue<string>()
        );
    }
    [Fact]
    public void ASubprogramCarriesItsOwnInstructionArmsAndNothingElse() {
        var schema = new ExpressionProgramJsonConverter().BuildSchema(exportType: static _ => new JsonObject());
        var subprogram = Assert.IsType<JsonObject>(@object: schema["properties"]!["subprograms"]!["items"]);

        Assert.False(condition: subprogram["additionalProperties"]!.GetValue<bool>());
        Assert.NotEmpty(collection: Assert.IsType<JsonArray>(@object: subprogram["properties"]!["instructions"]!["items"]!["anyOf"]));
    }
}
