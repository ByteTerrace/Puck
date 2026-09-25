using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Every Direct3D 12 call a device removal reaches is translated at the boundary
/// (<see cref="DirectXCommandCalls"/>): a <c>DXGI_ERROR_DEVICE_REMOVED</c>, <c>_RESET</c> or <c>_HUNG</c> result
/// becomes <see cref="DeviceLostException"/> carrying the device's removal reason, any other failure a
/// <see cref="DirectXException"/> naming the call, and a release path's drain counts a removal as drained. Each call
/// answers through a fake; no device is created.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXCommandCallsLawTests {
    private static readonly HRESULT DeviceHung = new(value: unchecked((int)0x887A0006));
    private static readonly HRESULT DeviceRemoved = new(value: unchecked((int)0x887A0005));
    private static readonly HRESULT DeviceReset = new(value: unchecked((int)0x887A0007));
    private static readonly HRESULT DriverInternalError = new(value: unchecked((int)0x887A0020));
    private static readonly HRESULT Failure = new(value: unchecked((int)0x80004005));

    /// <summary>The translated calls, each named by the operation its failure reports: the surface upload's map,
    /// reset pair and close; the buffer factory's and the readback's map; the recorder's reset pair and close; the
    /// queue submitter's, the device context's and the compositor's signal; and the fence wait's event.</summary>
    public static TheoryData<string, string> Sites => new() {
        { nameof(IDirectXCommandCalls.Map), "ID3D12Resource::Map" },
        { nameof(IDirectXCommandCalls.ResetAllocator), "ID3D12CommandAllocator::Reset" },
        { nameof(IDirectXCommandCalls.ResetList), "ID3D12GraphicsCommandList::Reset" },
        { nameof(IDirectXCommandCalls.Close), "ID3D12GraphicsCommandList::Close" },
        { nameof(IDirectXCommandCalls.Signal), "ID3D12CommandQueue::Signal" },
        { nameof(IDirectXCommandCalls.SetEventOnCompletion), "ID3D12Fence::SetEventOnCompletion" },
    };

    [MemberData(nameof(Sites))]
    [Theory]
    public void ARemovalAtEachCallIsADeviceLossCarryingTheRemovalReason(string call, string operation) {
        foreach (var removal in ((ReadOnlySpan<HRESULT>)[DeviceRemoved, DeviceReset, DeviceHung])) {
            var calls = new FakeCommandCalls {
                Failing = call,
                RemovalReason = DriverInternalError,
                Result = removal,
            };

            var loss = Assert.Throws<DeviceLostException>(testCode: () => Run(
                call: call,
                calls: calls
            ));

            Assert.Equal(
                actual: loss.ReasonCode,
                expected: DriverInternalError.Value
            );
            Assert.Contains(
                actualString: loss.Message,
                expectedSubstring: operation
            );
        }
    }
    [MemberData(nameof(Sites))]
    [Theory]
    public void ARemovalTheDeviceGivesNoReasonForCarriesItsOwnResult(string call, string operation) {
        var calls = new FakeCommandCalls {
            Failing = call,
            RemovalReason = new HRESULT(value: 0),
            Result = DeviceHung,
        };

        var loss = Assert.Throws<DeviceLostException>(testCode: () => Run(
            call: call,
            calls: calls
        ));

        Assert.Equal(
            actual: loss.ReasonCode,
            expected: DeviceHung.Value
        );
        Assert.Contains(
            actualString: loss.Message,
            expectedSubstring: operation
        );
    }
    [MemberData(nameof(Sites))]
    [Theory]
    public void AnyOtherFailureAtEachCallIsADirectXFailureNamingIt(string call, string operation) {
        var calls = new FakeCommandCalls {
            Failing = call,
            RemovalReason = DriverInternalError,
            Result = Failure,
        };

        var failure = Assert.Throws<DirectXException>(testCode: () => Run(
            call: call,
            calls: calls
        ));

        Assert.Equal(
            actual: failure.Result,
            expected: Failure.Value
        );
        Assert.Equal(
            actual: failure.Operation,
            expected: operation
        );
    }
    [Fact]
    public void AReleaseDrainCountsARemovalAsDrained() {
        foreach (var call in ((ReadOnlySpan<string>)[nameof(IDirectXCommandCalls.Signal), nameof(IDirectXCommandCalls.SetEventOnCompletion)])) {
            var calls = new FakeCommandCalls {
                Failing = call,
                RemovalReason = DriverInternalError,
                Result = DeviceRemoved,
            };
            var fenceValue = 1UL;

            var drained = DirectXCommandCalls.Drain(
                calls: calls,
                fence: null,
                fenceEvent: default,
                fenceValue: ref fenceValue,
                queue: null
            );

            Assert.False(condition: drained);
        }
    }
    [Fact]
    public void AReleaseDrainStillRefusesAFailureThatIsNotARemoval() {
        var calls = new FakeCommandCalls {
            Failing = nameof(IDirectXCommandCalls.Signal),
            RemovalReason = DriverInternalError,
            Result = Failure,
        };
        var fenceValue = 1UL;

        var failure = Assert.Throws<DirectXException>(testCode: () => DirectXCommandCalls.Drain(
            calls: calls,
            fence: null,
            fenceEvent: default,
            fenceValue: ref fenceValue,
            queue: null
        ));

        Assert.Equal(
            actual: failure.Result,
            expected: Failure.Value
        );
    }
    [Fact]
    public void AHealthyDrainSignalsOnceAndReturnsDrained() {
        var calls = new FakeCommandCalls();
        var fenceValue = 7UL;

        var drained = DirectXCommandCalls.Drain(
            calls: calls,
            fence: null,
            fenceEvent: default,
            fenceValue: ref fenceValue,
            queue: null
        );

        Assert.True(condition: drained);
        Assert.Equal(
            actual: fenceValue,
            expected: 8UL
        );
        Assert.Equal(
            actual: calls.Signalled,
            expected: 7UL
        );
    }

    private static void Run(string call, FakeCommandCalls calls) {
        var fenceValue = 1UL;

        switch (call) {
            case nameof(IDirectXCommandCalls.Map):
                _ = DirectXCommandCalls.Map(
                    calls: calls,
                    resource: null
                );
                break;
            case nameof(IDirectXCommandCalls.ResetAllocator):
            case nameof(IDirectXCommandCalls.ResetList):
                DirectXCommandCalls.Reset(
                    allocator: null,
                    calls: calls,
                    commandList: null
                );
                break;
            case nameof(IDirectXCommandCalls.Close):
                DirectXCommandCalls.Close(
                    calls: calls,
                    commandList: null
                );
                break;
            case nameof(IDirectXCommandCalls.Signal):
            case nameof(IDirectXCommandCalls.SetEventOnCompletion):
                DirectXCommandCalls.SignalAndWait(
                    calls: calls,
                    fence: null,
                    fenceEvent: default,
                    fenceValue: ref fenceValue,
                    queue: null
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    actualValue: call,
                    message: "No helper reaches that call.",
                    paramName: nameof(call)
                );
        }
    }

    // Answers S_OK everywhere except the one call named Failing, which answers Result. The fence reads the value last
    // signalled, so a wait returns at once, except when the event arm is the failing call: then it reads incomplete, and
    // the failing arm throws before any event is waited on.
    private sealed class FakeCommandCalls : IDirectXCommandCalls {
        public string? Failing { get; init; }
        public HRESULT RemovalReason { get; init; }
        public HRESULT Result { get; init; }
        public ulong Signalled { get; private set; }

        private HRESULT Answer(string call) => ((call == Failing)
            ? Result
            : new HRESULT(value: 0)
        );

        public HRESULT Map(ID3D12Resource* resource, void** data) => Answer(call: nameof(Map));
        public HRESULT ResetAllocator(ID3D12CommandAllocator* allocator) => Answer(call: nameof(ResetAllocator));
        public HRESULT ResetList(ID3D12GraphicsCommandList* commandList, ID3D12CommandAllocator* allocator) => Answer(call: nameof(ResetList));
        public HRESULT Close(ID3D12GraphicsCommandList* commandList) => Answer(call: nameof(Close));
        public HRESULT Signal(ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value) {
            Signalled = value;

            return Answer(call: nameof(Signal));
        }
        public ulong CompletedValue(ID3D12Fence* fence) => ((Failing == nameof(SetEventOnCompletion))
            ? 0UL
            : Signalled
        );
        public HRESULT SetEventOnCompletion(ID3D12Fence* fence, ulong value, HANDLE fenceEvent) => Answer(call: nameof(SetEventOnCompletion));
        public HRESULT DeviceRemovedReason() => RemovalReason;
    }
}
