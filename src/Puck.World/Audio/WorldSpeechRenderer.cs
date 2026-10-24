using System.Text;
using Puck.Audio.Mixing;

namespace Puck.World.Audio;

/// <summary>Device-free WAV export over the same text source the world mixer pulls.</summary>
public static class WorldSpeechRenderer {
    /// <summary>Writes the unconsumed utterance as 48 kHz, stereo, signed 16-bit PCM. The source must be fresh.</summary>
    public static void WriteWave(Stream output, TextSpeechSource source) {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);
        var bytes = checked((uint)(source.TotalFrames * 4));
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        writer.Write(buffer: "RIFF"u8);
        writer.Write(value: checked((36u + bytes)));
        writer.Write(buffer: "WAVEfmt "u8);
        writer.Write(value: 16u);
        writer.Write(value: ((ushort)1));
        writer.Write(value: ((ushort)2));
        writer.Write(value: AudioMixer.SampleRate);
        writer.Write(value: (AudioMixer.SampleRate * 4));
        writer.Write(value: ((ushort)4));
        writer.Write(value: ((ushort)16));
        writer.Write(buffer: "data"u8);
        writer.Write(value: bytes);
        var block = new short[(AudioMixer.MaxBlockFrames * 2)];

        while (!source.Finished) {
            var frames = source.Pull(frames: AudioMixer.MaxBlockFrames, interleavedStereo: block);

            for (var index = 0; (index < (frames * 2)); index++) {
                writer.Write(value: block[index]);
            }
        }
    }
}
