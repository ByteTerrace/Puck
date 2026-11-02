using Puck.Abstractions.Memory;
using Puck.Memory;
using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>
/// Laws over <see cref="MimallocAllocator"/> and the threads that use it: mimalloc keeps the heap state of the thread
/// that loads it in static storage and tears it down when that thread exits, so the library must load on a thread that
/// outlives every allocation. Threads that start, allocate, free and exit, the first of them making the process's first
/// native allocation, never fault the allocator; a thread pool retiring idle workers is this pattern.
/// </summary>
public sealed class MimallocThreadLifecycleLawTests {
    [Fact]
    public unsafe void ShortLivedThreadsAllocatingAndExitingNeverFaultTheAllocator() {
        var allocator = (IAllocator?)null;
        var first = new Thread(start: () => {
            allocator = new MimallocAllocator();
            allocator.Free(ptr: allocator.Allocate(size: 64));
        });

        first.Start();
        first.Join();

        var shared = allocator!;
        var handoff = new System.Collections.Concurrent.ConcurrentQueue<nint>();
        var made = 0L;

        for (var wave = 0; (wave < 150); wave++) {
            var threads = new Thread[16];

            for (var index = 0; (index < threads.Length); index++) {
                var seed = ((wave * threads.Length) + index);

                threads[index] = new Thread(start: () => {
                    var random = new Random(Seed: seed);

                    for (var block = 0; (block < 32); block++) {
                        var size = (nuint)random.Next(minValue: 16, maxValue: (1 << 18));
                        var pointer = (byte*)shared.Allocate(size: size);

                        if (pointer is null) { continue; }
                        new Span<byte>(pointer: pointer, length: (int)size).Fill(value: (byte)seed);
                        _ = Interlocked.Increment(location: ref made);
                        if ((block % 2) == 0) { handoff.Enqueue(item: (nint)pointer); } else { shared.Free(ptr: pointer); }
                        if (handoff.TryDequeue(result: out var other)) { shared.Free(ptr: (void*)other); }
                    }
                });
                threads[index].Start();
            }
            foreach (var thread in threads) { thread.Join(); }
        }
        while (handoff.TryDequeue(result: out var left)) { shared.Free(ptr: (void*)left); }
        Assert.Equal(expected: (150L * 16 * 32), actual: made);
    }
}
