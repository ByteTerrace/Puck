namespace Puck.Cli;

/// <summary>Queues leased work through the lease's ticket-preserving admission door.</summary>
public static class LeasedAdmission {
    /// <summary>Waits for headroom without a resource hold, then reserves on a fresh, nonwaiting reading under
    /// the resource guard. Lost headroom parks the same ticket without changing its lane's service history.</summary>
    public static Task<CliLease> AcquireAsync(string root, string name, string holder, string? lane, HostAdmit? admit,
        string checkout, string step, bool device, bool heavySuite, HostStartKind kind, TimeProvider clock,
        TimeSpan bound, TextWriter progress, Action<string>? stage, CancellationToken cancellationToken) =>
        CliLease.AcquireAsync(root: root, name: name, holder: holder, lane: lane, checkout: checkout,
            clock: clock, wait: bound, ancestor: ProcessInspection.IsAncestor, cancellationToken: cancellationToken,
            admission: admit is null ? null : (reserve, remaining, token) => {
                stage?.Invoke("host-admission");
                return admit(checkout, step, device, heavySuite, kind, remaining, reserve, progress, stage, token);
            });
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
