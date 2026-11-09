using Puck.Confinement;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Attestation;
using System.Globalization;
using System.Text;

namespace Puck.Launcher.Release;

/// <summary>
/// The durable per-<c>(domain, subject)</c> sequence high-water mark a release manifest's bearer claim verifies
/// against (README.md §8's replay-commit contract, applied to a single-consumer client rather than a concurrent
/// server). <see cref="AttestationReleaseVerifier"/> compares against the stored mark BEFORE trusting anything else
/// in the manifest (a replayed old manifest is refused before its embedded <c>revoked</c>/<c>minimumSupported</c>
/// are even read), and calls <see cref="TryAdvance"/> only after every other verification check has passed.
/// File-backed compare-and-advance excludes other writers, including separate processes. Install reservations
/// bind the mark to a full signed-manifest pin so UpdateService can resume its durable intent before activation.
/// </summary>
public interface IReleaseSequenceStore {
    /// <summary>Atomically reserves an install, or resumes the same epoch, sequence and full manifest pin.
    /// An ordinary committed mark or a different manifest never grants recovery.</summary>
    ReleaseDecision TryReserve(ReplayCommitRequirement requirement, ContentPin manifestPin);
    /// <summary>Whether this exact install reservation is still the current mark for its identity.</summary>
    bool IsReserved(ReplayCommitRequirement requirement, ContentPin manifestPin);
    /// <summary>Atomically compares and advances the stored mark to <paramref name="requirement"/>. A preceding
    /// <see cref="IsAcceptable"/> is only a preview: another verifier may have committed since then.</summary>
    /// <param name="requirement">The replay-commit requirement to record.</param>
    /// <returns>Accepted only when this call advanced the mark; terminal for stale or malformed state, transient for lease contention.</returns>
    ReleaseDecision TryAdvance(ReplayCommitRequirement requirement);
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
    private readonly Dictionary<(string Domain, string Subject), (long EpochStart, ulong Sequence, ContentPin? ManifestPin)> m_marks = [];

    /// <inheritdoc/>
    public bool IsReserved(ReplayCommitRequirement requirement, ContentPin manifestPin) {
        lock (m_gate) {
            return m_marks.TryGetValue(key: (requirement.Domain, requirement.Subject), value: out var mark) &&
                mark.EpochStart == requirement.EpochStartUnixSeconds && mark.Sequence == requirement.Sequence && mark.ManifestPin == manifestPin;
        }
    }

    /// <inheritdoc/>
    public ReleaseDecision TryReserve(ReplayCommitRequirement requirement, ContentPin manifestPin) {
        lock (m_gate) {
            if (IsReserved(requirement: requirement, manifestPin: manifestPin)) { return ReleaseDecision.Accepted; }
            if (!IsAcceptable(requirement: requirement)) { return ReleaseDecision.TerminalRefusal; }
            m_marks[(requirement.Domain, requirement.Subject)] = (requirement.EpochStartUnixSeconds, requirement.Sequence, manifestPin);
            return ReleaseDecision.Accepted;
        }
    }

    /// <inheritdoc/>
    public ReleaseDecision TryAdvance(ReplayCommitRequirement requirement) {
        lock (m_gate) {
            if (!IsAcceptable(requirement: requirement)) {
                return ReleaseDecision.TerminalRefusal;
            }

            m_marks[(requirement.Domain, requirement.Subject)] = (requirement.EpochStartUnixSeconds, requirement.Sequence, null);
            return ReleaseDecision.Accepted;
        }
    }
    /// <inheritdoc/>
    public bool IsAcceptable(ReplayCommitRequirement requirement) {
        lock (m_gate) {
            if (!m_marks.TryGetValue(
                key: (requirement.Domain, requirement.Subject),
                value: out var mark
            )) {
                return true;
            }

            return ReleaseSequenceComparison.IsAcceptable(
                requirement: requirement,
                storedEpochStart: mark.EpochStart,
                storedSequence: mark.Sequence
            );
        }
    }
}
/// <summary>A file-backed <see cref="IReleaseSequenceStore"/>: one mark per domain/subject in a text file, written through
/// <see cref="AtomicFile"/> — the mark a compromised CDN cannot roll back by replaying old bytes, because it is never
/// carried in the bytes being replayed.</summary>
/// <param name="filePath">The mark file's path (created, with its parent directory, on first <see cref="TryAdvance"/>).</param>
public sealed class FileReleaseSequenceStore(string filePath) : IReleaseSequenceStore {
    private readonly string m_filePath = Path.GetFullPath(path: filePath);
    private readonly Lock m_gate = new();

