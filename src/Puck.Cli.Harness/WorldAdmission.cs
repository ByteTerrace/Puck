namespace Puck.Cli;

/// <summary>The host admission (<c>HostAdmission</c>, in this harness) that <c>puck gate</c>,
/// <c>puck affected</c> and <c>puck lease heavy|gpu</c> wait on, as the World starts reach it. The harness installs it by default; <see cref="Install"/> can replace it for a test. A World process passes it inside <see cref="WorldLaunch.Admit"/> before every process a verb
/// starts (each replica of <c>puck test --reproduce</c> and <c>--workers</c>, a sweep's legs, a canary's boots); a World
/// booted inside this process (<c>puck bench</c>, <c>puck determinism</c>) passes it through <see cref="WaitInProcess"/>.
/// The wait is the admission's own bounded, heartbeat-printing wait on the host's timer, never a spin, and the waiting
/// process's own descendants hold back neither a heavy-suite wait nor a device step's wait for an idle GPU, so a verb
/// under <c>puck lease heavy</c> or the gate does not wait on its own runs.</summary>
public static class WorldAdmission {
    private static HostAdmit? Installed = Host.HostAdmission.For(clock: TimeProvider.System);

    /// <summary>Overrides the harness's default admission for a test or an explicitly composed host.</summary>
    /// <param name="admit">The admission, or <see langword="null"/> to admit every start at once in a test.</param>
    public static void Install(HostAdmit? admit) => Installed = admit;
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
