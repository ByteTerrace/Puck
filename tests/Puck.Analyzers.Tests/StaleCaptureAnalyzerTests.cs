using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Exercises <see cref="StaleCaptureAnalyzer"/> over small compilations shaped like the rule evaluator and its host: a
/// scratch taken before a transactional commit and read after it is refused, a re-read or a hand-off is not, and the
/// marks that carry a replacement across an assembly or a dispatch are held complete. The fixtures declare the marks the
/// way <c>build/ReplacementAttributes.cs</c> does.
/// </summary>
public sealed class StaleCaptureAnalyzerTests {
    private const string Marks = """
        namespace Puck {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            internal sealed class ReplaceableAttribute(System.Type? root = null) : System.Attribute { public System.Type? Root { get; } = root; }
            [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Property | System.AttributeTargets.Constructor, AllowMultiple = true)]
            internal sealed class ReplacesAttribute(System.Type root) : System.Attribute { public System.Type Root { get; } = root; }
        }

        namespace Subject {
            [Puck.Replaceable]
            public sealed class Arena { public Scratch Scratch { get; } = new(); }
            [Puck.Replaceable(typeof(Arena))]
            public sealed class Scratch { public int Enter() => 0; public void Leave(int scope) { } }
            public interface IHost {
                Arena Arena { get; }
                Scratch Scratch => Arena.Scratch;
                [Puck.Replaces(typeof(Arena))]
                void CommitTransactional();
                void Deliver();
            }
        }

        """;

    // The fixtures compile without xUnit: a compilation that references it is a test assembly, which owes no mark to a
    // caller, and the cases here are production code.
    private static AnalysisResult Run(string body) {
        var compilation = Harness.Compile(
            assemblyName: Harness.DefaultAssemblyName,
            sources: new SourceFile(Name: "Subject.cs", Text: (Marks + "namespace Subject {\n" + body + "\n}\n"))
        );
        var result = Harness.Analyze(
            analyzer: new StaleCaptureAnalyzer(),
            compilation: compilation.WithReferences(references: Harness.RuntimeReferences.Where(predicate: static reference => !(reference.Display ?? string.Empty).Contains(value: "xunit", comparisonType: StringComparison.OrdinalIgnoreCase)))
        );

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Empty(collection: result.WithId(id: "AD0001"));

        return result;
    }

    [Fact]
    public void AScratchTakenBeforeTheCommitAndReadAfterItIsRefused() {
        var result = Run(body: """
            internal sealed class Evaluator(IHost host) {
                internal void Fire() {
                    var scratch = host.Scratch;
                    host.CommitTransactional();
                    var scope = scratch.Enter();
                    host.Deliver();
                    scratch.Leave(scope: scope);
                }
            }
            """);

        var diagnostic = result.Single(id: "STALE001");

        Assert.Contains(actualString: diagnostic.GetMessage(), comparisonType: StringComparison.Ordinal, expectedSubstring: "'scratch' holds the Arena taken before 'IHost.CommitTransactional()'");
    }

    [Fact]
    public void AScratchReadAgainAfterTheCommitIsAccepted() {
        var result = Run(body: """
            internal sealed class Evaluator(IHost host) {
                internal void Fire() {
                    var scratch = host.Scratch;
                    _ = scratch.Enter();
                    host.CommitTransactional();
                    scratch = host.Scratch;
                    _ = scratch.Enter();
                    Deliver();
                }
                private void Deliver() {
                    var scratch = host.Scratch;
                    scratch.Leave(scope: scratch.Enter());
                }
            }
            """);

        Assert.Empty(collection: result.WithId(id: "STALE001"));
    }

    [Fact]
    public void AReplacementReachedThroughTheCompilationsOwnCallsIsFollowed() {
        var result = Run(body: """
            internal sealed class Evaluator(IHost host) {
                internal void RunFixpoint() {
                    var arena = host.Arena;
                    for (var pass = 0; pass < 2; pass++) {
                        EvaluateRule();
                    }
                    _ = arena.Scratch;
                }
                private void EvaluateRule() => FireEffects();
                private void FireEffects() => host.CommitTransactional();
            }
            """);

        Assert.Contains(actualString: result.Single(id: "STALE001").GetMessage(), comparisonType: StringComparison.Ordinal, expectedSubstring: "'arena' holds the Arena taken before 'Evaluator.EvaluateRule()'");
    }

