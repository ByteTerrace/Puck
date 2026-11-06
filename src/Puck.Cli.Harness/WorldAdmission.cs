namespace Puck.Cli;

/// <summary>The host's refusal to take a World start: the machine had no memory, commit or disk headroom (or, for a step that opens
/// a device, its GPU stayed busy) within the admission's bound. It is an <see cref="InvalidOperationException"/> so a verb's own
/// "could not start the process" handling reports it, and one that has none reports it on one named line
/// (<see cref="CliExit.Guard"/>).</summary>
/// <param name="step">The step that was refused.</param>
public sealed class HostAdmissionRefusedException(string step) : InvalidOperationException(message: $"the machine had no memory, commit or disk headroom (or its GPU stayed busy) within the admission bound, so {step} did not start.");

/// <summary>Waits until the host can take a World start, as <see cref="HostAdmit"/> does, with the caller choosing whether an
/// admitted start reserves its expected peak: a start that only waits for headroom to exist, and is judged again before it
/// launches, reserves nothing.</summary>
/// <param name="checkout">The checkout whose drive's free disk is judged.</param>
/// <param name="step">The step's name, for the wait lines.</param>
/// <param name="device">Whether the start opens a GPU device, and so also waits for an idle GPU.</param>
/// <param name="heavySuite">Whether the start runs under the heavy lease, and so also waits while another process runs a
/// heavy suite.</param>
/// <param name="kind">What the start is, which sets the commit it is expected to peak at.</param>
/// <param name="bound">How long to wait, at most, or <see langword="null"/> for the admission's own bound.</param>
/// <param name="reserve">Whether an admitted start reserves its expected peak.</param>
/// <param name="progress">Receives the wait lines.</param>
/// <param name="stage">Receives what the wait is on, as a phrase, on every reading while it waits; <see langword="null"/> to
/// ignore it.</param>
/// <param name="cancellationToken">Stops the wait.</param>
/// <returns><see langword="true"/> once the host can take the start; <see langword="false"/> when the bound passed first.</returns>
public delegate Task<bool> WorldAdmit(string checkout, string step, bool device, bool heavySuite, HostStartKind kind, TimeSpan? bound, bool reserve, TextWriter progress, Action<string>? stage, CancellationToken cancellationToken);

/// <summary>The host admission (<c>HostAdmission</c>, in the gate's assembly above this one) that <c>puck gate</c>,
/// <c>puck affected</c> and <c>puck lease heavy|gpu</c> wait on, as the World starts reach it. The root installs it once
/// (<see cref="Install"/>). A World process passes it inside <see cref="WorldLaunch.Admit"/> before every process a verb
/// starts (each replica of <c>puck test --reproduce</c> and <c>--workers</c>, a sweep's legs, a canary's boots); a World
/// booted inside this process (<c>puck bench</c>, <c>puck determinism</c>) passes it through <see cref="WaitInProcess"/>.
/// The wait is the admission's own bounded, heartbeat-printing wait on the host's timer, never a spin, and the waiting
/// process's own descendants hold back neither a heavy-suite wait nor a device step's wait for an idle GPU, so a verb
/// under <c>puck lease heavy</c> or the gate does not wait on its own runs.</summary>
public static class WorldAdmission {
    private static WorldAdmit? Installed;

    /// <summary>Installs the admission every World start waits on, once per process, from the root that composes the
    /// verbs.</summary>
    /// <param name="admit">The admission, or <see langword="null"/> to admit every start at once, which is what a process that
    /// installed none (a test of one verb) does.</param>
    public static void Install(WorldAdmit? admit) => Installed = admit;
    /// <summary>Waits until the host can take a headless World booted inside this process, at most the admission's own
    /// bound, and prints what holds it back on standard error.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="HostAdmissionRefusedException">The bound passed first.</exception>
    public static void WaitInProcess(string step, CancellationToken cancellationToken = default) {
        if (!TryAdmitAsync(bound: null, cancellationToken: cancellationToken, device: false, heavySuite: false, kind: HostStartKind.HeadlessWorld, reserve: true, stage: null, step: step).GetAwaiter().GetResult()) {
            throw new HostAdmissionRefusedException(step: step);
        }
    }

    /// <summary>Waits until the host can take a start, at most <paramref name="bound"/>, printing what holds it back on
    /// standard error.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="device">Whether the start opens a GPU device, and so also waits for an idle GPU.</param>
    /// <param name="heavySuite">Whether the start runs under the heavy lease.</param>
    /// <param name="kind">What the start is, which sets the commit it is expected to peak at.</param>
    /// <param name="bound">How long to wait, at most, or <see langword="null"/> for the admission's own bound.</param>
    /// <param name="reserve">Whether an admitted start reserves its expected peak.</param>
    /// <param name="stage">Receives what the wait is on, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns><see langword="true"/> once admitted; <see langword="false"/> when the bound passed first.</returns>
    internal static Task<bool> TryAdmitAsync(string step, bool device, bool heavySuite, HostStartKind kind, TimeSpan? bound, bool reserve, Action<string>? stage, CancellationToken cancellationToken) {
        if (Installed is not { } admit) { return Task.FromResult(result: true); }

        var checkout = (CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root) ? root : Environment.CurrentDirectory);

        return admit(checkout: checkout, step: step, device: device, heavySuite: heavySuite, kind: kind, bound: bound, reserve: reserve, progress: Console.Error, stage: stage, cancellationToken: cancellationToken);
    }
}
