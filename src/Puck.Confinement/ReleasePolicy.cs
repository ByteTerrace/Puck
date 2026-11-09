using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Confinement;

/// <summary>A payload identity that exhausted its startup attempts.</summary>
/// <param name="PayloadPin">The rejected payload's identity (<c>ReleasePayloadIdentity</c>'s pin), compared ordinally.</param>
/// <param name="Version">The version the payload failed as, for messages.</param>
public sealed record RejectedPayload(string PayloadPin, string Version);
/// <summary>An install that reached startup health, by its complete installed identity.</summary>
/// <param name="Version">The version directory's name.</param>
/// <param name="Sequence">The install's sequence, which no other install of the root ever takes.</param>
/// <param name="PayloadPin">The installed payload's identity.</param>
public sealed record ProvenInstall(string Version, long Sequence, string PayloadPin);
/// <summary>The stub launches of one payload identity since it last proved healthy.</summary>
/// <param name="PayloadPin">The payload's identity.</param>
/// <param name="Attempts">The launches counted, each before its process started.</param>
public sealed record LaunchCount(string PayloadPin, int Attempts);
/// <summary>
/// An install root's release record, <c>state/release-policy.json</c>: the version the stub launches and every fact
/// launch, rollback, admission and retention depend on, kept in one file so a transition is one durable replacement.
/// Only <see cref="InstallTransaction"/> writes it; <see cref="ReadUnlocked"/> reads it for display.
/// </summary>
/// <param name="Current">The version the stub launches, or <see langword="null"/> before the first install.</param>
/// <param name="LastSequence">The install order's high-water mark; the next install takes one more.</param>
/// <param name="StateGenerationFloor">The highest state generation a launch from this install was admitted at.</param>
/// <param name="RevokedVersions">The accepted revocations that can still name a selectable or admissible version.</param>
/// <param name="RejectedPayloads">The payload identities that exhausted their startup attempts.</param>
/// <param name="ProvenInstalls">The installs that reached startup health.</param>
/// <param name="LaunchAttempts">The stub launches counted per payload identity since it last proved healthy.</param>
public sealed record ReleasePolicy(
    string? Current,
    long LastSequence,
    int StateGenerationFloor,
    IReadOnlyList<string> RevokedVersions,
    IReadOnlyList<RejectedPayload> RejectedPayloads,
    IReadOnlyList<ProvenInstall> ProvenInstalls,
    IReadOnlyList<LaunchCount> LaunchAttempts
) {
    private const int MaximumBytes = (4 * 1024 * 1024);

    /// <summary>The record's schema tag.</summary>
    public const string Schema = "puck.release.policy.v1";
    /// <summary>The most revocations the record holds; a merge past it is refused.</summary>
    public const int MaximumRevokedVersions = 4096;
    /// <summary>The most rejected payloads and attempt counts the record holds together before an install is refused;
    /// the stub may add the one payload it launches beyond it.</summary>
    public const int MaximumPayloadHistory = 4096;
    /// <summary>The most proven installs the record holds.</summary>
    public const int MaximumProvenInstalls = 4096;

    /// <summary>Gets the record of an install root that has none yet.</summary>
    public static ReleasePolicy Empty { get; } = new(Current: null, LastSequence: 0, StateGenerationFloor: 0, RevokedVersions: [], RejectedPayloads: [],
        ProvenInstalls: [], LaunchAttempts: []);

    /// <summary>Reports whether <paramref name="version"/> is revoked, ignoring case, since version directories collide that
    /// way on Windows.</summary>
    /// <param name="version">The version name.</param>
    /// <returns><see langword="true"/> when a revocation names it.</returns>
    public bool IsRevoked(string version) => RevokedVersions.Contains(value: version, comparer: StringComparer.OrdinalIgnoreCase);
    /// <summary>Reports whether a payload identity is rejected.</summary>
    /// <param name="payloadPin">The payload identity.</param>
    /// <returns><see langword="true"/> when it exhausted its startup attempts.</returns>
    public bool IsRejected(string payloadPin) => RejectedPayloads.Any(predicate: rejected => string.Equals(a: rejected.PayloadPin, b: payloadPin, comparisonType: StringComparison.Ordinal));
    /// <summary>Returns the stub launches counted for a payload identity.</summary>
    /// <param name="payloadPin">The payload identity.</param>
    /// <returns>The count, zero when none is recorded.</returns>
    public int AttemptsFor(string payloadPin) =>
        (LaunchAttempts.FirstOrDefault(predicate: entry => string.Equals(a: entry.PayloadPin, b: payloadPin, comparisonType: StringComparison.Ordinal))?.Attempts ?? 0);
    /// <summary>Returns the reason a version may not run or be admitted, or <see langword="null"/> when it may: it is
    /// revoked, a payload identity it carries is rejected, or its state generation is below the floor. The one admission
    /// predicate for updates, launches and registrations.</summary>
    /// <param name="version">The version name.</param>
    /// <param name="stateGeneration">The version's state generation.</param>
    /// <param name="payloadPins">The payload identities it carries.</param>
    /// <returns>The refusal, naming what disqualifies it, or <see langword="null"/>.</returns>
    public string? Refusal(string version, int stateGeneration, IEnumerable<string> payloadPins) {
        ArgumentNullException.ThrowIfNull(argument: version);
        ArgumentNullException.ThrowIfNull(argument: payloadPins);
        if (IsRevoked(version: version)) { return $"version '{version}' is revoked"; }
        foreach (var pin in payloadPins) {
            if (RejectedPayloads.FirstOrDefault(predicate: rejected => string.Equals(a: rejected.PayloadPin, b: pin, comparisonType: StringComparison.Ordinal)) is { } rejected) {
                return $"payload {pin} was rejected after failing startup as version '{rejected.Version}'";
            }
        }
        return ((stateGeneration < StateGenerationFloor)
            ? $"version '{version}' declares state generation {stateGeneration}, below this install's floor {StateGenerationFloor}, which an earlier launch may already have written"
            : null);
    }
    /// <summary>Reads an install root's record without the update lease, for display: the whole file at once, sharing
    /// delete access, so it never blocks a replacement and never sees a partial write.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <returns>The record, or <see cref="Empty"/> when the root holds none.</returns>
    /// <exception cref="InvalidDataException">The record exists but does not validate.</exception>
    /// <exception cref="IOException">The record cannot be read.</exception>
    public static ReleasePolicy ReadUnlocked(string installRoot) {
        var path = Path.Combine(path1: installRoot, path2: InstallLayout.StateDirectoryName, path3: InstallLayout.ReleasePolicyFileName);

        try {
            return Parse(bytes: AtomicFile.ReadAllBytes(path: path));
        } catch (Exception error) when ((error is FileNotFoundException or DirectoryNotFoundException)) {
            return Empty;
        }
    }

    internal static ReleasePolicy Read(ConfinedDirectory root) {
        using var state = root.Child(name: InstallLayout.StateDirectoryName);
        using var stream = state?.OpenFile(name: InstallLayout.ReleasePolicyFileName);

        if (stream is null) { return Empty; }
        if (stream.Length > MaximumBytes) { throw new InvalidDataException(message: $"'{InstallLayout.ReleasePolicyFileName}' exceeds {MaximumBytes} bytes."); }
        var bytes = new byte[stream.Length];

        stream.ReadExactly(buffer: bytes);
        return Parse(bytes: bytes);
    }
    internal static ReleasePolicy Parse(byte[] bytes) {
        Document? document;

        try {
            document = JsonSerializer.Deserialize(utf8Json: bytes, jsonTypeInfo: InstallJsonContext.Default.Document);
        } catch (JsonException error) {
            throw new InvalidDataException(message: $"'{InstallLayout.ReleasePolicyFileName}' does not parse: {error.Message}", innerException: error);
        }
        if ((document is null) || !string.Equals(a: document.Schema, b: Schema, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidDataException(message: $"'{InstallLayout.ReleasePolicyFileName}' is not a {Schema} record.");
        }
        var policy = new ReleasePolicy(Current: document.Current, LastSequence: document.LastSequence, StateGenerationFloor: document.StateGenerationFloor,
            RevokedVersions: (document.RevokedVersions ?? []), RejectedPayloads: (document.RejectedPayloads ?? []), ProvenInstalls: (document.ProvenInstalls ?? []),
            LaunchAttempts: (document.LaunchAttempts ?? []));

        policy.Validate();
        return policy;
    }
    internal byte[] Serialize() {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(jsonTypeInfo: InstallJsonContext.Default.Document, value: new Document(Schema: Schema, Current: Current,
            LastSequence: LastSequence, StateGenerationFloor: StateGenerationFloor, RevokedVersions: [.. RevokedVersions], RejectedPayloads: [.. RejectedPayloads],
            ProvenInstalls: [.. ProvenInstalls], LaunchAttempts: [.. LaunchAttempts]));
    }
    // Every write validates its whole new state before anything is staged, so a record that would not read back is never
    // published, and every read validates the same rules.
    private void Validate() {
        static void Require(bool condition, string message) {
            if (!condition) { throw new InvalidDataException(message: $"'{InstallLayout.ReleasePolicyFileName}' {message}."); }
        }
        static bool IsPin(string? pin) => (!string.IsNullOrEmpty(value: pin) && !pin.Any(predicate: char.IsWhiteSpace));

        Require(condition: ((Current is null) || InstallLayout.IsPlainName(name: Current)), message: "names a current version that is not a plain directory name");
        Require(condition: (LastSequence >= 0), message: "holds a negative install sequence");
        Require(condition: (StateGenerationFloor >= 0), message: "holds a negative state generation floor");
        Require(condition: (RevokedVersions.Count <= MaximumRevokedVersions), message: "holds more revocations than it may");
        Require(condition: ((RejectedPayloads.Count + LaunchAttempts.Count) <= (MaximumPayloadHistory + 1)), message: "holds more payload history than it may");
        Require(condition: (ProvenInstalls.Count <= MaximumProvenInstalls), message: "holds more proven installs than it may");
        Require(condition: RevokedVersions.All(predicate: static version => InstallLayout.IsPlainName(name: version)), message: "revokes a name that is not a plain directory name");
        Require(condition: (RevokedVersions.Distinct(comparer: StringComparer.OrdinalIgnoreCase).Count() == RevokedVersions.Count), message: "revokes a version twice");
        Require(condition: RejectedPayloads.All(predicate: static entry => (entry is not null) && IsPin(pin: entry.PayloadPin) && InstallLayout.IsPlainName(name: entry.Version)),
            message: "holds a malformed rejected payload");
        Require(condition: (RejectedPayloads.Select(selector: static entry => entry.PayloadPin).Distinct(comparer: StringComparer.Ordinal).Count() == RejectedPayloads.Count),
            message: "rejects a payload twice");
        Require(condition: ProvenInstalls.All(predicate: entry => (entry is not null) && InstallLayout.IsPlainName(name: entry.Version) && (entry.Sequence > 0) &&
            (entry.Sequence <= LastSequence) && IsPin(pin: entry.PayloadPin)), message: "holds a malformed proven install");
        Require(condition: (ProvenInstalls.Select(selector: static entry => entry.Sequence).Distinct().Count() == ProvenInstalls.Count), message: "proves an install twice");
        Require(condition: LaunchAttempts.All(predicate: static entry => (entry is not null) && IsPin(pin: entry.PayloadPin) && (entry.Attempts > 0)),
            message: "holds a malformed launch count");
        Require(condition: (LaunchAttempts.Select(selector: static entry => entry.PayloadPin).Distinct(comparer: StringComparer.Ordinal).Count() == LaunchAttempts.Count),
            message: "counts a payload twice");
    }

    internal sealed record Document(string? Schema, string? Current, long LastSequence, int StateGenerationFloor, string[]? RevokedVersions,
        RejectedPayload[]? RejectedPayloads, ProvenInstall[]? ProvenInstalls, LaunchCount[]? LaunchAttempts);
}
/// <summary>
/// What the applier records about a version when it installs it, in <c>versions/&lt;version&gt;/install-record.json</c>:
/// its place in the root's install order, its runtime, its state generation, its payload's identity and the content pins of
/// its payload files. Written once, durably, before the release record commit that makes the version current, and never
/// changed; the record lives inside the version directory, so a version and its record leave the tree together.
/// </summary>
/// <param name="Sequence">The install's sequence: <see cref="ReleasePolicy.LastSequence"/> plus one when it was installed,
/// never reused within the root. A higher sequence is a more recent install.</param>
/// <param name="Rid">The .NET runtime identifier of the installed payload.</param>
/// <param name="StateGeneration">The state generation the installed payload reads and writes.</param>
/// <param name="PayloadPin">The installed payload's identity (<c>ReleasePayloadIdentity</c>).</param>
/// <param name="Files">The payload files' content pins, as the manifest spells them.</param>
public sealed record InstallRecord(long Sequence, string Rid, int StateGeneration, string PayloadPin, IReadOnlyList<string> Files) {
    private const int MaximumBytes = ((16 * 1024) * 1024);

    /// <summary>The record's file name inside a version directory.</summary>
    public const string FileName = "install-record.json";

    /// <summary>Reads a version directory's record through its handle.</summary>
    /// <param name="version">The held version directory.</param>
    /// <returns>The record, or <see langword="null"/> when it is absent, does not parse, or lacks a positive sequence, a
    /// runtime, a nonnegative generation, a payload identity or a non-empty file list.</returns>
    /// <exception cref="IOException">The record is a link, oversized or cannot be read.</exception>
    public static InstallRecord? TryRead(ConfinedDirectory version) {
        ArgumentNullException.ThrowIfNull(argument: version);
        using var stream = version.OpenFile(name: FileName);

        if (stream is null) { return null; }
        if (stream.Length > MaximumBytes) { throw new IOException(message: $"'{FileName}' exceeds {MaximumBytes} bytes."); }
        try {
            return ((JsonSerializer.Deserialize(utf8Json: stream, jsonTypeInfo: InstallJsonContext.Default.InstallRecord) is { } record) && record.IsValid) ? record : null;
        } catch (JsonException) {
            return null;
        }
    }

    internal bool IsValid =>
        ((Sequence > 0) && !string.IsNullOrWhiteSpace(value: Rid) && (StateGeneration >= 0) && !string.IsNullOrWhiteSpace(value: PayloadPin) &&
        (Files is { Count: > 0 }) && Files.All(predicate: static file => !string.IsNullOrWhiteSpace(value: file)));
    internal byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(jsonTypeInfo: InstallJsonContext.Default.InstallRecord, value: this);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n",
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ReleasePolicy.Document))]
[JsonSerializable(typeof(InstallRecord))]
internal sealed partial class InstallJsonContext : JsonSerializerContext;
