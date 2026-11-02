using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;
using Puck.Memory;
using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>
/// Laws over <see cref="MimallocAllocator"/> and the threads that use it. mimalloc records the thread that loads it as
/// its main thread, by the thread's TEB address, keeps that thread's heap state in static storage and tears it down when
/// the thread exits; a later thread given the same TEB address then takes the main path onto the dead state and faults
/// inside <c>mi_malloc</c>. The law establishes that trigger: in a process that has not loaded mimalloc yet, a
/// short-lived thread makes the first native allocation and exits, and a later thread that reuses its TEB address
/// allocates. The allocator loads the library on an owner thread that never exits, so the reusing thread is an ordinary
/// one; a loader that was the short-lived thread ends the test host with an access violation.
/// </summary>
public sealed unsafe partial class MimallocThreadLifecycleLawTests {
    // How many successive short-lived threads the law starts while looking for one on the first thread's TEB.
    private const int ReuseAttempts = 4096;

    [Fact]
    public void AThreadOnTheFirstAllocatingThreadsTebAllocatesAfterThatThreadExits() {
        if (GetModuleHandleW(moduleName: "mimalloc.dll") != 0) {
            Assert.Skip(reason: "mimalloc is already loaded in this test process, so its first load cannot be on this law's thread; run the law alone (--filter MimallocThreadLifecycleLawTests).");
        }
        IAllocator? allocator = null;
        nint firstTeb = 0;
        var first = new Thread(start: () => {
            firstTeb = CurrentTeb();
            allocator = new MimallocAllocator();
            allocator.Free(ptr: allocator.Allocate(size: 64));
        });

        first.Start();
        first.Join();

        var shared = allocator!;
        var reused = false;

        for (var attempt = 0; ((attempt < ReuseAttempts) && !reused); attempt++) {
            var thread = new Thread(start: () => {
                if (CurrentTeb() != firstTeb) { return; }
                reused = true;
                for (var block = 0; (block < 64); block++) { shared.Free(ptr: shared.Allocate(size: (nuint)(16 << (block % 12)))); }
            });

            thread.Start();
            thread.Join();
        }
        if (!reused) { Assert.Skip(reason: $"No thread reused the first thread's TEB in {ReuseAttempts} attempts, so the trigger was not established."); }
    }

    private static nint CurrentTeb() {
        var status = NtQueryInformationThread(information: out var information, informationClass: 0, length: sizeof(ThreadBasicInformation), returnLength: 0, thread: -2);

        Assert.Equal(expected: 0, actual: status);
        return information.TebBaseAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadBasicInformation {
        public int ExitStatus;
        public nint TebBaseAddress;
        public nint UniqueProcess;
        public nint UniqueThread;
        public nint AffinityMask;
        public int Priority;
        public int BasePriority;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationThread(nint thread, int informationClass, out ThreadBasicInformation information, int length, nint returnLength);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string moduleName);
}
