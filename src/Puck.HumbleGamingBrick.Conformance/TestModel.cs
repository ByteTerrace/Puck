namespace Puck.HumbleGamingBrick.Conformance;

/// <summary>The outcome of running a single conformance ROM.</summary>
public enum TestStatus {
    /// <summary>The ROM reported a pass (or matched its reference image).</summary>
    Pass,
    /// <summary>The ROM reported a failure (or diverged from its reference image).</summary>
    Fail,
    /// <summary>The ROM neither passed nor failed within its cycle budget (no result signal observed).</summary>
    Inconclusive,
    /// <summary>The ROM was not run (e.g. the test corpus is not available).</summary>
    Skipped,
}

/// <summary>How important a test is to the cycle-accuracy claim. The verdict weighs these differently:
/// functional tests are necessary but not sufficient; timing tests are the actual discriminators.</summary>
public enum TestTier {
    /// <summary>Functional baseline: a coarse, non-cycle-accurate emulator can also pass these.</summary>
    FunctionalBaseline,
    /// <summary>Cycle/timing discriminator: passing these is what distinguishes a cycle-accurate core.</summary>
    Timing,
    /// <summary>Game Boy Color-specific behaviour (palettes, double-speed, CGB audio).</summary>
    CgbSpecific,
}

/// <summary>The emulator subsystem a test primarily exercises, for the per-subsystem verdict rollup.</summary>
public enum TestSubsystem {
    /// <summary>CPU instruction behaviour and quirks (HALT bug, boot registers).</summary>
    Cpu,
    /// <summary>CPU instruction / memory-access cycle timing.</summary>
    CpuTiming,
    /// <summary>The divider and TIMA timer.</summary>
    TimerDiv,
    /// <summary>PPU mode timing, STAT/LYC, and mid-scanline register effects.</summary>
    PpuTiming,
    /// <summary>OAM DMA timing and bus locking.</summary>
    OamDma,
    /// <summary>Interrupt dispatch timing and the IE/IF edge cases.</summary>
    Interrupts,
    /// <summary>The serial link port timing.</summary>
    Serial,
    /// <summary>The audio processing unit.</summary>
    Apu,
    /// <summary>CGB-only behaviour (color palettes, double-speed switching).</summary>
    Cgb,
    /// <summary>Anything that does not fit a more specific subsystem.</summary>
    Other,
}

/// <summary>The result-reporting protocol a test ROM uses, which selects the runner that executes it.</summary>
public enum ResultProtocol {
    /// <summary>Mooneye-style: Fibonacci registers + <c>LD B,B</c> + serial bytes (also used by same-suite and age).</summary>
    Mooneye,
    /// <summary>Blargg-style: serial ASCII ending in "Passed"/"Failed" plus the <c>0xA000</c> memory protocol.</summary>
    Blargg,
    /// <summary>GBMicrotest: a pass/fail byte written to <c>0xFF82</c>.</summary>
    GbMicrotest,
    /// <summary>Screenshot comparison: the framebuffer is compared to a reference PNG.</summary>
    Screenshot,
}

/// <summary>One classified test ROM: its location, the model to run it on, how to read its result, and how it is
/// weighted in the verdict.</summary>
/// <param name="Suite">The suite name (e.g. "mooneye", "blargg", "mealybug").</param>
/// <param name="RelativePath">The ROM path relative to the corpus root (stable, used as the test id).</param>
/// <param name="FullPath">The absolute ROM path on disk.</param>
/// <param name="Model">The console model to emulate for this run.</param>
/// <param name="Protocol">The result-reporting protocol.</param>
/// <param name="Tier">The cycle-accuracy weight tier.</param>
/// <param name="Subsystem">The subsystem this test primarily exercises.</param>
/// <param name="ReferenceImagePath">For screenshot tests, the reference PNG path; otherwise <see langword="null"/>.</param>
/// <param name="FrameLimit">For screenshot tests, the number of frames to render before comparing (0 = run to LD B,B).</param>
/// <param name="CycleLimit">The maximum master-clock cycles to run before declaring the test inconclusive.</param>
public sealed record RomCase(
    string Suite,
    string RelativePath,
    string FullPath,
    ConsoleModel Model,
    ResultProtocol Protocol,
    TestTier Tier,
    TestSubsystem Subsystem,
    string? ReferenceImagePath,
    int FrameLimit,
    long CycleLimit
);

/// <summary>The result of executing one <see cref="RomCase"/>.</summary>
/// <param name="Case">The ROM that was run.</param>
/// <param name="Status">The pass/fail/inconclusive outcome.</param>
/// <param name="Detail">A human-readable explanation (the failing sub-test, mismatch count, serial text, etc.).</param>
public sealed record TestOutcome(RomCase Case, TestStatus Status, string Detail);
