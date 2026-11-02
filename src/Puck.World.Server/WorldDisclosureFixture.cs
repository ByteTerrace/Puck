using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One operator console line a variant runs to author its hidden state, pinned to a tick like a
/// <c>world.at</c> step.</summary>
/// <param name="At">The completed tick after which the line runs; at least 1.</param>
/// <param name="Run">The console line, run under the console principal.</param>
public sealed record WorldDisclosureStep(ulong At, string Run);

/// <summary>One world of a pair: the fixture world plus the hidden state its operator steps author.</summary>
/// <param name="Name">The variant's name, unique within the fixture.</param>
/// <param name="Steps">The operator lines that author this variant's hidden state.</param>
public sealed record WorldDisclosureVariant(string Name, IReadOnlyList<WorldDisclosureStep> Steps);

/// <summary>The seeded random inputs both worlds of a pair receive.</summary>
/// <param name="Seed">The seed of the draws.</param>
/// <param name="Lines">The number of console lines the observed seat submits.</param>
/// <param name="Actions">The number of actions each observed session performs.</param>
/// <param name="From">The first tick an input may be submitted after; absent, the tick after the last variant step.</param>
/// <param name="Until">The last tick an input may be submitted after; absent, the schedule's export tick less its settle ticks.</param>
/// <param name="Words">The words drawn as arguments: every row, key, seat and value name the principal can spell, existing
/// or not. Never read from either world, whose names differ with the hidden state under test.</param>
/// <param name="Verbs">The console verbs drawn; absent, every verb the registry lets any principal run, less
/// <paramref name="Exclude"/> and <see cref="WorldDisclosureFixture.OutsideTheGuarantee"/>.</param>
/// <param name="Exclude">Further verbs (or <c>prefix.</c> families) not drawn.</param>
public sealed record WorldDisclosureFuzz(ulong Seed = 1, int Lines = 0, int Actions = 0, ulong? From = null, ulong? Until = null,
    IReadOnlyList<string>? Words = null, IReadOnlyList<string>? Verbs = null, IReadOnlyList<string>? Exclude = null);

/// <summary>What a worker runs for one world of a disclosure pair: a variant's steps, the principals observed, and the
/// window the drawn inputs fall in.</summary>
/// <param name="Variant">The variant's name.</param>
/// <param name="Observer">The observed seat (<c>seat1</c>..<c>seat4</c>).</param>
/// <param name="Steps">The variant's operator steps.</param>
/// <param name="Script">The observed seat's authored console lines.</param>
/// <param name="Sessions">The authorities observed sessions are admitted from.</param>
/// <param name="Fuzz">The drawn inputs.</param>
/// <param name="From">The resolved first input tick.</param>
/// <param name="Until">The resolved last input tick.</param>
public sealed record WorldDisclosureLeg(string Variant, string Observer, IReadOnlyList<WorldDisclosureStep> Steps, IReadOnlyList<WorldDisclosureStep> Script,
    IReadOnlyList<string> Sessions, WorldDisclosureFuzz Fuzz, ulong From, ulong Until);

