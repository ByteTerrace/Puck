using System.Globalization;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The console surface for per-instance scheduling: <c>world.rate</c> reads
/// back one instance's own declared rate and live schedule state (the boot instance by default, or a named one via
/// the trailing <c>instance:&lt;name&gt;</c> token every drive-a-player verb already carries — see
/// <see cref="PlayerCommandModule"/>'s own <c>TryStripInstanceToken</c>), and <c>world.rate pause</c>/<c>resume</c>
/// drives the live lever (<see cref="WorldInstance.IsPaused"/>) that stops/resumes stepping without touching the
/// document's own authored <c>simulation.rateHz</c> — the durable stop is 0, this lever is the live one, and the two
/// never confuse each other in <see cref="WorldInstanceHost.TryPause"/>'s own refusal.
/// </summary>
/// <remarks><para>One registered verb name, not three: <c>world.rate</c> alone is Simulation-routed (matching the
/// state-changing instance verbs beside it — <c>world.instance.start</c>/<c>stop</c>) so the read form and the
/// pause/resume forms apply through the same per-tick dispatch the fixed-step thread already serializes every
/// instance-registry write through; a bare read still prints its answer synchronously in the returned
/// <see cref="CommandResult"/>, exactly like <c>world.instance.start</c>'s own accept echo. Reads land on stdout
/// after the ordinary console drain barrier holds a following read until a preceding pause/resume has applied — no
/// separate fence is needed.</para>
/// <para>A pause/resume that actually changes the boot instance's own lever state is also taped
/// (<see cref="WorldReplayTape.NoteRateLever"/>) — the tape covers the boot instance only, so a named instance's own
/// lever never reaches it. No derived-band API exists in the tree yet; the
/// read-back reports only what this module can honestly derive: the declared rate, the live state, the step width,
/// and completed ticks.</para></remarks>
internal sealed class WorldRateCommandModule(WorldInstanceHost instances, WorldReplayTape replayTape) : ICommandModule {
    private readonly WorldInstanceHost m_instances = instances;
    private readonly WorldReplayTape m_replayTape = replayTape;

