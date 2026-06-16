using System.CommandLine;
using Puck.Commands;
using Puck.Recursive.Scene;

namespace Puck.Recursive.Commands;

/// <summary>The recursive showcase's engine-specific command surface, shared by the keyboard and the stdin
/// text path. <c>pause</c> and the layout cycle back the in-window controls; <c>layout</c> and <c>scene</c>
/// are typed verbs that steer the SDF scene — the data-driven knobs a test script drives. <c>quit</c> is
/// the terminal's own verb (the launcher contributes it via the baton), so it is not defined here.</summary>
internal sealed class RecursiveCommandModule(
    SdfScene scene
) : ICommandModule {
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            description: "Freezes or resumes the animation. Bound to F1.",
            handler: _ => new CommandResult((scene.TogglePause()
                ? "paused"
                : "resumed")),
            name: "pause",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Transitions to the next split-screen layout. Bound to the right arrow.",
            handler: _ => new CommandResult($"layout: {scene.CycleLayout(direction: 1)}"),
            name: "layout.next",
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Transitions to the previous split-screen layout. Bound to the left arrow.",
            handler: _ => new CommandResult($"layout: {scene.CycleLayout(direction: -1)}"),
            name: "layout.prev",
            valueKind: CommandValueKind.Digital
        );

        var layoutName = new Argument<string>(name: "name") {
            Description = "single | split | quad | pip",
        };
        var layoutCommand = new Command(
            name: "layout",
            description: "Transitions to a split-screen layout. Usage: layout <single|split|quad|pip>."
        );

        layoutCommand.Arguments.Add(layoutName);
        yield return new CommandDefinition(
            Name: "layout",
            Description: "Transitions to a split-screen layout. Usage: layout <single|split|quad|pip>.",
            ValueKind: CommandValueKind.Digital,
            TextCommand: layoutCommand,
            Handler: context => {
                var name = (context.Parse?.GetValue(argument: layoutName) ?? "");

                return new CommandResult((scene.SetLayout(name: name)
                    ? $"layout: {scene.LayoutName}"
                    : $"unknown layout '{name}' (single | split | quad | pip)"));
            }
        );

        var sceneName = new Argument<string>(name: "name") {
            Description = "blobs | pillars",
        };
        var sceneCommand = new Command(
            name: "scene",
            description: "Switches the shared SDF scene. Usage: scene <blobs|pillars>."
        );

        sceneCommand.Arguments.Add(sceneName);
        yield return new CommandDefinition(
            Name: "scene",
            Description: "Switches the shared SDF scene. Usage: scene <blobs|pillars>.",
            ValueKind: CommandValueKind.Digital,
            TextCommand: sceneCommand,
            Handler: context => {
                var name = (context.Parse?.GetValue(argument: sceneName) ?? "");

                return new CommandResult((scene.SelectScene(name: name)
                    ? $"scene: {scene.SceneName}"
                    : $"unknown scene '{name}' (blobs | pillars)"));
            }
        );
    }
}
