using System.Collections.Immutable;
using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// Exercises <see cref="WaitAnalyzer"/>: a project held to the rule is refused every blocking member it reaches
/// outside the door, including through a method group, and its proven-complete task reads and zero-timeout polls pass;
/// a project that does not set the property is not held at all.
/// </summary>
public sealed class WaitAnalyzerTests {
    private const string Door = """
        namespace Puck.Abstractions.Waiting {
            public static class HostWaits {
                public static void Through(System.Threading.ManualResetEventSlim signal) => signal.Wait();
            }
        }

        """;

    private static readonly ImmutableDictionary<string, string> Held = ImmutableDictionary<string, string>.Empty.Add(key: WaitAnalyzer.RuleProperty, value: "true");

    private static AnalysisResult Analyze(string source, ImmutableDictionary<string, string>? options = null) {
        var compilation = Harness.Compile(Harness.DefaultAssemblyName, new SourceFile(Name: "Door.cs", Text: Door), new SourceFile(Name: "Subject.cs", Text: source));
        var result = Harness.Analyze(
            analyzer: new WaitAnalyzer(),
            // A compilation that references xUnit is a test assembly, which the rule never holds.
            compilation: compilation.WithReferences(references: Harness.RuntimeReferences.Where(predicate: static reference => !(reference.Display ?? string.Empty).Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "xunit"))),
            globalOptions: (options ?? Held)
        );

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        return result;
    }

    [Fact]
    public void EveryBlockingFamilyOutsideTheDoorIsReported() {
        var result = Analyze(source: """
            public sealed class Subject {
                private readonly object m_gate = new();
                public int Block(Task<int> task, WaitHandle handle, SemaphoreSlim semaphore, Thread thread) {
                    task.Wait();
                    _ = handle.WaitOne(10);
                    semaphore.Wait();
                    thread.Join();
                    Thread.Sleep(1);
                    lock (m_gate) { _ = Monitor.Wait(m_gate); }
                    _ = Task.WaitAny(task);
                    return (task.Result + task.GetAwaiter().GetResult());
                }
            }
            """);

        Assert.Equal(expected: 9, actual: result.WithId(id: "WAIT001").Length);
    }
    [Fact]
    public void LockWaitEntryPointsCannotBypassTheDoor() {
        var result = Analyze(source: """
            namespace Puck.Confinement {
                public sealed class ConfinedDirectory { public void AcquireLock() { } }
                public static class FileLockWait { public static void Acquire() { } }
            }
            namespace Puck.Abstractions {
                public static class FileLock { public static void Acquire() { } }
            }
            public sealed class Subject {
                public void Block(Puck.Confinement.ConfinedDirectory directory) {
                    directory.AcquireLock();
                    Puck.Confinement.FileLockWait.Acquire();
                    Puck.Abstractions.FileLock.Acquire();
                }
            }
            """);

        Assert.Equal(expected: 3, actual: result.WithId(id: "WAIT001").Length);
    }
    [Fact]
    public void AMethodGroupBoundToABlockingMemberIsReported() {
        var result = Analyze(source: """
            public sealed class Recovery {
                private readonly Action<TimeSpan> m_sleep;
                public Recovery(Action<TimeSpan>? sleep) { m_sleep = (sleep ?? Thread.Sleep); }
                public void Back() => m_sleep(TimeSpan.FromMilliseconds(250));
            }
            """);

        var diagnostic = result.Single(id: "WAIT001");

        Assert.Contains(expectedSubstring: "Thread.Sleep", actualString: diagnostic.GetMessage(formatProvider: System.Globalization.CultureInfo.InvariantCulture));
    }
    [Fact]
    public void ProvenCompleteReadsAndZeroTimeoutPollsPass() {
        var result = Analyze(source: """
            public sealed class Subject {
                public sealed class Observation { public Task<int>? Pending; }
                public int Read(Task<int> task, Observation observation, WaitHandle handle, SemaphoreSlim semaphore) {
                    var total = 0;
                    if (task.IsCompleted) { total += task.Result; }
                    if (task.IsCompletedSuccessfully && (task.Result > 0)) { total++; }
                    if (observation.Pending is { IsCompleted: true } pending) { total += pending.GetAwaiter().GetResult(); }
                    if (task.IsFaulted) { task.GetAwaiter().GetResult(); }
                    total += (task.IsCompleted ? task.Result : 0);
                    _ = handle.WaitOne(0);
                    _ = semaphore.Wait(TimeSpan.Zero);
                    return total;
                }
            }
            """);

        Assert.Empty(collection: result.WithId(id: "WAIT001"));
    }
    [Fact]
    public void ANegatedOrDisjunctiveTestProvesNothing() {
        var result = Analyze(source: """
            public sealed class Subject {
                public int Read(Task<int> task, bool other) {
                    var total = 0;
                    if (!task.IsCompleted) { total += task.Result; }
                    if (task.IsCompleted || other) { total += task.Result; }
                    if (task.IsCompleted) { } else { total += task.Result; }
                    return total;
                }
            }
            """);

        Assert.Equal(expected: 3, actual: result.WithId(id: "WAIT001").Length);
    }
    [Fact]
    public void TheDoorItselfAndAProjectNotHeldAreNotReported() {
        Assert.Empty(collection: Analyze(source: "public static class Empty { }").WithId(id: "WAIT001"));
        Assert.Empty(collection: Analyze(
            options: ImmutableDictionary<string, string>.Empty,
            source: "public static class Sleeper { public static void Nap() => Thread.Sleep(1); }"
        ).WithId(id: "WAIT001"));
    }
}