    private Dictionary<(string Domain, string Subject), (long EpochStart, ulong Sequence, ContentPin? ManifestPin)> ReadMarks() {
        var marks = new Dictionary<(string Domain, string Subject), (long EpochStart, ulong Sequence, ContentPin? ManifestPin)>();

        if (!File.Exists(path: m_filePath)) {
            return marks;
        }

        var lines = File.ReadAllLines(path: m_filePath);

        if ((lines.Length < 2) || (lines[0] != "puck.release.sequence.v1")) {
            throw new InvalidDataException(message: "the release sequence store is malformed");
        }

        foreach (var line in lines.AsSpan(start: 1)) {
            var fields = line.Split(separator: '\t');

            if ((fields.Length != 5) ||
                !long.TryParse(s: fields[2], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var epochStart) ||
                !ulong.TryParse(s: fields[3], style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var sequence)) {
                throw new InvalidDataException(message: "the release sequence store is malformed");
            }

            ContentPin? manifestPin = null;
            if (fields[4].Length != 0) {
                if (!ContentPin.TryParse(text: fields[4], pin: out var parsedPin)) {
                    throw new InvalidDataException(message: "the release sequence store contains a malformed manifest pin");
                }
                manifestPin = parsedPin;
            }

            // Encode identities so a subject containing a tab or newline cannot change the record boundaries.
            try {
                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                var domain = utf8.GetString(bytes: Convert.FromBase64String(s: fields[0]));
                var subject = utf8.GetString(bytes: Convert.FromBase64String(s: fields[1]));

                if ((domain.Length == 0) || (subject.Length == 0) ||
                    !marks.TryAdd(key: (domain, subject), value: (epochStart, sequence, manifestPin))) {
                    throw new InvalidDataException(message: "the release sequence store contains an empty or duplicate identity");
                }
            } catch (Exception error) when (error is FormatException or DecoderFallbackException) {
                throw new InvalidDataException(message: "the release sequence store contains a malformed identity", innerException: error);
            }
        }

        return marks;
    }

    /// <inheritdoc/>
    public ReleaseDecision TryAdvance(ReplayCommitRequirement requirement) => TryWrite(requirement: requirement, manifestPin: null);

    /// <inheritdoc/>
    public ReleaseDecision TryReserve(ReplayCommitRequirement requirement, ContentPin manifestPin) => TryWrite(requirement: requirement, manifestPin: manifestPin);

    /// <inheritdoc/>
    public bool IsReserved(ReplayCommitRequirement requirement, ContentPin manifestPin) {
        lock (m_gate) {
            try {
                return ReadMarks().TryGetValue(key: (requirement.Domain, requirement.Subject), value: out var mark) &&
                    mark.EpochStart == requirement.EpochStartUnixSeconds && mark.Sequence == requirement.Sequence && mark.ManifestPin == manifestPin;
            } catch (InvalidDataException) {
                return false;
            }
        }
    }

    private ReleaseDecision TryWrite(ReplayCommitRequirement requirement, ContentPin? manifestPin) {
        lock (m_gate) {
            Dictionary<(string Domain, string Subject), (long EpochStart, ulong Sequence, ContentPin? ManifestPin)> marks;

            // Keep the lease file in place: deleting it could give two processes locks on different inodes.
            using var lease = FileLock.TryAcquire(path: (m_filePath + ".lock"));
            if (lease is null) { return ReleaseDecision.TransientRefusal; }

            try {
                marks = ReadMarks();
            } catch (InvalidDataException) {
                return ReleaseDecision.TerminalRefusal;
            }

            if (marks.TryGetValue(key: (requirement.Domain, requirement.Subject), value: out var stored) &&
                !ReleaseSequenceComparison.IsAcceptable(requirement: requirement, storedEpochStart: stored.EpochStart, storedSequence: stored.Sequence)) {
                return manifestPin is not null && stored.ManifestPin == manifestPin &&
                    stored.EpochStart == requirement.EpochStartUnixSeconds && stored.Sequence == requirement.Sequence
                    ? ReleaseDecision.Accepted : ReleaseDecision.TerminalRefusal;
            }

            marks[(requirement.Domain, requirement.Subject)] = (requirement.EpochStartUnixSeconds, requirement.Sequence, manifestPin);

            var text = new StringBuilder(value: "puck.release.sequence.v1\n");

            foreach (var (identity, mark) in marks.OrderBy(keySelector: pair => pair.Key.Domain, comparer: StringComparer.Ordinal)
                .ThenBy(keySelector: pair => pair.Key.Subject, comparer: StringComparer.Ordinal)) {
                text.Append(value: Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: identity.Domain))).Append(value: '\t')
                    .Append(value: Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: identity.Subject))).Append(value: '\t')
                    .Append(value: mark.EpochStart.ToString(provider: CultureInfo.InvariantCulture)).Append(value: '\t')
                    .Append(value: mark.Sequence.ToString(provider: CultureInfo.InvariantCulture)).Append(value: '\t')
                    .Append(value: mark.ManifestPin?.ToString()).Append(value: '\n');
            }

            // Durable before acceptance: a mark a power loss could roll back would reopen the sequences it consumed.
            AtomicFile.WriteAllBytesDurably(bytes: Encoding.UTF8.GetBytes(s: text.ToString()), path: m_filePath);
            return ReleaseDecision.Accepted;
        }
    }
    /// <inheritdoc/>
    public bool IsAcceptable(ReplayCommitRequirement requirement) {
        lock (m_gate) {
            Dictionary<(string Domain, string Subject), (long EpochStart, ulong Sequence, ContentPin? ManifestPin)> marks;

            try {
                marks = ReadMarks();
            } catch (InvalidDataException) {
                // An existing corrupt mark is not an empty store: refuse rather than reopening every old release.
                return false;
            }

            return !marks.TryGetValue(key: (requirement.Domain, requirement.Subject), value: out var stored) || ReleaseSequenceComparison.IsAcceptable(
                requirement: requirement,
                storedEpochStart: stored.EpochStart,
                storedSequence: stored.Sequence
            );
        }
    }
}

internal static class ReleaseSequenceComparison {
    public static bool IsAcceptable(ReplayCommitRequirement requirement, long storedEpochStart, ulong storedSequence) {
        if (requirement.EpochStartUnixSeconds < storedEpochStart) {
            return false;
        }

        return (
            (requirement.EpochStartUnixSeconds > storedEpochStart) ||
            (requirement.Sequence > storedSequence)
        );
    }
}
