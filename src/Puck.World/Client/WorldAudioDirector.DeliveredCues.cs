using Puck.Audio.Simulation;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

public sealed partial class WorldAudioDirector {
    private readonly List<MusicSenseEdge> m_deliveredSenses = [];
    private MusicDirector? m_deliveredMusic;
    private string? m_deliveredScoreHash;
    private WorldDefinition? m_bootAudioDefinition;
    private WorldDefinition? m_routedAudioDefinition;
    private WorldSessionMirror? m_routedAudio;

    internal void PresentLocalCues(WorldDefinition definition, WorldPresentationCueTick tick) {
        lock (m_gate) {
            if (m_routedAudio is null) {
                if (m_definition is null) { ReconcileSpeakers(definition); }
                PresentDeliveredCues(definition, tick);
            }
        }
    }

    /// <inheritdoc/>
    public void PresentRoutedAudio(WorldSessionMirror? mirror) {
        lock (m_gate) {
            if (!ReferenceEquals(mirror, m_routedAudio)) {
                m_routedAudio = mirror;
                m_routedAudioDefinition = null;
                m_deliveredMusic = null;
                m_deliveredScoreHash = null;
                m_activeMusicLayers = [];
                m_transients.Clear();
                m_pendingTriggers.Clear();
            }
            if (mirror is null) {
                if (m_bootAudioDefinition is { } local && !ReferenceEquals(local, m_definition)) { ReconcileDefinition(local); }
                return;
            }
            var definition = mirror.Definition;
            if (!ReferenceEquals(definition, m_routedAudioDefinition)) {
                m_routedAudioDefinition = definition;
                ReconcileDefinition(definition with {
                    PatchesRaw = [.. definition.Patches.Where(patch => WorldAssetRowLoader.IsEmbeddedAudio(patch.Source))],
                    TunesRaw = [.. definition.Tunes.Where(tune => WorldAssetRowLoader.IsEmbeddedAudio(tune.Source))],
                });
            }
            while (mirror.AudioCueFrames.TryTake(out var tick)) { PresentDeliveredCues(definition, tick); }
        }
    }

    // A local delivery serves every seat, so a cue is heard only when the listener's seat may be told it; a routed or
    // remote delivery was already admitted for its one recipient. Every definition reaching here passed a validating
    // door (the authority's own validator, the replica decode or the projection hydration), so the score compiles.
    private void PresentDeliveredCues(WorldDefinition definition, WorldPresentationCueTick tick) {
        var listener = m_listenerSeat;
        foreach (var delivered in tick.Cues) {
            if (delivered.Cue.AdmitsSeat(listener)) { SubmitCue(delivered.Cue.Name, delivered.Site, delivered.Cue.Argument); }
        }

        var score = definition.Music is { Count: > 0 } music &&
            (m_routedAudio is null || WorldAssetRowLoader.IsEmbeddedAudio(music[0].Source)) ? music[0] : null;
        if (!string.Equals(score?.Hash, m_deliveredScoreHash, StringComparison.Ordinal)) {
            m_deliveredScoreHash = score?.Hash;
            m_deliveredMusic = score is not null && WorldAssetRowLoader.TryLoadMusic(definition.DocumentDirectory, score, out var document, out _)
                ? new MusicDirector(MusicDirectorFactory.CompileGraph(document!)) : null;
        }
        if (m_deliveredMusic is not { } director) { SetActiveMusicLayers([]); return; }

        m_deliveredSenses.Clear();
        for (var family = 0; family <= (int)MusicSenseFamily.SeatJoin; family++) {
            if ((tick.MusicSenses & (1 << family)) != 0) { m_deliveredSenses.Add(new((MusicSenseFamily)family, 0, 0)); }
        }
        foreach (var delivered in tick.Cues) {
            if (delivered.Cue.AdmitsSeat(listener)) { m_deliveredSenses.Add(new(MusicSenseFamily.RuleCue, 0, 0, delivered.Cue.Name)); }
        }

        director.Step(tick.Tick, (MusicClockBoundary)tick.MusicBoundary, m_deliveredSenses);
        SetActiveMusicLayers(director.ActiveLayers);
        if (director.LastTransitionTick == tick.Tick) { SubmitCue(WorldAudioCue.MusicTransition, null); }
        if (director.LastEmbellishmentTick == tick.Tick) { SubmitEmbellishment(director.LastEmbellishmentPatchId!); }
    }
}
