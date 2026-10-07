using System.Text.Json;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The replay console surface — <c>replay.record</c> / <c>replay.stop</c> / <c>replay.cancel</c> / <c>replay.verify</c>
/// / <c>replay.list</c> / <c>replay.status</c> / <c>replay.inspect</c> (the tape's read-back, in
/// <c>WorldReplayCommandModule.Inspect.cs</c>) / <c>replay.drive</c> and <c>replay.fork</c> (the two verbs that touch the
/// live session — they reset it to a tape's boot image and feed the recorded ticks through its own doors, in
/// <c>WorldReplayCommandModule.Drive.cs</c>), the true-deterministic-replay control plane over the pipe (the seed of a
/// future <c>Puck.Replay</c>). It arms the <see cref="WorldReplayTape"/> that captures the running session's per-tick
/// server-input stream and starting state: <c>replay.record</c> begins capture, <c>replay.stop</c> persists the
/// self-contained <see cref="WorldReplaySnapshot"/> under the LIVE session's tail population hash and re-drives it once to
/// report the verdict, and <c>replay.verify</c> re-drives a saved recording through a fresh world and reports whether the
/// replayed tail hash MATCHES the recorded LIVE tail — a genuine live-vs-replay fidelity proof, not a re-drive compared
/// against another re-drive of the same stream. Every verb is Immediate (a client-local control, no direct simulation effect):
/// verification runs offline over an isolated shadow world, so it never re-injects into the live session and its verdict
/// is readable the instant the verb returns. A SEPARATE module to keep each class under its analyzer ceilings.
/// </summary>
public sealed partial class WorldReplayCommandModule(WorldReplayTape tape, WorldReplayInspector inspector, WorldInstanceHost instances) : ICommandModule {
    private readonly WorldReplayInspector m_inspector = inspector;
    private readonly WorldInstanceHost m_instances = instances;
    private readonly WorldReplayTape m_tape = tape;

