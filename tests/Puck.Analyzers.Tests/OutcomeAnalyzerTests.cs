using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Exercises <see cref="OutcomeAnalyzer"/> over small compilations that declare the engine's outcome types under their
/// own namespaces: each rule reports its code on the spelling it refuses, and the handled spellings next to it pass.
/// </summary>
public sealed class OutcomeAnalyzerTests {
    private const string Types = """
        namespace Puck.State {
            public enum FaultCase : byte { Faulted = 0, Finite, Absent, Forever }
            public readonly struct Outcome {
                public bool TryFinite(out long raw) { raw = 0; return false; }
            }
            public readonly struct Resolved<T> {
                public bool TryNamed(out T value) { value = default!; return false; }
            }
            public enum Verdict : byte { Faulted = 0, Closed, Open }
            public enum FiringDecision : byte { Faulted = 0, Refused, Closed, Held, Fires }
            public interface IStateReader { }
        }

        namespace Puck.State.Rules {
            using Puck.State;
            public enum RuleOutcome : byte { Idle, Fired, Refused, Faulted }
            public readonly record struct CompiledExpressionToken(int Operation);
            public readonly record struct CompiledCellRef(int RowOrdinal);
            public sealed class LiveRow {
                public Resolved<int> Resolve(IStateReader reader) => default;
                public Resolved<int> Select(long index) => default;
            }
            public interface IRuleKey { Resolved<int> Resolve(IStateReader reader); }
            public interface IRuleOperand { Outcome Read(IStateReader reader); }
            public static class RuleExpressions {
                public static Outcome EvaluateDetached(IStateReader? reader, ReadOnlySpan<CompiledExpressionToken> program) => default;
            }
        }

        """;

    private static AnalysisResult Run(string body, string file = "Subject.cs", string assemblyName = Harness.DefaultAssemblyName, bool withXunit = false) {
        var compilation = Harness.Compile(
            assemblyName: assemblyName,
            sources: new SourceFile(Name: file, Text: (Types + "namespace Subject {\nusing Puck.State;\nusing Puck.State.Rules;\n" + body + "\n}\n"))
        );
        var result = Harness.Analyze(
            analyzer: new OutcomeAnalyzer(),
            compilation: (withXunit
                ? compilation
                : compilation.WithReferences(references: Harness.RuntimeReferences.Where(predicate: static reference => !(reference.Display ?? string.Empty).Contains(value: "xunit", comparisonType: StringComparison.OrdinalIgnoreCase))))
        );

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.WithId(id: "AD0001"));

