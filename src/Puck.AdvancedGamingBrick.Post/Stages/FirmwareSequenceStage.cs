namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Bounded native sequencer control-flow and note-lifetime fixtures, optionally compared with a verified retail image.</summary>
internal sealed class FirmwareSequenceStage : IPostStage<PostContext> {
    public string Name => "firmware-sequence";
    public PostTier Tier => PostTier.A;
    public bool IsConcurrent => true;

    public PostStageOutcome Run(PostContext context) {
        var retail = AgbBiosProfile.Identify(image: context.BiosImage.Span).Kind == AgbBiosKind.RealVerified
            ? context.BiosImage.ToArray() : null;
        var failures = new List<string>();
        var cases = 0;
        foreach (var vector in FirmwareSequenceVectors.ControlFlow()) {
            Check(name: vector.Name, action: () => ControlFlow(vector: vector, retail: retail), failures: failures);
            ++cases;
        }
        foreach (var (command, extra, duration) in new (byte, byte, byte)[] { (0xD0, 0, 1), (0xD1, 0, 2), (0xD3, 0, 4), (0xD0, 3, 4) }) {
            Check(name: $"gate-{duration}-extra-{extra}", action: () => Gate(command: command, extra: extra, duration: duration, retail: retail), failures: failures);
            ++cases;
        }
        Check(name: "tie-selective-end", action: () => Tie(retail: retail), failures: failures);
        Check(name: "tie-cached-end", action: () => Tie(retail: retail, implicitEnd: true), failures: failures);
        Check(name: "fixed-wave-loop", action: () => WaveLoop(retail: retail), failures: failures);
        cases += 3;
        return failures.Count == 0
            ? PostStageOutcome.Pass(detail: $"{cases} original native song/sample fixtures: GOTO, pattern depth1/3 and return, repeat0/1/3, finite note gates and optional length, selective tied-note end, fixed-wave loop; caller-owned records and192 PCM bytes per tick; {(retail is null ? "retail comparison unavailable" : "identical fixtures matched verified retail records/PCM")}; bounded functional evidence, not full music or timing parity")
            : PostStageOutcome.Fail(detail: string.Join(separator: "; ", values: failures));
    }

    private static void ControlFlow(FirmwareSequenceVectors.Vector vector, byte[]? retail) {
        Pair(program: vector.Program, retail: retail, ticks: vector.Points.Length, assertion: (fixture, tick) => {
            var point = vector.Points[tick];
            Equal(actual: fixture.Byte(address: FirmwareSequenceFixture.Track + 0x12), expected: point.Volume, detail: $"tick{tick} volume");
            Equal(actual: fixture.Byte(address: FirmwareSequenceFixture.Track + 1), expected: point.Wait, detail: $"tick{tick} wait");
            Equal(actual: fixture.Byte(address: FirmwareSequenceFixture.Track + 2), expected: point.Depth, detail: $"tick{tick} pattern depth");
            Equal(actual: fixture.Byte(address: FirmwareSequenceFixture.Track + 3), expected: point.Repeat, detail: $"tick{tick} repeat counter");
            Equal(actual: fixture.Word(address: FirmwareSequenceFixture.Track + 0x40), expected: FirmwareSequenceFixture.Program + (uint)point.Offset, detail: $"tick{tick} command cursor");
            Require(condition: ((fixture.Byte(address: FirmwareSequenceFixture.Track) & 0x80) != 0) == point.Active, detail: $"tick{tick} track active flag");
        });
    }

    private static void Gate(byte command, byte extra, byte duration, byte[]? retail) => Pair(
        program: FirmwareSequenceVectors.Note(command: command, extra: extra), retail: retail, ticks: 7, assertion: (fixture, tick) => {
            Require(condition: fixture.Byte(address: FirmwareSequenceFixture.Channel + 0x10) == Math.Max(0, duration - tick), detail: $"tick{tick} remaining gate duration");
            Require(condition: ((fixture.Byte(address: FirmwareSequenceFixture.Channel) & 0x40) != 0) == (tick >= duration), detail: $"tick{tick} gate release edge");
        });

