namespace Puck.HumbleGamingBrick.Conformance.Protocol;

/// <summary>Runs a ROM that reports via the GBMicrotest protocol: it runs for a fixed number of frames, then reads
/// the result byte at <c>0xFF82</c> (<c>0x01</c> pass / <c>0xFF</c> fail); <c>0xFF80</c>/<c>0xFF81</c> hold the
/// actual/expected values for diagnostics.</summary>
internal static class GbMicrotestRunner {
    public static TestOutcome Run(RomCase romCase, Sm83Machine machine) {
        machine.Run(cycles: (ulong)(romCase.FrameLimit * RomCatalog.CyclesPerFrame));

        var bus = machine.Bus;
        var result = bus.ReadByte(address: 0xFF82);
        var actual = bus.ReadByte(address: 0xFF80);
        var expected = bus.ReadByte(address: 0xFF81);

        return result switch {
            0x01 => new(Case: romCase, Status: TestStatus.Pass, Detail: "0xFF82 = 0x01"),
            0xFF => new(Case: romCase, Status: TestStatus.Fail, Detail: FormattableString.Invariant($"0xFF82 = 0xFF (actual 0x{actual:X2}, expected 0x{expected:X2})")),
            _ => new(Case: romCase, Status: TestStatus.Inconclusive, Detail: FormattableString.Invariant($"0xFF82 = 0x{result:X2} (never resolved)")),
        };
    }
}