    private CommandResult Cancel(WireArgs args) {
        if (CommandResult.RequireNoArguments(
            args: args,
            verb: "replay.cancel"
        ) is { } refusal) {
            return refusal;
        }

        switch (m_tape.Mode) {
            case WorldReplayMode.Recording: {
                    var name = m_tape.CancelRecording();

                    return new CommandResult(Output: $"[replay.cancel: dropped '{name}' — nothing written]");
                }
            case WorldReplayMode.Replaying:
                return CancelDrive();
            default:
                return CommandResult.Error(output: "[replay.cancel: not recording or replaying]");
        }
    }
    private CommandResult ListReplays(WireArgs args) {
        if (CommandResult.RequireNoArguments(
            args: args,
            verb: "replay.list"
        ) is { } refusal) {
            return refusal;
        }

        var names = m_tape.List();

        return new CommandResult(Output: ((names.Count == 0)
            ? "[replay.list: none saved — replay.record <name> then replay.stop records one]"
            : $"[replay.list: {string.Join(
                separator: ", ",
                values: names
            )}]"));
    }
    private CommandResult Record(WireArgs args) {
        if (args.Count != 1) {
            return CommandResult.Error(output: "[replay.record: usage — replay.record <name>]");
        }

        var name = args[0].ToString();

        if (!WorldReplayTape.IsValidName(name: name)) {
            return CommandResult.Error(output: "[replay.record: name must be non-empty, with no '.', '/', '\\', or other filename-invalid characters]");
        }
        // The host mints its own recordings (an extension runtime's recovery recording) as generated file names.
        if (!GeneratedName.TryValidateAuthoredFile(
            name: name,
            reason: out var reserved
        )) {
            return CommandResult.Error(output: $"[replay.record: {reserved}]");
        }

        if (m_tape.Mode == WorldReplayMode.Recording) {
            return CommandResult.Error(output: $"[replay.record: busy — already recording '{m_tape.Name}'; replay.stop persists it or replay.cancel drops it first]");
        }

        if (m_tape.Mode == WorldReplayMode.Replaying) {
            return CommandResult.Error(output: $"[replay.record: busy — a replay drive of '{m_tape.DriveProgress?.SourceName}' is in progress; replay.cancel ends it, or use replay.fork to record from a drive]");
        }

        if (!m_tape.TryBeginRecording(
            name: name,
            refusal: out var refusal
        )) {
            return CommandResult.Error(output: $"[replay.record: refused to arm — {refusal}]");
        }
        m_instances.RecordCompanions(tape: m_tape);

        return new CommandResult(Output: $"[replay.record: recording '{name}' — replay.stop persists it, replay.cancel drops it]");
    }
    private CommandResult Status(WireArgs args) {
        if (CommandResult.RequireNoArguments(
            args: args,
            verb: "replay.status"
        ) is { } refusal) {
            return refusal;
        }

        if (m_tape.DriveProgress is { } drive) {
            var divergence = ((drive.DivergedAt < 0)
                ? "no divergence"
                : $"diverged at tick {drive.DivergedAt}"
            );

            return new CommandResult(Output: $"[replay.status: replaying '{drive.SourceName}' | tick {drive.Cursor} of {drive.Target} (tape {drive.TapeTicks}) | {divergence}{((drive.ForkName is { } fork)
                ? $" | fork -> '{fork}' (fast-forward)"
                : "")}]");
        }

        return new CommandResult(Output: ((m_tape.Mode == WorldReplayMode.Idle)
            ? "[replay.status: idle]"
            : $"[replay.status: recording '{m_tape.Name}' | {m_tape.TickCount} ticks captured]"));
    }
    private CommandResult Stop(WireArgs args) {
        if (CommandResult.RequireNoArguments(
            args: args,
            verb: "replay.stop"
        ) is { } refusal) {
            return refusal;
        }

        if (m_tape.Mode == WorldReplayMode.Replaying) {
            return CommandResult.Error(output: $"[replay.stop: not recording — a replay drive of '{m_tape.DriveProgress?.SourceName}' is in progress; replay.cancel ends it]");
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

            var verdict = result.Verdict!;

            if (verdict.Passing) {
                return new CommandResult(Output: $"[replay.stop: wrote {result.Path} | {verdict.Describe()} — faithful capture]");
            }

            // Every authority matched, so the set fails only on a crossing whose other half no tape here replays.
            // Otherwise tick 0 indicts the STARTING state (the boot image or start checkpoint the re-drive restored), and
            // any later tick means the start matched and the trajectory drifted, a determinism defect.
            var reading = (verdict.Match
                ? "every authority replayed, but a crossing's other half is on no tape in this set, so the crossing is not verified"
                : ((verdict.Primary.DivergedAtStart || verdict.Companions.Any(predicate: static companion => companion.Verdict.DivergedAtStart))
                    ? "the start the re-drive restored is not where the live session stood when the recording armed"
                    : "the start matched, so this is TRAJECTORY drift — investigate the tick above"
                )
            );

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
            return new CommandResult(Output: $"[replay.verify: '{name}' | {verdict.Describe()}]") { IsError = !verdict.Passing };
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

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.record",
            description: "Arms deterministic recording (Immediate): replay.record <name> begins capturing the running session's per-tick server-input stream and starting state, and tapes every other row of the process beside it as one set (a row that cannot be taped is named on stderr); replay.stop persists the set. Armed before the world's first step, the tape starts from the definition's boot image and refuses once a screen op (insert/eject/select/options/link/unlink) has applied; armed later, it starts from an authority checkpoint taken at the arm (without the owned-world catalog), so it re-drives from exactly where the session stood, and refuses by name (StartNotCheckpointable) when that state is one no checkpoint captures: a mounted or pumped addon guest, a screen op, a stepped machine without checkpoint support, a coupled link or rewind history, a live session, an engagement in flight, or an edit not yet applied. Grant verb masks ride the shared tape leaf codec.",
            handler: (_, args) => Record(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.stop",
            description: "Stops and persists the active recording (Immediate): writes <name>.puckreplay under the LIVE session's tail population hash FIRST — the tape is evidence, so it persists even when the verdict below will refuse — then re-drives it once through a fresh world and echoes the path plus either the tick count and MATCH/MISMATCH verdict (MISMATCH at tick 0 = the start the re-drive restored is not where the session stood at the arm; later = trajectory drift) or, if the re-drive itself could not run (e.g. the mount pin), a refusal naming the tape as written and the live tree as moved past it.",
            handler: (_, args) => Stop(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.cancel",
            description: "Aborts the active recording WITHOUT persisting it (Immediate): drops the captured stream and detaches the taps. During a replay drive, ends the drive where it stands instead: the world stays at that tick, local seats return to live input, and a pending fork is abandoned.",
            handler: (_, args) => Cancel(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.drive",
            description: "Drives a saved recording INTO THE LIVE SESSION (Immediate to arm; the drive itself runs one recorded tick per live tick, at the world's own rate, so it renders on screen and every read-back verb answers mid-drive): replay.drive <name> [to <tick>] resets the running world to the tape's boot image through the ordinary world.load door (its embedded definition, document grants, the record-start seats on their pinned profile rates) and feeds each recorded tick's authority entries and intents through the server's own doors. Local seat input — device sticks and every body.* drive verb — is masked for the drive's whole span (the recorded intents are the only intents); camera and look stay live. Each tick's live population hash is compared against the recording and the first divergence is narrated on stderr by tick without stopping the drive. At the end (or the <tick>) the world stays where the drive left it and the seats are live again; nothing is recorded. Refuses while recording or already driving, when the live player set differs from the tape's seats, or when a booted screen machine or pumped addon guest holds state the rebuild door cannot reset.",
            handler: (_, args) => Drive(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.fork",
            description: "Forks a saved recording at a tick into a new live recording (Immediate to arm): replay.fork <name> <tick> <new> drives '<name>' to its first <tick> recorded ticks as fast as the host loop allows (rendering may lag), then hands the seats back to live input and keeps RECORDING into '<new>' — the parent's boot image, ticks 0..<tick>-1 copied verbatim, then the live ticks that follow, with forkedFrom(<name>, <tick>) in the child's header. The child is standalone: replay.verify/inspect/fork all work on it with no parent lookup; replay.stop persists it like any recording. Same refusals as replay.drive, plus <new> must differ from <name>.",
            handler: (_, args) => Fork(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.verify",
            description: "Replays a saved recording through a FRESH world and reports MATCH/MISMATCH (Immediate): replay.verify <name> rehydrates the boot-image starting state, re-drives the recorded stream offline, and compares the replayed tail hash against the recorded LIVE tail (a genuine live-vs-replay fidelity check). Every row taped beside it is re-driven the same way, and every crossing between them is paired by its handoff token; a crossing whose other half is on a remote or untaped authority is NOT VERIFIED, and the verb fails unless every authority matches and every crossing is verified.",
            handler: (_, args) => Verify(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "replay.inspect",
            description: "Reads a saved recording back (Immediate): replay.inspect <name> [<from>-<to>] [--all] [--poses] prints the tape's header (shape magic, recorded rate, tick count, the pinned seats with their profile rates, the mounted-addon receipts) and then one line per tick carrying the recorded population hash beside what CHANGED that tick — every authority/server-event entry (command, grant, revoke, session, mutation, rebuild, screen op, …) named compactly, and every intent channel whose value moved from the entity's previous submission (p1 forward=1). Default prints only ticks carrying such an edge; --all prints every tick; <from>-<to> clamps the printed ticks (a from beyond the tape refuses by name). --poses re-drives the tape through the same offline shadow drive replay.verify uses and prints each active body's body.where-style pose beside every printed tick, plus the first tick where the re-driven hash diverges from the recorded one, if any.",
            handler: (_, args) => Inspect(args: args)
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
            description: "Reports the tape state (Immediate): idle, recording (the active name and ticks captured so far), or replaying (the source tape, tick <cursor> of <target>, the first divergent tick if any, and the fork target when one is armed).",
            handler: (_, args) => Status(args: args)
        );
    }

}
