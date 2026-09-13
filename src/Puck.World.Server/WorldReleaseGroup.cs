using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>The durable deployment-group state that fences release publication and admission as one decision.</summary>
public sealed record WorldReleaseGroupRecord {
    [JsonPropertyName("schema")] public required string Schema { get; init; }
    [JsonPropertyName("deploymentGroup")] public required string DeploymentGroup { get; init; }
    [JsonPropertyName("owner")] public required Guid Owner { get; init; }
    [JsonPropertyName("activeRelease")] public required string ActiveRelease { get; init; }
    [JsonPropertyName("previousRelease")] public string? PreviousRelease { get; init; }
    [JsonPropertyName("pendingOperationId")] public Guid? PendingOperationId { get; init; }
    [JsonPropertyName("pendingSourceRelease")] public string? PendingSourceRelease { get; init; }
    [JsonPropertyName("pendingTargetRelease")] public string? PendingTargetRelease { get; init; }
    [JsonPropertyName("pendingPhase")] public WorldReleaseOperationPhase? PendingPhase { get; init; }
    [JsonPropertyName("pendingCommitted")] public bool PendingCommitted { get; init; }
    [JsonPropertyName("recoveryRoots")] public IReadOnlyDictionary<string, string> RecoveryRoots { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    [JsonPropertyName("admission")] public WorldReleaseAdmissionState Admission { get; init; }
    [JsonPropertyName("rollbackEligible")] public bool RollbackEligible { get; init; }
    [JsonPropertyName("authorityLease")] public Guid AuthorityLease { get; init; }
    [JsonPropertyName("history")] public IReadOnlyList<WorldReleaseGroupHistoryEntry> History { get; init; } = [];
    [JsonPropertyName("revision")] public long Revision { get; init; }
}

/// <summary>Immutable history retained after an operation is finalized or recovered.</summary>
public sealed record WorldReleaseGroupHistoryEntry {
    [JsonPropertyName("operationId")] public required Guid OperationId { get; init; }
    [JsonPropertyName("sourceRelease")] public required string SourceRelease { get; init; }
    [JsonPropertyName("targetRelease")] public required string TargetRelease { get; init; }
    [JsonPropertyName("result")] public required string Result { get; init; }
    [JsonPropertyName("recoveryRoots")] public IReadOnlyDictionary<string, string> RecoveryRoots { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    [JsonPropertyName("revision")] public long Revision { get; init; }
}

/// <summary>A group state plus its conditional-write token.</summary>
public readonly record struct WorldReleaseGroupSnapshot(WorldReleaseGroupRecord Record, string VersionToken);

/// <summary>Named result from a guarded deployment-group state change.</summary>
public readonly record struct WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind Kind, string Detail, WorldReleaseGroupSnapshot? Snapshot = null) {
    public bool Ok => Kind == WorldReleaseOperationOutcomeKind.Ok;
}

/// <summary>Derives the group activation identity from the exact fresh per-world fence census.</summary>
public static class WorldReleaseFenceClaim {
    public static Guid Compute(IEnumerable<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)> fences) {
        ArgumentNullException.ThrowIfNull(fences);
        var canonical = string.Join("\n", fences.OrderBy(item => item.Identity.Owner).ThenBy(item => item.Identity.World.Value, StringComparer.Ordinal).Select(item => $"{item.Identity.Owner:D}/{item.Identity.World.Value}/{item.Fence.Epoch}/{item.Fence.Token:D}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// Persists the single mutable deployment-group root in the existing private object store. Operation records remain
/// useful detail, while this root is the only authority for active/previous release pointers and public admission.
/// </summary>
public sealed class WorldReleaseGroupStore {
    public const string Schema = "puck.world.release-group.v1";
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;
    private readonly Guid m_owner;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = null, WriteIndented = false };

    public WorldReleaseGroupStore(IObjectBlobStore store, ObjectStorageTarget target, Guid owner) {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_target = target ?? throw new ArgumentNullException(nameof(target));
        if (owner == Guid.Empty) { throw new ArgumentException("The deployment-group owner cannot be empty.", nameof(owner)); }
        m_owner = owner;
    }

    /// <summary>Loads and validates the guarded group root.</summary>
    public async Task<WorldReleaseGroupSnapshot?> LoadAsync(string deploymentGroup, CancellationToken cancellationToken = default) {
        var content = await m_store.ReadAsync(m_target, Address(deploymentGroup), cancellationToken).ConfigureAwait(false);
        if (content is not { } found) { return null; }
        ValidateJson(found.Content.Span);
        var record = JsonSerializer.Deserialize<WorldReleaseGroupRecord>(found.Content.Span, Options) ?? throw new InvalidDataException("release group record is empty");
        Validate(record, deploymentGroup, m_owner);
        return new(record, found.VersionToken ?? throw new InvalidDataException("release group record has no version token"));
    }

    /// <summary>Creates an open serving group. The active identity is established before managed activation starts.</summary>
    public async Task<WorldReleaseGroupOutcome> CreateAsync(string deploymentGroup, string activeRelease, CancellationToken cancellationToken = default) {
        ValidateIdentity(deploymentGroup, activeRelease);
        var record = new WorldReleaseGroupRecord {
            Schema = Schema, DeploymentGroup = deploymentGroup, Owner = m_owner, ActiveRelease = activeRelease,
            Admission = WorldReleaseAdmissionState.Open, AuthorityLease = Guid.Empty, Revision = 0,
        };
        var result = await m_store.WriteAsync(m_target, Address(deploymentGroup), Serialize(record), ObjectBlobWriteMode.CreateOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? new(WorldReleaseOperationOutcomeKind.Ok, string.Empty, new(record, result.VersionToken ?? string.Empty))
            : new(result.PreconditionFailed ? WorldReleaseOperationOutcomeKind.PreconditionFailed : WorldReleaseOperationOutcomeKind.AlreadyExists, "deployment-group state already exists");
    }

    /// <summary>Begins one operation from the currently active release and closes admission immediately.</summary>
    public async Task<WorldReleaseGroupOutcome> BeginAsync(WorldReleaseGroupSnapshot current, Guid operationId, string targetRelease, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(current.Record);
        ValidateIdentity(current.Record.DeploymentGroup, targetRelease);
        if (operationId == Guid.Empty || current.Record.PendingOperationId is not null || current.Record.RollbackEligible) {
            return new(WorldReleaseOperationOutcomeKind.Conflict, "the deployment group already owns a pending or retained rollback operation");
        }
        if (!string.Equals(current.Record.ActiveRelease, targetRelease, StringComparison.Ordinal)) {
            var next = current.Record with {
                PendingOperationId = operationId, PendingSourceRelease = current.Record.ActiveRelease, PendingTargetRelease = targetRelease,
                PendingPhase = WorldReleaseOperationPhase.Prepare, PendingCommitted = false, RecoveryRoots = new SortedDictionary<string, string>(StringComparer.Ordinal),
                // Prepare is a read-only preflight. The source remains the serving release until Drain coordinates the
                // maintenance boundary and closes admission.
                Admission = current.Record.Admission, Revision = checked(current.Record.Revision + 1),
            };
            return await WriteAsync(current, next, cancellationToken).ConfigureAwait(false);
        }
        return new(WorldReleaseOperationOutcomeKind.Conflict, "the target release is already active");
    }

    /// <summary>Advances the pending operation with an immutable request and guarded revision.</summary>
    public Task<WorldReleaseGroupOutcome> AdvanceAsync(WorldReleaseGroupSnapshot current, WorldReleaseGroupRecord next, CancellationToken cancellationToken = default) {
        Validate(next, current.Record.DeploymentGroup, m_owner);
        var old = current.Record;
        if (next.Owner != old.Owner || next.DeploymentGroup != old.DeploymentGroup || next.ActiveRelease != old.ActiveRelease || next.PreviousRelease != old.PreviousRelease || next.PendingOperationId != old.PendingOperationId || next.PendingSourceRelease != old.PendingSourceRelease || next.PendingTargetRelease != old.PendingTargetRelease || next.Revision != old.Revision + 1 || next.AuthorityLease != old.AuthorityLease || next.History.Count != old.History.Count) {
            return Task.FromResult(new WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind.Conflict, "deployment-group identity, pointers, history, or revision is immutable"));
        }
        if (!SameRoots(old.RecoveryRoots, next.RecoveryRoots) && !(old.RecoveryRoots.Count == 0 && next.RecoveryRoots.Count > 0 && ((old.PendingPhase == WorldReleaseOperationPhase.Prepare && next.PendingPhase == WorldReleaseOperationPhase.Drain) || (old.PendingPhase == WorldReleaseOperationPhase.Drain && next.PendingPhase == WorldReleaseOperationPhase.Activate)))) {
            return Task.FromResult(new WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind.Conflict, "recovery roots may be captured once during drain and are immutable afterward"));
        }
        if (old.Admission == WorldReleaseAdmissionState.Open && next.Admission != WorldReleaseAdmissionState.Open) {
            // Closing is allowed only when a new operation claims the group (BeginAsync). It cannot be an old writer's replay.
            if (next.PendingOperationId is null || old.PendingOperationId != next.PendingOperationId || next.PendingPhase != WorldReleaseOperationPhase.Drain) {
                return Task.FromResult(new WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind.Conflict, "an older writer cannot close group admission"));
            }
        }
        if (!CanAdvance(old, next)) { return Task.FromResult(new WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind.Conflict, "release phase cannot regress or skip")); }
        return WriteAsync(current, next, cancellationToken);
    }

