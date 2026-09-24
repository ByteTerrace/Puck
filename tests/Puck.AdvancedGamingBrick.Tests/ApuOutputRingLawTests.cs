namespace Puck.AdvancedGamingBrick.Tests;

/// <summary>Pins the GBA APU's host output ring under overflow. The ring holds one emulated second of stereo frames;
/// a host that stops draining for longer must find the newest second waiting, never an empty ring and never a stretch
/// of older audio.</summary>
public sealed class ApuOutputRingLawTests {
    private const int ChunkCycles = 8_192;
    private const int MasterClock = 16_777_216;
    // A power-of-two rate against the master clock is exact: one frame every 512 cycles, 32768 frames per second.
    private const int SampleRate = 32_768;

    // A pulse channel whose envelope rises from silence to full volume over about 1.6 s, so every stretch of the
    // stream differs from every other and a window's content identifies its place in time.
    private static AgbApu CreateRisingTone() {
        var apu = new AgbApu();

        apu.ConfigureOutput(sampleRate: SampleRate);
        apu.WriteRegister(
            offset: 0x84u,
            value: 0x0080
        ); // SOUNDCNT_X: master enable, before any channel write
        apu.WriteRegister(
            offset: 0x80u,
            value: 0xFF77
        ); // SOUNDCNT_L: every PSG channel left and right, master volume max
        apu.WriteRegister(
            offset: 0x82u,
            value: 0x0002
        ); // SOUNDCNT_H: PSG at 100%
        apu.WriteRegister(
            offset: 0x62u,
            value: 0x0F80
        ); // duty 50%; envelope from volume 0, increasing, step 7
        apu.WriteRegister(
            offset: 0x64u,
            value: 0x8700
        ); // frequency and trigger

        return apu;
    }
    private static short[] DrainAll(AgbApu apu) {
        var drained = new List<short>();
        var buffer = new short[4_096];
        int count;

        while ((count = apu.DrainSamples(destination: buffer)) != 0) {
            drained.AddRange(collection: buffer[..count]);
        }

        return [.. drained];
    }
    private static void StepChunks(AgbApu apu, int chunks) {
        for (var chunk = 0; (chunk < chunks); ++chunk) {
            apu.Step(cycles: ChunkCycles);
        }
    }

    [Theory]
    // Exactly two seconds: a ring that let the writer lap the reader would read as empty here.
    [InlineData((2 * (MasterClock / ChunkCycles)))]
    // Two and a quarter seconds: the lapped reader would hand back only the last quarter second.
    [InlineData(((9 * (MasterClock / ChunkCycles)) / 4))]
    public void AnUndrainedRingHoldsTheNewestSecond(int chunks) {
        var oneSecondChunks = (MasterClock / ChunkCycles);
        var stalled = CreateRisingTone();

        StepChunks(
            apu: stalled,
            chunks: chunks
        );

        var survived = DrainAll(apu: stalled);

        Assert.Equal(
            actual: survived.Length,
            expected: (SampleRate * 2)
        );

        // The same stream drained on time: discard everything before the final second, then drain that second.
        var drained = CreateRisingTone();

        StepChunks(
            apu: drained,
            chunks: (chunks - oneSecondChunks)
        );
        _ = DrainAll(apu: drained);
        StepChunks(
            apu: drained,
            chunks: oneSecondChunks
        );

        var newest = DrainAll(apu: drained);

        Assert.Equal(
            actual: survived,
            expected: newest
        );

        // The first second of the stream is a different signal, so the survivor is not older audio that happens to
        // have the right length.
        var oldest = CreateRisingTone();

        StepChunks(
            apu: oldest,
            chunks: oneSecondChunks
        );

        Assert.NotEqual(
            actual: survived,
            expected: DrainAll(apu: oldest)
        );
    }
    [Fact]
    public void AnOddDrainNeverSplitsAFrame() {
        var apu = CreateRisingTone();

        StepChunks(
            apu: apu,
            chunks: 1
        );

        var odd = new short[3];

        Assert.Equal(
            actual: apu.DrainSamples(destination: odd),
            expected: 2
        );

        // 8192 cycles at 512 cycles a frame is 16 frames: one read, fifteen left, still whole left/right pairs.
        Assert.Equal(
            actual: DrainAll(apu: apu).Length,
            expected: 30
        );
    }
}
