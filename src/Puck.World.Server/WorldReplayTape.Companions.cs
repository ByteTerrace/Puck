using Puck.World.Protocol;

namespace Puck.World;

public sealed partial class WorldReplayTape {
    private readonly List<(WorldInstance Row, WorldReplayTape Tape, bool Owned)> m_companions = [];

    /// <summary>Arms a companion recording on another row of this process under this recording's name, so the
    /// recording becomes a set: each row's own tape rides beside this one in the same file, and verification pairs
    /// every crossing between them. The companion taps the row's own loopback and server exactly as this tape taps its
    /// own, and the row's step closes its ticks. A no-op refusal while this tape is not recording.</summary>
    /// <param name="row">The row to tape.</param>
    /// <param name="refusal">Why the row was not taped, when it was not.</param>
    /// <returns><see langword="true"/> when the companion is recording.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public bool TryRecordCompanion(WorldInstance row, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: row);

        if (
            (m_mode != WorldReplayMode.Recording) ||
            (m_recordName is not { } name)
        ) {
            refusal = "no recording is active";
            return false;
        }
        if (ReferenceEquals(
            objA: row.Server,
            objB: m_liveServer
        )) {
            refusal = "the row is this recording's own authority";
            return false;
        }
        if (row.Link is not LoopbackTransport transport) {
            refusal = "the row is driven through no local loopback";
            return false;
        }
        if (row.Tape is { Mode: not WorldReplayMode.Idle }) {
            refusal = "the row's tape is already recording or replaying";
            return false;
        }

        var owned = (row.Tape is null);
        var companion = (row.Tape ?? new WorldReplayTape(
            addonHostFactory: m_addonHostFactory,
            engines: m_engines,
            liveServer: row.Server,
            machineHostFactory: m_machineHostFactory,
            profiles: row.Server.Profiles,
            stateRoot: m_stateRoot,
            transport: transport
        ) {
            m_documentPath = row.SourcePath,
        });

        if (!companion.TryBeginRecording(
            name: name,
            refusal: out refusal
        )) {
            return false;
        }

        row.Tape = companion;
        m_companions.Add(item: (row, companion, owned));
        return true;
    }

    private void CancelCompanions() {
        foreach (var (row, companion, owned) in m_companions) {
            if (companion.Mode == WorldReplayMode.Recording) {
                _ = companion.CancelRecording();
            }
            if (owned && ReferenceEquals(
                objA: row.Tape,
                objB: companion
            )) {
                row.Tape = null;
            }
        }
        m_companions.Clear();
    }
    // Each companion stops at the same boundary as this tape: its closed ticks become a standalone tape, and the
    // input it gathered since its last closed tick is discarded for the same reason this tape discards its own.
    private WorldReplaySnapshot[] StopCompanions() {
        var tapes = new List<WorldReplaySnapshot>(capacity: m_companions.Count);

        foreach (var (row, companion, owned) in m_companions) {
            if (companion.Mode == WorldReplayMode.Recording) {
                tapes.Add(item: companion.SnapshotRecording());
                companion.DetachTaps();
                companion.ResetRecordingState();
            }
            if (owned && ReferenceEquals(
                objA: row.Tape,
                objB: companion
            )) {
                row.Tape = null;
            }
        }
        m_companions.Clear();

        return [.. tapes];
    }
    // The tape and every companion holding a half of a crossing re-drive against their own recorded worlds; a
    // companion no crossing names adds no evidence about any crossing and is not re-driven. A companion row's seats
    // re-resolve against this process's catalog: their pinned rates, not the catalog, drive the re-run.
    private WorldReplaySetVerdict CompareSet(WorldReplaySnapshot recording) {
        var primary = Compare(recording: recording);
        var tapes = new List<(WorldReplaySnapshot Tape, WorldReplayVerdict Verdict)>(capacity: (recording.Companions.Count + 1)) {
            (recording, primary),
        };
        var companions = new List<WorldReplayAuthorityVerdict>(capacity: recording.Companions.Count);
        var crossing = WorldReplaySetVerdict.CrossingAuthorities(tapes: [recording, .. recording.Companions]);

        foreach (var companion in recording.Companions) {
            if (!crossing.Contains(item: companion.Authority)) {
                continue;
            }

            var verdict = Compare(recording: companion);

            tapes.Add(item: (companion, verdict));
            companions.Add(item: new WorldReplayAuthorityVerdict(
                Authority: companion.Authority,
                Instance: companion.Instance,
                Verdict: verdict
            ));
        }

        return new WorldReplaySetVerdict(
            Companions: companions,
            Crossings: WorldReplaySetVerdict.PairCrossings(tapes: tapes),
            Primary: primary
        );
    }
}
