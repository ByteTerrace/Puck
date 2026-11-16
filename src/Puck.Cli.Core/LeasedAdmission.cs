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

/// <summary>A host start was refused before its process could run.</summary>
public sealed class HostAdmissionRefusedException(string step) : InvalidOperationException(message: $"the machine had no memory, commit or disk headroom (or its GPU stayed busy) within the admission bound, so {step} did not start.");
