using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins <see cref="CommandForms"/> as the one door of a grammar refusal: its echo of the offending token, the
/// form it names when a verb has several, the registration check that keeps a description and its forms to one
/// spelling, and <see cref="CommandResult.Error"/> staying a value refusal whatever its text says.</summary>
public sealed class CommandUsageRefusalTests {
    [Fact]
    public void RefusalNamesTheVerbAndTheDeclaredForm() {
        var result = CommandForms.Of("x.y", "<a> [b]").Refuse();

        Assert.True(condition: result.IsError);
        Assert.True(condition: result.IsGrammar);
        Assert.Equal(actual: result.Output, expected: "[x.y: expected <a> [b]]");
    }

    [Fact]
    public void RefusalEchoesTheTokenItCouldNotRead() {
        Assert.Equal(actual: CommandForms.Of("wire.ack", "[on|quiet]").Refuse(got: "loud").Output, expected: "[wire.ack: unrecognized 'loud' — expected [on|quiet]]");
    }

    [Fact]
    public void TheWireVerbsEchoTheBadWord() {
        var registry = new CommandRegistry(modules: []);

        Assert.Equal(actual: registry.Submit(line: "wire.ack loud").Output, expected: "[wire.ack: unrecognized 'loud' — expected [on|quiet]]");
        Assert.Equal(actual: registry.Submit(line: "wire.errors now").Output, expected: "[wire.errors: unrecognized 'now' — expected [reset]]");
        Assert.True(condition: registry.Submit(line: "wire.ack loud").IsGrammar);
    }

    [Fact]
    public void ANoArgumentVerbRefusesAnArgumentAsGrammar() {
        var registry = new CommandRegistry(modules: [new BareModule()]);
        var result = registry.Submit(line: "x.bare extra");

        Assert.Equal(actual: result.Output, expected: "[x.bare: unrecognized 'extra' — expected no arguments]");
        Assert.True(condition: result.IsGrammar);
    }

    [Theory]
    [InlineData("hold", "[world.timeline: expected hold|run <clock>]")]
    [InlineData("run", "[world.timeline: expected hold|run <clock>]")]
    [InlineData("at c", "[world.timeline: expected at <clock> <engine-tick>]")]
    [InlineData("rate c x y", "[world.timeline: expected rate <clock> <rate>]")]
    [InlineData("c d e", "[world.timeline: expected hold|run <clock> | at <clock> <engine-tick> | rate <clock> <rate>]")]
    public void ARefusalNamesTheFormNearestWhatWasTyped(string typed, string expected) {
        var forms = CommandForms.Of("world.timeline", "hold|run <clock>", "at <clock> <engine-tick>", "rate <clock> <rate>");
        var registry = new CommandRegistry(modules: [new Module(forms: forms)]);

        Assert.Equal(actual: registry.Submit(line: $"world.timeline {typed}").Output, expected: expected);
    }

    [Fact]
    public void TheFormsSpellOnceForTheDescriptionAndTheRefusal() {
        var forms = CommandForms.Of("x.y", "<a>", "<a> <b>");

        Assert.Equal(actual: forms.Spelling, expected: "x.y <a> | <a> <b>");
        Assert.Equal(actual: forms.Refuse().Output, expected: "[x.y: expected <a> | <a> <b>]");
    }

    [Fact]
    public void ADescriptionWithoutItsFormsIsRefusedAtRegistration() {
        var forms = CommandForms.Of("x.y", "<a>");

        _ = Assert.Throws<ArgumentException>(testCode: () => Define(description: "Does a thing: x.y <b>.", forms: forms, name: "x.y"));
        _ = Assert.Throws<ArgumentException>(testCode: () => Define(description: $"Does a thing: {forms.Spelling}.", forms: forms, name: "x.z"));
        Assert.Equal(actual: Define(description: $"Does a thing: {forms.Spelling}.", forms: forms, name: "x.y").Forms, expected: forms);
    }

    [Fact]
    public void ADeclaredVerbAnswersItsFormsAsUsage() {
        var forms = CommandForms.Of("x.y", "<a> [b]", "on|off");
        var registry = new CommandRegistry(modules: [new Module(forms: forms)]);

        Assert.Equal(expected: ["<a> [b]", "on|off"], actual: registry.Usage(name: "x.y").Select(selector: static form => form.ToString()));
    }

    [Theory]
    [InlineData("[body.fly: expected 7 values]")]
    [InlineData("[world.wait: expected <ticks>]")]
    [InlineData("[world.target: 'x' is not a Hz value — expected is not special here]")]
    [InlineData("not bracketed: expected")]
    public void AValueRefusalIsNeverGrammarWhateverItsTextSays(string text) {
        var result = CommandResult.Error(output: text);

        Assert.Equal(actual: result.Output, expected: text);
        Assert.True(condition: result.IsError);
        Assert.False(condition: result.IsGrammar);
    }

    private static CommandDefinition Define(string description, CommandForms forms, string name) => CommandDefinition.WithWireArgs(
        audience: CommandAudience.Anyone,
        bindability: CommandBindability.Unbindable,
        description: description,
        forms: forms,
        handler: (_, args) => forms.Refuse(args: in args),
        name: name
    );

    private sealed class Module(CommandForms forms) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return Define(description: $"Does a thing: {forms.Spelling}.", forms: forms, name: forms.Verb);
        }
    }

    private sealed class BareModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                audience: CommandAudience.Anyone,
                bindability: CommandBindability.Unbindable,
                description: "Does nothing: x.bare.",
                handler: (_, args) => (CommandResult.RequireNoArguments(args: in args, verb: "x.bare") ?? CommandResult.None),
                name: "x.bare"
            );
        }
    }
}
