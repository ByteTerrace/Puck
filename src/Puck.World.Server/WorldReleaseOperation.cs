using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Durable phases of one world release transaction.</summary>
public enum WorldReleaseOperationPhase {
    Prepare,
    Drain,
    Activate,
    Verify,
    Commit,
    Recover,
    Finalized,
}

/// <summary>Whether public and federation admission is open for an operation.</summary>
public enum WorldReleaseAdmissionState { Closed, Open }

/// <summary>A durable operation record. It is the recovery authority after a coordinator or runner disappears.</summary>
public sealed record WorldReleaseOperationRecord {
    [JsonPropertyName("operationId")] public required Guid OperationId { get; init; }
    [JsonPropertyName("deploymentGroup")] public required string DeploymentGroup { get; init; }
    [JsonPropertyName("sourceRelease")] public required string SourceRelease { get; init; }
    [JsonPropertyName("targetRelease")] public required string TargetRelease { get; init; }
    [JsonPropertyName("phase")] public required WorldReleaseOperationPhase Phase { get; init; }
    [JsonPropertyName("admission")] public WorldReleaseAdmissionState Admission { get; init; } = WorldReleaseAdmissionState.Closed;
    [JsonPropertyName("committed")] public bool Committed { get; init; }
    [JsonPropertyName("recoveryRoots")] public IReadOnlyDictionary<string, string> RecoveryRoots { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    [JsonPropertyName("failure")] public string? Failure { get; init; }
    [JsonPropertyName("revision")] public long Revision { get; init; }

    /// <summary>True only after durable commit has published the target and admission may open.</summary>
    [JsonIgnore] public bool IsPostCommit => Committed || Admission == WorldReleaseAdmissionState.Open;
}

/// <summary>A release operation record together with its conditional-write token.</summary>
public readonly record struct WorldReleaseOperationSnapshot(WorldReleaseOperationRecord Record, string VersionToken);

/// <summary>Named result from an operation record CAS.</summary>
public enum WorldReleaseOperationOutcomeKind { Ok, Missing, AlreadyExists, PreconditionFailed, Conflict, Failed }

/// <summary>The durable operation store result.</summary>
public readonly record struct WorldReleaseOperationOutcome(WorldReleaseOperationOutcomeKind Kind, string Detail, WorldReleaseOperationSnapshot? Snapshot = null) {
    /// <summary>Whether the operation record write landed.</summary>
    public bool Ok => Kind == WorldReleaseOperationOutcomeKind.Ok;
}

/// <summary>Persists release operation records in the private object store with create-only and if-match writes.</summary>
public sealed class WorldReleaseOperationStore {
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;
    private readonly Guid m_objectId;

    /// <summary>Initializes the store for one deployment-group owner/container.</summary>
    public WorldReleaseOperationStore(IObjectBlobStore store, ObjectStorageTarget target, Guid objectId) {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_target = target ?? throw new ArgumentNullException(nameof(target));
        if (objectId == Guid.Empty) {
            throw new ArgumentException("The release operation owner cannot be empty.", nameof(objectId));
        }
        m_objectId = objectId;
    }

    /// <summary>Loads the operation record, or null when the group has no operation.</summary>
    public async Task<WorldReleaseOperationSnapshot?> LoadAsync(string deploymentGroup, CancellationToken cancellationToken = default) {
        var content = await m_store.ReadAsync(m_target, Address(deploymentGroup), cancellationToken);
        if (content is not { } found) {
            return null;
        }
        try {
            ValidateJson(found.Content.Span);
            var record = JsonSerializer.Deserialize<WorldReleaseOperationRecord>(found.Content.Span, Options) ?? throw new InvalidDataException("release operation record is empty");
            Validate(record, deploymentGroup);
            return new WorldReleaseOperationSnapshot(record, found.VersionToken ?? throw new InvalidDataException("release operation record has no version token"));
        } catch (JsonException error) {
            throw new InvalidDataException("release operation record is malformed", error);
        }
    }

