using System.Text;

using Puck.Abstractions.Waiting;
using Xunit;

namespace Puck.Launcher.Tests;

public sealed class BufferedConsoleOutputLawTests {
    private sealed class OrderingStream(List<string> events, string tag) : Stream {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush() {
            lock (events) {
                events.Add(item: $"{tag}:flush");
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) {
            var text = Encoding.UTF8.GetString(bytes: buffer, index: offset, count: count);

            lock (events) {
                events.Add(item: $"{tag}:{text.Replace(oldValue: "\r\n", newValue: "\n", comparisonType: StringComparison.Ordinal)}");
            }
        }
    }

    private sealed class GatedStream : Stream {
        private static readonly WaitReason GateReason = new(Subsystem: "console.test", WaitsFor: "release blocked pipe");
        private readonly object m_gate = new();
        private readonly MemoryStream m_inner = new();
        private bool m_released;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => m_inner.Length;
        public override long Position { get => m_inner.Position; set => throw new NotSupportedException(); }

        public void Release() {
            lock (m_gate) {
                m_released = true;
                Monitor.PulseAll(obj: m_gate);
            }
        }
        public string WrittenText() {
            lock (m_gate) {
                return Encoding.UTF8.GetString(bytes: m_inner.ToArray());
            }
        }

        public override void Flush() {
            lock (m_gate) {
                while (!m_released) {
                    if (HostWaits.OnCondition(
                        deadline: WaitDeadline.Synchronous(budget: TimeSpan.FromSeconds(seconds: 5)),
                        monitor: m_gate,
                        reason: in GateReason
                    ) == WaitOutcome.DeadlinePassed) {
                        break;
                    }
                }
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) {
            lock (m_gate) {
                while (!m_released) {
                    if (HostWaits.OnCondition(
                        deadline: WaitDeadline.Synchronous(budget: TimeSpan.FromSeconds(seconds: 5)),
                        monitor: m_gate,
                        reason: in GateReason
                    ) == WaitOutcome.DeadlinePassed) {
                        break;
                    }
                }

                m_inner.Write(buffer: buffer, offset: offset, count: count);
            }
        }
    }

    [Fact]
    public void WriteErrorLineFlushesPrecedingStdoutBeforeWritingStderr() {
        var events = new List<string>();
        using var stdout = new OrderingStream(events: events, tag: "out");
        using var stderr = new OrderingStream(events: events, tag: "err");

        using (var output = new BufferedConsoleOutput(output: stdout, error: stderr)) {
            output.WriteLine(value: "one");
            output.WriteLine(value: "two");
            output.WriteErrorLine(value: "bad");
            output.WriteLine(value: "three");
            output.Drain();
        }

        var errIndex = events.FindIndex(match: static item => item.StartsWith(value: "err:bad", comparisonType: StringComparison.Ordinal));
        var outOneTwoIndex = events.FindIndex(match: static item => item.StartsWith(value: "out:", comparisonType: StringComparison.Ordinal) && item.Contains(value: "one", comparisonType: StringComparison.Ordinal));
        var outThreeIndex = events.FindIndex(match: static item => item.StartsWith(value: "out:", comparisonType: StringComparison.Ordinal) && item.Contains(value: "three", comparisonType: StringComparison.Ordinal));

        Assert.True(condition: (outOneTwoIndex >= 0), userMessage: "stdout lines before stderr were not written");
        Assert.True(condition: (errIndex > outOneTwoIndex), userMessage: "stderr arrived before preceding stdout was flushed");
        Assert.True(condition: (outThreeIndex > errIndex), userMessage: "stdout line after stderr arrived out of order");
    }

    [Fact]
    public void SlowPipeDoesNotBlockFrameWritesOrFlush() {
        using var stdout = new GatedStream();
        using var stderr = new MemoryStream();
        using var output = new BufferedConsoleOutput(output: stdout, error: stderr, queueCapacity: 32);

        // Even while the underlying stdout pipe blocks on write/flush, enqueuing within capacity and calling Flush
        // return immediately without blocking the caller thread.
        for (var index = 0; (index < 16); index++) {
            output.WriteLine(value: $"line-{index}");
            output.Flush();
        }

        stdout.Release();
        output.Drain();

        var written = stdout.WrittenText();
        Assert.Contains(expectedSubstring: "line-0", actualString: written);
        Assert.Contains(expectedSubstring: "line-15", actualString: written);
    }
}
