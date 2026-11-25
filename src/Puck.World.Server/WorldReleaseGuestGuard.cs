using System.Text.Json.Serialization;

namespace Puck.World.Server;

/// <summary>What one delayed effect on a hosting machine (a bootstrap, a drain, a publication) was issued for: the
/// deployment group's durable operation as the controller saw it. A machine re-checks it against the group record
/// immediately before the effect, under the machine's release lock, so an effect Azure or any host delivers late runs only
/// while the operation it belongs to is still the current one.</summary>
/// <param name="Owner">The world owner whose private namespace holds the group record.</param>
/// <param name="Group">The deployment group's name.</param>
/// <param name="Operation">The pending operation the effect belongs to, or <see langword="null"/> for a bootstrap,
/// which names its release instead.</param>
/// <param name="Phases">The operation phases the effect may run in, or <see langword="null"/> for any.</param>
/// <param name="Source">The operation's source release the effect expects, or <see langword="null"/> to not check it.</param>
/// <param name="Target">The operation's target release the effect expects, or <see langword="null"/> to not check it.</param>
/// <param name="BootRelease">The release a bootstrap starts, or <see langword="null"/> for any other effect.</param>
public sealed record WorldReleaseGuestGuardRequest(
    [property: JsonPropertyName("owner")] Guid Owner,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("operation")] Guid? Operation = null,
    [property: JsonPropertyName("phases")] IReadOnlyList<WorldReleaseOperationPhase>? Phases = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("bootRelease")] string? BootRelease = null
);
/// <summary>Refuses a hosting machine's delayed effect whose durable release operation is no longer current. The group
/// record is read through <see cref="WorldReleaseGroupStore.LoadAsync"/>, the record's one reader, so a group whose
/// discard is unfinished refuses every effect, and the check reads the same record type the coordinator writes.</summary>
public static class WorldReleaseGuestGuard {
    /// <summary>Checks a request against the group's current record.</summary>
    /// <param name="groups">The owner's release group store.</param>
    /// <param name="request">The effect's request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="null"/> when the effect may run; otherwise why it is refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    public static async Task<string?> CheckAsync(WorldReleaseGroupStore groups, WorldReleaseGuestGuardRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: groups);
        ArgumentNullException.ThrowIfNull(argument: request);
        var record = (await groups.LoadAsync(cancellationToken: cancellationToken, deploymentGroup: request.Group).ConfigureAwait(continueOnCapturedContext: false))?.Record;

        return Check(record: record, request: request);
    }
    /// <summary>Checks a request against a group record.</summary>
    /// <param name="record">The group's record, or <see langword="null"/> when none exists.</param>
    /// <param name="request">The effect's request.</param>
    /// <returns><see langword="null"/> when the effect may run; otherwise why it is refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static string? Check(WorldReleaseGroupRecord? record, WorldReleaseGuestGuardRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);
        if ((record is null) || (record.DeploymentGroup != request.Group) || (record.Owner != request.Owner)) { return "no matching managed release group exists"; }
        if (record.PendingOperationId is not { } operation) {
            // A replacement machine may boot the admitted release after an OS failure; an obsolete bootstrap still cannot
            // boot a release that is no longer active.
            return (((request.BootRelease is { } boot) && (record.ActiveRelease == boot) && (record.Admission == WorldReleaseAdmissionState.Open))
                ? null
                : "no matching managed release operation is pending");
        }
        if ((request.Operation is { } expected) && (operation != expected)) { return "the release operation changed before the guest effect"; }
        var phase = record.PendingPhase;

        if ((request.Phases is { } phases) && ((phase is not { } current) || !phases.Contains(value: current))) { return "the release phase changed before the guest effect"; }
        if ((request.Source is not null) && (record.PendingSourceRelease != request.Source)) { return "the source release changed before the guest effect"; }
        if ((request.Target is not null) && (record.PendingTargetRelease != request.Target)) { return "the target release changed before the guest effect"; }
        if (request.BootRelease is { } release) {
            var candidate = ((phase is WorldReleaseOperationPhase.Activate or WorldReleaseOperationPhase.Verify or WorldReleaseOperationPhase.Commit) && (record.PendingTargetRelease == release));
            var recovery = ((phase == WorldReleaseOperationPhase.RecoverActivate) && (record.PendingSourceRelease == release));

            if (!(candidate || recovery)) { return "this retained bootstrap is not authorized in the current phase"; }
        }
        return null;
    }
}
