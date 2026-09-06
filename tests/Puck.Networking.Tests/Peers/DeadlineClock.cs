using System.Threading.Channels;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>Exposes one-shot deadline timers to a test without waiting for wall time. Disposed handshake timers
/// are ignored when a later send deadline is selected; the requested production duration is still checked.</summary>
internal sealed class DeadlineClock : TimeProvider {
    private readonly Channel<DeadlineTimer> m_timers = Channel.CreateUnbounded<DeadlineTimer>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        Assert.Equal(Timeout.InfiniteTimeSpan, period);
        var timer = new DeadlineTimer(callback, state, dueTime);
        Assert.True(m_timers.Writer.TryWrite(timer));
        return timer;
    }

    public async Task ExpireAsync(TimeSpan expected, CancellationToken cancellationToken) {
        while (true) {
            var timer = await m_timers.Reader.ReadAsync(cancellationToken);
            if (timer.IsDisposed) { continue; }
            Assert.Equal(expected, timer.DueTime);
            timer.Fire();
            return;
        }
    }

    private sealed class DeadlineTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer {
        private int m_disposed;
        public TimeSpan DueTime { get; } = dueTime;
        public bool IsDisposed => Volatile.Read(ref m_disposed) != 0;
        public void Fire() { if (!IsDisposed) { callback(state); } }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException("Deadline timers are one-shot.");
        public void Dispose() => Interlocked.Exchange(ref m_disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
