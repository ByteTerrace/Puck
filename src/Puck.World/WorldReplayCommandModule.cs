using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The replay-tape console surface — <c>replay.record</c> / <c>replay.stop</c> / <c>replay.play</c> / <c>replay.list</c>
/// / <c>replay.status</c>, the live record/replay control plane over the pipe (the seed of a future <c>Puck.Replay</c>).
/// It arms the <see cref="WorldReplayTape"/> that taps World's real per-tick <see cref="CommandSnapshot"/> stream:
/// <c>replay.record</c> captures the running session, <c>replay.stop</c> persists it as a real
/// <see cref="SnapshotRecording"/> binary, and <c>replay.play</c> re-drives the session from a saved tape. Every verb is
/// Immediate (a client-local lever, no direct simulation effect) and echoes honestly — the persisted path, tick count,
/// and state hash — with loud declines. A SEPARATE module to keep each class under its analyzer ceilings.
/// </summary>
internal sealed class WorldReplayCommandModule(WorldReplayTape tape) : ICommandModule {
    private readonly WorldReplayTape m_tape = tape;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "replay.record",
            description: "Arms live recording (Immediate): replay.record <name> begins appending the running session's per-tick command snapshots; replay.stop persists them.",
            handler: (_, args) => Record(args: args)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "replay.stop",
            description: "Stops and persists the active recording (Immediate): writes <name>.puckreplay and echoes the path, tick count, and final state hash.",
            handler: (_, args) => Stop(args: args)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "replay.play",
            description: "Loads a saved replay and re-drives the running session from it (Immediate): replay.play <name> feeds the saved snapshots back one per tick until the tape runs out.",
            handler: (_, args) => Play(args: args)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "replay.list",
            description: "Lists every persisted replay by name (Immediate).",
            handler: (_, args) => ListReplays(args: args)
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "replay.status",
            description: "Reports the tape state (Immediate): idle/recording/replaying, the active name, ticks so far, and the latest state hash.",
            handler: (_, args) => Status(args: args)
        );
    }

    private CommandResult Record(string[] args) {
        if (args.Length != 1) {
            return Error(text: "[replay.record: usage — replay.record <name>]");
        }

        if (!WorldReplayTape.IsValidName(name: args[0])) {
            return Error(text: "[replay.record: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }

        if (m_tape.Mode != WorldReplayMode.Idle) {
            return Busy(verb: "replay.record");
        }

        m_tape.BeginRecording(name: args[0]);

        return new CommandResult(Output: $"[replay.record: recording '{args[0]}' — replay.stop persists it]");
    }

    private CommandResult Stop(string[] args) {
        if (args.Length > 0) {
            return Error(text: "[replay.stop: expected no arguments]");
        }

        if (m_tape.Mode != WorldReplayMode.Recording) {
            return Error(text: "[replay.stop: not recording]");
        }

        try {
            var (path, ticks, hash) = m_tape.StopRecording();

            return new CommandResult(Output: $"[replay.stop: wrote {path} | {ticks} ticks | final hash=0x{hash:X16}]");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return Error(text: $"[replay.stop: could not persist — {exception.Message}]");
        }
    }

    private CommandResult Play(string[] args) {
        if (args.Length != 1) {
            return Error(text: "[replay.play: usage — replay.play <name>]");
        }

        if (!WorldReplayTape.IsValidName(name: args[0])) {
            return Error(text: "[replay.play: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }

        if (m_tape.Mode != WorldReplayMode.Idle) {
            return Busy(verb: "replay.play");
        }

        try {
            var ticks = m_tape.BeginReplay(name: args[0]);

            return new CommandResult(Output: $"[replay.play: replaying '{args[0]}' — {ticks} ticks]");
        } catch (FileNotFoundException) {
            return Error(text: $"[replay.play: no replay named '{args[0]}' — replay.list shows what's saved]");
        } catch (Exception exception) when (exception is InvalidDataException or IOException) {
            return Error(text: $"[replay.play: '{args[0]}' is unreadable/corrupt — {exception.Message}]");
        }
    }

    private static CommandResult ListReplays(string[] args) {
        if (args.Length > 0) {
            return new CommandResult(Output: "[replay.list: expected no arguments]") { IsError = true };
        }

        var names = WorldReplayTape.List();

        return new CommandResult(Output: ((names.Count == 0)
            ? "[replay.list: none saved — replay.record <name> then replay.stop records one]"
            : $"[replay.list: {string.Join(separator: ", ", values: names)}]"));
    }

    private CommandResult Status(string[] args) {
        if (args.Length > 0) {
            return Error(text: "[replay.status: expected no arguments]");
        }

        var mode = m_tape.Mode.ToString().ToLowerInvariant();

        return new CommandResult(Output: (m_tape.Mode == WorldReplayMode.Idle)
            ? "[replay.status: idle]"
            : $"[replay.status: {mode} '{m_tape.Name}' | {m_tape.TickCount} ticks | last hash=0x{m_tape.LastHash:X16}]");
    }

    private CommandResult Busy(string verb) {
        // The remedy is mode-accurate: replay.stop ends a recording, but nothing stops an in-progress replay — it runs
        // to the end of its saved ticks and auto-idles — so a busy-because-replaying decline must not advise stopping it.
        var remedy = (m_tape.Mode == WorldReplayMode.Recording)
            ? "replay.stop persists it first"
            : "let it run out first (a replay has no stop — it auto-ends when the tape runs out)";

        return Error(text: $"[{verb}: busy — tape is {m_tape.Mode.ToString().ToLowerInvariant()} '{m_tape.Name}'; {remedy}]");
    }

    private static CommandResult Error(string text) => new(Output: text) { IsError = true };
}
