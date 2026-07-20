using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The replay console surface — <c>replay.record</c> / <c>replay.stop</c> / <c>replay.cancel</c> / <c>replay.verify</c>
/// / <c>replay.list</c> / <c>replay.status</c>, the true-deterministic-replay control plane over the pipe (the seed of a
/// future <c>Puck.Replay</c>). It arms the <see cref="WorldReplayTape"/> that captures the running session's per-tick
/// server-input stream and starting state: <c>replay.record</c> begins capture, <c>replay.stop</c> persists the
/// self-contained <see cref="WorldReplaySnapshot"/> under the LIVE session's tail pose hash and re-drives it once to
/// report the verdict, and <c>replay.verify</c> re-drives a saved recording through a fresh world and reports whether the
/// replayed tail hash MATCHES the recorded LIVE tail — a genuine live-vs-replay fidelity proof, not a re-drive compared
/// against another re-drive of the same stream. Every verb is Immediate (a client-local control, no direct simulation effect):
/// verification runs offline over an isolated shadow world, so it never re-injects into the live session and its verdict
/// is readable the instant the verb returns. A SEPARATE module to keep each class under its analyzer ceilings.
/// </summary>
internal sealed class WorldReplayCommandModule(WorldReplayTape tape) : ICommandModule {
    private readonly WorldReplayTape m_tape = tape;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "replay.record",
            description: "Arms deterministic recording (Immediate): replay.record <name> begins capturing the running session's per-tick server-input stream and starting state; replay.stop persists it.",
            handler: (_, args) => Record(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            name: "replay.stop",
            description: "Stops and persists the active recording (Immediate): writes <name>.puckreplay under the LIVE session's tail pose hash, re-drives it once through a fresh world, and echoes the path, tick count, and MATCH/MISMATCH verdict (MISMATCH = a mid-session capture whose fresh re-drive starts from the definition boot image).",
            handler: (_, args) => Stop(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            name: "replay.cancel",
            description: "Aborts the active recording WITHOUT persisting it (Immediate): drops the captured stream and detaches the taps.",
            handler: (_, args) => Cancel(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            name: "replay.verify",
            description: "Replays a saved recording through a FRESH world and reports MATCH/MISMATCH (Immediate): replay.verify <name> rehydrates the boot-image starting state, re-drives the recorded stream offline, and compares the replayed tail hash against the recorded LIVE tail (a genuine live-vs-replay fidelity check).",
            handler: (_, args) => Verify(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            name: "replay.list",
            description: "Lists every persisted replay by name (Immediate).",
            handler: (_, args) => ListReplays(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            name: "replay.status",
            description: "Reports the tape state (Immediate): idle or recording, the active name, and ticks captured so far.",
            handler: (_, args) => Status(args: args)
        );
    }

    private CommandResult Record(WireArgs args) {
        if (args.Count != 1) {
            return Error(text: "[replay.record: usage — replay.record <name>]");
        }

        var name = args[0].ToString();

        if (!WorldReplayTape.IsValidName(name: name)) {
            return Error(text: "[replay.record: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }

        if (m_tape.Mode != WorldReplayMode.Idle) {
            return Error(text: $"[replay.record: busy — already recording '{m_tape.Name}'; replay.stop persists it or replay.cancel drops it first]");
        }

        m_tape.BeginRecording(name: name);

        return new CommandResult(Output: $"[replay.record: recording '{name}' — replay.stop persists it, replay.cancel drops it]");
    }

    private CommandResult Stop(WireArgs args) {
        if (args.Count > 0) {
            return Error(text: "[replay.stop: expected no arguments]");
        }

        if (m_tape.Mode != WorldReplayMode.Recording) {
            return Error(text: "[replay.stop: not recording]");
        }

        try {
            var (path, ticks, recorded, replayed, match) = m_tape.StopRecording();

            return match
                ? new CommandResult(Output: $"[replay.stop: wrote {path} | {ticks} ticks | MATCH live tail=0x{recorded:X16} — faithful, boot-anchored capture]")
                : new CommandResult(Output: $"[replay.stop: wrote {path} | {ticks} ticks | MISMATCH live tail=0x{recorded:X16} replayed=0x{replayed:X16} — mid-session capture; the fresh re-drive starts from the definition boot image]");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) {
            return Error(text: $"[replay.stop: could not persist — {exception.Message}]");
        }
    }

    private CommandResult Cancel(WireArgs args) {
        if (args.Count > 0) {
            return Error(text: "[replay.cancel: expected no arguments]");
        }

        if (m_tape.Mode != WorldReplayMode.Recording) {
            return Error(text: "[replay.cancel: not recording]");
        }

        var name = m_tape.CancelRecording();

        return new CommandResult(Output: $"[replay.cancel: dropped '{name}' — nothing written]");
    }

    private CommandResult Verify(WireArgs args) {
        if (args.Count != 1) {
            return Error(text: "[replay.verify: usage — replay.verify <name>]");
        }

        var name = args[0].ToString();

        if (!WorldReplayTape.IsValidName(name: name)) {
            return Error(text: "[replay.verify: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }

        try {
            var (recorded, replayed, ticks, match) = m_tape.Verify(name: name);

            return match
                ? new CommandResult(Output: $"[replay.verify: MATCH '{name}' | {ticks} ticks | hash=0x{recorded:X16}]")
                : new CommandResult(Output: $"[replay.verify: MISMATCH '{name}' | {ticks} ticks | recorded=0x{recorded:X16} replayed=0x{replayed:X16}]") { IsError = true };
        } catch (FileNotFoundException) {
            return Error(text: $"[replay.verify: no replay named '{name}' — replay.list shows what's saved]");
        } catch (Exception exception) when (exception is InvalidDataException or IOException) {
            return Error(text: $"[replay.verify: '{name}' is unreadable/corrupt — {exception.Message}]");
        }
    }

    private static CommandResult ListReplays(WireArgs args) {
        if (args.Count > 0) {
            return new CommandResult(Output: "[replay.list: expected no arguments]") { IsError = true };
        }

        var names = WorldReplayTape.List();

        return new CommandResult(Output: ((names.Count == 0)
            ? "[replay.list: none saved — replay.record <name> then replay.stop records one]"
            : $"[replay.list: {string.Join(separator: ", ", values: names)}]"));
    }

    private CommandResult Status(WireArgs args) {
        if (args.Count > 0) {
            return Error(text: "[replay.status: expected no arguments]");
        }

        return new CommandResult(Output: (m_tape.Mode == WorldReplayMode.Idle)
            ? "[replay.status: idle]"
            : $"[replay.status: recording '{m_tape.Name}' | {m_tape.TickCount} ticks captured]");
    }

    private static CommandResult Error(string text) => new(Output: text) { IsError = true };
}
