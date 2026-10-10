using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// The core protocol between a shader build and its host (<see cref="StreamShaderCoreBroker"/>), against a host whose
/// budget, like MSBuild's, answers a later request only once a core is free: a request the host cannot answer yet never
/// stops the build from finishing compiles on the cores it holds and giving them back, and the build ends with every
/// core it was granted returned, the late grant included.
/// </summary>
public sealed partial class ShaderBuildLawTests {
    [Fact]
    public async Task ACoreRequestTheHostCannotAnswerYetNeverStopsTheBuildGivingItsCoresBack() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        var runner = new Runner { Delay = TimeSpan.FromMilliseconds(milliseconds: 50) };
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));
        var build = BuildOf(cache: Path.Combine(path1: scratch.RootPath, path2: "cache"), checkout: checkout, runner: runner);
        // One core in all: the first request takes it, so every later request waits for the build to give it back.
        using var host = new BudgetHost(cores: 1);
        var cores = new StreamShaderCoreBroker(answers: host.Answers, requests: host.Requests);

        // On a pool thread: a broker that reads its answers on the asking thread blocks the call itself, and the law must
        // still see the time run out.
        var compile = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: () => build.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: cores, outputs: checkout.Outputs)
        );

        try {
            Assert.True(condition: await compile.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30)));
        } catch (TimeoutException) {
            var transcript = host.Transcript;

            // Ending the answers lets a stopped build finish before its directory goes.
            host.Dispose();
            try {
                _ = await compile.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30));
            } catch (Exception exception) when ((exception is TimeoutException or OperationCanceledException or IOException or InvalidOperationException)) {
                // The build's own outcome no longer matters: the law has already failed.
            }
            Assert.Fail(message: $"The build stopped while its core request waited on the host; the host saw: {transcript}");
        }
        Assert.Equal(expected: 3, actual: build.Compiled.Count);
        Assert.Equal(expected: 1, actual: runner.PeakConcurrent);
        Assert.True(condition: host.AnsweredFromARelease, userMessage: $"The host never had to wait for a release to answer a request, so the law exercised nothing; it saw: {host.Transcript}");

        // The build returns every core: the one it compiled on, and the grant its abandoned request won, which the
        // broker gives back when it arrives.
        var deadline = (DateTime.UtcNow + TimeSpan.FromSeconds(seconds: 10));

        while ((host.Held != 0) && (DateTime.UtcNow < deadline)) {
            await Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TimeSpan.FromMilliseconds(milliseconds: 10));
        }
        Assert.Equal(expected: 0, actual: host.Held);
    }
    [Fact]
    public async Task ACompileBesideRunningOnesWaitsWhileTheMemoryFreeIsShortOfItsReserve() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"), extra: 3);

        async Task<int> PeakWith(long free, string cache) {
            var runner = new Runner { Delay = TimeSpan.FromMilliseconds(milliseconds: 100) };
            var build = new ShaderBuild(
                availableMemory: () => free,
                compiler: new ShaderCompiler(cacheDirectory: Path.Combine(path1: scratch.RootPath, path2: cache), processRunner: runner, toolchainDirectory: ToolchainStandIn(root: scratch.RootPath)),
                lockFile: Path.Combine(path1: checkout.Root, path2: "obj", path3: "shader-publish.lock"),
                log: TextWriter.Null,
                projectDirectory: checkout.Root
            );

            Assert.True(condition: await build.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 4), outputs: checkout.Outputs));
            Assert.Equal(expected: 6, actual: build.Compiled.Count);

            return runner.PeakConcurrent;
        }

        // A gigabyte free: one compile at a time, however many cores are granted, and every compile still runs.
        Assert.Equal(expected: 1, actual: await PeakWith(cache: "short", free: (1L << 30)));
        // Plenty free: the granted cores bound the compiles.
        Assert.Equal(expected: 4, actual: await PeakWith(cache: "plenty", free: (64L << 30)));
    }

    // A build host with a fixed core budget, speaking the broker's line protocol. The first request is granted at once,
    // up to what is free and at least one (the core a task already runs on); a later request waits, in order, until a
    // release frees a core.
    private sealed class BudgetHost : IDisposable {
        private readonly BlockingCollection<string> m_answers = [];
        private readonly Queue<int> m_waiting = new();
        private readonly StringBuilder m_transcript = new();
        private readonly Lock m_gate = new();

        private int m_free;
        private int m_held;

        private bool m_first = true;

        private bool m_disposed;

        public BudgetHost(int cores) {
            m_free = cores;
            Answers = new AnswerReader(answers: m_answers);
            Requests = new RequestWriter(host: this);
        }

        public bool AnsweredFromARelease { get; private set; }
        public TextReader Answers { get; }
        public int Held {
            get {
                lock (m_gate) {
                    return m_held;
                }
            }
        }
        public TextWriter Requests { get; }
        public string Transcript {
            get {
                lock (m_gate) {
                    return m_transcript.ToString().ReplaceLineEndings(replacementText: " | ");
                }
            }
        }

        public void Dispose() {
            lock (m_gate) {
                if (m_disposed) {
                    return;
                }
                m_disposed = true;
            }
            m_answers.CompleteAdding();
            m_answers.Dispose();
        }

        private void Receive(string line) {
            lock (m_gate) {
                m_transcript.Append(value: line).Append(value: '\n');
                if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: (StreamShaderCoreBroker.Prefix + "request "))) {
                    var count = int.Parse(provider: CultureInfo.InvariantCulture, s: line[(StreamShaderCoreBroker.Prefix + "request ").Length..]);

                    if (m_first) {
                        m_first = false;
                        Grant(count: Math.Max(val1: 1, val2: Math.Min(val1: count, val2: m_free)));
                    } else {
                        m_waiting.Enqueue(item: count);
                    }
                } else if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: (StreamShaderCoreBroker.Prefix + "release "))) {
                    var count = int.Parse(provider: CultureInfo.InvariantCulture, s: line[(StreamShaderCoreBroker.Prefix + "release ").Length..]);

                    m_held -= count;
                    m_free += count;
                    while ((m_free > 0) && m_waiting.TryDequeue(result: out var waiting)) {
                        AnsweredFromARelease = true;
                        Grant(count: Math.Min(val1: waiting, val2: m_free));
                    }
                }
            }
        }
        private void Grant(int count) {
            if (m_disposed) {
                return;
            }
            m_free = Math.Max(val1: 0, val2: (m_free - count));
            m_held += count;
            m_transcript.Append(value: $"granted {count}\n");
            m_answers.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"granted {count}"));
        }

        private sealed class AnswerReader(BlockingCollection<string> answers) : TextReader {
            public override string? ReadLine() {
                try {
                    return answers.Take();
                } catch (InvalidOperationException) {
                    // Completed, or disposed (ObjectDisposedException is one): the host has stopped answering.
                    return null;
                }
            }
        }
        private sealed class RequestWriter(BudgetHost host) : TextWriter {
            private readonly StringBuilder m_line = new();

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value) {
                if (value == '\n') {
                    host.Receive(line: m_line.ToString().TrimEnd(trimChar: '\r'));
                    m_line.Clear();
                } else {
                    m_line.Append(value: value);
                }
            }
        }
    }
}
