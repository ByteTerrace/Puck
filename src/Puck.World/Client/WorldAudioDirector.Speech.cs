using Puck.Audio.Mixing;
using Puck.Maths;

namespace Puck.World.Client;

public sealed partial class WorldAudioDirector {
    private const int SpeechCapacity = 4;

    private readonly Dictionary<string, SpeechPlayback> m_textSpeech = new(comparer: StringComparer.Ordinal);

    private ulong m_textSpeechStarted;

    /// <summary>Replaces one HUD element's utterance, retaining the existing synth and mixer as the engine.</summary>
    public void SpeakText(string key, WorldVoiceProfile voice, string text) {
        lock (m_gate) {
            StopText(key: key);
            if (voice is not { PatchId: { } patchId, CadenceTicks: { } cadence }) {
                return;
            }
            foreach (var (id, patch) in m_patchSet) {
                if (!string.Equals(a: id, b: patchId, comparisonType: StringComparison.Ordinal)) {
                    continue;
                }
                if (m_textSpeech.Count == SpeechCapacity) {
                    StopText(key: m_textSpeech.MinBy(keySelector: pair => pair.Value.Id).Key);
                }
                var source = new TextSpeechSource(text, patch, cadence, voice.PitchThousandths,
                    voice.FormantThousandths, voice.PaceThousandths);
                var playback = new SpeechPlayback(Id: m_nextEmitterId++, Key: AudioSourceKey.Speech(id: key), Source: source);

                m_textSpeech.Add(key: key, value: playback);
                m_mixer?.SetSource(key: playback.Key, source: source);
                m_textSpeechStarted++;
                return;
            }
        }
    }
    /// <summary>Stops a hidden, removed or changed HUD element's current utterance.</summary>
    public void StopText(string key) {
        lock (m_gate) {
            if (m_textSpeech.Remove(key: key, value: out var playback)) {
                m_mixer?.RemoveSource(key: playback.Key);
            }
        }
    }

    private void PublishTextSpeech(AudioSnapshot slab) {
        foreach (var playback in m_textSpeech.Values) {
            // Keep the emitter until its owner changes or hides. A completed source is silent and bounded by
            // the same four-entry pool, so finishing on the device thread never mutates a collection being walked.
            if (playback.Source.Finished) {
                continue;
            }
            slab.TryAddEmitter(emitter: new AudioEmitter(playback.Id, AudioEmitterKind.Point, slab.Listener.Position,
                FixedQ4816.Zero, FixedQ4816.One, AudioAttenuationCurve.Linear, 0, 65536,
                AudioChannel.Mix, playback.Key));
        }
    }

    private sealed record SpeechPlayback(int Id, AudioSourceKey Key, TextSpeechSource Source);
}
