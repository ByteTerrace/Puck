namespace Puck.World.Browser.Engine;

/// <summary>An analysis-only result for a draft that is never installed into a browser session.</summary>
public sealed record BrowserCostAnalysisResult(bool Ok, bool Validated, BrowserCostReport? Report,
    IReadOnlyList<BrowserErrorPath>? Errors, IReadOnlyList<BrowserErrorPath>? ValidationErrors,
    IReadOnlyList<string>? Deferred);
/// <summary>Produces the shared authored cost report for drafts whose rule programs compile, retaining ordinary
/// local-validation refusals and platform-deferred notices separately from analysis failure.</summary>
public static class BrowserCostAnalyzer {
    /// <summary>Parses, resolves, validates, and analyzes a standalone JSON document without allocating an arena.</summary>
    public static BrowserCostAnalysisResult Analyze(byte[] utf8Json) {
        ArgumentNullException.ThrowIfNull(utf8Json);
        var errors = new List<string>();

        if (!BrowserParser.TryParseAndResolve(definition: out var definition, errors: errors, utf8Json: utf8Json)) {
            return Failure(errors: errors);
        }

        var deferred = new List<string>();

        if (WorldDefinitionValidator.TryValidateLocally(compilation: out var validatedCompilation, deferred: deferred, definition: definition!, errors: errors)) {
            return Success(validatedCompilation!, validated: true, validationErrors: null, deferred);
        }

        try {
            return Success(
                WorldRuleCompilation.Compile(definition: definition!),
                validated: false,
                validationErrors: [.. errors.Select(selector: BrowserErrorPaths.Split)],
                deferred
            );
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
            errors.Add(item: exception.Message.ReplaceLineEndings(replacementText: " "));
            return Failure(errors: errors);
        }
    }

    private static BrowserCostAnalysisResult Success(WorldRuleCompilation compilation, bool validated,
        IReadOnlyList<BrowserErrorPath>? validationErrors, IReadOnlyList<string> deferred) => new(
        Ok: true,
        Validated: validated,
        Report: BrowserCostReport.From(report: compilation.CostReport),
        Errors: null,
        ValidationErrors: validationErrors,
        Deferred: deferred
    );
    private static BrowserCostAnalysisResult Failure(IEnumerable<string> errors) => new(
        Ok: false,
        Validated: false,
        Report: null,
        Errors: [.. errors.Select(selector: BrowserErrorPaths.Split)],
        ValidationErrors: null,
        Deferred: null
    );
}
