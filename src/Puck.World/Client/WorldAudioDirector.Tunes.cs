using Puck.Audio.Mixing;
using Puck.World.Audio;

namespace Puck.World.Client;

internal sealed partial class WorldAudioDirector {
    // The row's Source/Hash were already proven to load, canonicalize, and pin-verify by WorldDefinitionValidator —
    // this load is expected to succeed by construction, the same discipline WorldServer's music/judge loads take.
    private static TuneHost CreateTuneHost(string? documentDirectory, WorldTune tune, AudioMixer mixer) {
        if (!WorldAssetRowLoader.TryLoadTune(
            document: out var document,
            documentDirectory: documentDirectory,
            error: out var loadError,
            row: tune
        )) {
            throw new InvalidOperationException(message: $"tune[{tune.Name}]: {loadError} (a validated document must still resolve at construction)");
        }

        var source = new TuneMachineSource(document: document!);

        mixer.SetSource(
            key: AudioSourceKey.Tune(id: tune.Name),
            source: source
        );

        return new TuneHost(
            TuneId: tune.Name,
            Hash: tune.Hash,
            Source: source
        );
    }
}
