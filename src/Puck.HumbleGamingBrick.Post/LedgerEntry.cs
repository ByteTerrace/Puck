namespace Puck.HumbleGamingBrick.Post;

/// <summary>The recorded disposition of one ledger entry.</summary>
internal enum LedgerOutcome {
    /// <summary>The case is expected to pass its probe.</summary>
    Pass,
    /// <summary>The case is expected to fail its probe (a known, recorded defect).</summary>
    Fail,
    /// <summary>The case cannot be checked mechanically; <see cref="LedgerEntry.Reason"/> says why.</summary>
    Unrunnable,
    /// <summary>The probe is expected to produce no result within its frame budget — a distinct recorded verdict from
    /// <see cref="Pass"/>/<see cref="Fail"/>, never folded into either. The gate requires the recorded and actual
    /// verdicts to be equal, so a probe that regresses from a real pass/fail into inconclusiveness (a liveness-gate
    /// catch, an undecodable glyph, a vanished expected image) is caught the same way any other regression is.</summary>
    Inconclusive,
}
/// <summary>One row of <c>Expectations.json</c>: the identity of a ledger case (<see cref="Suite"/>, <see cref="Path"/>,
/// <see cref="Model"/>) and its recorded disposition.</summary>
/// <param name="Suite">The suite key (matches a <see cref="LedgerCase.Suite"/>).</param>
/// <param name="Path">The ROM's path relative to its suite's on-disk root, forward-slash separated.</param>
/// <param name="Model">The console model — the exact <see cref="ConsoleModel"/> member name the case ran on (e.g.
/// <c>DmgC</c>, <c>CgbE</c>, <c>Agb</c>, <c>Mgb</c>, <c>Sgb2</c>), not a coarse family tag.</param>
/// <param name="Probe">The probe kind's name.</param>
/// <param name="RomHash">The ROM bytes' 64-bit FNV-1a hash, lowercase hexadecimal.</param>
/// <param name="Outcome">The recorded disposition.</param>
/// <param name="Reason">The failure detail, or the reason an unrunnable case is not run.</param>
/// <param name="DiffPixels">The recorded differing-pixel count for a failed <see cref="ProbeKind.Screenshot"/> case.</param>
/// <param name="ExpectedImageHash">For <see cref="ProbeKind.Screenshot"/>, the first existing expected-image
/// candidate's 64-bit FNV-1a hash — pinned the same way <see cref="RomHash"/> pins the ROM, so a swapped, corrupted, or
/// deleted fixture is a recorded mismatch rather than a silently different comparison. <see langword="null"/> when no
/// candidate exists on disk (still gated: a case that goes from having an image to having none is a mismatch).</param>
internal sealed record LedgerEntry(string Suite, string Path, string Model, string Probe, string RomHash, LedgerOutcome Outcome, string? Reason, int? DiffPixels, string? ExpectedImageHash = null) {
    /// <summary>Builds the tuple key an entry and its matching <see cref="LedgerCase"/> are compared under.</summary>
    public (string Suite, string Path, string Model) Key =>
        (Suite, Path, Model);
}