/// <summary>
/// A noninterference fixture: the <c>&lt;world&gt;.disclosure.json</c> beside a world that says how to build a pair of
/// it identical except for hidden state, and which non-operator principals to observe. <c>puck disclosure</c> boots one
/// world per variant (and the first variant twice, as the control), drives the same inputs in each, and compares what
/// every observed principal received.
/// </summary>
/// <param name="Schema">The fixture's schema id, <see cref="SchemaId"/>.</param>
/// <param name="Observer">The seat whose console session is observed: the world's own <c>schedule</c> rows it submits,
/// <paramref name="Script"/> and the drawn console lines.</param>
/// <param name="Variants">The worlds of the pair, at least two; the first is the one every other is compared with.</param>
/// <param name="Script">The observed seat's authored console lines, any verb, each pinned to a tick. Absent submits only
/// the schedule's rows.</param>
/// <param name="Sessions">The authorities an observed session is admitted from; the world's <c>admission</c> rows decide
/// each one's tier. Absent observes none.</param>
/// <param name="Fuzz">The drawn inputs; absent draws none.</param>
public sealed record WorldDisclosureFixture(string Schema, string Observer, IReadOnlyList<WorldDisclosureVariant> Variants,
    IReadOnlyList<WorldDisclosureStep>? Script = null, IReadOnlyList<string>? Sessions = null, WorldDisclosureFuzz? Fuzz = null) {
    /// <summary>The fixture's schema id.</summary>
    public const string SchemaId = "puck.disclosure.fixture.v1";
    /// <summary>The suffix a fixture's file name puts after its world's stem.</summary>
    public const string Suffix = ".disclosure.json";

    /// <summary>The verbs never drawn: their answers read wall time or machine resources, which the disclosure guarantee
    /// leaves out, or they pace the session rather than ask the world anything. A trailing dot names a family.</summary>
    public static readonly IReadOnlyList<string> OutsideTheGuarantee = ["host.", "capture.", "world.counters", "world.gpu-timing",
        "world.screenshot", "world.wait", "world.at", "quit"];

    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Returns whether the drawn verbs leave <paramref name="verb"/> out.</summary>
    /// <param name="verb">The verb.</param>
    /// <param name="exclude">The fixture's own exclusions.</param>
    /// <returns><see langword="true"/> when <paramref name="verb"/> is never drawn.</returns>
    public static bool Excludes(string verb, IReadOnlyList<string>? exclude) => OutsideTheGuarantee.Concat(second: (exclude ?? [])).Any(predicate: excluded =>
        (excluded.EndsWith(value: '.') ? verb.StartsWith(value: excluded, comparisonType: StringComparison.Ordinal) : string.Equals(a: verb, b: excluded, comparisonType: StringComparison.Ordinal)));

    /// <summary>Reads and validates a fixture.</summary>
    /// <param name="path">The fixture file.</param>
    /// <param name="fixture">The fixture, when valid.</param>
    /// <param name="reason">Why the fixture was refused, naming the member.</param>
    /// <returns><see langword="true"/> when the fixture is valid.</returns>
    public static bool TryRead(string path, out WorldDisclosureFixture? fixture, out string? reason) {
        fixture = null;
        reason = null;
        try {
            fixture = JsonSerializer.Deserialize<WorldDisclosureFixture>(utf8Json: File.ReadAllBytes(path: path), options: Options);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException)) {
            reason = $"{path} could not be read: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
            return false;
        }
        reason = (fixture switch {
            null => "is empty",
            { Schema: var schema } when (schema != SchemaId) => $"schema is '{schema}', not '{SchemaId}'",
            { Observer: var observer } when (!PrincipalTokens.TryParse(principal: out var principal, token: observer) || (principal.Kind != PrincipalKind.Seat)) =>
                $"observer '{observer}' is not a seat (seat1..seat4); the in-process principals are named by sessions",
            { Variants: not { Count: >= 2 } } => "variants names fewer than two worlds, so there is no pair",
            { Variants: var variants } when (variants.Select(selector: static variant => variant.Name).Distinct(comparer: StringComparer.Ordinal).Count() != variants.Count) =>
                "variants names one variant twice",
            { Variants: var variants } when variants.Any(predicate: static variant => variant.Steps.Any(predicate: static step => (step.At == 0UL) || string.IsNullOrWhiteSpace(value: step.Run))) =>
                "a variant step runs at tick 0 or runs nothing; a step runs after a completed tick, at 1 or later",
            { Fuzz: { Lines: > 0, Words: not { Count: > 0 } } } => "fuzz draws lines but names no words to spell their arguments with",
            _ => null,
        });
        if (reason is not null) {
            reason = $"{path}: {reason}.";
            fixture = null;
            return false;
        }
        return true;
    }
}
