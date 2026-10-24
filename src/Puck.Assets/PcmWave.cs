using System.Buffers.Binary;

namespace Puck.Assets;

/// <summary>Strict PCM intake for short authored syllables: 48 kHz, signed 16-bit mono or stereo WAV.</summary>
public static class PcmWave {
    /// <summary>Reads a complete RIFF/WAVE asset and returns mono samples, averaging stereo channels.</summary>
    public static short[] ReadMono(ReadOnlySpan<byte> bytes) {
        if ((bytes.Length < 12) || !bytes[..4].SequenceEqual(other: "RIFF"u8) || !bytes.Slice(length: 4, start: 8).SequenceEqual(other: "WAVE"u8)
            || (BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[4..]) != (bytes.Length - 8))) {
            throw new InvalidDataException(message: "Expected a complete RIFF/WAVE file.");
        }
        var channels = 0;
        ReadOnlySpan<byte> data = default;

        for (var offset = 12; (offset < bytes.Length);) {
            if ((bytes.Length - offset) < 8) { throw new InvalidDataException(message: "Truncated WAV chunk."); }
            var size = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[(offset + 4)..]);

            if (size > ((bytes.Length - offset) - 8)) { throw new InvalidDataException(message: "Truncated WAV payload."); }
            var chunk = bytes.Slice(length: ((int)size), start: (offset + 8));

            if (bytes.Slice(length: 4, start: offset).SequenceEqual(other: "fmt "u8)) {
                if ((chunk.Length < 16) || (BinaryPrimitives.ReadUInt16LittleEndian(source: chunk) != 1)
                    || (BinaryPrimitives.ReadUInt32LittleEndian(source: chunk[4..]) != 48000)
                    || (BinaryPrimitives.ReadUInt16LittleEndian(source: chunk[14..]) != 16)) {
                    throw new InvalidDataException(message: "Syllables require 48 kHz signed 16-bit PCM.");
                }
                channels = BinaryPrimitives.ReadUInt16LittleEndian(source: chunk[2..]);
                if ((channels is not (1 or 2)) || (BinaryPrimitives.ReadUInt16LittleEndian(source: chunk[12..]) != (channels * 2))
                    || (BinaryPrimitives.ReadUInt32LittleEndian(source: chunk[8..]) != ((48000 * channels) * 2))) {
                    throw new InvalidDataException(message: "Syllables require mono or stereo PCM with matching alignment.");
                }
            } else if (bytes.Slice(length: 4, start: offset).SequenceEqual(other: "data"u8)) {
                data = chunk;
            }
            offset = checked((((offset + 8) + ((int)size)) + (((int)size) & 1)));
            if (offset > bytes.Length) { throw new InvalidDataException(message: "Missing WAV chunk padding."); }
        }
        if ((channels == 0) || data.IsEmpty || ((data.Length % (channels * 2)) != 0) || (data.Length > ((48000 * channels) * 2))) {
            throw new InvalidDataException(message: "Syllables require nonempty, aligned PCM lasting at most one second.");
        }
        var mono = new short[(data.Length / (channels * 2))];

        for (var frame = 0; (frame < mono.Length); frame++) {
            var value = ((int)BinaryPrimitives.ReadInt16LittleEndian(source: data[((frame * channels) * 2)..]));

            if (channels == 2) { value = ((value + BinaryPrimitives.ReadInt16LittleEndian(source: data[((frame * 4) + 2)..])) / 2); }
            mono[frame] = ((short)value);
        }
        return mono;
    }
}