    /// <summary>Publishes target pointers at the commit boundary; admission remains closed until publication is explicit.</summary>
    public Task<WorldReleaseGroupOutcome> CommitAsync(WorldReleaseGroupSnapshot current, IEnumerable<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)> fences, CancellationToken cancellationToken = default) {
        var freshAuthorityLease = WorldReleaseFenceClaim.Compute(fences);
        var old = current.Record;
        if (old.PendingOperationId is null || old.PendingPhase is not WorldReleaseOperationPhase.Verify || old.RecoveryRoots.Count == 0 || freshAuthorityLease == Guid.Empty || freshAuthorityLease == old.AuthorityLease) {
            return Task.FromResult(new WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind.Conflict, "commit requires a verified operation, recovery roots, and a fresh group authority lease"));
        }
        var next = old with { ActiveRelease = old.PendingTargetRelease!, PreviousRelease = old.PendingSourceRelease, PendingPhase = WorldReleaseOperationPhase.Commit, PendingCommitted = true, Admission = WorldReleaseAdmissionState.Closed, RollbackEligible = true, AuthorityLease = freshAuthorityLease, Revision = checked(old.Revision + 1) };
        return WriteAsync(current, next, cancellationToken);
    }

    /// <summary>Opens public/federation admission only after rechecking the exact committed target and fresh fence.</summary>
    public async Task<WorldReleaseGroupOutcome> OpenAdmissionAsync(WorldReleaseGroupSnapshot current, string expectedRelease, Guid operationId, Guid authorityLease, CancellationToken cancellationToken = default) {
        var old = current.Record;
        if (old.PendingOperationId != operationId || old.PendingPhase != WorldReleaseOperationPhase.Commit || !old.PendingCommitted || !string.Equals(old.ActiveRelease, expectedRelease, StringComparison.Ordinal) || authorityLease == Guid.Empty || authorityLease != old.AuthorityLease) {
            return new(WorldReleaseOperationOutcomeKind.Conflict, "candidate identity or authority fence does not match the committed group state");
        }
        var next = old with { Admission = WorldReleaseAdmissionState.Open, Revision = checked(old.Revision + 1) };
        return await WriteAsync(current, next, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rebinds an already serving source after restart under a fresh authority fence.</summary>
    public Task<WorldReleaseGroupOutcome> RebindAdmissionAsync(WorldReleaseGroupSnapshot current, string expectedRelease, Guid freshAuthorityLease, CancellationToken cancellationToken = default) {
        var old = current.Record;
        if (old.PendingOperationId is not null || old.Admission != WorldReleaseAdmissionState.Open || !string.Equals(old.ActiveRelease, expectedRelease, StringComparison.Ordinal) || freshAuthorityLease == Guid.Empty || freshAuthorityLease == old.AuthorityLease) {
            return Task.FromResult(new WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind.Conflict, "serving release or authority fence changed before restart rebind"));
        }
        return WriteAsync(current, old with { AuthorityLease = freshAuthorityLease, Revision = checked(old.Revision + 1) }, cancellationToken);
    }

    /// <summary>Reopens the recovered source only after a fresh host fence has been established.</summary>
    public Task<WorldReleaseGroupOutcome> OpenRecoveredAdmissionAsync(WorldReleaseGroupSnapshot current, string expectedRelease, Guid freshAuthorityLease, CancellationToken cancellationToken = default) {
        var old = current.Record;
        if (old.PendingOperationId is not null || old.Admission != WorldReleaseAdmissionState.Closed || !string.Equals(old.ActiveRelease, expectedRelease, StringComparison.Ordinal) || freshAuthorityLease == Guid.Empty || freshAuthorityLease == old.AuthorityLease) {
            return Task.FromResult(new WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind.Conflict, "recovered source or authority fence does not match the group state"));
        }
        return WriteAsync(current, old with { Admission = WorldReleaseAdmissionState.Open, AuthorityLease = freshAuthorityLease, Revision = checked(old.Revision + 1) }, cancellationToken);
    }

    /// <summary>Finalizes an open commit, retaining history and the previous artifact while ending rollback eligibility.</summary>
    public Task<WorldReleaseGroupOutcome> FinalizeAsync(WorldReleaseGroupSnapshot current, CancellationToken cancellationToken = default) {
        var old = current.Record;
        if (old.PendingOperationId is null || old.PendingPhase != WorldReleaseOperationPhase.Commit || !old.PendingCommitted || old.Admission != WorldReleaseAdmissionState.Open) {
            return Task.FromResult(new WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind.Conflict, "only an admitted committed operation can be finalized"));
        }
        var history = old.History.Append(new WorldReleaseGroupHistoryEntry { OperationId = old.PendingOperationId.Value, SourceRelease = old.PendingSourceRelease!, TargetRelease = old.PendingTargetRelease!, Result = "committed", RecoveryRoots = old.RecoveryRoots, Revision = old.Revision + 1 }).ToArray();
        var next = old with { PendingOperationId = null, PendingSourceRelease = null, PendingTargetRelease = null, PendingPhase = null, PendingCommitted = false, RecoveryRoots = new SortedDictionary<string, string>(StringComparer.Ordinal), RollbackEligible = false, History = history, Revision = checked(old.Revision + 1) };
        return WriteAsync(current, next, cancellationToken);
    }

    /// <summary>Completes pre-commit recovery to the source. The failed target is never made active or committed.</summary>
    public Task<WorldReleaseGroupOutcome> RecoverToSourceAsync(WorldReleaseGroupSnapshot current, string failure, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        var old = current.Record;
        if (old.PendingOperationId is null || old.PendingCommitted || old.PendingPhase is WorldReleaseOperationPhase.Commit or WorldReleaseOperationPhase.Finalized) {
            return Task.FromResult(new WorldReleaseGroupOutcome(WorldReleaseOperationOutcomeKind.Conflict, "post-commit operations cannot recover to the source release"));
        }
        var history = old.History.Append(new WorldReleaseGroupHistoryEntry { OperationId = old.PendingOperationId.Value, SourceRelease = old.PendingSourceRelease!, TargetRelease = old.PendingTargetRelease!, Result = $"recovered: {failure}", RecoveryRoots = old.RecoveryRoots, Revision = old.Revision + 1 }).ToArray();
        var next = old with { PendingOperationId = null, PendingSourceRelease = null, PendingTargetRelease = null, PendingPhase = null, PendingCommitted = false, RecoveryRoots = new SortedDictionary<string, string>(StringComparer.Ordinal), Admission = old.Admission, RollbackEligible = false, History = history, Revision = checked(old.Revision + 1) };
        return WriteAsync(current, next, cancellationToken);
    }

    private async Task<WorldReleaseGroupOutcome> WriteAsync(WorldReleaseGroupSnapshot current, WorldReleaseGroupRecord next, CancellationToken cancellationToken) {
        var result = await m_store.WriteAsync(m_target, Address(next.DeploymentGroup), Serialize(next), ObjectBlobWriteMode.Overwrite, current.VersionToken, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? new(WorldReleaseOperationOutcomeKind.Ok, string.Empty, new(next, result.VersionToken ?? string.Empty)) : new(WorldReleaseOperationOutcomeKind.PreconditionFailed, "deployment-group state changed before the guarded write");
    }
    private static bool CanAdvance(WorldReleaseGroupRecord old, WorldReleaseGroupRecord next) {
        if (old.PendingOperationId is null) { return false; }
        if (next.PendingPhase == old.PendingPhase) { return true; }
        return (old.PendingPhase, next.PendingPhase) switch {
            (WorldReleaseOperationPhase.Prepare, WorldReleaseOperationPhase.Drain) => true,
            (WorldReleaseOperationPhase.Drain, WorldReleaseOperationPhase.Activate) => next.RecoveryRoots.Count > 0,
            (WorldReleaseOperationPhase.Activate, WorldReleaseOperationPhase.Verify) => next.RecoveryRoots.Count > 0,
            _ => false,
        };
    }
    private static bool SameRoots(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) => left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && string.Equals(item.Value, value, StringComparison.Ordinal));
    private ObjectBlobAddress Address(string group) {
        ValidateGroup(group);
        return new(m_owner, $"{WorldOwnedWorldSync.HostedPrivateNamespace}/release-groups/{group}.json");
    }
    private static byte[] Serialize(WorldReleaseGroupRecord record) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Options));
    private static void ValidateIdentity(string group, string release) {
        ValidateGroup(group);
        if (string.IsNullOrWhiteSpace(release)) { throw new ArgumentException("A release identity is required.", nameof(release)); }
    }
    private static void ValidateGroup(string group) {
        if (string.IsNullOrWhiteSpace(group) || group.Any(char.IsWhiteSpace) || group.Contains("..", StringComparison.Ordinal) || group.Contains('/')) { throw new ArgumentException("Deployment group must be one safe name.", nameof(group)); }
    }
    private static void Validate(WorldReleaseGroupRecord record, string expectedGroup, Guid expectedOwner) {
        if (!string.Equals(record.Schema, Schema, StringComparison.Ordinal) || record.Owner != expectedOwner || !string.Equals(record.DeploymentGroup, expectedGroup, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(record.ActiveRelease) || record.Revision < 0 || record.History is null || record.RecoveryRoots is null) { throw new InvalidDataException("release group record fields are incomplete"); }
        if (record.Admission == WorldReleaseAdmissionState.Open && record.PendingOperationId is not null && record.PendingPhase is not WorldReleaseOperationPhase.Prepare && (record.PendingPhase != WorldReleaseOperationPhase.Commit || !record.PendingCommitted)) { throw new InvalidDataException("open admission requires prepare or a committed pending operation"); }
        if (record.PendingOperationId is null && (record.PendingSourceRelease is not null || record.PendingTargetRelease is not null || record.PendingPhase is not null || record.PendingCommitted || record.RecoveryRoots.Count != 0)) { throw new InvalidDataException("pending operation fields are incomplete"); }
        if (record.PendingOperationId is not null && (string.IsNullOrWhiteSpace(record.PendingSourceRelease) || string.IsNullOrWhiteSpace(record.PendingTargetRelease) || record.PendingPhase is null || record.PendingSourceRelease == record.PendingTargetRelease)) { throw new InvalidDataException("pending operation identity is incomplete"); }
        foreach (var item in record.RecoveryRoots) { if (string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)) { throw new InvalidDataException("recovery roots require names and pins"); } }
        foreach (var item in record.History) { if (item.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(item.SourceRelease) || string.IsNullOrWhiteSpace(item.TargetRelease) || string.IsNullOrWhiteSpace(item.Result) || item.RecoveryRoots is null) { throw new InvalidDataException("release history is incomplete"); } foreach (var root in item.RecoveryRoots) { if (string.IsNullOrWhiteSpace(root.Key) || string.IsNullOrWhiteSpace(root.Value)) { throw new InvalidDataException("release history recovery roots are incomplete"); } } }
    }
    private static void ValidateJson(ReadOnlySpan<byte> bytes) {
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("release group record must be an object"); }
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "schema", "deploymentGroup", "owner", "activeRelease", "previousRelease", "pendingOperationId", "pendingSourceRelease", "pendingTargetRelease", "pendingPhase", "pendingCommitted", "recoveryRoots", "admission", "rollbackEligible", "authorityLease", "history", "revision" };
        var required = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) { if (!allowed.Contains(property.Name)) { throw new InvalidDataException($"release group record contains unknown member '{property.Name}'"); } if (!required.Remove(property.Name)) { throw new InvalidDataException($"release group record contains duplicate member '{property.Name}'"); } }
        if (required.Count != 0) { throw new InvalidDataException($"release group record is missing required member(s): {string.Join(", ", required.Order(StringComparer.Ordinal))}"); }
        if (!IsDefinedEnum<WorldReleaseAdmissionState>(document.RootElement.GetProperty("admission")) || document.RootElement.GetProperty("recoveryRoots").ValueKind != JsonValueKind.Object || document.RootElement.GetProperty("history").ValueKind != JsonValueKind.Array) { throw new InvalidDataException("release group record has invalid field types"); }
        foreach (var entry in document.RootElement.GetProperty("history").EnumerateArray()) {
            if (entry.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("release history entry must be an object"); }
            var historyAllowed = new HashSet<string>(StringComparer.Ordinal) { "operationId", "sourceRelease", "targetRelease", "result", "recoveryRoots", "revision" };
            var historyRequired = new HashSet<string>(historyAllowed, StringComparer.Ordinal);
            foreach (var property in entry.EnumerateObject()) { if (!historyAllowed.Contains(property.Name) || !historyRequired.Remove(property.Name)) { throw new InvalidDataException("release history contains an unknown or duplicate member"); } }
            if (historyRequired.Count != 0 || entry.GetProperty("recoveryRoots").ValueKind != JsonValueKind.Object) { throw new InvalidDataException("release history is missing recovery roots"); }
        }
    }
    private static bool IsDefinedEnum<T>(JsonElement element) where T : struct, Enum => element.ValueKind switch { JsonValueKind.Number when element.TryGetInt32(out var number) => Enum.IsDefined(typeof(T), number), JsonValueKind.String when element.GetString() is { } text && Enum.TryParse<T>(text, out var value) => Enum.IsDefined(typeof(T), value), _ => false };
}
