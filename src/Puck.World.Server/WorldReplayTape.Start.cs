using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

// Where a recording starts. Armed before the world's first step, a tape starts from its definition's boot image with
// its seats joined. Armed later, it starts from an authority checkpoint of the live state taken at the arm, with the
// owned-world catalog left out (WorldReplaySnapshot.ForTape), so its first recorded tick continues from exactly where
// the live world stood. A state no checkpoint can capture refuses the arm by name; a tape that could not re-drive is
// never produced.
public sealed partial class WorldReplayTape {
    // The checkpoint the active recording starts from, or null for a recording that starts from its boot image.
    private WorldAuthorityCheckpoint? m_startCheckpoint;
    // How many entries and intents the open tick already held when a checkpoint-started recording armed: they reached
    // the server before the checkpoint was taken (an intent waits in its buffer, which the checkpoint holds), so the
    // recording's first tick leaves them out. Non-zero only while the history kept the capture attached before the arm.
    private int m_startSkipAuthority;
    private int m_startSkipIntents;

    // Captures the checkpoint a recording armed after the first step starts from, or names why the live state cannot
    // be one: a mounted guest's memory and a live session's input are outside every checkpoint, and the capture
    // itself refuses pumped guests, screen operations, a stepped machine without checkpoint support, a coupled link or
    // rewind history, an engagement in flight, and an edit not yet applied.
    internal bool TryCaptureStart(out WorldAuthorityCheckpoint? start, out string reason) {
        start = null;

        if (m_liveServer.Addons is { MountedCount: > 0 }) {
            reason = "addon guests are mounted, and a checkpoint cannot hold a guest's memory";

            return false;
        }

        if (m_liveServer.GrantTable.LiveSessionPrincipals().Count != 0) {
            reason = "a live session holds input and grants an authority checkpoint does not capture";

            return false;
        }

        try {
            if (!m_liveServer.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out reason
            )) {
                return false;
            }

            start = WorldReplaySnapshot.ForTape(checkpoint: checkpoint!);

            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message;

            return false;
        }
    }

    /// <summary>Writes a drafted tape under <paramref name="name"/> — a kept history branch
    /// (<see cref="WorldHistory.TryBranchTape"/>), which records authoritative hashes only: re-drives the draft once
    /// through a fresh shadow, takes that re-drive's pose trace as the tape's own, writes the tape, and returns the
    /// verdict of the re-drive's authoritative trace against the hashes the draft recorded. Nothing is written when the
    /// re-drive refuses.</summary>
    /// <param name="name">The tape's name; a valid name (<see cref="IsValidName"/>).</param>
    /// <param name="draft">The drafted tape.</param>
    /// <param name="path">The path the tape was written to.</param>
    /// <returns>The re-drive's verdict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid tape name.</exception>
    /// <exception cref="InvalidDataException">The re-drive refused the draft by name.</exception>
    /// <exception cref="WorldReplayCodecException">A host-side codec bug.</exception>
    public WorldReplayVerdict SaveDraft(string name, WorldReplaySnapshot draft, out string path) {
        ArgumentNullException.ThrowIfNull(argument: draft);

        if (!IsValidName(name: name)) {
            throw new ArgumentException(message: $"'{name}' is not a valid tape name", paramName: nameof(name));
        }

        var traces = draft.DriveTraces(
            addonHostFactory: m_addonHostFactory,
            documents: m_liveServer.RebuildDocuments,
            engines: m_engines,
            machineHostFactory: m_machineHostFactory,
            profiles: m_profiles
        );
        var tape = draft.WithPoseTrace(pose: traces.Pose);

        path = PathFor(name: name);
        WorldReplaySnapshot.WriteFile(
            path: path,
            recording: tape
        );

        return new WorldReplayVerdict(
            DivergedAt: HashTrace.FirstDivergence(
                left: tape.RecordedAuthoritativeHashes,
                right: traces.Authoritative
            ),
            Recorded: tape.RecordedTailHash,
            Replayed: ((traces.Authoritative.Length > 0)
                ? traces.Authoritative[^1]
                : 0UL),
            Ticks: tape.TickCount
        );
    }

    // Drops the leading input a checkpoint-started recording's first tick must not replay a second time.
    internal void TrimStartSkip(List<WorldReplayEntry> authority, List<IntentSubmission> intents) {
        if (m_startSkipAuthority > 0) {
            authority.RemoveRange(
                count: Math.Min(val1: m_startSkipAuthority, val2: authority.Count),
                index: 0
            );
        }

        if (m_startSkipIntents > 0) {
            intents.RemoveRange(
                count: Math.Min(val1: m_startSkipIntents, val2: intents.Count),
                index: 0
            );
        }

        m_startSkipAuthority = 0;
        m_startSkipIntents = 0;
    }
}
