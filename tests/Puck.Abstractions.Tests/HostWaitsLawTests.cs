using System.Diagnostics;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Waiting;

namespace Puck.Abstractions.Tests;

/// <summary>
/// The wait door's contract, which running the engine cannot show: a deadline wait on something that never completes
/// returns its expiry as a value and is listed with its reason; on the pump thread the door clips a slice wait to the
/// iteration's allowance and charges it, and never clips a synchronous one; the consumer role is refused to the pump and to any thread but its own; and a
/// wait whose object is already complete allocates nothing.
/// </summary>
public sealed class HostWaitsLawTests {
    private static readonly WaitReason Reason = new(Subsystem: "laws", WaitsFor: "a signal nobody sets");

    // Runs `body` on a fresh thread, so pump and consumer registration never leak into the test runner's threads.
    private static void OnOwnThread(Action body) {
        Exception? failure = null;
        var thread = new Thread(start: () => {
            try {
                body();
            } catch (Exception exception) {
                failure = exception;
            }
        }) { IsBackground = true, Name = "host-waits law" };

        thread.Start();
        Assert.True(condition: thread.Join(timeout: TimeSpan.FromSeconds(value: 30)), userMessage: "the law's thread did not finish");
        if (failure is not null) {
            throw new Xunit.Sdk.XunitException(userMessage: $"the law's thread failed: {failure}");
        }
    }

    [Fact]
    public void AWaitNobodySignalsReturnsDeadlinePassedAndIsListedWithItsReason() {
        OnOwnThread(body: () => {
            using var signal = new ManualResetEventSlim(initialState: false);
            var deadline = WaitDeadline.In(budget: TimeSpan.FromMilliseconds(value: 40));

            Assert.Equal(expected: WaitOutcome.DeadlinePassed, actual: HostWaits.Wait(deadline: deadline, reason: Reason, signal: signal));
            Assert.True(condition: deadline.HasPassed);

            var expired = new WaitRecord[16];
            var count = HostWaits.Expired(destination: expired);

            Assert.Contains(expected: Reason, collection: expired[..count].ToArray().Where(predicate: record => (record.ThreadId == Environment.CurrentManagedThreadId)).Select(selector: record => record.Reason));

            var standing = new WaitRecord[64];

            Assert.DoesNotContain(collection: standing[..HostWaits.Standing(destination: standing)].ToArray(), filter: record => (record.ThreadId == Environment.CurrentManagedThreadId));
        });
    }
    [Fact]
    public void APumpWaitIsClippedToTheIterationsAllowanceAndCharged() {
        OnOwnThread(body: () => {
            using var signal = new ManualResetEventSlim(initialState: false);
            var allowance = TimeSpan.FromMilliseconds(value: 30);

            HostWaits.BeginPumpIteration(allowance: allowance);
            try {
                Assert.Equal(expected: WaitOutcome.DeadlinePassed, actual: HostWaits.Wait(deadline: WaitDeadline.In(budget: TimeSpan.FromMinutes(value: 5)), reason: Reason, signal: signal));
                Assert.Equal(expected: TimeSpan.Zero, actual: HostWaits.PumpAllowance);

                var expired = new WaitRecord[16];
                var count = HostWaits.Expired(destination: expired);
                var mine = expired[..count].ToArray().First(predicate: record => (record.ThreadId == Environment.CurrentManagedThreadId));

                Assert.True(condition: mine.OnPump);
                Assert.True(condition: (mine.BudgetTicks <= WaitDeadline.TicksOf(budget: allowance)), userMessage: $"budget {mine.BudgetTicks} ticks exceeds the allowance");

                // The allowance is spent, so a second wait in the same iteration does not block at all.
                Assert.Equal(expected: WaitOutcome.DeadlinePassed, actual: HostWaits.Wait(deadline: WaitDeadline.In(budget: TimeSpan.FromMinutes(value: 5)), reason: Reason, signal: signal));
                count = HostWaits.Expired(destination: expired);
                Assert.Equal(expected: 0L, actual: expired[..count].ToArray().First(predicate: record => (record.ThreadId == Environment.CurrentManagedThreadId)).BudgetTicks);

                // A synchronous deadline's caller cannot resume its wait, so the spent allowance does not clip it.
                using (new Timer(callback: _ => signal.Set(), dueTime: TimeSpan.FromMilliseconds(value: 20), period: Timeout.InfiniteTimeSpan, state: null)) {
                    Assert.Equal(expected: WaitOutcome.Completed, actual: HostWaits.Wait(deadline: WaitDeadline.Synchronous(budget: TimeSpan.FromSeconds(value: 20)), reason: Reason, signal: signal));
                }
                signal.Reset();

                // The next control phase opens a fresh allowance.
                HostWaits.BeginPumpIteration(allowance: allowance);
                Assert.Equal(expected: allowance, actual: HostWaits.PumpAllowance);
            } finally {
                HostWaits.EndPump();
            }
            Assert.Null(@object: HostWaits.PumpAllowance);
        });
    }
    [Fact]
    public void APumpsWaitForWorkItHandedAConsumerIsNeitherClippedNorCharged() {
        var monitor = new object();
        var done = false;
        WaitConsumer? worker = null;
        using var claimed = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);
        var workerThread = new Thread(start: () => {
            worker = HostWaits.ClaimConsumer(reason: Reason);
            claimed.Set();
            release.Wait();
            lock (monitor) {
                done = true;
                Monitor.PulseAll(obj: monitor);
            }
        }) { IsBackground = true, Name = "host-waits law worker" };

