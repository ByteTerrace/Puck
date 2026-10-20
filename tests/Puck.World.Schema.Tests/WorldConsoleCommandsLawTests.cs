using System.Numerics;
using Puck.Commands;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Pins how a document's <c>commands</c> rows compile: each parameter kind admits and canonicalizes exactly
/// what it declares, and every malformed row is refused by row rather than dropped.</summary>
public sealed class WorldConsoleCommandsLawTests {
    private static WorldDefinition Document(params WorldConsoleCommand?[] commands) => new(
        CommandsRaw: commands!,
        SpawnPointsRaw: [new WorldSpawnPoint(Id: "chess", Position: Vector3.Zero)]
    );

    [Fact]
    public void EachParameterKindAdmitsWhatItDeclares() {
        var errors = new List<string>();
        var set = WorldConsoleCommands.Compile(
            definition: Document(new WorldConsoleCommand(
                Lines: ["probe {place} {mode} {body} {n} {i} {t}"],
                Name: "gx.all",
                Parameters: [
                    new WorldConsoleCommandParameter(Name: "place", Kind: WorldConsoleCommandParameterKind.Spawn),
                    new WorldConsoleCommandParameter(Name: "mode", Kind: WorldConsoleCommandParameterKind.Choice, Values: ["Fast", "slow"]),
                    new WorldConsoleCommandParameter(Name: "body", Kind: WorldConsoleCommandParameterKind.Body, Default: "0"),
                    new WorldConsoleCommandParameter(Name: "n", Kind: WorldConsoleCommandParameterKind.Number, Default: "1.5"),
                    new WorldConsoleCommandParameter(Name: "i", Kind: WorldConsoleCommandParameterKind.Integer, Default: "-3"),
                    new WorldConsoleCommandParameter(Name: "t", Default: "word"),
                ]
            )),
            errors: errors
        );

        Assert.Empty(collection: errors);
        Assert.True(condition: set.TryGet(command: out var command, name: "gx.all"));
        Assert.True(condition: command!.TryExpand(arguments: ["CHESS", "fast"], lines: out var lines, reason: out _));
        Assert.Equal(actual: lines, expected: ["probe chess Fast 0 1.5 -3 word"]);

        foreach (var (arguments, expected) in new (string[] Arguments, string Expected)[] {
            (["nowhere", "fast"], "is no spawn point (admitted: chess)"),
            (["chess", "medium"], "is no choice (admitted: Fast, slow)"),
            (["chess", "slow", "4096"], "is not a body index (0..4095)"),
            (["chess", "slow", "1", "NaN"], "is not a number"),
            (["chess", "slow", "1", "2", "2.5"], "is not an integer"),
        }) {
            Assert.False(condition: command.TryExpand(arguments: arguments, lines: out _, reason: out var reason));
            Assert.Contains(actualString: reason, expectedSubstring: expected);
        }

        Assert.Same(expected: WorldConsoleCommands.For(definition: Document()), actual: AuthoredCommandSet.Empty);
    }
    [Fact]
    public void AMalformedRowIsRefusedByRow() {
        var errors = new List<string>();
        var set = WorldConsoleCommands.Compile(
            definition: Document(
                null,
                new WorldConsoleCommand(Name: "gx.nullparam", Parameters: [null!], Lines: ["probe"]),
                new WorldConsoleCommand(Name: "gx.novalues", Parameters: [new WorldConsoleCommandParameter(Name: "a", Kind: WorldConsoleCommandParameterKind.Choice)], Lines: ["probe {a}"]),
                new WorldConsoleCommand(Name: "gx.values", Parameters: [new WorldConsoleCommandParameter(Name: "a", Values: ["x"])], Lines: ["probe {a}"]),
                new WorldConsoleCommand(Name: "gx.badvalue", Parameters: [new WorldConsoleCommandParameter(Name: "a", Kind: WorldConsoleCommandParameterKind.Choice, Values: ["{x}"])], Lines: ["probe {a}"]),
                new WorldConsoleCommand(Name: "gx.kind", Parameters: [new WorldConsoleCommandParameter(Name: "a", Kind: (WorldConsoleCommandParameterKind)99)], Lines: ["probe {a}"]),
                new WorldConsoleCommand(Name: "gx.nolines"),
                new WorldConsoleCommand(Name: "gx.twice", Lines: ["probe"]),
                new WorldConsoleCommand(Name: "GX.TWICE", Lines: ["probe"])
            ),
            errors: errors
        );

        foreach (var expected in new[] {
            "commands[0] is required.",
            "commands[1] ('gx.nullparam') carries a null parameter.",
            "parameter 'a' is a Choice and lists no values.",
            "parameter 'a' lists values, which only a Choice parameter takes.",
            "parameter 'a' lists '{x}', which is not one plain token.",
            "parameter 'a' declares no known kind.",
            "commands[6]: command 'gx.nolines' runs no lines",
            "commands[8]: command 'GX.TWICE' is declared more than once",
        }) {
            Assert.Contains(collection: errors, filter: error => error.Contains(value: expected, comparisonType: StringComparison.Ordinal));
        }

        Assert.Equal(actual: set.Commands.Select(selector: static command => command.Name), expected: ["gx.twice"]);
    }
}
