using Puck.Attestation;

namespace Puck.Launcher.Release;

/// <summary>
/// The durable per-<c>(domain, subject)</c> sequence high-water mark a release manifest's bearer claim verifies
/// against (README.md §8's replay-commit contract, applied to a single-consumer client rather than a concurrent
/// server). <see cref="AttestationReleaseVerifier"/> compares against the stored mark BEFORE trusting anything else
/// in the manifest (a replayed old manifest is refused before its embedded <c>revoked</c>/<c>minimumSupported</c>
/// are even read), and calls <see cref="Advance"/> only after every other check (hash, revocation,
/// <c>minimumSupported</c>, version monotonicity) has passed — the mark must never record a claim whose effect was
/// not actually applied.
/// </summary>
public interface IReleaseSequenceStore {
    /// <summary>Advances the stored mark to <paramref name="requirement"/>. Callers must have already confirmed
    /// <see cref="IsAcceptable"/> for the same requirement — this method does not itself refuse a stale mark.</summary>
    /// <param name="requirement">The replay-commit requirement to record.</param>
    void Advance(ReplayCommitRequirement requirement);
    /// <summary>Compares <paramref name="requirement"/> against the stored mark for its <c>(domain, subject)</c>
    /// pair without writing anything.</summary>
    /// <param name="requirement">The replay-commit requirement to check.</param>
    /// <returns><see langword="true"/> when no mark is stored yet, or when the stored epoch is older, or when the
    /// epoch matches and the stored sequence is strictly less than <paramref name="requirement"/>'s.</returns>
    bool IsAcceptable(ReplayCommitRequirement requirement);
}
/// <summary>A process-lifetime <see cref="IReleaseSequenceStore"/> — for laws and the throwaway-chain canary path,
/// where a durable file would leak state across runs that expect a clean high-water mark.</summary>
public sealed class InMemoryReleaseSequenceStore : IReleaseSequenceStore {
    private readonly Lock m_gate = new();
    private readonly Dictionary<(string Domain, string Subject), (long EpochStart, ulong Sequence)> m_marks = [];

    /// <inheritdoc/>
    public void Advance(ReplayCommitRequirement requirement) {
        lock (m_gate) {
            m_marks[(requirement.Domain, requirement.Subject)] = (requirement.EpochStartUnixSeconds, requirement.Sequence);
        }
    }
    /// <inheritdoc/>
    public bool IsAcceptable(ReplayCommitRequirement requirement) {
        lock (m_gate) {
            if (!m_marks.TryGetValue(key: (requirement.Domain, requirement.Subject), value: out var mark)) {
                return true;
            }

            return ReleaseSequenceComparison.IsAcceptable(requirement: requirement, storedEpochStart: mark.EpochStart, storedSequence: mark.Sequence);
        }
    }
}
/// <summary>A file-backed <see cref="IReleaseSequenceStore"/>: one small text file, written atomically
/// (write-to-temp then rename) exactly like <see cref="Puck.Assets.ContentAddressedStore"/>'s own writes — the
/// mark a compromised CDN cannot roll back by replaying old bytes, because it is never carried in the bytes being
/// replayed.</summary>
/// <param name="filePath">The mark file's path (created on first <see cref="Advance"/>; its parent directory must exist).</param>
public sealed class FileReleaseSequenceStore(string filePath) : IReleaseSequenceStore {
    private readonly string m_filePath = Path.GetFullPath(path: filePath);
    private readonly Lock m_gate = new();

    private (string Domain, string Subject, long EpochStart, ulong Sequence)? ReadMark() {
        if (!File.Exists(path: m_filePath)) {
            return null;
        }

        var fields = File.ReadAllText(path: m_filePath).Split(separator: '\t');

        if ((fields.Length != 4) ||
            !long.TryParse(s: fields[2], result: out var epochStart) ||
            !ulong.TryParse(s: fields[3], result: out var sequence)
        ) {
            return null;
        }

        return (fields[0], fields[1], epochStart, sequence);
    }

    /// <inheritdoc/>
    public void Advance(ReplayCommitRequirement requirement) {
        lock (m_gate) {
            var directory = Path.GetDirectoryName(path: m_filePath)!;
            var tmpPath = Path.Combine(path1: directory, path2: $"{Guid.NewGuid():n}.tmp");

            _ = Directory.CreateDirectory(path: directory);
            File.WriteAllText(path: tmpPath, contents: $"{requirement.Domain}\t{requirement.Subject}\t{requirement.EpochStartUnixSeconds}\t{requirement.Sequence}");
            File.Move(destFileName: m_filePath, overwrite: true, sourceFileName: tmpPath);
        }
    }
    /// <inheritdoc/>
    public bool IsAcceptable(ReplayCommitRequirement requirement) {
        lock (m_gate) {
            var mark = ReadMark();

            if ((mark is not { } stored) ||
                !string.Equals(a: stored.Domain, b: requirement.Domain, comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: stored.Subject, b: requirement.Subject, comparisonType: StringComparison.Ordinal)
            ) {
                return true;
            }

            return ReleaseSequenceComparison.IsAcceptable(requirement: requirement, storedEpochStart: stored.EpochStart, storedSequence: stored.Sequence);
        }
    }
}

internal static class ReleaseSequenceComparison {
    public static bool IsAcceptable(ReplayCommitRequirement requirement, long storedEpochStart, ulong storedSequence) {
        if (requirement.EpochStartUnixSeconds < storedEpochStart) {
            return false;
        }

        return ((requirement.EpochStartUnixSeconds > storedEpochStart) || (requirement.Sequence > storedSequence));
    }
}
