namespace Puck.Cli.Canary;

internal enum CanaryBootShape {
    Headless,
    Windowed,
    /// <summary>A multi-boot leg through <c>Puck.Launcher.Stub</c> rather than <c>Puck.World.dll</c> directly — the
    /// only shape that observes a second process launch. Never automatic (<see cref="CanaryManifest.IsAutomatic"/>).</summary>
    Stub,
    /// <summary>A real GPU device with no window (<c>host.presentation: offscreen</c>, authored by the leg's world), run
    /// once per declared backend (<see cref="CanaryManifest.Backends"/>). Never automatic.</summary>
    Offscreen,
}
/// <summary>How an <see cref="CanaryImageRegionAssertion"/> folds the pixels of its region.</summary>
internal enum CanaryImageReduce {
    /// <summary>Every pixel in the region satisfies the bounds.</summary>
    Every,
    /// <summary>The region's per-channel mean satisfies the bounds.</summary>
    Mean,
}
internal enum CanaryCommandOutcome {
    Accepted,
    Refused,
}
internal enum CanaryStream {
    Stdout,
    Stderr,
}
internal enum CanaryLineMatch {
    Exact,
    Contains,
}
internal enum CanaryRelationOperator {
    Equal,
    NotEqual,
    Greater,
    BetweenInclusive,
    AtLeast,
    AtMost,
    MinimumMargin,
}
internal sealed record CanaryManifest(
    // The graphics backends an offscreen proof runs every leg on, in authored order; empty for every other shape.
    IReadOnlyList<string> Backends,
    string Binding,
    CanaryBootShape BootShape,
    CanaryLeg Discriminating,
    string DirectoryPath,
    IReadOnlyList<string> Fixtures,
    string Id,
    CanaryLeg Positive,
    IReadOnlyList<string> Requirements,
    // The wall-clock ceiling one boot of a leg may take before the runner kills it. Not the leg's length: every leg
    // ends when its script ends, because the runner closes the script with wire.errors and quit.
    int TimeoutSeconds,
    string Title
) {
    public bool IsAutomatic => ((BootShape == CanaryBootShape.Headless) && (Requirements.Count == 0));
}
internal sealed record CanaryLeg(
    IReadOnlyList<CanaryAssertion> Assertions,
    // Non-empty only for a federated mesh leg (see CanaryAuthorityRole): every entry is a listener, none dials out,
    // and exactly one entry's World/Script equal this leg's own — the entry unscoped assertions read by default.
    // Empty for the pre-existing shapes (a lone process, or a two-process client/authorityWorld pair).
    IReadOnlyList<CanaryAuthorityRole> Authorities,
    string? AuthorityWorldPath,
    bool Connect,
    IReadOnlyList<CanaryCommandClaim> Commands,
    string Name,
    string ScriptPath,
    string WorldPath,
    // The declared world a composition source boots in place of its entry (World's --entry), or null.
    string? Entry = null,
    // A second boot of a lone-process leg after its first process exits, or null.
    CanaryRelaunch? Relaunch = null,
    // A shader package the runner prepares in the leg's run directory before the first boot, or null.
    CanaryPackage? Package = null,
    // Whether the World boots with every directory holding the shader compiler removed from its search path, so any
    // compile it attempts finds no tool. The runner's own package build still sees the compiler.
    bool HideShaderCompiler = false
);
// A relocated shader package whose source tree is gone: once per run the runner copies the files of the directory
// holding SourcePath into a scratch directory, packages SourcePath with this CLI's own `shaders package`, and deletes
// the copy; before a leg's first boot it copies the package to <run>/<OutputName>. Alter, a logical path inside the
// package, then has a line appended in the leg's copy, so its pin no longer holds.
internal sealed record CanaryPackage(string SourcePath, string OutputName, string? Alter);
// A second boot of a leg: the World relaunches on a document the first boot wrote into the leg's run directory, under
// the same state directory, and runs its own script. Assertions read both boots' streams in order; each boot's
// commands are accounted against its own process.
internal sealed record CanaryRelaunch(IReadOnlyList<CanaryCommandClaim> Commands, string ScriptPath, string WorldFileName);
// One listener in a federated mesh leg: its own world document, its own driving script, addressed by Id everywhere
// a manifest assertion or a peer's admission row needs to name it. Deliberately silent about HOW it is hosted — see
// CANARY-SHAPE.md item 7 — so a future non-Process launch strategy (a Silo grain standing in for one entry) is an
// addition here, never a reshape of this record or the manifest members that reference an Id.
internal sealed record CanaryAuthorityRole(string Id, string ScriptPath, string WorldPath);
internal sealed record CanaryCommandClaim(string Verb, int Occurrence, CanaryCommandOutcome Outcome, CanaryStream? StreamOverride);
internal abstract record CanaryAssertion(string Name);
internal sealed record CanaryLineAssertion(
    string? Authority,
    CanaryLineMatch Match,
    string Name,
    bool Present,
    CanaryStream Stream,
    string Text
) : CanaryAssertion(Name: Name);
internal sealed record CanaryResponseAssertion(
    string? Authority,
    int Count,
    IReadOnlyList<CanaryValueExtraction> Extractions,
    string Name,
    int Occurrence,
    CanaryStream Stream,
    string Verb
) : CanaryAssertion(Name: Name);
// Lines that must each appear, in the listed order: every line matches at an index past the previous line's match.
internal sealed record CanaryLineOrderAssertion(
    string? Authority,
    IReadOnlyList<string> Lines,
    CanaryLineMatch Match,
    string Name,
    CanaryStream Stream
) : CanaryAssertion(Name: Name);
internal sealed record CanarySequenceAssertion(
    string? Authority,
    string Name,
    IReadOnlyList<CanaryResponseSelector> Responses,
    CanaryStream Stream
) : CanaryAssertion(Name: Name);
internal sealed record CanaryRelationAssertion(
    CanaryOperand Left,
    double? Margin,
    double? Maximum,
    double? Minimum,
    string Name,
    CanaryRelationOperator Operator,
    CanaryOperand? Right
) : CanaryAssertion(Name: Name);
internal sealed record CanaryFileDifferenceAssertion(
    string After,
    string Before,
    bool Different,
    string Name
) : CanaryAssertion(Name: Name);
/// <summary>Two captures compared as images against the live-capture noise floor
/// (<see cref="CanaryFrameNoise"/>) rather than byte-for-byte, since two windowed captures of identical simulation
/// state are never bit-equal. <c>Agree</c> true requires the changed-pixel count within the noise budget; false
/// requires it beyond — a real relocation or recolor.</summary>
internal sealed record CanaryFrameAgreementAssertion(
    string After,
    bool Agree,
    string Before,
    string Name
) : CanaryAssertion(Name: Name);
/// <summary>One capture's pixels inside a normalized rectangle, checked against per-channel bounds derived by the
/// author rather than recorded from a run. A pixel belongs to the region when its center lies inside
/// [<c>Left</c>, <c>Right</c>] x [<c>Top</c>, <c>Bottom</c>]; bounds are normalized channel values in [0, 1], widened by
/// <c>ToleranceCodes</c> 8-bit codes; a null bound leaves that side open. <c>Holds</c> false requires the bounds to
/// fail — never a missing capture or a wrong extent, which fail either way.</summary>
internal sealed record CanaryImageRegionAssertion(
    double Bottom,
    string Capture,
    int Height,
    bool Holds,
    double Left,
    IReadOnlyList<double>? Maximum,
    IReadOnlyList<double>? Minimum,
    string Name,
    CanaryImageReduce Reduce,
    double Right,
    int ToleranceCodes,
    double Top,
    int Width
) : CanaryAssertion(Name: Name);
internal sealed record CanaryResponseSelector(string Verb, int Occurrence, int Count);
// Line, when set, reads the field from the first continuation line of the selected response that starts with it
// (after the transcript's indent) instead of from the response's own first line.
internal sealed record CanaryValueExtraction(string Field, int? Component, string Name, string? Line = null);
internal sealed record CanaryOperand(string? ValueName, string? StringLiteral, double? NumberLiteral);
