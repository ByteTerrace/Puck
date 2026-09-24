using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldReplayTape {
    /// <summary>Captures a versioned replay prefix as causal recovery evidence without stopping the recording.
    /// This supports hosts whose deterministic addon state cannot be checkpointed. Call on the simulation pump
    /// immediately after NoteTick, before admitting more input; retain the pinned modules with the recovery image.</summary>
    /// <returns>A base64-encoded replay prefixed by its recovery format name.</returns>
    /// <exception cref="InvalidOperationException">Recording is not active or the current boundary is not fully recorded.</exception>
    public string CaptureExternalOperationCause() => m_liveServer.ExecuteAuthorityOperation(operation: () => {
        if (
            (m_mode != WorldReplayMode.Recording) ||
            (m_ticks is not { Count: > 0 }) ||
            (m_liveAuthoritativeHashes.Count != m_ticks.Count) ||
            (m_liveHashes.Count != m_ticks.Count) ||
            (m_currentAuthority.Count != 0) ||
            (m_currentIntents.Count != 0) ||
            (m_openMutationEntryIndices.Count != 0) ||
            (m_liveAuthoritativeHashes[^1] != WorldStateHashComposition.HashAuthoritative(
            server: m_liveServer,
            tick: (m_liveServer.NextInputTick - 1UL)
        ))
        ) {
            throw new InvalidOperationException(message: "External operation recovery requires an active recording at a fully closed tick boundary.");
        }
        using var stream = new MemoryStream();

        WorldReplaySnapshot.Write(
            stream,
            SnapshotRecording()
        );
        return ("puck-replay:" + Convert.ToBase64String(inArray: stream.ToArray()));
    });

    private WorldReplaySnapshot SnapshotRecording() {
        if (
            (m_mode != WorldReplayMode.Recording) ||
            (m_definitionJson is null) ||
            (m_mountedAddons is null) ||
            (m_seats is null) ||
            (m_ticks is null)
        ) {
            throw new InvalidOperationException(message: "No recording is active.");
        }
        return new WorldReplaySnapshot {
            DefinitionJson = [.. m_definitionJson],
            ForkedFrom = m_forkedFrom,
            MountedAddons = [.. m_mountedAddons],
            PipelineSourceDirectory = m_pipelineSourceDirectory,
            RecordedAuthoritativeHashes = [.. m_liveAuthoritativeHashes],
            RecordedHashes = [.. m_liveHashes],
            Seats = [.. m_seats],
            SimulationRate = m_recordRateHz,
            Ticks = [.. m_ticks],
        };
    }
}