        workerThread.Start();
        Assert.True(condition: claimed.Wait(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(value: 30)));
        OnOwnThread(body: () => {
            using var never = new ManualResetEventSlim(initialState: false);

            HostWaits.BeginPumpIteration(allowance: TimeSpan.FromMilliseconds(value: 5));
            try {
                Assert.Equal(expected: WaitOutcome.DeadlinePassed, actual: HostWaits.Wait(deadline: WaitDeadline.In(budget: TimeSpan.FromMinutes(value: 5)), reason: Reason, signal: never));
                Assert.Equal(expected: TimeSpan.Zero, actual: HostWaits.PumpAllowance);

                // The allowance is spent, and the wait for the handed work still blocks until the worker finishes it.
                using (new Timer(callback: _ => release.Set(), dueTime: TimeSpan.FromMilliseconds(value: 40), period: Timeout.InfiniteTimeSpan, state: null)) {
                    lock (monitor) {
                        while (!done) {
                            Assert.Equal(expected: WaitOutcome.Completed, actual: HostWaits.OnDelegatedWork(deadline: WaitDeadline.In(budget: TimeSpan.FromSeconds(value: 20)), monitor: monitor, reason: Reason, worker: worker!));
                        }
                    }
                }

                // A wait for handed work that blocks to its own deadline leaves a fresh allowance whole.
                var allowance = TimeSpan.FromSeconds(value: 10);

                HostWaits.BeginPumpIteration(allowance: allowance);
                lock (monitor) {
                    Assert.Equal(expected: WaitOutcome.DeadlinePassed, actual: HostWaits.OnDelegatedWork(deadline: WaitDeadline.In(budget: TimeSpan.FromMilliseconds(value: 30)), monitor: monitor, reason: Reason, worker: worker!));
                }
                Assert.Equal(expected: allowance, actual: HostWaits.PumpAllowance);
            } finally {
                HostWaits.EndPump();
            }
        });
        Assert.True(condition: workerThread.Join(timeout: TimeSpan.FromSeconds(value: 30)));
        // The form is for work on another thread: a consumer cannot take it for work it runs itself.
        OnOwnThread(body: () => {
            var own = HostWaits.ClaimConsumer(reason: Reason);

            lock (monitor) {
                _ = Assert.Throws<InvalidOperationException>(testCode: () => HostWaits.OnDelegatedWork(deadline: WaitDeadline.In(budget: TimeSpan.FromMilliseconds(value: 1)), monitor: monitor, reason: Reason, worker: own));
            }
        });
    }
    [Fact]
    public void ASynchronousDoorThrowsOnlyWhileItsOperationIsPendingAndNamesTheExpiredWait() {
        OnOwnThread(body: () => {
            using var signal = new ManualResetEventSlim(initialState: false);

            HostWaits.ThrowIfPending(pending: null);
            Assert.Equal(expected: WaitOutcome.DeadlinePassed, actual: HostWaits.Wait(deadline: WaitDeadline.Synchronous(budget: TimeSpan.FromMilliseconds(value: 5)), reason: Reason, signal: signal));

            var thrown = Assert.Throws<TimeoutException>(testCode: () => HostWaits.ThrowIfPending(pending: Reason));

            Assert.StartsWith(expectedStartString: $"{Reason} (expired: {Reason} on ", actualString: thrown.Message);
        });
    }
    [Fact]
    public void TheConsumerRoleIsRefusedToThePumpAndToOtherThreads() {
        WaitConsumer? consumer = null;

        OnOwnThread(body: () => {
            HostWaits.BeginPumpIteration(allowance: TimeSpan.FromMilliseconds(value: 1));
            try {
                _ = Assert.Throws<InvalidOperationException>(testCode: () => HostWaits.ClaimConsumer(reason: Reason));
            } finally {
                HostWaits.EndPump();
            }
        });
        OnOwnThread(body: () => {
            consumer = HostWaits.ClaimConsumer(reason: Reason);
            Assert.Same(expected: consumer, actual: HostWaits.ClaimConsumer(reason: Reason));
            _ = Assert.Throws<InvalidOperationException>(testCode: () => HostWaits.BeginPumpIteration(allowance: TimeSpan.FromMilliseconds(value: 1)));
        });
        OnOwnThread(body: () => {
            using var signal = new ManualResetEvent(initialState: true);

            _ = Assert.Throws<InvalidOperationException>(testCode: () => HostWaits.Idle(consumer: consumer!, handle: signal));
        });
    }
    [Fact]
    public void AWaitWhoseObjectIsAlreadyCompleteAllocatesNothing() {
        using var signal = new ManualResetEventSlim(initialState: true);
        using var semaphore = new SemaphoreSlim(initialCount: 1, maxCount: 1);
        var task = Task.CompletedTask;

        void Body() {
            var deadline = WaitDeadline.In(budget: TimeSpan.FromMilliseconds(value: 5));

            if (HostWaits.Wait(deadline: deadline, reason: Reason, signal: signal) != WaitOutcome.Completed) {
                throw new InvalidOperationException(message: "a set event did not complete");
            }
            if (HostWaits.Wait(deadline: deadline, reason: Reason, task: task) != WaitOutcome.Completed) {
                throw new InvalidOperationException(message: "a completed task did not complete");
            }
            if (HostWaits.Acquire(deadline: deadline, reason: Reason, semaphore: semaphore) != WaitOutcome.Completed) {
                throw new InvalidOperationException(message: "a free semaphore did not admit");
            }
            _ = semaphore.Release();
        }

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Body));
    }
    [Fact]
    public void ThePacersWaitServesWhenWokenAndWhenAServiceAsksAndEndsOnItsDeadline() {
        OnOwnThread(body: () => {
            var runs = 0;
            var asked = new List<TimeSpan>();
            TimeSpan? next = null;
            using var service = HostWaits.Serve(reason: Reason, service: remaining => {
                runs++;
                asked.Add(item: remaining);

                return next;
            });
            var time = TimeProvider.System;
            var frequency = time.TimestampFrequency;

            // With nothing waking it, the wait serves once as it begins and sleeps to the deadline.
            var started = time.GetTimestamp();

            HostWaits.Pace(deadlineTimestamp: (started + (frequency / 20L)), precisionWaiter: null, time: time);
            Assert.Equal(expected: 1, actual: runs);
            Assert.True(condition: (time.GetTimestamp() >= (started + (frequency / 20L))), userMessage: "the wait ended before its deadline");
            Assert.True(condition: (asked[0] <= TimeSpan.FromMilliseconds(value: 50)), userMessage: $"the service was told {asked[0]} remained of a 50 ms wait");

            // A wake from another thread runs the services again inside the wait, which still ends on its deadline.
            runs = 0;
            started = time.GetTimestamp();

            var waker = new Thread(start: () => {
                Thread.Sleep(millisecondsTimeout: 20);
                service.Wake();
            }) { IsBackground = true };

            waker.Start();
            HostWaits.Pace(deadlineTimestamp: (started + (frequency / 5L)), precisionWaiter: null, time: time);
            waker.Join();
            Assert.Equal(expected: 2, actual: runs);
            Assert.True(condition: (time.GetTimestamp() >= (started + (frequency / 5L))), userMessage: "a wake ended the wait before its deadline");

            // A service that asks to run again is run again without a wake, and no sooner than the least delay: a
            // service asking for none at all cannot spin the wait.
            runs = 0;
            next = TimeSpan.Zero;
            started = time.GetTimestamp();
            HostWaits.Pace(deadlineTimestamp: (started + (frequency / 20L)), precisionWaiter: null, time: time);
            Assert.True(condition: (runs >= 2), userMessage: $"a service that asked to run again ran {runs} time(s)");
            Assert.True(condition: (runs <= ((int)(TimeSpan.FromMilliseconds(value: 50) / HostWaits.LeastServiceDelay) + 2)), userMessage: $"the wait ran its service {runs} times in 50 ms");

            // A disposed registration is served no more, and the wait is then a plain sleep to its deadline.
            service.Dispose();
            runs = 0;
            started = time.GetTimestamp();
            HostWaits.Pace(deadlineTimestamp: (started + (frequency / 50L)), precisionWaiter: null, time: time);
            Assert.Equal(expected: 0, actual: runs);
        });
    }
    [Fact]
    public void AWakeSetBeforeThePacersWaitIsServedNotLost() {
        OnOwnThread(body: () => {
            var runs = 0;
            using var service = HostWaits.Serve(reason: Reason, service: _ => {
                runs++;

                return null;
            });
            var time = TimeProvider.System;

            // Set while the thread was busy with its iteration: the wait serves as it begins and again for the wake.
            service.Wake();
            HostWaits.Pace(deadlineTimestamp: (time.GetTimestamp() + (time.TimestampFrequency / 50L)), precisionWaiter: null, time: time);
            Assert.Equal(expected: 2, actual: runs);
            // A wait whose deadline has passed serves nothing and returns at once.
            runs = 0;
            HostWaits.Pace(deadlineTimestamp: time.GetTimestamp(), precisionWaiter: null, time: time);
            Assert.Equal(expected: 0, actual: runs);
        });
    }
    [Fact]
    public void ServicesShareThePacersRemainderAndStopAtItsDeadline() {
        OnOwnThread(body: () => {
            var order = new List<string>();
            var handed = new Dictionary<string, TimeSpan>();
            var spentByFirst = TimeSpan.Zero;
            var firstSpends = TimeSpan.FromMilliseconds(value: 15);
            using var first = HostWaits.Serve(reason: Reason, service: remaining => {
                var start = Stopwatch.GetTimestamp();

                order.Add(item: "first");
                handed["first"] = remaining;
                Thread.Sleep(timeout: firstSpends);
                spentByFirst = Stopwatch.GetElapsedTime(startingTimestamp: start);

                return null;
            });
            using var second = HostWaits.Serve(reason: Reason, service: remaining => {
                order.Add(item: "second");
                handed["second"] = remaining;

                return null;
            });
            var time = TimeProvider.System;
            var frequency = time.TimestampFrequency;

            // The second service is handed what the first left, not the remainder the turn began with.
            HostWaits.Pace(deadlineTimestamp: (time.GetTimestamp() + (frequency / 4L)), precisionWaiter: null, time: time);
            Assert.Equal(expected: ["first", "second"], actual: order);
            Assert.True(
                condition: (handed["second"] <= ((handed["first"] - spentByFirst) + TimeSpan.FromMicroseconds(value: 1))),
                userMessage: $"the second service was handed {handed["second"]} after the first, handed {handed["first"]}, spent {spentByFirst}"
            );

            // A first service that spends past the deadline ends the turn: the second is not run in this wait...
            order.Clear();
            firstSpends = TimeSpan.FromMilliseconds(value: 40);
            HostWaits.Pace(deadlineTimestamp: (time.GetTimestamp() + (frequency / 50L)), precisionWaiter: null, time: time);
            Assert.Equal(expected: ["first"], actual: order);

            // ...and the next wait's turn starts with it, so the long service cannot starve it.
            order.Clear();
            firstSpends = TimeSpan.Zero;
            HostWaits.Pace(deadlineTimestamp: (time.GetTimestamp() + (frequency / 50L)), precisionWaiter: null, time: time);
            Assert.Equal(expected: ["second", "first"], actual: order);
        });
    }
    [Fact]
    public void AnAdmissionThatExpiresTookNoPermit() {
        OnOwnThread(body: () => {
            using var semaphore = new SemaphoreSlim(initialCount: 0, maxCount: 1);

            Assert.Equal(expected: WaitOutcome.DeadlinePassed, actual: HostWaits.Acquire(deadline: WaitDeadline.In(budget: TimeSpan.FromMilliseconds(value: 20)), reason: Reason, semaphore: semaphore));
            Assert.Equal(expected: 0, actual: semaphore.CurrentCount);
        });
    }
}
