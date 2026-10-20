using System.Text.Json;
using Puck.Commands;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Pins the two vocabularies a document's authored commands are judged against: a live registry, where a
/// binding row names the candidate document's own commands rather than the live world's, and the generated console
/// verb catalog, which refuses an engine-verb name, an operator or unbindable line and a verb no boot shape registers,
/// offline.</summary>
public sealed class AuthoredCommandVocabularyLawTests {
    private static AuthoredCommandSet Set(params (string Name, string Line)[] commands) => new(commands: commands.Select(selector: static command => {
        var errors = new List<string>();
        var compiled = AuthoredCommand.TryCreate(errors: errors, help: null, lines: [command.Line], name: command.Name, parameters: []);

        Assert.Empty(collection: errors);

        return compiled!;
    }));
    private static BindingProfileDocument Binding(string command) => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [],
        Chords: [new BindingChordDefinition(
            Group: "play",
            Chord: [],
            Page: new BindingPageDefinition(
                Id: "rest",
                Entries: [new BindingPageEntryDefinition(Sources: ["keyboard.f9"], Command: command)]
            )
        )]
    );

    [Fact]
    public void ABindingNamesTheCandidateDocumentsCommandsAgainstALiveRegistry() {
        var registry = new CommandRegistry(modules: [new Probe()]);
        var live = Set(("gx.live", "probe.go"));

        registry.UseAuthoredCommands(source: _ => live);

        List<string> Check(string command, AuthoredCommandSet? commands) {
            var errors = new List<string>();

            WorldAffordances.ValidateAgainst(
                channels: WorldChannelTable.Compile(channels: []),
                commands: commands,
                document: Binding(command: command),
                errors: errors,
                registry: registry,
                seatModes: []
            );

            return errors;
        }

        var candidate = Set(("gx.cand", "probe.go"));

        Assert.Empty(collection: Check(command: "gx.cand", commands: candidate));
        Assert.Empty(collection: Check(command: "gx.live", commands: null));
        Assert.Empty(collection: Check(command: "probe.go", commands: candidate));
        Assert.Contains(collection: Check(command: "gx.live", commands: candidate), filter: error => error.Contains(value: "names no registered command", comparisonType: StringComparison.Ordinal));

        var errors = new List<string>();

        WorldAffordances.ValidateCommandsAgainst(commands: Set(("probe.go", "probe.secret")), errors: errors, registry: registry);

        Assert.Contains(collection: errors, filter: error => error.Contains(value: "'probe.go' takes the name of the engine verb", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "answers the operator alone", comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void TheCatalogRefusesOfflineAClashAReachAndAVerbNoShapeRegisters() {
        var errors = new List<string>();

        WorldAffordances.ValidateCommandsAgainst(
            commands: Set(("body.pose", "body.where"), ("gx.leave", "quit"), ("gx.later", "presentation.only.verb")),
            errors: errors,
            registry: null
        );

        Assert.Equal(actual: errors.Count, expected: 3);
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "calls 'presentation.only.verb', which no registered verb", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "'body.pose' takes the name of the engine verb 'body.pose'", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "calls 'quit', which is not bindable", comparisonType: StringComparison.Ordinal));
        Assert.True(condition: (WorldConsoleVerbCatalog.Count > 0));
        Assert.Equal(actual: WorldConsoleVerbCatalog.Lookup(name: "BODY.POSE")?.Name, expected: "body.pose");
        Assert.Null(@object: WorldConsoleVerbCatalog.Lookup(name: "debug.go"));

        var rendered = WorldConsoleVerbCatalog.Render(verbs: [
            new CommandMetadata(Audience: CommandAudience.Operator, Bindability: CommandBindability.Unbindable, Name: "b.secret", Routing: CommandRouting.Immediate, ValueKind: CommandValueKind.Digital),
            new CommandMetadata(Name: "a.go", Bindability: CommandBindability.Bindable, Routing: CommandRouting.Immediate, ValueKind: CommandValueKind.Digital),
        ]);
        using var document = JsonDocument.Parse(json: rendered);
        var parsed = WorldConsoleVerbCatalog.Parse(json: document);

        Assert.Equal(actual: parsed["B.SECRET"].Audience, expected: CommandAudience.Operator);
        Assert.Equal(actual: parsed["a.go"].Bindability, expected: CommandBindability.Bindable);

        using var other = JsonDocument.Parse(json: "{\"schema\":\"other\",\"verbs\":[]}");

        Assert.Throws<InvalidDataException>(testCode: () => WorldConsoleVerbCatalog.Parse(json: other));
    }

    private sealed class Probe : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(bindability: CommandBindability.Bindable, description: "Bindable.", handler: static (_, _) => CommandResult.None, name: "probe.go");
            yield return CommandDefinition.WithWireArgs(audience: CommandAudience.Operator, bindability: CommandBindability.Bindable, description: "Operator.", handler: static (_, _) => CommandResult.None, name: "probe.secret");
        }
    }
}
