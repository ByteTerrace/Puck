using System.Text.Json;
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
            bindability: CommandBindability.Unbindable,
            name: "replay.record",
            description: "Arms deterministic recording (Immediate): replay.record <name> begins capturing the running session's per-tick server-input stream and starting state; replay.stop persists it. Refuses to arm, loudly, on any of THREE boot-anchored conditions this session: an addon has already had an admitted execution attempted (offline replay creates fresh guests at sim-counter zero, which cannot re-establish a guest's prior accumulated state), a screen machine has already stepped, or a screen op (insert/eject/select/options/link/unlink) has already applied — the latter two because offline replay reconstructs a FRESH WorldMachineHost from the tape's own definition snapshot, which can never recover a booted cartridge's accumulated core state or an already-landed screen op. Grant verb masks ride the shared tape leaf codec.",
            handler: (_, args) => Record(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.stop",
            description: "Stops and persists the active recording (Immediate): writes <name>.puckreplay under the LIVE session's tail pose hash FIRST — the tape is evidence, so it persists even when the verdict below will refuse — then re-drives it once through a fresh world and echoes the path plus either the tick count and MATCH/MISMATCH verdict (MISMATCH = a mid-session capture whose fresh re-drive starts from the definition boot image) or, if the re-drive itself could not run (e.g. the mount pin), a refusal naming the tape as written and the live tree as moved past it.",
            handler: (_, args) => Stop(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.cancel",
            description: "Aborts the active recording WITHOUT persisting it (Immediate): drops the captured stream and detaches the taps.",
            handler: (_, args) => Cancel(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.verify",
            description: "Replays a saved recording through a FRESH world and reports MATCH/MISMATCH (Immediate): replay.verify <name> rehydrates the boot-image starting state, re-drives the recorded stream offline, and compares the replayed tail hash against the recorded LIVE tail (a genuine live-vs-replay fidelity check).",
            handler: (_, args) => Verify(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.list",
            description: "Lists every persisted replay by name (Immediate).",
            handler: (_, args) => ListReplays(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.status",
            description: "Reports the tape state (Immediate): idle or recording, the active name, and ticks captured so far.",
            handler: (_, args) => Status(args: args)
        );
    }

    private CommandResult Record(WireArgs args) {
        if (args.Count != 1) {
            return CommandResult.Error(output: "[replay.record: usage — replay.record <name>]");
        }

        var name = args[0].ToString();

        if (!WorldReplayTape.IsValidName(name: name)) {
            return CommandResult.Error(output: "[replay.record: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }

        if (m_tape.Mode != WorldReplayMode.Idle) {
            return CommandResult.Error(output: $"[replay.record: busy — already recording '{m_tape.Name}'; replay.stop persists it or replay.cancel drops it first]");
        }

        if (!m_tape.TryBeginRecording(name: name, refusal: out var refusal)) {
            return CommandResult.Error(output: $"[replay.record: refused to arm — {refusal}]");
        }

        return new CommandResult(Output: $"[replay.record: recording '{name}' — replay.stop persists it, replay.cancel drops it]");
    }
    private CommandResult Stop(WireArgs args) {
        if (args.Count > 0) {
            return CommandResult.Error(output: "[replay.stop: expected no arguments]");
        }

        if (m_tape.Mode != WorldReplayMode.Recording) {
            return CommandResult.Error(output: "[replay.stop: not recording]");
        }

        try {
            var result = m_tape.StopRecording();

            if (result.VerifyFault is { } fault) {
                // The tape is already on disk at result.Path — this is the LIVE TREE having moved past this
                // recording's mounted set, never a persistence failure. Typical cause: a document-only
                // world.row.set addons/world.row.remove addons ran during the capture, mutating the definition while the live runtime kept
                // its boot receipts (mounting only happens at boot), so the recorded receipts and the embedded
                // definition legitimately disagree at the offline re-drive (see WorldReplaySnapshot.VerifyMountedAddons).
                return CommandResult.Error(output: $"[replay.stop: wrote {result.Path}, but the post-persist verify refused — the LIVE TREE moved past this recording's mounted set: {fault}]");
            }

            var verdict = result.Verdict!.Value;

            if (verdict.Match) {
                return new CommandResult(Output: $"[replay.stop: wrote {result.Path} | {verdict.Describe()} — faithful, boot-anchored capture]");
            }

            // Tick 0 indicts the STARTING state (a mid-session capture the boot image cannot reproduce); any later tick
            // means the start matched and the trajectory drifted, which is a determinism defect, not a capture boundary.
            var reading = (verdict.DivergedAtStart
                ? "mid-session capture; the fresh re-drive starts from the definition boot image"
                : "the capture was boot-anchored, so this is TRAJECTORY drift — investigate the tick above");

            return new CommandResult(Output: $"[replay.stop: wrote {result.Path} | {verdict.Describe()} — {reading}]");
        } catch (WorldReplayCodecException exception) {
            // A host-side codec bug — WriteFile's encoding refusing a value it cannot represent, or the post-persist
            // re-drive meeting an authority-entry kind it does not handle. Never a corrupt tape, never untrusted bytes,
            // and never the benign tree-move the VerifyFault branch above reports. THE SAME reading replay.verify gives
            // this exception one verb over: the two agree because they classify one type, not one shared base.
            return CommandResult.Error(output: $"[replay.stop: host-side codec bug (not a persistence failure) — {exception.Message}]");
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException)) {
            return CommandResult.Error(output: $"[replay.stop: could not persist — {exception.Message}]");
        }
    }
    private CommandResult Cancel(WireArgs args) {
        if (args.Count > 0) {
            return CommandResult.Error(output: "[replay.cancel: expected no arguments]");
        }

        if (m_tape.Mode != WorldReplayMode.Recording) {
            return CommandResult.Error(output: "[replay.cancel: not recording]");
        }

        var name = m_tape.CancelRecording();

        return new CommandResult(Output: $"[replay.cancel: dropped '{name}' — nothing written]");
    }
    private CommandResult Verify(WireArgs args) {
        if (args.Count != 1) {
            return CommandResult.Error(output: "[replay.verify: usage — replay.verify <name>]");
        }

        var name = args[0].ToString();

        if (!WorldReplayTape.IsValidName(name: name)) {
            return CommandResult.Error(output: "[replay.verify: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }

        try {
            var verdict = m_tape.Verify(name: name);

            // One rendering, one error flag: the verdict decides both, so a MATCH and a MISMATCH cannot drift apart in
            // wording the way two hand-written branches do.
            return new CommandResult(Output: $"[replay.verify: '{name}' | {verdict.Describe()}]") { IsError = !verdict.Match };
        } catch (FileNotFoundException) {
            return CommandResult.Error(output: $"[replay.verify: no replay named '{name}' — replay.list shows what's saved]");
        } catch (WorldReplayCodecException exception) {
            // A host-side codec bug (an authority-entry kind Drive's re-drive switch does not handle) — not a corrupt
            // tape; every untrusted-byte fault this codec detects throws InvalidDataException instead (see
            // WorldReplaySnapshot.Read's own normalization of the BCL exceptions it can otherwise leak).
            return CommandResult.Error(output: $"[replay.verify: '{name}' hit a host-side codec bug (not a corrupt tape) — {exception.Message}]");
        } catch (Exception exception) when ((exception is InvalidDataException or IOException or JsonException)) {
            return CommandResult.Error(output: $"[replay.verify: '{name}' is unreadable/corrupt — {exception.Message}]");
        }
    }
    private static CommandResult ListReplays(WireArgs args) {
        if (args.Count > 0) {
            return CommandResult.Error(output: "[replay.list: expected no arguments]");
        }

        var names = WorldReplayTape.List();

        return new CommandResult(Output: ((names.Count == 0)
            ? "[replay.list: none saved — replay.record <name> then replay.stop records one]"
            : $"[replay.list: {string.Join(separator: ", ", values: names)}]"));
    }
    private CommandResult Status(WireArgs args) {
        if (args.Count > 0) {
            return CommandResult.Error(output: "[replay.status: expected no arguments]");
        }

        return new CommandResult(Output: ((m_tape.Mode == WorldReplayMode.Idle)
            ? "[replay.status: idle]"
            : $"[replay.status: recording '{m_tape.Name}' | {m_tape.TickCount} ticks captured]"));
    }

}