    /// <summary>Creates the first record for a deployment group. Create-only loss is a named concurrent-operation refusal.</summary>
    public async Task<WorldReleaseOperationOutcome> CreateAsync(WorldReleaseOperationRecord record, CancellationToken cancellationToken = default) {
        Validate(record, record.DeploymentGroup);
        if (record.Phase != WorldReleaseOperationPhase.Prepare || record.Admission != WorldReleaseAdmissionState.Closed || record.Committed || record.Revision != 0) {
            throw new InvalidDataException("the first release operation record must be an uncommitted, closed Prepare record at revision zero");
        }
        var result = await m_store.WriteAsync(m_target, Address(record.DeploymentGroup), Serialize(record), ObjectBlobWriteMode.CreateOnly, cancellationToken: cancellationToken);
        if (result.Succeeded) {
            return new(WorldReleaseOperationOutcomeKind.Ok, string.Empty, new(record, result.VersionToken ?? string.Empty));
        }
        return result.PreconditionFailed
            ? new(WorldReleaseOperationOutcomeKind.PreconditionFailed, "another operation changed the deployment group")
            : new(WorldReleaseOperationOutcomeKind.AlreadyExists, "a release operation already owns the deployment group");
    }

    /// <summary>Advances a record only when its version and revision still match.</summary>
    public async Task<WorldReleaseOperationOutcome> AdvanceAsync(WorldReleaseOperationSnapshot current, WorldReleaseOperationRecord next, CancellationToken cancellationToken = default) {
        Validate(next, current.Record.DeploymentGroup);
        if (next.OperationId != current.Record.OperationId || next.DeploymentGroup != current.Record.DeploymentGroup || next.SourceRelease != current.Record.SourceRelease || next.TargetRelease != current.Record.TargetRelease || !RootsCanAdvance(current.Record, next) || next.Revision != current.Record.Revision + 1) {
            return new(WorldReleaseOperationOutcomeKind.Conflict, "release operation identity, roots, or revision is immutable");
        }
        if (current.Record.Committed && !next.Committed) {
            return new(WorldReleaseOperationOutcomeKind.Conflict, "a committed operation cannot return to pre-commit recovery");
        }
        if (current.Record.Admission == WorldReleaseAdmissionState.Open && next.Admission != WorldReleaseAdmissionState.Open) {
            return new(WorldReleaseOperationOutcomeKind.Conflict, "open admission cannot be closed by an older operation record");
        }
        if (!CanAdvancePhase(current.Record, next)) {
            return new(WorldReleaseOperationOutcomeKind.Conflict, "release operation phase cannot regress or skip into an unsupported recovery state");
        }
        var result = await m_store.WriteAsync(m_target, Address(next.DeploymentGroup), Serialize(next), ObjectBlobWriteMode.Overwrite, current.VersionToken, cancellationToken);
        if (result.Succeeded) {
            return new(WorldReleaseOperationOutcomeKind.Ok, string.Empty, new(next, result.VersionToken ?? string.Empty));
        }
        return new(WorldReleaseOperationOutcomeKind.PreconditionFailed, "release operation record changed before the guarded advance");
    }

    /// <summary>Resumes an operation with the same operation id, refusing a different request payload.</summary>
    public async Task<WorldReleaseOperationOutcome> ResumeAsync(WorldReleaseOperationRecord requested, CancellationToken cancellationToken = default) {
        var current = await LoadAsync(requested.DeploymentGroup, cancellationToken);
        if (current is null) {
            return new(WorldReleaseOperationOutcomeKind.Missing, "no durable operation exists for the deployment group");
        }
        if (current.Value.Record.OperationId != requested.OperationId || current.Value.Record.SourceRelease != requested.SourceRelease || current.Value.Record.TargetRelease != requested.TargetRelease) {
            return new(WorldReleaseOperationOutcomeKind.Conflict, "operation id is bound to a different release request");
        }
        return new(WorldReleaseOperationOutcomeKind.Ok, "operation resumed", current);
    }

    /// <summary>Marks the prior operation finalized without deleting its record or recovery roots.</summary>
    public Task<WorldReleaseOperationOutcome> FinalizeAsync(WorldReleaseOperationSnapshot current, CancellationToken cancellationToken = default) => AdvanceAsync(current, current.Record with { Phase = WorldReleaseOperationPhase.Finalized, Admission = WorldReleaseAdmissionState.Open, Revision = checked(current.Record.Revision + 1) }, cancellationToken);

