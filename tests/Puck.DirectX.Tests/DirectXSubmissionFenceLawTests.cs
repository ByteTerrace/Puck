using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

// Native stand-ins exercise the actual fence implementation without a driver or billions of submissions.
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXSubmissionFenceLawTests {
    [Fact]
    public void ADrainedFenceRenewsBeforeTheDeviceLossSentinelAndNeverWrapsToZero() {
        using var native = new NativeFences();
        using var fence = new DirectXGpuSubmissionFence((ID3D12Device*)native.Device);
        typeof(DirectXGpuSubmissionFence).GetField("m_nextValue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fence, ulong.MaxValue - 1);
        fence.Arm((ID3D12CommandQueue*)native.Queue);
        Assert.True(fence.IsSignaled);
        fence.Wait();
        fence.Arm((ID3D12CommandQueue*)native.Queue);
        Assert.True(fence.IsSignaled);
        Assert.Equal(1UL, native.Queue->LastSignal);
        Assert.Equal(2, native.Device->Created);
        Assert.Equal(1, native.Device->Fences[0].Releases);
        fence.Wait();
    }

    private struct DeviceObject { public void** Vtable; public FenceObject* Fences; public int Created; }
    private struct FenceObject { public void** Vtable; public ulong Completed; public int Releases; }
    private struct QueueObject { public void** Vtable; public ulong LastSignal; }

    private sealed class NativeFences : IDisposable {
        public DeviceObject* Device { get; }
        public QueueObject* Queue { get; }
        private readonly void** m_deviceTable;
        private readonly void** m_fenceTable;
        private readonly void** m_queueTable;
        public NativeFences() {
            m_deviceTable = (void**)NativeMemory.AllocZeroed(38, (nuint)sizeof(void*));
            m_fenceTable = (void**)NativeMemory.AllocZeroed(11, (nuint)sizeof(void*));
            m_queueTable = (void**)NativeMemory.AllocZeroed(15, (nuint)sizeof(void*));
            m_deviceTable[36] = (delegate* unmanaged[Stdcall]<DeviceObject*, ulong, uint, Guid*, void**, int>)&CreateFence;
            m_deviceTable[37] = (delegate* unmanaged[Stdcall]<DeviceObject*, int>)&RemovedReason;
            m_fenceTable[2] = (delegate* unmanaged[Stdcall]<FenceObject*, uint>)&Release;
            m_fenceTable[8] = (delegate* unmanaged[Stdcall]<FenceObject*, ulong>)&Completed;
            m_queueTable[14] = (delegate* unmanaged[Stdcall]<QueueObject*, FenceObject*, ulong, int>)&Signal;
            Device = (DeviceObject*)NativeMemory.AllocZeroed((nuint)sizeof(DeviceObject));
            Device->Vtable = m_deviceTable;
            Device->Fences = (FenceObject*)NativeMemory.AllocZeroed(2, (nuint)sizeof(FenceObject));
            Device->Fences[0].Vtable = m_fenceTable;
            Device->Fences[1].Vtable = m_fenceTable;
            Queue = (QueueObject*)NativeMemory.AllocZeroed((nuint)sizeof(QueueObject));
            Queue->Vtable = m_queueTable;
        }
        public void Dispose() {
            NativeMemory.Free(Device->Fences);
            NativeMemory.Free(Device);
            NativeMemory.Free(Queue);
            NativeMemory.Free(m_deviceTable);
            NativeMemory.Free(m_fenceTable);
            NativeMemory.Free(m_queueTable);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateFence(DeviceObject* device, ulong initial, uint flags, Guid* iid, void** result) {
        if (device->Created == 2) { return unchecked((int)0x80004005); }
        var fence = &device->Fences[device->Created++];
        fence->Completed = initial;
        *result = fence;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RemovedReason(DeviceObject* device) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(FenceObject* fence) { fence->Releases++; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong Completed(FenceObject* fence) => fence->Completed;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Signal(QueueObject* queue, FenceObject* fence, ulong value) {
        queue->LastSignal = value;
        fence->Completed = value;
        return 0;
    }
}
