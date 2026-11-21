namespace Puck.Cli;

/// <summary>Queues leased work through the lease's ticket-preserving admission door.</summary>
public static class LeasedAdmission {
    /// <summary>Waits for headroom without a resource hold, then reserves on a fresh, nonwaiting reading under
    /// the resource guard. Lost headroom parks the same ticket without changing its lane's service history. The hold records
    /// <paramref name="kind"/> and <paramref name="device"/> beside its holder, so every other process's admission counts
    /// the growth its work still has and waits for its device from the grant (<see cref="CliLease.AdmittedHolds"/>); with an
    /// admission, heavy holds share the lease's slots unless <paramref name="exclusive"/> is set.</summary>
    /// <param name="root">The shared lease directory.</param>
    /// <param name="name">The resource: build, gpu or heavy.</param>
    /// <param name="holder">The holder name.</param>
    /// <param name="lane">The lane admission rotates between, or null for the holder's first word.</param>
    /// <param name="admit">The host admission, or null to admit at once (a heavy hold without one is exclusive).</param>
    /// <param name="checkout">The checkout the holder works in.</param>
    /// <param name="step">The step's name, for the admission's wait lines.</param>
    /// <param name="device">Whether the held work opens a GPU device.</param>
    /// <param name="heavySuite">Whether the held work runs under the heavy lease.</param>
    /// <param name="kind">The expected peak class of the held work.</param>
    /// <param name="clock">The acquisition clock.</param>
    /// <param name="bound">The longest the acquisition waits, queue and headroom together.</param>
    /// <param name="progress">Receives the wait lines.</param>
    /// <param name="stage">Receives what the wait is on, or null.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <param name="exclusive">Whether a heavy hold runs alone, keeping every other slot empty.</param>
    /// <returns>The held lease.</returns>
    public static Task<CliLease> AcquireAsync(string root, string name, string holder, string? lane, HostAdmit? admit,
        string checkout, string step, bool device, bool heavySuite, HostStartKind kind, TimeProvider clock,
        TimeSpan bound, TextWriter progress, Action<string>? stage, CancellationToken cancellationToken, bool exclusive = false) =>
        CliLease.AcquireAsync(root: root, name: name, holder: holder, lane: lane, checkout: checkout,
            clock: clock, wait: bound, ancestor: ProcessInspection.IsAncestor, cancellationToken: cancellationToken,
            admission: admit is null ? null : (reserve, remaining, token) => {
                stage?.Invoke("host-admission");
                return admit(checkout, step, device, heavySuite, kind, remaining, reserve, progress, stage, token);
            }, kind: kind, device: device, exclusive: exclusive);
}

/// <summary>A host start was refused before its process could run: the machine had no headroom within the start's wait,
/// or the start can never fit on it.</summary>
/// <param name="step">The step that did not start.</param>
/// <param name="reason">Why no wait could have admitted the start, or <see langword="null"/> when the start's wait passed
/// first.</param>
public sealed class HostAdmissionRefusedException(string step, string? reason = null) : InvalidOperationException(message: ((reason is null)
    ? $"the machine had no memory, commit or disk headroom (or its GPU stayed busy) within the admission bound, so {step} did not start."
    : $"{step} did not start: {reason}.")) {
    /// <summary>Gets why no wait could have admitted the start, or <see langword="null"/> when the start's wait passed
    /// first.</summary>
    public string? Reason { get; } = reason;
}
