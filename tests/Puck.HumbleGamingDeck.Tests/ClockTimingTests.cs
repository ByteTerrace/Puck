using Puck.Machines;

namespace Puck.HumbleGamingDeck.Tests;

/// <summary>Verifies the RP2A03G M2 duty cycle independently of the internal CPU phases.</summary>
public sealed class ClockTimingTests {
    /// <summary>Checks exact edge timestamps and restoration at every master phase.</summary>
    /// <param name="alignment">The configured master-tick alignment.</param>
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Theory]
    public void M2UsesFifteenOfTwentyFourHalfTicks(int alignment) {
        var image = new byte[(16 + 16384)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = 1;
        image[7] = 8;
        image[11] = 7;
        var configuration = new HgdMachineConfiguration(cartridge: HgdCartridge.Load(image: image),
            powerOn: new HgdPowerOnProfile(alignmentPhase: alignment));
        var clock = new HgdClock(configuration: configuration);
        var twin = new HgdClock(configuration: configuration);
        var edges = new List<(HgdM2Edge Edge, ulong HalfTick)>();
        var writer = new StateWriter();

        clock.AddBudget(masterTicks: 36);
        for (var tick = 1; (tick <= 36); ++tick) {
            writer.Reset();
            clock.SaveState(writer: writer);
            twin.LoadState(reader: new StateReader(buffer: writer.ToArray()));
            Assert.Equal(expected: 36UL, actual: twin.RunTargetCycles);
            var edge = clock.StepTick(edgeHalfTick: out var halfTick);

            Assert.Equal(expected: edge, actual: twin.StepTick(edgeHalfTick: out var twinHalfTick));
            Assert.Equal(actual: twinHalfTick, expected: halfTick);
            Assert.Equal(expected: ((((tick + alignment) % 12) < 6) ? HgdCpuSubphase.Phi1 : HgdCpuSubphase.Phi2), actual: clock.Subphase);
            if (edge != HgdM2Edge.None) {
                edges.Add(item: (edge, halfTick));
            }
        }
        Assert.Equal(actual: edges, expected: new (HgdM2Edge, ulong)[] {
            (HgdM2Edge.Rising, ((ulong)(9 - (alignment * 2)))),
            (HgdM2Edge.Falling, ((ulong)(24 - (alignment * 2)))),
            (HgdM2Edge.Rising, ((ulong)(33 - (alignment * 2)))),
            (HgdM2Edge.Falling, ((ulong)(48 - (alignment * 2)))),
            (HgdM2Edge.Rising, ((ulong)(57 - (alignment * 2)))),
            (HgdM2Edge.Falling, ((ulong)(72 - (alignment * 2)))),
        });
    }
}
