namespace Puck.Cli.Canary;

internal enum CanaryBootShape {
    Headless,
    Windowed,
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
    IReadOnlyList<CanaryCommandClaim> Commands,
    string Name,
    string ScriptPath,
    string WorldPath
);

internal sealed record CanaryCommandClaim(string Verb, int Occurrence, CanaryCommandOutcome Outcome);

internal abstract record CanaryAssertion(string Name);

internal sealed record CanaryLineAssertion(
    CanaryLineMatch Match,
    string Name,
    bool Present,
    CanaryStream Stream,
    string Text
) : CanaryAssertion(Name: Name);

internal sealed record CanaryResponseAssertion(
    int Count,
    IReadOnlyList<CanaryValueExtraction> Extractions,
    string Name,
    int Occurrence,
    CanaryStream Stream,
    string Verb
) : CanaryAssertion(Name: Name);

internal sealed record CanarySequenceAssertion(
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

internal sealed record CanaryResponseSelector(string Verb, int Occurrence, int Count);

internal sealed record CanaryValueExtraction(string Field, int? Component, string Name);

internal sealed record CanaryOperand(string? ValueName, string? StringLiteral, double? NumberLiteral);
