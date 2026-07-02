namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Runs a mooneye acceptance ROM and reads its verdict from the serial signature it prints on completion: the register
/// file B, C, D, E, H, L set to the Fibonacci sequence 3, 5, 8, 13, 21, 34 means pass, and all six bytes <c>0x42</c>
/// means fail. The bytes are captured through <see cref="SerialComponent.ByteTransmitted"/> (the same seam the blargg
/// reader uses). The machine is advanced one frame at a time and polled after each, exiting as soon as a signature
/// appears.
/// </summary>
internal static class MooneyeProbe {
    private static readonly byte[] PassSignature = [3, 5, 8, 13, 21, 34];
    private static readonly byte[] FailSignature = [0x42, 0x42, 0x42, 0x42, 0x42, 0x42];

    /// <summary>Runs a case to a verdict.</summary>
    /// <param name="romCase">The case to run.</param>
    /// <returns><see langword="true"/> for pass, <see langword="false"/> for the failure signature, or
    /// <see langword="null"/> when neither appeared within the frame cap; paired with a one-line detail.</returns>
    public static (bool? Passed, string Detail) Run(RomCase romCase) {
        var rom = File.ReadAllBytes(path: romCase.FullPath);

        using var machine = PostMachine.Build(model: romCase.Model, rom: rom);

        var transmitted = new List<byte>();

        machine.GetRequiredService<SerialComponent>().ByteTransmitted = value => transmitted.Add(item: value);

        for (var frame = 0; (frame < romCase.FrameCap); ++frame) {
            PostMachine.RunFrames(instance: machine, frames: 1);

            if (EndsWith(sequence: transmitted, suffix: PassSignature)) {
                return (true, $"fib signature after {frame + 1} frames");
            }

            if (EndsWith(sequence: transmitted, suffix: FailSignature)) {
                return (false, $"0x42 failure signature after {frame + 1} frames");
            }
        }

        return (null, (transmitted.Count == 0)
            ? $"no serial output within {romCase.FrameCap} frames"
            : $"no signature within {romCase.FrameCap} frames; serial=[{string.Join(separator: ' ', values: transmitted.Select(selector: static b => b.ToString(format: "X2")))}]");
    }

    private static bool EndsWith(List<byte> sequence, byte[] suffix) {
        if (sequence.Count < suffix.Length) {
            return false;
        }

        var offset = (sequence.Count - suffix.Length);

        for (var index = 0; (index < suffix.Length); ++index) {
            if (sequence[offset + index] != suffix[index]) {
                return false;
            }
        }

        return true;
    }
}
