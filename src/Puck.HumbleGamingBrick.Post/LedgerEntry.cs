namespace Puck.HumbleGamingBrick.Post;

/// <summary>The recorded disposition of one ledger entry.</summary>
internal enum LedgerOutcome {
    /// <summary>The case is expected to pass its probe.</summary>
    Pass,
    /// <summary>The case is expected to fail its probe (a known, recorded defect).</summary>
    Fail,
    /// <summary>The case cannot be checked mechanically; <see cref="LedgerEntry.Reason"/> says why.</summary>
    Unrunnable,
}
/// <summary>One row of <c>Expectations.json</c>: the identity of a ledger case (<see cref="Suite"/>, <see cref="Path"/>,
/// <see cref="Model"/>) and its recorded disposition.</summary>
/// <param name="Suite">The suite key (matches a <see cref="LedgerCase.Suite"/>).</param>
/// <param name="Path">The ROM's path relative to its suite's on-disk root, forward-slash separated.</param>
/// <param name="Model">The console model (<c>"Dmg"</c>, <c>"Cgb"</c>, or <c>"Agb"</c>).</param>
/// <param name="Probe">The probe kind's name.</param>
/// <param name="RomHash">The ROM bytes' 64-bit FNV-1a hash, lowercase hexadecimal.</param>
/// <param name="Outcome">The recorded disposition.</param>
/// <param name="Reason">The failure detail, or the reason an unrunnable case is not run.</param>
/// <param name="DiffPixels">The recorded differing-pixel count for a failed <see cref="ProbeKind.Screenshot"/> case.</param>
internal sealed record LedgerEntry(string Suite, string Path, string Model, string Probe, string RomHash, LedgerOutcome Outcome, string? Reason, int? DiffPixels) {
    /// <summary>Builds the tuple key an entry and its matching <see cref="LedgerCase"/> are compared under.</summary>
    public (string Suite, string Path, string Model) Key =>
        (Suite, Path, Model);
}