    private ObjectBlobAddress Address(string deploymentGroup) {
        if (string.IsNullOrWhiteSpace(deploymentGroup) || deploymentGroup.Any(char.IsWhiteSpace) || deploymentGroup.Contains("..", StringComparison.Ordinal)) {
            throw new ArgumentException("Deployment group must be a single safe name.", nameof(deploymentGroup));
        }
        return new(m_objectId, $"{WorldOwnedWorldSync.HostedPrivateNamespace}/release-operations/{deploymentGroup}.json");
    }
    private static byte[] Serialize(WorldReleaseOperationRecord record) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Options));
    private static void Validate(WorldReleaseOperationRecord record, string expectedGroup) {
        if (!Enum.IsDefined(record.Phase) || !Enum.IsDefined(record.Admission)) {
            throw new InvalidDataException("release operation phase or admission state is undefined");
        }
        if (record.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(record.DeploymentGroup) || !string.Equals(record.DeploymentGroup, expectedGroup, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(record.SourceRelease) || string.IsNullOrWhiteSpace(record.TargetRelease) || record.Revision < 0 || record.RecoveryRoots is null || (record.Admission == WorldReleaseAdmissionState.Open && !record.Committed)) {
            throw new InvalidDataException("release operation fields are incomplete or violate the commit boundary");
        }
        if (record.Phase is WorldReleaseOperationPhase.Commit or WorldReleaseOperationPhase.Finalized && !record.Committed) {
            throw new InvalidDataException("a committed phase requires the durable commit marker");
        }
        if (record.Phase == WorldReleaseOperationPhase.Finalized && record.Admission != WorldReleaseAdmissionState.Open) {
            throw new InvalidDataException("a finalized operation must have open admission");
        }
        if (record.Phase is WorldReleaseOperationPhase.Activate or WorldReleaseOperationPhase.Verify or WorldReleaseOperationPhase.Commit && record.RecoveryRoots.Count == 0) {
            throw new InvalidDataException("activation and commit phases require captured recovery roots");
        }
        if (record.Phase is WorldReleaseOperationPhase.Prepare or WorldReleaseOperationPhase.Drain or WorldReleaseOperationPhase.Activate or WorldReleaseOperationPhase.Verify or WorldReleaseOperationPhase.Recover && record.Committed) {
            throw new InvalidDataException("pre-commit phases cannot carry the durable commit marker");
        }
        foreach (var root in record.RecoveryRoots) {
            if (string.IsNullOrWhiteSpace(root.Key) || string.IsNullOrWhiteSpace(root.Value)) {
                throw new InvalidDataException("recovery root names and pins are required");
            }
        }
    }
    private static void ValidateJson(ReadOnlySpan<byte> bytes) {
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException("release operation record must be an object");
        }
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "operationId", "deploymentGroup", "sourceRelease", "targetRelease", "phase", "admission", "committed", "recoveryRoots", "failure", "revision" };
        var required = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) {
            if (!allowed.Contains(property.Name)) {
                throw new InvalidDataException($"release operation record contains unknown member '{property.Name}'");
            }
            if (!seen.Add(property.Name)) {
                throw new InvalidDataException($"release operation record contains duplicate member '{property.Name}'");
            }
            required.Remove(property.Name);
        }
        if (required.Count != 0) {
            throw new InvalidDataException($"release operation record is missing required member(s): {string.Join(", ", required.Order(StringComparer.Ordinal))}");
        }
        if (!document.RootElement.TryGetProperty("phase", out var phase) || !IsDefinedEnum<WorldReleaseOperationPhase>(phase)) {
            throw new InvalidDataException("release operation phase is invalid");
        }
        if (!document.RootElement.TryGetProperty("admission", out var admission) || !IsDefinedEnum<WorldReleaseAdmissionState>(admission)) {
            throw new InvalidDataException("release operation admission is invalid");
        }
        if (document.RootElement.GetProperty("recoveryRoots").ValueKind != JsonValueKind.Object || document.RootElement.GetProperty("committed").ValueKind != JsonValueKind.False && document.RootElement.GetProperty("committed").ValueKind != JsonValueKind.True || document.RootElement.GetProperty("revision").ValueKind != JsonValueKind.Number) {
            throw new InvalidDataException("release operation record has an invalid field type");
        }
    }
    private static bool RootsCanAdvance(WorldReleaseOperationRecord current, WorldReleaseOperationRecord next) {
        if (SameRoots(next.RecoveryRoots, current.RecoveryRoots)) {
            return true;
        }
        return current.RecoveryRoots.Count == 0 && current.Phase == WorldReleaseOperationPhase.Drain && next.RecoveryRoots.Count > 0 && (next.Phase is WorldReleaseOperationPhase.Drain or WorldReleaseOperationPhase.Activate);
    }
    private static bool CanAdvancePhase(WorldReleaseOperationRecord current, WorldReleaseOperationRecord next) {
        if (next.Phase == WorldReleaseOperationPhase.Recover) {
            return !current.IsPostCommit;
        }
        if (current.Phase == WorldReleaseOperationPhase.Recover) {
            return next.Phase == WorldReleaseOperationPhase.Recover;
        }
        if (next.Phase == current.Phase) {
            return true;
        }
        return (current.Phase, next.Phase) switch {
            (WorldReleaseOperationPhase.Prepare, WorldReleaseOperationPhase.Drain) => true,
            (WorldReleaseOperationPhase.Drain, WorldReleaseOperationPhase.Activate) => true,
            (WorldReleaseOperationPhase.Activate, WorldReleaseOperationPhase.Verify) => true,
            (WorldReleaseOperationPhase.Verify, WorldReleaseOperationPhase.Commit) => true,
            (WorldReleaseOperationPhase.Commit, WorldReleaseOperationPhase.Finalized) => true,
            _ => false,
        };
    }
    private static bool SameRoots(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));
    private static bool IsDefinedEnum<T>(JsonElement element) where T : struct, Enum => element.ValueKind switch {
        JsonValueKind.Number when element.TryGetInt32(out var intValue) => Enum.IsDefined(typeof(T), intValue),
        JsonValueKind.String when element.GetString() is { } text && Enum.TryParse<T>(text, out var enumValue) => Enum.IsDefined(typeof(T), enumValue),
        _ => false,
    };
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = null, WriteIndented = false };
}

