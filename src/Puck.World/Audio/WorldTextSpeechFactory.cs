using Puck.Audio.Mixing;

namespace Puck.World.Audio;

/// <summary>The shared voice resolver for live HUD speech and device-free exports.</summary>
public static class WorldTextSpeechFactory {
    /// <summary>Resolves the voice's authored source and builds its deterministic character schedule.</summary>
    public static bool TryCreate(WorldDefinition definition, WorldVoiceProfile voice, string text, out TextSpeechSource? source, out string? error) {
        source = null;
        error = null;
        if (voice.CadenceTicks is not { } cadence) { error = "Voice needs cadenceTicks."; return false; }
        if (voice.SampleSet is { } sampleSet) {
            if (!WorldVoiceSampleLoader.TryLoad(definition.DocumentDirectory, sampleSet, out var samples, out var rate, out error)) { return false; }
            source = new TextSpeechSource(text, default, cadence, voice.PitchThousandths, voice.FormantThousandths,
                voice.PaceThousandths, samples, rate);
            return true;
        }
        var patch = definition.Patches.FirstOrDefault(predicate: row => (row.Name == voice.PatchId));

        if (patch is null) { error = $"Voice patch '{voice.PatchId}' is missing."; return false; }
        if (!WorldAssetRowLoader.TryLoadPatch(definition.DocumentDirectory, patch, out var document, out error)) { return false; }
        source = new TextSpeechSource(text, WorldVoicePatchFactory.FromDocument(document: document!), cadence,
            voice.PitchThousandths, voice.FormantThousandths, voice.PaceThousandths);
        return true;
    }
}