        return result;
    }

    [Fact]
    public void ADiscardedOutcomeIsRefusedAsOutcome001() {
        var result = Run(body: """
            internal static class Subject {
                static Outcome Make() => default;
                static Resolved<int> Key() => default;
                static Verdict Gate() => Verdict.Open;
                static RuleOutcome Fire() => RuleOutcome.Idle;
                internal static void Drop() {
                    Make();
                    _ = Key();
                    Fire();
                }
                internal static void Declare() {
                    var _ = Gate();
                }
            }
            """);

        Assert.Equal(expected: ["OUTCOME001", "OUTCOME001", "OUTCOME001", "OUTCOME001"], actual: result.Ids);
    }

    [Fact]
    public void AnOutcomeThatIsStoredHandedOnOrReadIsNotADiscard() {
        var result = Run(body: """
            internal sealed class Subject {
                Outcome m_held;
                static Outcome Make() => default;
                internal Outcome Hand() => Make();
                internal void Store() { m_held = Make(); }
                internal bool Read() => (Make().TryFinite(out var raw) && (raw != 0L));
            }
            """);

        Assert.Empty(collection: result.Ids);
    }

    [Fact]
    public void ADiscardInACatchThatRethrowsIsNotADrop() {
        var rethrows = Run(body: """
            internal static class Subject {
                static RuleOutcome Rewind() => RuleOutcome.Faulted;
                internal static void Fire() {
                    try { _ = Rewind(); } catch {
                        _ = Rewind();
                        throw;
                    }
                }
            }
            """);
        var swallows = Run(body: """
            internal static class Subject {
                static RuleOutcome Rewind() => RuleOutcome.Faulted;
                internal static void Fire() {
                    try { Rewind(); } catch {
                        _ = Rewind();
                    }
                }
            }
            """);

        // The try body's own drop is refused either way; only the rethrowing catch's rewind is let through.
        Assert.Equal(expected: ["OUTCOME001"], actual: rethrows.Ids);
        Assert.Equal(expected: ["OUTCOME001", "OUTCOME001"], actual: swallows.Ids);
    }

    [Fact]
    public void AnAllowlistedMemberMayDropAnOutcome() {
        var result = Run(
            assemblyName: "Puck.State.Rules",
            body: """
            internal sealed class RuleEvaluator {
                RuleOutcome Evaluate(out bool applied) { applied = false; return RuleOutcome.Idle; }
                internal bool Evaluate() { _ = Evaluate(out var applied); return applied; }
                internal bool Other() { _ = Evaluate(out var applied); return applied; }
            }
            """);

        Assert.Equal(expected: ["OUTCOME001"], actual: result.Ids);
    }

    [Fact]
    public void ATryWhoseAnswerIsDroppedIsRefusedAsOutcome002() {
        var result = Run(body: """
            internal static class Subject {
                internal static long Read(Outcome outcome, Resolved<int> key) {
                    outcome.TryFinite(out var raw);
                    _ = key.TryNamed(out var named);
                    return (raw + named);
                }
            }
            """);

        Assert.Equal(expected: ["OUTCOME002", "OUTCOME002"], actual: result.Ids);
        Assert.Contains(actualString: result.WithId(id: "OUTCOME002")[0].GetMessage(), comparisonType: StringComparison.Ordinal, expectedSubstring: "Outcome.TryFinite");
    }

    [Fact]
    public void AVerdictOrDecisionComparedToOneCaseIsRefusedAsOutcome002() {
        var result = Run(body: """
            internal static class Subject {
                internal static int Read(Verdict verdict, FiringDecision decision) {
                    var open = (verdict == Verdict.Open);
                    var closed = (verdict != Verdict.Closed);
                    var fires = (decision is FiringDecision.Fires);
                    var held = (decision is not FiringDecision.Held);
                    return ((open ? 1 : 0) + (closed ? 1 : 0) + (fires ? 1 : 0) + (held ? 1 : 0));
                }
            }
            """);

        Assert.Equal(expected: ["OUTCOME002", "OUTCOME002", "OUTCOME002", "OUTCOME002"], actual: result.Ids);
    }

    [Fact]
    public void ASwitchOverEveryCaseAndAComparisonToFaultedAreAccepted() {
        var result = Run(body: """
            internal static class Subject {
                internal static int Read(Verdict verdict, FiringDecision decision) {
                    if (verdict == Verdict.Faulted) { return -1; }
                    if (decision is FiringDecision.Faulted) { return -2; }
                    if (decision is FiringDecision.Closed or FiringDecision.Held) { return 0; }
                    return (verdict switch {
                        Verdict.Open => 1,
                        Verdict.Closed => 0,
                        _ => throw new InvalidOperationException(),
                    });
                }
            }
            """);

        Assert.Empty(collection: result.Ids);
    }

    [Fact]
    public void ADetachedCallerOutsideTheListIsRefusedAsOutcome003() {
        var body = """
            internal static class Subject {
                internal static Outcome Fold(IStateReader reader) => RuleExpressions.EvaluateDetached(reader, default);
            }
            """;

        var refused = Run(body: body, file: "src/Puck.World.Server/Elsewhere.cs", assemblyName: "Puck.World.Server");
        var listed = Run(body: body, file: "src/Puck.World.Server/WorldRuleHost.FlockAffinities.cs", assemblyName: "Puck.World.Server");
        var tests = Run(body: body, file: "tests/Puck.State.Tests/Oracle.cs", assemblyName: "Puck.State.Tests", withXunit: true);

        Assert.Equal(expected: ["OUTCOME003"], actual: refused.Ids);
        Assert.Empty(collection: listed.Ids);
        Assert.Empty(collection: tests.Ids);
    }

    [Fact]
    public void AnOperandThatResolvesAnAddressItselfIsRefusedAsOutcome004() {
        var result = Run(body: """
            internal sealed class SelfResolving(LiveRow row, IRuleKey key) : IRuleOperand {
                public Outcome Read(IStateReader reader) {
                    var ordinal = row.Resolve(reader);
                    var named = key.Resolve(reader);
                    return default;
                }
            }
            internal sealed class Declaring(LiveRow row, CompiledCellRef cell, CompiledExpressionToken[] program) : IRuleOperand {
                public int Cost => (row.GetHashCode() + cell.RowOrdinal + program.Length);
                public Outcome Read(IStateReader reader) =>
                    (row.Select(0L).TryNamed(out _) ? default : default);
            }
            internal sealed class NotAnOperand(LiveRow row) {
                internal Resolved<int> Read(IStateReader reader) => row.Resolve(reader);
            }
            """);

        Assert.Equal(expected: ["OUTCOME004", "OUTCOME004"], actual: result.Ids);
    }
}