    private static string Describe(string name, WorldInstanceHost.WorldInstanceRateStatus status) {
        var state = (status.Stopped
            ? "stopped"
            : (status.Paused
                ? "paused"
                : "running"
        ));
        var stepWidth = ((status.StepWidthTicks is { } width)
            ? width.ToString(provider: CultureInfo.InvariantCulture)
            : "stopped"
        );

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.rate {name}: rateHz {status.RateHz} state {state} step {stepWidth} completed-ticks {status.CompletedTicks}]"
        );
    }
    // args[0] is the operation token ("pause"/"resume") when present and NOT itself the trailing instance: token —
    // a bare read carries either nothing or just the instance: token at index 0.
    private static int DetermineTokenIndex(in WireArgs args) {
        if (args.Count == 0) {
            return 0;
        }

        if (IsInstanceToken(
            args: in args,
            index: 0
        )) {
            return 0;
        }

        return 1;
    }
    private static bool IsInstanceToken(in WireArgs args, int index) =>
        ((((uint)index) < ((uint)args.Count)) && WorldArgs.IsInstanceToken(token: args[index]));
    // Tapes a pause/resume that targets the BOOT instance specifically — the tape's own scope (see
    // WorldReplayTape.NoteRateLever's remarks). A named instance's own lever never reaches the tape.
    private void NoteBootLever(string name, bool paused) {
        if (string.Equals(
            a: name,
            b: WorldInstanceHost.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            m_replayTape.NoteRateLever(paused: paused);
        }
    }
    private CommandResult Pause(string name) {
        if (!m_instances.TryPause(
            name: name,
            reason: out var reason
        )) {
            return CommandResult.Error(output: $"[world.rate: pause refused ({reason})]");
        }

        NoteBootLever(
            name: name,
            paused: true
        );

        return ReadBack(name: name);
    }
    private CommandResult ReadBack(string name) {
        if (!m_instances.TryDescribeRate(
            name: name,
            reason: out var reason,
            status: out var status
        )) {
            return CommandResult.Error(output: $"[world.rate: refused ({reason})]");
        }

        return new CommandResult(Output: Describe(
            name: name,
            status: status
        ));
    }
    private CommandResult Resume(string name) {
        if (!m_instances.TryResume(
            name: name,
            reason: out var reason,
            wasPaused: out var wasPaused
        )) {
            return CommandResult.Error(output: $"[world.rate: resume refused ({reason})]");
        }

        if (wasPaused) {
            NoteBootLever(
                name: name,
                paused: false
            );
        }

        var readBack = ReadBack(name: name);

        // A no-op echo names itself as such — the caller asked to resume an instance that was never paused (or
        // already resumed), and the spec is explicit that this is an ACCEPTED echo, never a refusal.
        return (wasPaused
            ? readBack
            : new CommandResult(Output: $"{readBack.Output} (no-op — was not paused)")
        );
    }
    // The trailing instance:<name> token — absent addresses the boot instance; 'instance:boot' is refused as
    // redundant, the SAME convention PlayerCommandModule.TryStripInstanceToken already establishes (never a second
    // spelling of "the world this process booted with").
    private bool TryResolveTarget(in WireArgs args, int tokenIndex, string verb, out string instanceName, out CommandResult? error) {
        if (((uint)tokenIndex) >= ((uint)args.Count)) {
            instanceName = WorldInstanceHost.BootInstanceName;
            error = null;

            return true;
        }

        if (args.Count > (tokenIndex + 1)) {
            instanceName = string.Empty;
            error = CommandResult.Error(output: $"[{verb}: too many arguments]");

            return false;
        }

        if (!WorldArgs.IsInstanceToken(token: args[tokenIndex])) {
            instanceName = string.Empty;
            error = CommandResult.Error(output: $"[{verb}: expected 'instance:<name>']");

            return false;
        }

        return WorldArgs.TryParseInstanceName(
            token: args[tokenIndex],
            verb: verb,
            name: out instanceName,
            error: out error
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WorldCommandDefinition.Simulation(
            name: "world.rate",
            description: "Reads back or drives one instance's own schedule — the boot instance by default, or a named one via a trailing instance:<name> token. world.rate [instance:<name>]: reports declared rateHz (verbatim — never validated against a 'known rates' notion), effective state (running|paused|stopped — stopped is the document's own durable rateHz 0, paused is the live lever below, running is neither), step width in engine ticks (or 'stopped' when rateHz is 0), and completed ticks. world.rate pause [instance:<name>]: arms the live pause lever, which stops stepping WITHOUT touching the authored rateHz; refuses an unknown instance and a rate-0 instance BY NAME (already stopped by the document itself — the lever would only duplicate that under a misleading name), naming which; pausing an already-paused instance is accepted and re-echoes the held state. world.rate resume [instance:<name>]: releases the lever, resuming on the EXACT schedule its accumulator already held (no skew); refuses only an unknown instance — resuming an instance that was never paused (or already resumed) is a NO-OP ECHO, never a refusal. A pause/resume that changes the BOOT instance's own lever state is recorded into an in-progress replay recording as an ordered rate-lever event. Simulation-routed, like the neighboring state-changing instance verbs (world.instance.start/stop) — a following read observes a preceding pause/resume through the ordinary console drain barrier.",
            handler: (_, args) => {
                if (!TryResolveTarget(
                    args: in args,
                    tokenIndex: DetermineTokenIndex(args: in args),
                    verb: "world.rate",
                    instanceName: out var name,
                    error: out var tokenError
                )) {
                    return tokenError!.Value;
                }

                if (
                    (args.Count == 0) ||
                    IsInstanceToken(
                    args: in args,
                    index: 0
                )
                ) {
                    return ReadBack(name: name);
                }

                var operation = args[0].ToString();

                if (string.Equals(
                    a: operation,
                    b: "pause",
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    return Pause(name: name);
                }

                if (string.Equals(
                    a: operation,
                    b: "resume",
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    return Resume(name: name);
                }

                return CommandResult.Error(output: $"[world.rate: unrecognized '{operation}' — expected [instance:<name>], 'pause [instance:<name>]', or 'resume [instance:<name>]']");
            }
        );
    }
}
