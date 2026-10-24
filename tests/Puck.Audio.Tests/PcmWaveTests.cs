using Puck.Assets;

namespace Puck.Audio.Tests;

public sealed class PcmWaveTests {
    private static byte[] Wave(int channels = 1) {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output: output);

        writer.Write(buffer: "RIFF"u8);
        writer.Write(value: (36 + (channels * 4)));
        writer.Write(buffer: "WAVEfmt "u8);
        writer.Write(value: 16);
        writer.Write(value: ((short)1));
        writer.Write(value: ((short)channels));
        writer.Write(value: 48000);
        writer.Write(value: ((48000 * channels) * 2));
        writer.Write(value: ((short)(channels * 2)));
        writer.Write(value: ((short)16));
        writer.Write(buffer: "data"u8);
        writer.Write(value: (channels * 4));
        for (var index = 0; (index < (channels * 2)); index++) { writer.Write(value: ((short)(((index % 2) == 0) ? -200 : 100))); }
        return output.ToArray();
    }

    [Fact]
    public void MonoAndStereoAreReadAndStereoIsAveraged() {
        Assert.Equal(new short[] { -200, 100 }, PcmWave.ReadMono(bytes: Wave()));
        Assert.Equal(new short[] { -50, -50 }, PcmWave.ReadMono(bytes: Wave(channels: 2)));
    }
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(22)]
    [InlineData(24)]
    [InlineData(28)]
    [InlineData(32)]
    [InlineData(34)]
    [InlineData(40)]
    [Theory]
    public void MalformedOrUnsupportedWaveRefuses(int offset) {
        var bytes = Wave();

        bytes[offset] ^= 0x7f;
        Assert.Throws<InvalidDataException>(testCode: () => PcmWave.ReadMono(bytes: bytes));
    }
}