    [Fact]
    public void AReadEarlierInALoopThanTheCommitIsAfterItOnTheNextPass() {
        var result = Run(body: """
            internal sealed class Evaluator(IHost host) {
                internal void Each(int count) {
                    var scratch = host.Scratch;
                    for (var index = 0; index < count; index++) {
                        _ = scratch.Enter();
                        host.CommitTransactional();
                    }
                }
            }
            """);

        _ = result.Single(id: "STALE001");
    }

    [Fact]
    public void AReadNoPathFromTheCommitReachesIsAccepted() {
        var result = Run(body: """
            internal sealed class Evaluator(IHost host) {
                internal bool Decide(bool open, Arena? prepared) {
                    var scratch = host.Scratch;
                    if (!open) {
                        host.CommitTransactional();
                        return false;
                    }
                    _ = scratch.Enter();
                    if (prepared is null) {
                        host.CommitTransactional();
                    } else {
                        _ = prepared.Scratch;
                    }
                    return true;
                }
            }
            """);

        Assert.Empty(collection: result.WithId(id: "STALE001"));
    }

    [Fact]
    public void AFinallyAfterTheCommitReadsTheOldScratch() {
        var result = Run(body: """
            internal sealed class Evaluator(IHost host) {
                internal void EvaluateOnce() {
                    var scratch = host.Scratch;
                    var scope = scratch.Enter();
                    try {
                        host.CommitTransactional();
                    } finally {
                        scratch.Leave(scope: scope);
                    }
                }
            }
            """);

        _ = result.Single(id: "STALE001");
    }

    [Fact]
    public void AnArenaHandedToTheReplacingCallIsTheReplacementNotACapture() {
        var result = Run(body: """
            internal sealed class Server {
                private Arena m_arena = new();
                public Arena Arena => m_arena;
                [Puck.Replaces(typeof(Arena))]
                internal void Adopt(Arena arena) => m_arena = arena;
                internal void Reload() {
                    var prepared = new Arena();
                    Adopt(arena: prepared);
                    _ = prepared.Scratch;
                }
            }
            """);

        Assert.Empty(collection: result.WithId(id: "STALE001"));
        Assert.Empty(collection: result.WithId(id: "STALE003"));
    }

    [Fact]
    public void APublicMemberThatReachesAReplacementCarriesTheMark() {
        var result = Run(body: """
            public sealed class Evaluator(IHost host) {
                public Arena Arena => host.Arena;
                public void Evaluate() => host.CommitTransactional();
                [Puck.Replaces(typeof(Arena))]
                public void EvaluateMarked() => host.CommitTransactional();
                internal void EvaluateInside() => host.CommitTransactional();
            }
            public sealed class Driver(Evaluator evaluator) {
                public void Step() => evaluator.EvaluateMarked();
            }
            """);

        Assert.Contains(actualString: result.Single(id: "STALE002").GetMessage(), comparisonType: StringComparison.Ordinal, expectedSubstring: "'Evaluator.Evaluate()' can replace Arena through 'IHost.CommitTransactional()', but it is visible outside its assembly");
    }

    [Fact]
    public void AnOverrideThatReplacesNeedsItsBaseMarked() {
        var result = Run(body: """
            public class HostBase {
                public virtual void Commit() { }
            }
            public sealed class WorldHost : HostBase {
                private Arena m_arena = new();
                public Arena Arena => m_arena;
                [Puck.Replaces(typeof(Arena))]
                public override void Commit() => m_arena = new Arena();
            }
            """);

        Assert.Contains(actualString: result.Single(id: "STALE002").GetMessage(), comparisonType: StringComparison.Ordinal, expectedSubstring: "'HostBase.Commit()', which it overrides or implements, does not carry");
    }

    [Fact]
    public void AssigningAServedFieldOutsideAConstructorCarriesTheMark() {
        var result = Run(body: """
            internal sealed class Server {
                private Arena m_arena;
                private Arena? m_cache;
                internal Server() => m_arena = new Arena();
                internal Arena Arena => m_arena;
                internal void Rebuild() => m_arena = new Arena();
                internal Arena Cached() {
                    m_cache = new Arena();
                    return m_cache;
                }
            }
            """);

        Assert.Contains(actualString: result.Single(id: "STALE003").GetMessage(), comparisonType: StringComparison.Ordinal, expectedSubstring: "'Server.Rebuild()' assigns 'm_arena', the Arena 'Server.Arena' serves");
    }
}
