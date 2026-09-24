using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Puck.Assets;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>The exact coherent checkpoint selected by an explicitly acknowledged intentional rewind.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldReleaseRestoreSelection(
    [property: JsonPropertyName("pointId")] Guid PointId,
    [property: JsonPropertyName("identity")] string Identity);
/// <summary>The durable deployment-group state that fences release publication and admission as one decision.</summary>
public sealed record WorldReleaseGroupRecord {
    [JsonPropertyName("activeRelease")] public required string? ActiveRelease { get; init; }
    [JsonPropertyName("admission")] public WorldReleaseAdmissionState Admission { get; init; }
    [JsonPropertyName("authorityLease")] public Guid AuthorityLease { get; init; }
    [JsonPropertyName("deploymentGroup")] public required string DeploymentGroup { get; init; }
    /// <summary>Whether the retained operation still requires coordination. A committed, admitted operation
    /// remains recorded for rollback but does not prevent a fresh qualification capture or rollback preflight.</summary>
    [JsonIgnore]
    public bool HasUnfinishedOperation => ((PendingOperationId is not null) &&
        ((PendingPhase != WorldReleaseOperationPhase.Commit) || !PendingCommitted || (Admission != WorldReleaseAdmissionState.Open)));
    [JsonPropertyName("owner")] public required Guid Owner { get; init; }
    [JsonPropertyName("pendingCommitted")] public bool PendingCommitted { get; init; }
    [JsonPropertyName("pendingFailure")] public string? PendingFailure { get; init; }
    [JsonPropertyName("pendingOperationId")] public Guid? PendingOperationId { get; init; }
    [JsonPropertyName("pendingPhase")] public WorldReleaseOperationPhase? PendingPhase { get; init; }
    [JsonPropertyName("pendingSourceRelease")] public string? PendingSourceRelease { get; init; }
    [JsonPropertyName("pendingTargetRelease")] public string? PendingTargetRelease { get; init; }
    [JsonPropertyName("previousRelease")] public string? PreviousRelease { get; init; }
    [JsonPropertyName("restorePoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldReleaseRestoreSelection? RestorePoint { get; init; }
    [JsonPropertyName("revision")] public long Revision { get; init; }
    [JsonPropertyName("rollbackEligible")] public bool RollbackEligible { get; init; }
    [JsonPropertyName("schema")] public required string Schema { get; init; }

    [JsonPropertyName("recoveryRoots")] public IReadOnlyDictionary<string, string> RecoveryRoots { get; init; } = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
    [JsonPropertyName("history")] public IReadOnlyList<WorldReleaseGroupHistoryEntry> History { get; init; } = [];

    /// <summary>Whether an identifier already owns pending or retained work. New operations must never reuse
    /// an identifier because immutable recovery roots are addressed by that identifier.</summary>
    public bool ContainsOperation(Guid operationId) => ((PendingOperationId == operationId) || History.Any(predicate: entry => (entry.OperationId == operationId)));
}
/// <summary>Immutable history retained after an operation is finalized or recovered.</summary>
public sealed record WorldReleaseGroupHistoryEntry {
    [JsonPropertyName("operationId")] public required Guid OperationId { get; init; }
    [JsonPropertyName("recoveryRoots")] public IReadOnlyDictionary<string, string> RecoveryRoots { get; init; } = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
    [JsonPropertyName("restorePoint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldReleaseRestoreSelection? RestorePoint { get; init; }
    [JsonPropertyName("result")] public required string Result { get; init; }
    [JsonPropertyName("revision")] public long Revision { get; init; }
    [JsonPropertyName("sourceRelease")] public string? SourceRelease { get; init; }
    [JsonPropertyName("targetRelease")] public required string TargetRelease { get; init; }
}
/// <summary>A group state plus its conditional-write token.</summary>
public readonly record struct WorldReleaseGroupSnapshot(WorldReleaseGroupRecord Record, string VersionToken);
/// <summary>Named result from a guarded deployment-group state change.</summary>
public readonly record struct WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind Kind, string Detail, WorldReleaseGroupSnapshot? Snapshot = null) {
    public bool Ok => (Kind == WorldReleaseOperationOutcomeKind.Ok);
}
/// <summary>Derives the group activation identity from the exact fresh per-world fence census.</summary>
public static class WorldReleaseFenceClaim {
    public static Guid Compute(IEnumerable<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)> fences) {
        ArgumentNullException.ThrowIfNull(fences);
        var census = fences.ToArray();

        if (census.Length == 0) { return Guid.Empty; }
        if (
            census.Any(predicate: item => ((item.Identity.Owner == Guid.Empty) || string.IsNullOrWhiteSpace(value: item.Identity.World.Value) ||
            (item.Fence.Epoch <= 0) || (item.Fence.Token == Guid.Empty))) ||
            (census.Select(selector: item => item.Identity).Distinct().Count() != census.Length)
        ) {
            throw new ArgumentException(
                message: "A fence census requires unique world identities and owned activation fences.",
                paramName: nameof(fences)
            );
        }
        var canonical = string.Join(
            separator: "\n",
            values: census.OrderBy(keySelector: item => item.Identity.Owner).ThenBy(
                item => item.Identity.World.Value,
                StringComparer.Ordinal
            ).Select(selector: item => $"{item.Identity.Owner:D}/{item.Identity.World.Value}/{item.Fence.Epoch}/{item.Fence.Token:D}")
        );
        var hash = SHA256.HashData(source: Encoding.UTF8.GetBytes(s: canonical));

        return new Guid(b: hash.AsSpan(
            length: 16,
            start: 0
        ));
    }
}
/// <summary>
/// Persists the single mutable deployment-group root in the existing private object store. This root is the only
/// authority for active/previous release pointers, pending operation state, and public admission.
/// </summary>
public sealed partial class WorldReleaseGroupStore {
    public const string Schema = "puck.world.release-group.v1";

    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;
    private readonly Guid m_owner;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = null, WriteIndented = false };

    public WorldReleaseGroupStore(IObjectBlobStore store, ObjectStorageTarget target, Guid owner) {
        m_store = (store ?? throw new ArgumentNullException(paramName: nameof(store)));
        m_target = (target ?? throw new ArgumentNullException(paramName: nameof(target)));
        if (owner == Guid.Empty) {
            throw new ArgumentException(
            message: "The deployment-group owner cannot be empty.",
            paramName: nameof(owner)
        );
        }
        m_owner = owner;
    }

    /// <summary>Reads and validates a serialized group root for offline status inspection.</summary>
    public static WorldReleaseGroupRecord DeserializeValidated(ReadOnlySpan<byte> bytes) {
        try {
            ValidateJson(bytes: bytes);
            var record = (JsonSerializer.Deserialize<WorldReleaseGroupRecord>(
                options: Options,
                utf8Json: bytes
            ) ?? throw new InvalidDataException(message: "release group record is empty"));

            Validate(
                record,
                record.DeploymentGroup,
                record.Owner
            );
            return record;
        } catch (JsonException error) {
            throw new InvalidDataException(
                innerException: error,
                message: "release group record is malformed"
            );
        }
    }
    /// <summary>Loads and validates the guarded group root.</summary>
    public async Task<WorldReleaseGroupSnapshot?> LoadAsync(string deploymentGroup, CancellationToken cancellationToken = default) {
        var content = await m_store.ReadAsync(
            m_target,
            Address(group: deploymentGroup),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (content is not { } found) { return null; }
        var record = DeserializeValidated(bytes: found.Content.Span);

        Validate(
            expectedGroup: deploymentGroup,
            expectedOwner: m_owner,
            record: record
        );
        return new(
            Record: record,
            VersionToken: (found.VersionToken ?? throw new InvalidDataException(message: "release group record has no version token"))
        );
    }
    /// <summary>Creates an adopted serving group, or a closed bootstrap group when no active release exists.</summary>
    public async Task<WorldReleaseGroupOutcome> CreateAsync(string deploymentGroup, string? activeRelease, CancellationToken cancellationToken = default) {
        ValidateGroup(group: deploymentGroup);
        if (
            (activeRelease is not null) &&
            string.IsNullOrWhiteSpace(value: activeRelease)
        ) {
            throw new ArgumentException(
            message: "An active release must be non-empty when supplied.",
            paramName: nameof(activeRelease)
        );
        }
        var record = new WorldReleaseGroupRecord {
            ActiveRelease = activeRelease,
            Admission = ((activeRelease is null)
            ? WorldReleaseAdmissionState.Closed
            : WorldReleaseAdmissionState.Open),
            AuthorityLease = Guid.Empty,
            DeploymentGroup = deploymentGroup,
            Owner = m_owner,
            Revision = 0,
            Schema = Schema,
        };
        var result = await m_store.WriteAsync(
            m_target,
            Address(group: deploymentGroup),
            Serialize(record: record),
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (result.Succeeded
            ? new(
                WorldReleaseOperationOutcomeKind.Ok,
                string.Empty,
                new(
                    Record: record,
                    VersionToken: (result.VersionToken ?? string.Empty)
                )
            )
            : new(
                (result.PreconditionFailed
                ? WorldReleaseOperationOutcomeKind.PreconditionFailed
                : WorldReleaseOperationOutcomeKind.AlreadyExists),
                "deployment-group state already exists"
            )
        );
    }
    /// <summary>Begins one operation from the active release. Prepare preserves source admission until Drain.</summary>
    public async Task<WorldReleaseGroupOutcome> BeginAsync(WorldReleaseGroupSnapshot current, Guid operationId, string targetRelease, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(current.Record);
        ValidateIdentity(
            group: current.Record.DeploymentGroup,
            release: targetRelease
        );
        if (
            (operationId == Guid.Empty) ||
            (current.Record.PendingOperationId is not null) ||
            current.Record.RollbackEligible
        ) {
            return new(
                WorldReleaseOperationOutcomeKind.Conflict,
                "the deployment group already owns a pending or retained rollback operation"
            );
        }
        if (current.Record.ContainsOperation(operationId: operationId)) {
            return new(
                WorldReleaseOperationOutcomeKind.Conflict,
                "operation ID already belongs to retained work; inspect status and use a new ID for a new operation"
            );
        }
        if (string.IsNullOrWhiteSpace(value: targetRelease)) {
            return new(
                WorldReleaseOperationOutcomeKind.Conflict,
                "the target release is required"
            );
        }
        if (
            (current.Record.ActiveRelease is null) ||
            !string.Equals(
            a: current.Record.ActiveRelease,
            b: targetRelease,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            var next = current.Record with {
                PendingOperationId = operationId,
                PendingSourceRelease = current.Record.ActiveRelease,
                PendingTargetRelease = targetRelease,
                PendingPhase = WorldReleaseOperationPhase.Prepare,
                PendingCommitted = false,
                RecoveryRoots = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal),
                PendingFailure = null,
                // Prepare is a read-only preflight. The source remains the serving release until Drain coordinates the
                // maintenance boundary and closes admission.
                Admission = current.Record.Admission,
                Revision = checked((current.Record.Revision + 1)),
            };

            return await WriteAsync(
                cancellationToken: cancellationToken,
                current: current,
                next: next
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        return new(
            WorldReleaseOperationOutcomeKind.Conflict,
            "the target release is already active"
        );
    }
    /// <summary>Begins a new rollback operation to the retained predecessor while leaving admission open for preflight.</summary>
    public async Task<WorldReleaseGroupOutcome> BeginRollbackAsync(WorldReleaseGroupSnapshot current, Guid operationId, CancellationToken cancellationToken = default) {
        var old = current.Record;

        if (old.ContainsOperation(operationId: operationId)) {
            return new(
                WorldReleaseOperationOutcomeKind.Conflict,
                "operation ID already belongs to pending or retained work; resume that operation instead of beginning another"
            );
        }
        if (
            (operationId == Guid.Empty) ||
            !old.RollbackEligible ||
            (old.Admission != WorldReleaseAdmissionState.Open) ||
            ((old.PendingOperationId is not null) && ((old.PendingPhase != WorldReleaseOperationPhase.Commit) || !old.PendingCommitted)) ||
            string.IsNullOrWhiteSpace(value: old.PreviousRelease) ||
            string.Equals(
            a: old.ActiveRelease,
            b: old.PreviousRelease,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return new(
                WorldReleaseOperationOutcomeKind.Conflict,
                "rollback requires an admitted committed release with a retained predecessor"
            );
        }

        // Preserve the completed operation and its recovery references before reusing the single pending slot.
        var history = ((old.PendingOperationId is { } completed)
            ? old.History.Append(element: new WorldReleaseGroupHistoryEntry {
                OperationId = completed,
                RecoveryRoots = old.RecoveryRoots,
                RestorePoint = old.RestorePoint,
                Result = "rollback-started",
                Revision = checked((old.Revision + 1)),
                SourceRelease = old.PendingSourceRelease!,
                TargetRelease = old.PendingTargetRelease!,
            }).ToArray()
            : old.History
        );
        var next = old with {
            PendingOperationId = operationId,
            RestorePoint = null,
            PendingSourceRelease = old.ActiveRelease,
            PendingTargetRelease = old.PreviousRelease,
            PendingPhase = WorldReleaseOperationPhase.Prepare,
            PendingCommitted = false,
            PendingFailure = null,
            RecoveryRoots = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal),
            RollbackEligible = false,
            History = history,
            Revision = checked((old.Revision + 1)),
        };

        return await WriteAsync(
            cancellationToken: cancellationToken,
            current: current,
            next: next
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Advances the pending operation with an immutable request and guarded revision.</summary>
    public Task<WorldReleaseGroupOutcome> AdvanceAsync(WorldReleaseGroupSnapshot current, WorldReleaseGroupRecord next, CancellationToken cancellationToken = default) {
        Validate(
            next,
            current.Record.DeploymentGroup,
            m_owner
        );
        var old = current.Record;

        if (
            (next.RestorePoint != old.RestorePoint) ||
            (next.Owner != old.Owner) ||
            (next.DeploymentGroup != old.DeploymentGroup) ||
            (next.ActiveRelease != old.ActiveRelease) ||
            (next.PreviousRelease != old.PreviousRelease) ||
            (next.PendingOperationId != old.PendingOperationId) ||
            (next.PendingSourceRelease != old.PendingSourceRelease) ||
            (next.PendingTargetRelease != old.PendingTargetRelease) ||
            (next.PendingCommitted != old.PendingCommitted) ||
            (next.PendingFailure != old.PendingFailure) ||
            (next.RollbackEligible != old.RollbackEligible) ||
            (next.Revision != (old.Revision + 1)) ||
            (next.AuthorityLease != old.AuthorityLease) ||
            !SameHistory(
            left: old.History,
            right: next.History
        )
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "deployment-group identity, pointers, history, or revision is immutable"
            ));
        }
        if (
            !WorldReleaseCompatibility.SameInventory(
            left: old.RecoveryRoots,
            right: next.RecoveryRoots
        ) &&
            !((old.RecoveryRoots.Count == 0) && (next.RecoveryRoots.Count > 0) && (((old.PendingPhase == WorldReleaseOperationPhase.Prepare) && (next.PendingPhase == WorldReleaseOperationPhase.Drain)) || ((old.PendingPhase == WorldReleaseOperationPhase.Drain) && (next.PendingPhase == WorldReleaseOperationPhase.Drain)) || ((old.PendingPhase == WorldReleaseOperationPhase.Drain) && (next.PendingPhase == WorldReleaseOperationPhase.Activate))))
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "recovery roots may be captured once during drain and are immutable afterward"
            ));
        }
        if (
            (old.Admission == WorldReleaseAdmissionState.Open) &&
            (next.Admission != WorldReleaseAdmissionState.Open)
        ) {
            // Closing is allowed only when a new operation claims the group (BeginAsync). It cannot be an old writer's replay.
            if (
                (next.PendingOperationId is null) ||
                (old.PendingOperationId != next.PendingOperationId) ||
                (next.PendingPhase != WorldReleaseOperationPhase.Drain)
            ) {
                return Task.FromResult(result: new WorldReleaseGroupOutcome(
                    WorldReleaseOperationOutcomeKind.Conflict,
                    "an older writer cannot close group admission"
                ));
            }
        }
        if (!CanAdvance(
            next: next,
            old: old
        )) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
            WorldReleaseOperationOutcomeKind.Conflict,
            "release phase cannot regress or skip"
        ));
        }
        return WriteAsync(
            cancellationToken: cancellationToken,
            current: current,
            next: next
        );
    }
    /// <summary>Persists a pre-commit recovery phase before any source restoration effect begins.</summary>
    public Task<WorldReleaseGroupOutcome> BeginRecoveryAsync(WorldReleaseGroupSnapshot current, string failure, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        var old = current.Record;

        if (
            (old.PendingOperationId is null) ||
            old.PendingCommitted ||
            (old.PendingPhase is WorldReleaseOperationPhase.Commit or WorldReleaseOperationPhase.Finalized) ||
            (old.RecoveryRoots.Count == 0)
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "recovery requires a pre-commit operation with protected roots"
            ));
        }
        var next = old with { PendingPhase = WorldReleaseOperationPhase.Recover, PendingFailure = failure, Admission = WorldReleaseAdmissionState.Closed, Revision = checked((old.Revision + 1)) };

        return WriteAsync(
            cancellationToken: cancellationToken,
            current: current,
            next: next
        );
    }
    /// <summary>Records that every protected source root has been restored before any source activation is allowed.</summary>
    public Task<WorldReleaseGroupOutcome> RecordRecoveryRestoredAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) {
        if (
            (current.Record.PendingPhase != WorldReleaseOperationPhase.Recover) ||
            (current.Record.PendingSourceRelease is null)
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "source restoration requires a pending recovery with a source"
            ));
        }
        return WriteAsync(
            current,
            current.Record with { PendingPhase = WorldReleaseOperationPhase.RecoverActivate, Revision = checked((current.Record.Revision + 1)) },
            cancellationToken
        );
    }
    /// <summary>Completes a verified source recovery, optionally opening its group at the same guarded boundary.</summary>
    public Task<WorldReleaseGroupOutcome> CompleteRecoveryAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default, Guid? sourceAuthorityLease = null) {
        var old = current.Record;

        if (
            (old.PendingOperationId is null) ||
            old.PendingCommitted ||
            (old.RecoveryRoots.Count == 0) ||
            ((old.PendingSourceRelease is null)
            ? (old.PendingPhase != WorldReleaseOperationPhase.Recover)
            : (old.PendingPhase != WorldReleaseOperationPhase.RecoverActivate)) ||
            ((sourceAuthorityLease is not null) && ((sourceAuthorityLease == Guid.Empty) || (old.PendingSourceRelease is null)))
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "recovery must be privately completed before its durable operation can close"
            ));
        }
        var history = old.History.Append(element: new WorldReleaseGroupHistoryEntry {
            OperationId = old.PendingOperationId.Value,
            RecoveryRoots = old.RecoveryRoots,
            RestorePoint = old.RestorePoint,
            Result = $"recovered: {(old.PendingFailure ?? "unspecified failure")}",
            Revision = checked((old.Revision + 1)),
            SourceRelease = old.PendingSourceRelease!,
            TargetRelease = old.PendingTargetRelease!,
        }).ToArray();
        var rollbackAttempt = ((old.PreviousRelease is not null) && string.Equals(
            a: old.PendingTargetRelease,
            b: old.PreviousRelease,
            comparisonType: StringComparison.Ordinal
        ));
        var next = old with {
            RestorePoint = null,
            PendingOperationId = null,
            PendingSourceRelease = null,
            PendingTargetRelease = null,
            PendingPhase = null,
            PendingCommitted = false,
            PendingFailure = null,
            RecoveryRoots = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal),
            Admission = ((sourceAuthorityLease is null)
            ? WorldReleaseAdmissionState.Closed
            : WorldReleaseAdmissionState.Open),
            AuthorityLease = (sourceAuthorityLease ?? old.AuthorityLease),
            RollbackEligible = ((old.RestorePoint is not null)
            ? old.RollbackEligible
            : rollbackAttempt),
            History = history,
            Revision = checked((old.Revision + 1)),
        };

        return WriteAsync(
            cancellationToken: cancellationToken,
            current: current,
            next: next
        );
    }
    /// <summary>Publishes target pointers at the commit boundary; admission remains closed until publication is explicit.</summary>
    public Task<WorldReleaseGroupOutcome> CommitAsync(WorldReleaseGroupSnapshot current, IEnumerable<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)> fences, CancellationToken cancellationToken = default) {
        var freshAuthorityLease = WorldReleaseFenceClaim.Compute(fences: fences);
        var old = current.Record;

        if (
            (old.PendingOperationId is null) ||
            (old.PendingPhase is not WorldReleaseOperationPhase.Verify) ||
            (old.RecoveryRoots.Count == 0) ||
            (freshAuthorityLease == Guid.Empty) ||
            (freshAuthorityLease == old.AuthorityLease)
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "commit requires a verified operation, recovery roots, and a fresh group authority lease"
            ));
        }
        var sameReleaseRestore = ((old.RestorePoint is not null) && (old.PendingSourceRelease == old.PendingTargetRelease));
        var next = old with {
            ActiveRelease = old.PendingTargetRelease!,
            PreviousRelease = (sameReleaseRestore
            ? old.PreviousRelease
            : old.PendingSourceRelease),
            PendingPhase = WorldReleaseOperationPhase.Commit,
            PendingCommitted = true,
            Admission = WorldReleaseAdmissionState.Closed,
            RollbackEligible = (sameReleaseRestore
            ? old.RollbackEligible
            : (old.PendingSourceRelease is not null)),
            AuthorityLease = freshAuthorityLease,
            Revision = checked((old.Revision + 1)),
        };

        return WriteAsync(
            cancellationToken: cancellationToken,
            current: current,
            next: next
        );
    }
    /// <summary>Opens public/federation admission only after rechecking the exact committed target and fresh fence.</summary>
    public async Task<WorldReleaseGroupOutcome> OpenAdmissionAsync(WorldReleaseGroupSnapshot current, string expectedRelease, Guid operationId, Guid authorityLease, CancellationToken cancellationToken = default) {
        var old = current.Record;

        if (
            (old.PendingOperationId != operationId) ||
            (old.PendingPhase != WorldReleaseOperationPhase.Commit) ||
            !old.PendingCommitted ||
            !string.Equals(
            a: old.ActiveRelease,
            b: expectedRelease,
            comparisonType: StringComparison.Ordinal
        ) ||
            (authorityLease == Guid.Empty)
        ) {
            return new(
                WorldReleaseOperationOutcomeKind.Conflict,
                "candidate identity or authority fence does not match the committed group state"
            );
        }
        // An idempotent publication still needs a guarded write. A stale open
        // snapshot may predate Drain or a newer rollback and must not announce
        // success without proving that its version token is current.
        var next = old with { Admission = WorldReleaseAdmissionState.Open, AuthorityLease = authorityLease, Revision = checked((old.Revision + 1)) };

        return await WriteAsync(
            cancellationToken: cancellationToken,
            current: current,
            next: next
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Rebinds an already serving source after restart under a fresh authority fence.</summary>
    public Task<WorldReleaseGroupOutcome> RebindAdmissionAsync(WorldReleaseGroupSnapshot current, string expectedRelease, Guid freshAuthorityLease, CancellationToken cancellationToken = default) {
        var old = current.Record;

        if (
            (old.PendingOperationId is not null) ||
            (old.Admission != WorldReleaseAdmissionState.Open) ||
            !string.Equals(
            a: old.ActiveRelease,
            b: expectedRelease,
            comparisonType: StringComparison.Ordinal
        ) ||
            (freshAuthorityLease == Guid.Empty)
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "serving release or authority fence changed before restart rebind"
            ));
        }
        return WriteAsync(
            current,
            old with { AuthorityLease = freshAuthorityLease, Revision = checked((old.Revision + 1)) },
            cancellationToken
        );
    }
    /// <summary>Rebinds the still-serving source during prepare, before drain closes admission.</summary>
    public Task<WorldReleaseGroupOutcome> RebindPrepareAdmissionAsync(WorldReleaseGroupSnapshot current, string expectedRelease, Guid freshAuthorityLease, CancellationToken cancellationToken = default) {
        var old = current.Record;

        if (
            (old.PendingOperationId is null) ||
            (old.PendingPhase != WorldReleaseOperationPhase.Prepare) ||
            (old.Admission != WorldReleaseAdmissionState.Open) ||
            !string.Equals(
            a: old.ActiveRelease,
            b: expectedRelease,
            comparisonType: StringComparison.Ordinal
        ) ||
            (freshAuthorityLease == Guid.Empty)
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "prepare source or group lease changed before restart rebind"
            ));
        }
        return WriteAsync(
            current,
            old with { AuthorityLease = freshAuthorityLease, Revision = checked((old.Revision + 1)) },
            cancellationToken
        );
    }
    /// <summary>Reopens the recovered source only after a fresh host fence has been established.</summary>
    public Task<WorldReleaseGroupOutcome> OpenRecoveredAdmissionAsync(WorldReleaseGroupSnapshot current, string expectedRelease, Guid freshAuthorityLease, CancellationToken cancellationToken = default) {
        var old = current.Record;

        if (
            (old.PendingOperationId is not null) ||
            (old.Admission != WorldReleaseAdmissionState.Closed) ||
            !string.Equals(
            a: old.ActiveRelease,
            b: expectedRelease,
            comparisonType: StringComparison.Ordinal
        ) ||
            (freshAuthorityLease == Guid.Empty) ||
            (freshAuthorityLease == old.AuthorityLease)
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "recovered source or authority fence does not match the group state"
            ));
        }
        return WriteAsync(
            current,
            old with { Admission = WorldReleaseAdmissionState.Open, AuthorityLease = freshAuthorityLease, Revision = checked((old.Revision + 1)) },
            cancellationToken
        );
    }
    /// <summary>Ends the admitted release's rollback window, retaining history and artifacts, including after a failed rollback recovered the source.</summary>
    public Task<WorldReleaseGroupOutcome> FinalizeAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) {
        var old = current.Record;

        if (
            (old.PendingOperationId is null) &&
            old.RollbackEligible &&
            (old.Admission == WorldReleaseAdmissionState.Open)
        ) {
            return WriteAsync(
                current,
                old with { RollbackEligible = false, Revision = checked((old.Revision + 1)) },
                cancellationToken
            );
        }
        if (
            (old.PendingOperationId is null) ||
            (old.PendingPhase != WorldReleaseOperationPhase.Commit) ||
            !old.PendingCommitted ||
            (old.Admission != WorldReleaseAdmissionState.Open)
        ) {
            return Task.FromResult(result: new WorldReleaseGroupOutcome(
                WorldReleaseOperationOutcomeKind.Conflict,
                "only an admitted committed operation can be finalized"
            ));
        }
        var history = old.History.Append(element: new WorldReleaseGroupHistoryEntry { OperationId = old.PendingOperationId.Value, RecoveryRoots = old.RecoveryRoots, RestorePoint = old.RestorePoint, Result = "committed", Revision = (old.Revision + 1), SourceRelease = old.PendingSourceRelease!, TargetRelease = old.PendingTargetRelease! }).ToArray();
        var next = old with { RestorePoint = null, PendingOperationId = null, PendingSourceRelease = null, PendingTargetRelease = null, PendingPhase = null, PendingCommitted = false, PendingFailure = null, RecoveryRoots = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal), RollbackEligible = false, History = history, Revision = checked((old.Revision + 1)) };

        return WriteAsync(
            cancellationToken: cancellationToken,
            current: current,
            next: next
        );
    }

    private async Task<WorldReleaseGroupOutcome> WriteAsync(WorldReleaseGroupSnapshot current, WorldReleaseGroupRecord next, CancellationToken cancellationToken) {
        Validate(
            next,
            current.Record.DeploymentGroup,
            m_owner
        );
        var result = await m_store.WriteAsync(
            m_target,
            Address(group: next.DeploymentGroup),
            Serialize(record: next),
            ObjectBlobWriteMode.Overwrite,
            current.VersionToken,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (result.Succeeded
            ? new(
                WorldReleaseOperationOutcomeKind.Ok,
                string.Empty,
                new(
                    Record: next,
                    VersionToken: (result.VersionToken ?? string.Empty)
                )
            )
            : new(
                WorldReleaseOperationOutcomeKind.PreconditionFailed,
                "deployment-group state changed before the guarded write"
            )
        );
    }
    private static bool CanAdvance(WorldReleaseGroupRecord old, WorldReleaseGroupRecord next) {
        if (old.PendingOperationId is null) { return false; }
        if (next.PendingPhase == old.PendingPhase) { return true; }
        return (old.PendingPhase, next.PendingPhase) switch {
            (WorldReleaseOperationPhase.Prepare, WorldReleaseOperationPhase.Drain) => true,
            (WorldReleaseOperationPhase.Drain, WorldReleaseOperationPhase.Activate) => (next.RecoveryRoots.Count > 0),
            (WorldReleaseOperationPhase.Activate, WorldReleaseOperationPhase.Verify) => (next.RecoveryRoots.Count > 0),
            _ => false,
        };
    }
    private static bool SameHistory(IReadOnlyList<WorldReleaseGroupHistoryEntry> left, IReadOnlyList<WorldReleaseGroupHistoryEntry> right) =>
        ((left.Count == right.Count) && left.Zip(second: right).All(predicate: pair =>
            ((pair.First.OperationId == pair.Second.OperationId) &&
            (pair.First.RestorePoint == pair.Second.RestorePoint) &&
            string.Equals(
            a: pair.First.SourceRelease,
            b: pair.Second.SourceRelease,
            comparisonType: StringComparison.Ordinal
        ) &&
            string.Equals(
            a: pair.First.TargetRelease,
            b: pair.Second.TargetRelease,
            comparisonType: StringComparison.Ordinal
        ) &&
            string.Equals(
            a: pair.First.Result,
            b: pair.Second.Result,
            comparisonType: StringComparison.Ordinal
        ) &&
            (pair.First.Revision == pair.Second.Revision) &&
            WorldReleaseCompatibility.SameInventory(
            left: pair.First.RecoveryRoots,
            right: pair.Second.RecoveryRoots
        ))));
    private ObjectBlobAddress Address(string group) {
        ValidateGroup(group: group);
        return new(
            Key: $"{WorldOwnedWorldSync.HostedPrivateNamespace}/release-groups/{group}.json",
            ObjectId: m_owner
        );
    }
    private static byte[] Serialize(WorldReleaseGroupRecord record) => Encoding.UTF8.GetBytes(s: JsonSerializer.Serialize(
        options: Options,
        value: record
    ));
    private static void ValidateIdentity(string group, string release) {
        ValidateGroup(group: group);
        if (string.IsNullOrWhiteSpace(value: release)) {
            throw new ArgumentException(
            message: "A release identity is required.",
            paramName: nameof(release)
        );
        }
    }
    private static void ValidateGroup(string group) {
        if (
            string.IsNullOrWhiteSpace(value: group) ||
            group.Any(predicate: char.IsWhiteSpace) ||
            group.Contains(
            comparisonType: StringComparison.Ordinal,
            value: ".."
        ) ||
            group.Contains(value: '/')
        ) {
            throw new ArgumentException(
            message: "Deployment group must be one safe name.",
            paramName: nameof(group)
        );
        }
    }
    private static void Validate(WorldReleaseGroupRecord record, string expectedGroup, Guid expectedOwner) {
        if (
            !string.Equals(
            a: record.Schema,
            b: Schema,
            comparisonType: StringComparison.Ordinal
        ) ||
            (record.Owner == Guid.Empty) ||
            (record.Owner != expectedOwner) ||
            !string.Equals(
            a: record.DeploymentGroup,
            b: expectedGroup,
            comparisonType: StringComparison.Ordinal
        ) ||
            (record.ActiveRelease is { Length: 0 }) ||
            (record.Revision < 0) ||
            (record.History is null) ||
            (record.RecoveryRoots is null)
        ) { throw new InvalidDataException(message: "release group record fields are incomplete"); }
        if (
            (record.ActiveRelease is null) &&
            ((record.PreviousRelease is not null) || (record.Admission != WorldReleaseAdmissionState.Closed) || record.RollbackEligible)
        ) { throw new InvalidDataException(message: "a bootstrap group cannot have an active release or open admission"); }
        if (
            (record.Admission == WorldReleaseAdmissionState.Open) &&
            (record.PendingOperationId is not null) &&
            (record.PendingPhase is not WorldReleaseOperationPhase.Prepare) &&
            ((record.PendingPhase != WorldReleaseOperationPhase.Commit) || !record.PendingCommitted)
        ) { throw new InvalidDataException(message: "open admission requires prepare or a committed pending operation"); }
        if (
            (record.PendingOperationId is null) &&
            ((record.PendingSourceRelease is not null) || (record.PendingTargetRelease is not null) || (record.PendingPhase is not null) || record.PendingCommitted || (record.RecoveryRoots.Count != 0))
        ) { throw new InvalidDataException(message: "pending operation fields are incomplete"); }
        if (
            (record.PendingOperationId is null) &&
            (record.PendingFailure is not null)
        ) { throw new InvalidDataException(message: "a pending failure requires a pending operation"); }
        if (
            (record.RestorePoint is { } point) &&
            ((record.PendingOperationId is null) || (point.PointId == Guid.Empty) || !ContentPin.TryParse(pin: out _, text: point.Identity))
        ) { throw new InvalidDataException(message: "restore selection requires an exact point and pending operation"); }
        if (
            (record.PendingOperationId is not null) &&
            ((record.PendingOperationId == Guid.Empty) || string.IsNullOrWhiteSpace(value: record.PendingTargetRelease) || (record.PendingPhase is null) || !Enum.IsDefined(value: record.PendingPhase.Value) || ((record.RestorePoint is null) && (record.PendingSourceRelease is not null) && (record.PendingSourceRelease == record.PendingTargetRelease)))
        ) { throw new InvalidDataException(message: "pending operation identity is incomplete"); }
        var sameReleaseRestore = ((record.RestorePoint is not null) && (record.PendingSourceRelease == record.PendingTargetRelease));

        if (
            (record.PendingOperationId is not null) &&
            (record.PendingSourceRelease != ((record.PendingCommitted && !sameReleaseRestore)
            ? record.PreviousRelease
            : record.ActiveRelease))
        ) { throw new InvalidDataException(message: "pending source must match the durable release pointers"); }
        if (
            record.RollbackEligible &&
            (record.PreviousRelease is null)
        ) { throw new InvalidDataException(message: "rollback eligibility requires a retained predecessor"); }
        if (
            (record.PendingOperationId is not null) &&
            ((record.PendingPhase == WorldReleaseOperationPhase.Commit) != record.PendingCommitted)
        ) { throw new InvalidDataException(message: "only a committed phase may carry the commit marker"); }
        if (
            (record.PendingPhase is WorldReleaseOperationPhase.Recover or WorldReleaseOperationPhase.RecoverActivate) &&
            string.IsNullOrWhiteSpace(value: record.PendingFailure)
        ) { throw new InvalidDataException(message: "recover phase requires a durable failure reason"); }
        if (
            (record.PendingPhase is not (WorldReleaseOperationPhase.Recover or WorldReleaseOperationPhase.RecoverActivate)) &&
            (record.PendingFailure is not null)
        ) { throw new InvalidDataException(message: "only recover phase may carry a pending failure"); }
        if (
            (record.PendingPhase == WorldReleaseOperationPhase.Commit) &&
            !string.Equals(
            a: record.ActiveRelease,
            b: record.PendingTargetRelease,
            comparisonType: StringComparison.Ordinal
        )
        ) { throw new InvalidDataException(message: "a committed group must point at its pending target"); }
        foreach (var item in record.RecoveryRoots) {
            if (
            string.IsNullOrWhiteSpace(value: item.Key) ||
            string.IsNullOrWhiteSpace(value: item.Value)
        ) { throw new InvalidDataException(message: "recovery roots require names and pins"); }
        }
        foreach (var item in record.History) {
            if (
            (item.OperationId == Guid.Empty) ||
            string.IsNullOrWhiteSpace(value: item.TargetRelease) ||
            string.IsNullOrWhiteSpace(value: item.Result) ||
            (item.RecoveryRoots is null)
        ) { throw new InvalidDataException(message: "release history is incomplete"); }
            foreach (var root in item.RecoveryRoots) {
                if (
            string.IsNullOrWhiteSpace(value: root.Key) ||
            string.IsNullOrWhiteSpace(value: root.Value)
        ) { throw new InvalidDataException(message: "release history recovery roots are incomplete"); }
            }
        }
    }
    private static void ValidateJson(ReadOnlySpan<byte> bytes) {
        using var document = JsonDocument.Parse(bytes.ToArray());

        if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(message: "release group record must be an object"); }
        var allowed = new HashSet<string>(comparer: StringComparer.Ordinal) { "schema", "deploymentGroup", "owner", "activeRelease", "previousRelease", "pendingOperationId", "pendingSourceRelease", "pendingTargetRelease", "pendingPhase", "pendingCommitted", "pendingFailure", "recoveryRoots", "admission", "rollbackEligible", "authorityLease", "history", "revision" };
        var required = new HashSet<string>(
            collection: allowed,
            comparer: StringComparer.Ordinal
        );

        allowed.Add(item: "restorePoint");
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject()) { if (!allowed.Contains(item: property.Name)) { throw new InvalidDataException(message: $"release group record contains unknown member '{property.Name}'"); } if (!seen.Add(item: property.Name)) { throw new InvalidDataException(message: $"release group record contains duplicate member '{property.Name}'"); } required.Remove(item: property.Name); }
        if (required.Count != 0) {
            throw new InvalidDataException(message: $"release group record is missing required member(s): {string.Join(
            separator: ", ",
            values: required.Order(comparer: StringComparer.Ordinal)
        )}");
        }
        var pendingPhase = document.RootElement.GetProperty(propertyName: "pendingPhase");

        if (
            ((pendingPhase.ValueKind != JsonValueKind.Null) && !IsDefinedEnum<WorldReleaseOperationPhase>(element: pendingPhase)) ||
            !IsDefinedEnum<WorldReleaseAdmissionState>(element: document.RootElement.GetProperty(propertyName: "admission")) ||
            (document.RootElement.GetProperty(propertyName: "recoveryRoots").ValueKind != JsonValueKind.Object) ||
            (document.RootElement.GetProperty(propertyName: "history").ValueKind != JsonValueKind.Array)
        ) { throw new InvalidDataException(message: "release group record has invalid field types"); }
        ValidateMapObject(
            document.RootElement.GetProperty(propertyName: "recoveryRoots"),
            "release recovery roots"
        );
        ValidateRestoreSelectionJson(parent: document.RootElement);
        foreach (var entry in document.RootElement.GetProperty(propertyName: "history").EnumerateArray()) {
            if (entry.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(message: "release history entry must be an object"); }
            var historyAllowed = new HashSet<string>(comparer: StringComparer.Ordinal) { "operationId", "sourceRelease", "targetRelease", "result", "recoveryRoots", "revision" };
            var historyRequired = new HashSet<string>(
                collection: historyAllowed,
                comparer: StringComparer.Ordinal
            );

            historyAllowed.Add(item: "restorePoint");
            var historySeen = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var property in entry.EnumerateObject()) {
                if (
                !historyAllowed.Contains(item: property.Name) ||
                !historySeen.Add(item: property.Name)
            ) { throw new InvalidDataException(message: "release history contains an unknown or duplicate member"); }
                historyRequired.Remove(item: property.Name);
            }
            if (
                (historyRequired.Count != 0) ||
                (entry.GetProperty(propertyName: "recoveryRoots").ValueKind != JsonValueKind.Object)
            ) { throw new InvalidDataException(message: "release history is missing recovery roots"); }
            ValidateMapObject(
                entry.GetProperty(propertyName: "recoveryRoots"),
                "release history recovery roots"
            );
            ValidateRestoreSelectionJson(parent: entry);
        }
    }
    private static void ValidateRestoreSelectionJson(JsonElement parent) {
        if (!parent.TryGetProperty(
            propertyName: "restorePoint",
            value: out var selection
        )) { return; }
        if (selection.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(message: "restore point must be an object"); }
        var fields = selection.EnumerateObject().Select(selector: row => row.Name).ToArray();

        if (
            (fields.Length != 2) ||
            !fields.ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: ["pointId", "identity"]) ||
            !selection.GetProperty(propertyName: "pointId").TryGetGuid(value: out var id) ||
            (id == Guid.Empty) ||
            (selection.GetProperty(propertyName: "identity").ValueKind != JsonValueKind.String) ||
            !ContentPin.TryParse(pin: out _, text: selection.GetProperty(propertyName: "identity").GetString())
        ) {
            throw new InvalidDataException(message: "restore point requires a unique point ID and full identity pin");
        }
    }
    private static void ValidateMapObject(JsonElement element, string description) {
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject()) {
            if (
                !seen.Add(item: property.Name) ||
                string.IsNullOrWhiteSpace(value: property.Name) ||
                (property.Value.ValueKind != JsonValueKind.String) ||
                string.IsNullOrWhiteSpace(value: property.Value.GetString())
            ) {
                throw new InvalidDataException(message: $"{description} contain a duplicate, empty, or non-string member");
            }
        }
    }
    private static bool IsDefinedEnum<T>(JsonElement element) where T : struct, Enum => element.ValueKind switch {
        JsonValueKind.Number when element.TryGetInt32(value: out var number) => Enum.IsDefined(
        enumType: typeof(T),
        value: number
    ),
        JsonValueKind.String when ((element.GetString() is { } text) && Enum.TryParse<T>(
        result: out var value,
        value: text
    )) => Enum.IsDefined(
        enumType: typeof(T),
        value: value
    ),
        _ => false
    };
}
