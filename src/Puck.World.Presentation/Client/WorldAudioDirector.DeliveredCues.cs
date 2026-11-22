using Puck.Audio.Simulation;
using Puck.World.Protocol;

namespace Puck.World.Client;

public sealed partial class WorldAudioDirector {
    private readonly List<MusicSenseEdge> m_deliveredSenses = [];
    private MusicDirector? m_deliveredMusic;
    private string? m_deliveredScoreHash;
    private WorldDefinition? m_bootAudioDefinition;
    private WorldDefinition? m_routedAudioDefinition;
    private WorldSessionMirror? m_routedAudio;
    // The span of the routed mirror the routed sound was made from: what it plays ends with that span.
    private WorldStreamSpan m_routedAudioSpan;

    /// <summary>Presents one tick the in-process authority delivered: its cues the listener's seat may hear sound, and
    /// the delivered score steps on the tick's boundary and the cues and senses the seat was told. Ignored while a routed
    /// mirror supplies the sound (<see cref="PresentRoutedAudio"/>).</summary>
    /// <param name="definition">The delivered definition, whose first <c>music</c> row is the score.</param>
    /// <param name="tick">The delivered tick.</param>
    public void PresentLocalCues(WorldDefinition definition, WorldPresentationCueTick tick) {
        lock (m_gate) {
            if (m_routedAudio is null) {
                if (m_definition is null) { ReconcileSpeakers(definition); }
                PresentDeliveredCues(definition, tick);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>A routed mirror's sound belongs to the span it was delivered in: a new mirror, or a cut or close of the
    /// same mirror's stream, drops the delivered score, its layers and every scheduled or sounding transient, as the
    /// mirror drops what the span delivered, so nothing the ended span carried is heard after it.</remarks>
    public void PresentRoutedAudio(WorldSessionMirror? mirror) {
        lock (m_gate) {
            var span = mirror?.Span ?? default;
            if (!ReferenceEquals(mirror, m_routedAudio) || (span.Stream != m_routedAudioSpan.Stream) ||
                (span.Generation != m_routedAudioSpan.Generation) || (span.Closed != m_routedAudioSpan.Closed)) {
                m_routedAudio = mirror;
                m_routedAudioSpan = span;
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
                ? new MusicDirector(MusicScoreCompiler.CompileGraph(document!)) : null;
        }
        if (m_deliveredMusic is not { } director) { SetActiveMusicLayers([]); return; }

        m_deliveredSenses.Clear();
        for (var family = 0; family < (int)MusicSenseFamily.RuleCue; family++) {
            if ((tick.MusicSenses & (1 << family)) != 0) { m_deliveredSenses.Add(new((MusicSenseFamily)family)); }
        }
        foreach (var delivered in tick.Cues) {
            if (delivered.Cue.AdmitsSeat(listener)) { m_deliveredSenses.Add(new(MusicSenseFamily.RuleCue, delivered.Cue.Name)); }
        }

        director.Step(tick.Tick, (MusicClockBoundary)tick.MusicBoundary, m_deliveredSenses);
        SetActiveMusicLayers(director.ActiveLayers);
        if (director.LastTransitionTick == tick.Tick) { SubmitCue(WorldAudioCue.MusicTransition, null); }
        if (director.LastEmbellishmentTick == tick.Tick) { SubmitEmbellishment(director.LastEmbellishmentPatchId!); }
    }

    /// <summary>Describes the score this listener hears (the score half of <c>music.state</c>): its current segment,
    /// any pending transition, the most recent transition and embellishment, and each sounding layer with the tick it
    /// joined on.</summary>
    /// <returns>The bracketed echo, or a fixed "none" line before a score has been delivered.</returns>
    public string DescribeMusicState() {
        lock (m_gate) {
            if (m_deliveredMusic is not { } director) { return "[music.state: none delivered]"; }
            var layers = director.ActiveLayers.Count == 0 ? "none"
                : string.Join(",", director.ActiveLayers.Select(static active => $"{active.Layer.TuneId}@{active.JoinedTick}"));
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[music.state: segment={director.CurrentSegmentId} pending={director.PendingSegmentId ?? "none"} transitions={director.TransitionCount} lastTick={(director.LastTransitionTick?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")} lastFrom={director.LastTransitionFromSegmentId ?? "none"} lastTo={director.LastTransitionToSegmentId ?? "none"} layers={layers} lastEmbellishment={director.LastEmbellishmentPatchId ?? "none"} lastEmbellishmentTick={(director.LastEmbellishmentTick?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")}]");
        }
    }
}
