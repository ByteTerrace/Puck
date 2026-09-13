using System.Net;
using Microsoft.AspNetCore.Http;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Loopback worker control for the durable release coordinator. Requests carry an operation identifier;
/// the worker reads the authoritative group itself and never accepts caller-supplied release pointers or roots.</summary>
public sealed class WorldSiloReleaseControl(WorldSiloHost silo) {
    private static void Require(bool condition, string reason) {
        if (!condition) { throw new InvalidOperationException(message: reason); }
    }

    /// <summary>Handles the release control routes on the existing lifecycle listener. Returns false for other paths.</summary>
    public async Task<bool> HandleAsync(HttpContext context) {
        if (!context.Request.Path.StartsWithSegments(other: "/release")) { return false; }
        if (
            (context.Connection.RemoteIpAddress is not { } address) ||
            !IPAddress.IsLoopback(address: address) ||
            (silo.Definition.Release is not { } managed)
        ) {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return true;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: context.RequestAborted);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: (silo.Definition.Lifecycle?.ShutdownSeconds ?? 120)));
        var token = deadline.Token;

        try {
            var current = await silo.ReadManagedReleaseAsync(ct: token).ConfigureAwait(continueOnCapturedContext: false);
            var state = current.Record;

            if (
                HttpMethods.IsGet(method: context.Request.Method) &&
                (context.Request.Path == "/release/status")
            ) {
                await context.Response.WriteAsJsonAsync(
                    new WorldReleaseWorkerStatus(
                        managed.Group,
                        managed.ExpectedRelease,
                        state.PendingOperationId,
                        state.PendingPhase,
                        silo.ReleaseAdmissionOpen
                    ),
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);
                return true;
            }
            var parts = context.Request.Path.Value!.Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: '/'
            );

            if (
                (parts.Length != 3) ||
                !Guid.TryParseExact(
                parts[2],
                "D",
                out var operation
            ) ||
                (operation == Guid.Empty)
            ) {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return true;
            }
            if (
                (parts[1] == "fixture") &&
                HttpMethods.IsPost(method: context.Request.Method)
            ) {
                var fixture = await silo.ExportReleaseFixtureAsync(
                    cancellationToken: token,
                    requestId: operation
                ).ConfigureAwait(continueOnCapturedContext: false);

                await context.Response.WriteAsJsonAsync(
                    new { fixture.RequestId, fixture.Identity, fixture.Release },
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);
                return true;
            }
            if (operation != state.PendingOperationId) {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return true;
            }
            switch (parts[1]) {
                case "drain" when HttpMethods.IsPost(method: context.Request.Method):
                    Require(
                        condition: ((state.PendingPhase == WorldReleaseOperationPhase.Drain) && (state.PendingSourceRelease == managed.ExpectedRelease)),
                        reason: "drain requires the current source and its durable drain operation"
                    );
                    await silo.DrainAsync(ct: token).ConfigureAwait(continueOnCapturedContext: false);
                    var roots = await silo.CaptureReleaseRootsAsync(
                        ct: token,
                        operationId: operation
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    await context.Response.WriteAsJsonAsync(
                        cancellationToken: token,
                        value: roots
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    break;
                case "fences" when HttpMethods.IsGet(method: context.Request.Method):
                    Require(
                        condition: ((state.PendingPhase is WorldReleaseOperationPhase.Verify or WorldReleaseOperationPhase.Commit) && (state.PendingTargetRelease == managed.ExpectedRelease)),
                        reason: "fence census requires the current candidate verification or commit"
                    );
                    var census = await silo.CaptureReleaseFencesAsync(ct: token).ConfigureAwait(continueOnCapturedContext: false);
                    await context.Response.WriteAsJsonAsync(
                        census.Select(selector: item => new WorldReleaseWorkerFence(
                            item.Identity.Owner,
                            item.Identity.World.Value,
                            item.Fence.Epoch,
                            item.Fence.Token,
                            item.Fence.RootVersion
                        )).ToArray(),
                        token
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    break;
                case "publish" when HttpMethods.IsPost(method: context.Request.Method):
                    var recovered = ((state.PendingPhase == WorldReleaseOperationPhase.RecoverActivate) && (state.PendingSourceRelease == managed.ExpectedRelease));
                    Require(
                        condition: (recovered || ((state.PendingPhase == WorldReleaseOperationPhase.Commit) && state.PendingCommitted && (state.PendingTargetRelease == managed.ExpectedRelease))),
                        reason: "publication requires the committed target or restored source activation"
                    );
                    var reason = await silo.CheckPrivateHealthAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                    Require(
                        condition: (reason.Length == 0),
                        reason: reason
                    );
                    var publication = await silo.PublishManagedReleaseAdmissionAsync(
                        token,
                        completeRecovery: recovered
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    Require(
                        condition: (publication == WorldReleaseAdmissionPublication.Opened),
                        reason: "group publication was refused"
                    );
                    await context.Response.WriteAsJsonAsync(
                        new WorldReleaseWorkerStatus(
                            managed.Group,
                            managed.ExpectedRelease,
                            operation,
                            state.PendingPhase,
                            silo.ReleaseAdmissionOpen
                        ),
                        token
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
            }
        } catch (InvalidOperationException error) {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync(
                error.Message,
                context.RequestAborted
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        return true;
    }
}
/// <summary>Identity and admission of the running managed worker, for exact-image deployment verification.</summary>
public sealed record WorldReleaseWorkerStatus(string Group, string Release, Guid? OperationId, WorldReleaseOperationPhase? Phase, bool AdmissionOpen);
/// <summary>The running worker's own per-world activation fence, checked against its current durable root.</summary>
public sealed record WorldReleaseWorkerFence(Guid Owner, string World, long Epoch, Guid Token, string RootVersion);
