using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Maths;
using static Puck.Commands.CommandArgs;

namespace Puck.Demo;

/// <summary>
/// The console verbs for the persisted-replay seed of a future <c>Puck.Replay</c>: <c>replay.capture</c> drives a
/// deterministic scripted overworld walk through the SAME <see cref="RecordingSnapshotSource"/> the determinism
/// harness proves, persists it as a real <see cref="OverworldRecording"/> binary file, <c>replay.list</c> shows
/// what's saved, and <c>replay.verify</c> re-runs a saved file through a FRESH <see cref="OverworldWorld"/> and
/// echoes the resulting hash — "a saved moment can re-happen" over the pipe. Self-contained: every verb builds its
/// own throwaway world and touches no live session state, so this module needs no <c>IRenderNode</c> root (unlike
/// <see cref="Puck.Demo.GardenCommandModule"/>/<see cref="Puck.Demo.RtsCommandModule"/>, which reach the live
/// overworld) and works whether or not the overworld is the active root.
/// <para>
/// SCOPING (see <see cref="OverworldReplayCapture"/>'s remarks for the full account): this captures a SCRIPTED
/// window, not the live interactive session — the default demo's console-mode tick loop
/// (<c>OverworldRenderNode.AdvanceConsoleMode</c>) builds each tick's input directly from the pad/binding-page
/// services and never constructs a <see cref="CommandSnapshot"/>, so there is no live snapshot stream this module
/// could record without a materially larger, differently-shaped change. There is deliberately no
/// <c>replay.record</c>/<c>replay.save</c> pair here — splitting <c>replay.capture</c> into an arm/commit dance
/// would not add real live-input fidelity (the mechanism underneath is the same scripted walk either way), so a
/// single honestly-named verb beats two verbs that imply a capability that is not actually there.
/// </para>
/// </summary>
internal sealed class ReplayCommandModule : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: $"Captures a deterministic scripted overworld walk and persists it: replay.capture <name> [ticks] (default {OverworldReplayCapture.DefaultTicks} ticks, ~3s at the capture's own 240 Hz rate). A SCRIPTED window, not the live session — see OverworldReplayCapture's remarks.",
            handler: Capture,
            name: "replay.capture"
        );
        yield return Plain(
            description: "Lists every persisted replay by name.",
            handler: List,
            name: "replay.list"
        );
        yield return WithArgs(
            description: "Loads a saved replay and re-runs it through a fresh OverworldWorld, echoing the final state hash + tick count: replay.verify <name>.",
            handler: Verify,
            name: "replay.verify"
        );
    }

    private static CommandResult Capture(CommandContext context, string[] args) {
        if (args.Length == 0) {
            return new CommandResult("[replay.capture: usage — replay.capture <name> [ticks]]");
        }

        var name = args[0];

        if (!OverworldReplayStore.IsValidName(name: name)) {
            return new CommandResult("[replay.capture: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }

        var ticks = OverworldReplayCapture.DefaultTicks;

        if ((args.Length > 1) && !TryParseInt(text: args[1], value: out ticks)) {
            return new CommandResult("[replay.capture: usage — replay.capture <name> [ticks] (ticks must be an integer)]");
        }

        ticks = Math.Clamp(value: ticks, min: 1, max: 36000);

        var capture = OverworldReplayCapture.Capture(seed: DeriveSeed(name: name), ticks: ticks);
        var registry = OverworldReplayCapture.BuildRegistry(moveId: out _, jumpId: out _, interactId: out _, cycleId: out _);
        var path = OverworldReplayStore.Save(name: name, recording: capture.Recording, registry: registry);

        return new CommandResult($"[replay.capture: '{name}' -> {path} ({capture.TickCount} ticks, final hash=0x{capture.FinalHash:X16})]");
    }
    private static CommandResult List(CommandContext context) {
        var names = OverworldReplayStore.List();

        return new CommandResult(((names.Count == 0)
            ? "[replay.list: none saved — replay.capture <name> [ticks] records one]"
            : $"[replay.list: {string.Join(separator: ", ", values: names)}]"));
    }
    private static CommandResult Verify(CommandContext context, string[] args) {
        if (args.Length == 0) {
            return new CommandResult("[replay.verify: usage — replay.verify <name>]");
        }

        var name = args[0];

        if (!OverworldReplayStore.IsValidName(name: name)) {
            return new CommandResult("[replay.verify: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }

        var registry = OverworldReplayCapture.BuildRegistry(moveId: out _, jumpId: out _, interactId: out _, cycleId: out _);
        OverworldRecording? recording;

        try {
            recording = OverworldReplayStore.Load(name: name, registry: registry);
        } catch (Exception exception) when (IsMalformedInput(exception: exception)) {
            return new CommandResult($"[replay.verify: '{name}' is unreadable/corrupt — {exception.Message}]");
        }

        if (recording is not { } loaded) {
            return new CommandResult($"[replay.verify: no replay named '{name}' — replay.list shows what's saved]");
        }

        var result = OverworldReplayCapture.Verify(recording: loaded);

        return new CommandResult($"[replay.verify: '{name}' replayed {result.TickCount} tick(s) -> final hash=0x{result.FinalHash:X16}]");
    }

    // A deterministic capture seed derived from the replay's own name (never wall-clock/System.Random), so two
    // captures under different names produce distinct scripted players/trajectories while the SAME name always
    // captures the identical tape.
    private static uint DeriveSeed(string name) {
        var hash = Fnv1aHash.Create();

        foreach (var character in name) {
            hash.Add(value: (uint)character);
        }

        return (uint)hash.Value;
    }

    // A no-argument console verb (mirrors GardenCommandModule.Plain).
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    // An argument-taking console verb: one trailing token list, parsed by the handler (mirrors GardenCommandModule.WithArgs).
    private static CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Name: name,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }
}
