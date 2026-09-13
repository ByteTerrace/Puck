using System.Net;
using Microsoft.AspNetCore.Http;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Loopback worker control for the durable release coordinator. Requests carry an operation identifier;
/// the worker reads the authoritative group itself and never accepts caller-supplied release pointers or roots.</summary>
public sealed class WorldSiloReleaseControl(WorldSiloHost silo) {
    /// <summary>Handles the release control routes on the existing lifecycle listener. Returns false for other paths.</summary>
    public async Task<bool> HandleAsync(HttpContext context) {
        if (!context.Request.Path.StartsWithSegments("/release")) { return false; }
        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) || silo.Definition.Release is not { } managed) {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return true;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(silo.Definition.Lifecycle?.ShutdownSeconds ?? 120));
        var token = deadline.Token;
        try {
            var current = await silo.ReadManagedReleaseAsync(token).ConfigureAwait(false);
            var state = current.Record;
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/release/status") {
                await context.Response.WriteAsJsonAsync(new WorldReleaseWorkerStatus(managed.Group, managed.ExpectedRelease,
                    state.PendingOperationId, state.PendingPhase, silo.ReleaseAdmissionOpen), token).ConfigureAwait(false);
                return true;
            }
            var parts = context.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !Guid.TryParseExact(parts[2], "D", out var operation) || operation == Guid.Empty) {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return true;
            }
            if (parts[1] == "fixture" && HttpMethods.IsPost(context.Request.Method)) {
                var fixture = await silo.ExportReleaseFixtureAsync(operation, token).ConfigureAwait(false);
                await context.Response.WriteAsJsonAsync(new { fixture.RequestId, fixture.Identity, fixture.Release }, token).ConfigureAwait(false);
                return true;
            }
            if (operation != state.PendingOperationId) {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return true;
            }
            switch (parts[1]) {
                case "drain" when HttpMethods.IsPost(context.Request.Method):
                    Require(state.PendingPhase == WorldReleaseOperationPhase.Drain && state.PendingSourceRelease == managed.ExpectedRelease,
                        "drain requires the current source and its durable drain operation");
                    await silo.DrainAsync(token).ConfigureAwait(false);
                    var roots = await silo.CaptureReleaseRootsAsync(operation, token).ConfigureAwait(false);
                    await context.Response.WriteAsJsonAsync(roots, token).ConfigureAwait(false);
                    break;
                case "fences" when HttpMethods.IsGet(context.Request.Method):
                    Require(state.PendingPhase is WorldReleaseOperationPhase.Verify or WorldReleaseOperationPhase.Commit && state.PendingTargetRelease == managed.ExpectedRelease,
                        "fence census requires the current candidate verification or commit");
                    var census = await silo.CaptureReleaseFencesAsync(token).ConfigureAwait(false);
                    await context.Response.WriteAsJsonAsync(census.Select(item => new WorldReleaseWorkerFence(item.Identity.Owner,
                        item.Identity.World.Value, item.Fence.Epoch, item.Fence.Token, item.Fence.RootVersion)).ToArray(), token).ConfigureAwait(false);
                    break;
                case "publish" when HttpMethods.IsPost(context.Request.Method):
                    var recovered = state.PendingPhase == WorldReleaseOperationPhase.RecoverActivate && state.PendingSourceRelease == managed.ExpectedRelease;
                    Require(recovered || (state.PendingPhase == WorldReleaseOperationPhase.Commit && state.PendingCommitted && state.PendingTargetRelease == managed.ExpectedRelease),
                        "publication requires the committed target or restored source activation");
                    var reason = await silo.CheckPrivateHealthAsync(token).ConfigureAwait(false);
                    Require(reason.Length == 0, reason);
                    var publication = await silo.PublishManagedReleaseAdmissionAsync(token, completeRecovery: recovered).ConfigureAwait(false);
                    Require(publication == WorldReleaseAdmissionPublication.Opened, "group publication was refused");
                    await context.Response.WriteAsJsonAsync(new WorldReleaseWorkerStatus(managed.Group, managed.ExpectedRelease,
                        operation, state.PendingPhase, silo.ReleaseAdmissionOpen), token).ConfigureAwait(false);
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
            }
        } catch (InvalidOperationException error) {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync(error.Message, context.RequestAborted).ConfigureAwait(false);
        }
        return true;
    }

    private static void Require(bool condition, string reason) {
        if (!condition) { throw new InvalidOperationException(reason); }
    }
}

/// <summary>Identity and admission of the running managed worker, for exact-image deployment verification.</summary>
public sealed record WorldReleaseWorkerStatus(string Group, string Release, Guid? OperationId, WorldReleaseOperationPhase? Phase, bool AdmissionOpen);

/// <summary>The running worker's own per-world activation fence, checked against its current durable root.</summary>
public sealed record WorldReleaseWorkerFence(Guid Owner, string World, long Epoch, Guid Token, string RootVersion);