/// <summary>Pure commit-boundary recovery rules shared by local and cloud coordinators.</summary>
public static class WorldReleaseRecoveryPolicy {
    /// <summary>Returns the only safe next action after coordinator loss.</summary>
    public static WorldReleaseOperationPhase Recover(WorldReleaseOperationRecord record) => record.IsPostCommit ? WorldReleaseOperationPhase.Commit : WorldReleaseOperationPhase.Recover;

    /// <summary>Builds a pre-commit recovery record that keeps admission closed and preserves captured roots.</summary>
    public static WorldReleaseOperationRecord PrepareRecovery(WorldReleaseOperationRecord record, string failure) {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        if (record.IsPostCommit) {
            throw new InvalidOperationException("post-commit failures follow the committed release and cannot restore pre-deployment roots");
        }
        return record with { Phase = WorldReleaseOperationPhase.Recover, Admission = WorldReleaseAdmissionState.Closed, Committed = false, Failure = failure, Revision = checked(record.Revision + 1) };
    }

    /// <summary>Builds the durable commit record. Admission remains closed until this record is published.</summary>
    public static WorldReleaseOperationRecord Commit(WorldReleaseOperationRecord record) {
        ArgumentNullException.ThrowIfNull(record);
        if (record.RecoveryRoots.Count == 0) {
            throw new InvalidOperationException("durable commit requires captured recovery roots");
        }
        return record with { Phase = WorldReleaseOperationPhase.Commit, Committed = true, Admission = WorldReleaseAdmissionState.Closed, Failure = null, Revision = checked(record.Revision + 1) };
    }

    /// <summary>Opens admission only from a committed operation.</summary>
    public static WorldReleaseOperationRecord OpenAdmission(WorldReleaseOperationRecord record) {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.Committed) {
            throw new InvalidOperationException("public admission cannot open before durable release commit");
        }
        return record with { Admission = WorldReleaseAdmissionState.Open, Revision = checked(record.Revision + 1) };
    }
}
