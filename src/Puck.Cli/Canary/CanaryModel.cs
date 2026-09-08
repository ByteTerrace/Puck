namespace Puck.Cli.Canary;

internal enum CanaryBootShape {
    Headless,
    Windowed,
    /// <summary>A multi-boot leg through <c>Puck.Launcher.Stub</c> rather than <c>Puck.World.dll</c> directly — the
    /// only shape that observes a second process launch. Never automatic (<see cref="CanaryManifest.IsAutomatic"/>).</summary>
    Stub,
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
    BetweenInclusive,
    AtLeast,
    AtMost,
    MinimumMargin,
}
internal sealed record CanaryManifest(
    string Binding,
    CanaryBootShape BootShape,
    CanaryLeg Discriminating,
    string DirectoryPath,
    IReadOnlyList<string> Fixtures,
    string Id,
    CanaryLeg Positive,
    IReadOnlyList<string> Requirements,
    int Seconds,
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
    string WorldPath
);
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
internal sealed record CanaryResponseSelector(string Verb, int Occurrence, int Count);
internal sealed record CanaryValueExtraction(string Field, int? Component, string Name);
internal sealed record CanaryOperand(string? ValueName, string? StringLiteral, double? NumberLiteral);