    private static void Tie(byte[]? retail, bool implicitEnd = false) => Pair(
        program: implicitEnd
            ? [0xBD, 0, 0xBE, 127, 0xBF, 64, 0xCF, 60, 127, 0x82, 0xCE, 61, 0x82, 0xCE, 0x82, 0xCE, 60, 0x82, 0xB1]
            : [0xBD, 0, 0xBE, 127, 0xBF, 64, 0xCF, 60, 127, 0x82, 0xCE, 61, 0x82, 0xCE, 60, 0x82, 0xB1],
        retail: retail, ticks: implicitEnd ? 9 : 7, assertion: (fixture, tick) => {
            var releaseTick = implicitEnd ? 6 : 4;
            Require(condition: fixture.Byte(address: FirmwareSequenceFixture.Channel + 0x10) == 0, detail: $"tick{tick} tied note acquired a finite gate");
            Require(condition: fixture.Byte(address: FirmwareSequenceFixture.Channel + 0x11) == 60, detail: $"tick{tick} original MIDI key changed");
            Equal(actual: fixture.Byte(address: FirmwareSequenceFixture.Track + 5), expected: tick >= 2 && tick < releaseTick ? 61u : 60u, detail: $"tick{tick} cached key after explicit/implicit note end");
            Require(condition: ((fixture.Byte(address: FirmwareSequenceFixture.Channel) & 0x40) != 0) == (tick >= releaseTick), detail: $"tick{tick} selective end-tie release edge");
            Require(condition: fixture.Word(address: FirmwareSequenceFixture.Channel + 0x2C) == (tick < releaseTick + 2 ? FirmwareSequenceFixture.Track : 0), detail: $"tick{tick} releasing-note ownership");
        });

    private static void WaveLoop(byte[]? retail) => Pair(program: FirmwareSequenceVectors.Note(command: 0xCF), retail: retail, ticks: 2,
        setup: fixture => {
            fixture.Word(address: FirmwareSequenceFixture.Wave + 8, value: 3);
            fixture.Word(address: FirmwareSequenceFixture.Wave + 12, value: 8);
            fixture.Put(address: FirmwareSequenceFixture.Wave + 16, bytes: [0, 16, 32, 48, 64, 80, 96, 112, 0]);
        }, assertion: (fixture, tick) => {
            Require(condition: (fixture.Byte(address: FirmwareSequenceFixture.Channel) & 0x10) != 0, detail: "looping channel lost its loop flag");
            var pcm = fixture.Pcm();
            // The two observed envelope levels (255 and254) both quantize these voice gains to125/124.
            for (var sample = 0; sample < 96; ++sample) {
                var absolute = tick * 96 + sample;
                var position = absolute < 8 ? absolute : 3 + (absolute - 8) % 5;
                var value = position * 16;
                var right = (value * 125) >> 8;
                var left = (value * 124) >> 8;
                Require(condition: pcm[sample] == right && pcm[sample + 96] == left, detail: $"tick{tick} loop PCM sample{sample}: {pcm[sample]}/{pcm[sample + 96]}, expected{right}/{left}");
            }
        });

    private static void Pair(byte[] program, byte[]? retail, int ticks, Action<FirmwareSequenceFixture, int> assertion, Action<FirmwareSequenceFixture>? setup = null) {
        using var candidate = new FirmwareSequenceFixture(bios: null, program: program);
        using var reference = retail is null ? null : new FirmwareSequenceFixture(bios: retail, program: program);
        setup?.Invoke(obj: candidate);
        if (reference is not null) { setup?.Invoke(obj: reference); }
        var records = new uint[ticks][];
        var samples = new byte[ticks][];
        if (reference is not null) {
            // Finish the entire original reference control before testing the candidate. A candidate's early
            // failure must not hide a later incorrect assumption in the independently authored fixture.
            for (var tick = 0; tick < ticks; ++tick) {
                TickAndAssert(fixture: reference, tick: tick, assertion: assertion, label: "retail");
                records[tick] = reference.Records();
                samples[tick] = reference.Pcm();
            }
        }
        for (var tick = 0; tick < ticks; ++tick) {
            TickAndAssert(fixture: candidate, tick: tick, assertion: assertion, label: "Puck");
            if (reference is null) { continue; }
            var expected = records[tick];
            var actual = candidate.Records();
            for (var field = 0; field < expected.Length; ++field) {
                Require(condition: actual[field] == expected[field], detail: $"tick{tick} record{field}: Puck={actual[field]:X8}, retail={expected[field]:X8}");
            }
            Require(condition: candidate.Pcm().AsSpan().SequenceEqual(other: samples[tick]), detail: $"tick{tick} stereo PCM differs from retail");
        }
    }

    private static void TickAndAssert(FirmwareSequenceFixture fixture, int tick, Action<FirmwareSequenceFixture, int> assertion, string label) {
        try {
            fixture.Tick();
            assertion(arg1: fixture, arg2: tick);
        } catch (InvalidOperationException exception) {
            throw new InvalidOperationException(message: $"{label}: {exception.Message}", innerException: exception);
        }
    }

    private static void Check(string name, Action action, List<string> failures) {
        try { action(); } catch (InvalidOperationException exception) { failures.Add(item: $"{name}: {exception.Message}"); }
    }
    private static void Require(bool condition, string detail) => FirmwareSwiProbe.Require(condition: condition, detail: detail);
    private static void Equal(uint actual, uint expected, string detail) => Require(condition: actual == expected, detail: $"{detail}: {actual:X8}, expected{expected:X8}");
}
