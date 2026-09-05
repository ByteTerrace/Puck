namespace Puck.HumbleGamingBrick.Post;

/// <summary>The verdict a <see cref="GbMicrotestProbe"/> run produced.</summary>
internal enum GbMicrotestResult {
    /// <summary>The ROM wrote <c>0x01</c> to <c>$FF82</c>.</summary>
    Pass,
    /// <summary>The ROM wrote <c>0xFF</c> to <c>$FF82</c>.</summary>
    Fail,
    /// <summary>Neither value appeared within the frame cap.</summary>
    Inconclusive,
}
/// <summary>
/// Reads a GBMicrotest ROM's verdict from its own convention: the actual result at <c>$FF80</c>, the expected result
/// at <c>$FF81</c>, and the pass/fail flag at <c>$FF82</c> (<c>0x01</c> pass, <c>0xFF</c> fail) — the suite's howto is
/// explicit that only <c>$FF82</c> is a reliable pass/fail indicator, since some tests set <c>$FF80 == $FF81</c> on
/// failure. Read through <see cref="SystemBus.DebugReadByte"/> so the poll itself has no bus side effects.
/// </summary>
internal static class GbMicrotestProbe {
    private const ushort ActualAddress = 0xFF80;
    private const byte FailFlag = 0xFF;
    private const ushort FlagAddress = 0xFF82;
    private const byte PassFlag = 0x01;

    /// <summary>Runs a case to a verdict.</summary>
    /// <param name="romCase">The case to run.</param>
    /// <returns>The verdict and a one-line detail.</returns>
    public static (GbMicrotestResult Result, string Detail) Run(RomCase romCase) {
        var rom = File.ReadAllBytes(path: romCase.FullPath);

        using var machine = PostMachine.Build(
            model: romCase.Model,
            rom: rom
        );

        var bus = machine.GetRequiredService<SystemBus>();

        for (var frame = 0; (frame < romCase.FrameCap); ++frame) {
            PostMachine.RunFrames(
                frames: 1,
                instance: machine
            );

            var flag = bus.DebugReadByte(address: FlagAddress);

            if (flag == PassFlag) {
                return (GbMicrotestResult.Pass, $"$FF82=0x01 after {(frame + 1)} frames");
            }

            if (flag == FailFlag) {
                var actual = bus.DebugReadByte(address: ActualAddress);
                var expected = bus.DebugReadByte(address: 0xFF81);

                return (GbMicrotestResult.Fail, $"$FF82=0xFF after {(frame + 1)} frames (actual=0x{actual:X2}, expected=0x{expected:X2})");
            }
        }

        return (GbMicrotestResult.Inconclusive, $"$FF82 never became 0x01/0xFF within {romCase.FrameCap} frames (read 0x{bus.DebugReadByte(address: FlagAddress):X2})");
    }
}
