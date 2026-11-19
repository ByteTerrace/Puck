using Puck.Audio.Mixing;
using Puck.World.Audio;

namespace Puck.World.Client;

public sealed partial class WorldAudioDirector {
    // The row passed a validating door, but its file can change on disk after validation and this runs on the frame
    // thread: a tune that no longer loads plays silent and is reported, never thrown.
    private static TuneHost? CreateTuneHost(string? documentDirectory, WorldTune tune, AudioMixer mixer) {
        if (!WorldAssetRowLoader.TryLoadTune(
            document: out var document,
            documentDirectory: documentDirectory,
            error: out var loadError,
            row: tune
        )) {
            Console.Error.WriteLine(value: $"[world.audio: tune[{tune.Name}] skipped: {loadError}]");

            return null;
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
