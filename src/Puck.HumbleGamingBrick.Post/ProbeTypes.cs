namespace Puck.HumbleGamingBrick.Post;

/// <summary>The reporting channel a ledger-gated ROM case is read through.</summary>
internal enum ProbeKind {
    /// <summary>The blargg <c>0xA000</c> result block, read through <see cref="ConformanceRomProbe"/>.</summary>
    ConformanceSerial,
    /// <summary>The mooneye Fibonacci signature over serial, read through <see cref="AcceptanceRomProbe"/>.</summary>
    AcceptanceFibonacci,
    /// <summary>The Fibonacci-or-<c>0x42</c> signature read directly from the register file, for suites that never
    /// emit it over serial (see <see cref="RegisterSignatureProbe"/>).</summary>
    RegisterSignature,
    /// <summary>The GBMicrotest <c>$FF80</c>-<c>$FF82</c> result block, read through <see cref="GbMicrotestProbe"/>.</summary>
    GbMicrotest,
    /// <summary>A framebuffer capture compared pixel-exact against a shipped expected PNG, through
    /// <see cref="ScreenshotProbe"/>.</summary>
    Screenshot,
}
/// <summary>What a probe found.</summary>
internal enum ProbeVerdict {
    /// <summary>The ROM reported success.</summary>
    Pass,
    /// <summary>The ROM reported failure, or matched an expected image inexactly.</summary>
    Fail,
    /// <summary>The ROM produced no result within its frame budget.</summary>
    Inconclusive,
}
/// <summary>A probe's result: the verdict, a one-line detail, and — for a <see cref="ProbeKind.Screenshot"/> failure —
/// the differing-pixel count.</summary>
/// <param name="Verdict">The probe's verdict.</param>
/// <param name="Detail">A one-line success summary or failure reason.</param>
/// <param name="DiffPixelCount">The differing-pixel count for a failed screenshot comparison; <see langword="null"/> otherwise.</param>
internal sealed record ProbeOutcome(ProbeVerdict Verdict, string Detail, int? DiffPixelCount = null);
/// <summary>Whether a ledger case is meant to run the emulator at all.</summary>
internal enum CaseDisposition {
    /// <summary>Run the probe and compare its verdict against the ledger.</summary>
    Runnable,
    /// <summary>The corpus's own howto or README makes this case impossible to check mechanically (button input,
    /// an undecoded result convention); recorded with a reason instead of run.</summary>
    Unrunnable,
}
/// <summary>One ledger-gated ROM case: where it lives, which model and probe it runs under, and — for a screenshot
/// case — the expected-image candidates to try in order (the first that exists on disk wins).</summary>
/// <param name="Suite">The ledger suite key (also the stage-name suffix).</param>
/// <param name="RelativePath">The ROM's path relative to its suite's on-disk root, forward-slash separated — the ledger key together with <paramref name="Suite"/> and <paramref name="Model"/>.</param>
/// <param name="FullPath">The absolute path to the ROM image.</param>
/// <param name="Model">The console model to run the case on.</param>
/// <param name="Probe">The probe to run for a <see cref="CaseDisposition.Runnable"/> case.</param>
/// <param name="FrameCap">The frame budget before a runnable case is declared inconclusive.</param>
/// <param name="Disposition">Whether this case runs the emulator at all.</param>
/// <param name="UnrunnableReason">The reason a <see cref="CaseDisposition.Unrunnable"/> case is not run.</param>
/// <param name="ExpectedImageCandidates">For <see cref="ProbeKind.Screenshot"/>, the expected-image paths to try in order.</param>
internal sealed record LedgerCase(
    string Suite,
    string RelativePath,
    string FullPath,
    ConsoleModel Model,
    ProbeKind Probe,
    int FrameCap,
    CaseDisposition Disposition = CaseDisposition.Runnable,
    string? UnrunnableReason = null,
    IReadOnlyList<string>? ExpectedImageCandidates = null
);
