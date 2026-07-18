namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Runs an acceptance ROM and reads its verdict from the serial signature it prints on completion: the register
/// file B, C, D, E, H, L set to the Fibonacci sequence 3, 5, 8, 13, 21, 34 means pass, and all six bytes <c>0x42</c>
/// means fail. The bytes are captured through <see cref="SerialComponent.ByteQueued"/> — the value the ROM writes to SB,
/// which is its intended output. (The conformance-ROM reader uses <see cref="SerialComponent.ByteTransmitted"/> instead,
/// because those ROMs wait for each transfer to finish; the acceptance suite's DMG output routine deliberately re-arms a
/// transfer that has not completed at the normal clock, so the value latched when the next transfer starts can be a
/// partially-shifted byte — the intended value is the one written to SB, which is what a real runner reads at the result
/// breakpoint.) The machine is advanced one frame at a time and polled after each, exiting as soon as a signature appears.
/// </summary>
internal static class AcceptanceRomProbe {
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

        machine.GetRequiredService<SerialComponent>().ByteQueued = value => transmitted.Add(item: value);

        for (var frame = 0; (frame < romCase.FrameCap); ++frame) {
            PostMachine.RunFrames(instance: machine, frames: 1);

            if (EndsWith(sequence: transmitted, suffix: PassSignature)) {
                return (true, $"fib signature after {(frame + 1)} frames");
            }

            if (EndsWith(sequence: transmitted, suffix: FailSignature)) {
                return (false, $"0x42 failure signature after {(frame + 1)} frames");
            }
        }

        return (null, ((transmitted.Count == 0)
            ? $"no serial output within {romCase.FrameCap} frames"
            : $"no signature within {romCase.FrameCap} frames; serial=[{string.Join(separator: ' ', values: transmitted.Select(selector: static b => b.ToString(format: "X2")))}]"));
    }

    private static bool EndsWith(List<byte> sequence, byte[] suffix) {
        if (sequence.Count < suffix.Length) {
            return false;
        }

        var offset = (sequence.Count - suffix.Length);

        for (var index = 0; (index < suffix.Length); ++index) {
            if (sequence[(offset + index)] != suffix[index]) {
                return false;
            }
        }

        return true;
    }
}
