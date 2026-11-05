namespace Puck.Cli;

/// <summary>The host's refusal to take a World start: the machine had no memory, commit or disk headroom (or, for a step that opens
/// a device, its GPU stayed busy) within the admission's bound. It is an <see cref="InvalidOperationException"/> so a verb's own
/// "could not start the process" handling reports it, and one that has none reports it on one named line
/// (<see cref="CliExit.Guard"/>).</summary>
/// <param name="step">The step that was refused.</param>
public sealed class HostAdmissionRefusedException(string step) : InvalidOperationException(message: $"the machine had no memory, commit or disk headroom (or its GPU stayed busy) within the admission bound, so {step} did not start.");

/// <summary>The one door a verb passes before it starts a <c>Puck.World</c> process or boots a World in process: the host
/// admission (<c>HostAdmission</c>, in the gate's assembly above this one) that <c>puck gate</c>, <c>puck affected</c>,
/// <c>puck lease heavy|gpu</c> and the MCP World start already wait on. The root installs it once (<see cref="Install"/>);
/// a verb below the gate calls <see cref="Wait(string, bool, bool, CancellationToken, HostStartKind?)"/> immediately before each start, so a run
/// that boots several processes in sequence (the replicas of <c>puck test --reproduce</c> and <c>--workers</c>, a sweep's legs, a
/// canary's boots) admits before every one of them. The wait is the admission's own bounded, heartbeat-printing wait on the
/// host's timer, never a spin, and the waiting process's own descendants never hold a heavy-suite wait back, so a verb under
/// <c>puck lease heavy</c> or the gate does not wait on its own runs. Nothing stops a new caller from starting a World without
/// passing this door.</summary>
public static class WorldAdmission {
    private static HostAdmit? Installed;

    /// <summary>Installs the admission every <see cref="Wait(string, bool, bool, CancellationToken, HostStartKind?)"/> waits on, once per
    /// process, from the root that composes the verbs.</summary>
    /// <param name="admit">The admission, or <see langword="null"/> to admit every start at once, which is what a process that
    /// installed none (a test of one verb) does.</param>
    public static void Install(HostAdmit? admit) => Installed = admit;
    /// <summary>Waits until the host can take a World start with these arguments, at most the admission's own bound, and prints
    /// what holds it back on standard error.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="worldArguments">The World's arguments, which say whether it opens a device (<see cref="WorldDevices.OpensDevice"/>).</param>
    /// <param name="heavySuite">Whether the start is part of a run that holds the heavy lease, and so also waits while another
    /// process runs a heavy test suite.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="HostAdmissionRefusedException">The bound passed first.</exception>
    public static void Wait(string step, IReadOnlyList<string> worldArguments, bool heavySuite = false, CancellationToken cancellationToken = default) =>
        Wait(step: step, device: WorldDevices.OpensDevice(arguments: worldArguments), heavySuite: heavySuite, cancellationToken: cancellationToken, kind: WorldDevices.StartKind(arguments: worldArguments));
    /// <summary>Waits until the host can take a start, at most the admission's own bound, and prints what holds it back on
    /// standard error.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="device">Whether the start opens a GPU device, and so also waits for an idle GPU.</param>
    /// <param name="heavySuite">Whether the start is part of a run that holds the heavy lease, and so also waits while another
    /// process runs a heavy test suite.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <param name="kind">What the start is, which sets the commit it is expected to peak at; <see langword="null"/> for a
    /// <see cref="HostStartKind.WindowedWorld"/> when it opens a device and a <see cref="HostStartKind.HeadlessWorld"/>
    /// when it does not.</param>
    /// <exception cref="HostAdmissionRefusedException">The bound passed first.</exception>
    public static void Wait(string step, bool device, bool heavySuite = false, CancellationToken cancellationToken = default, HostStartKind? kind = null) {
        if (Installed is not { } admit) { return; }

        var checkout = (CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root) ? root : Environment.CurrentDirectory);

        if (!admit(checkout: checkout, step: step, device: device, heavySuite: heavySuite, kind: (kind ?? (device ? HostStartKind.WindowedWorld : HostStartKind.HeadlessWorld)), bound: null, progress: Console.Error, stage: null, cancellationToken: cancellationToken).GetAwaiter().GetResult()) {
            throw new HostAdmissionRefusedException(step: step);
        }
    }
}
