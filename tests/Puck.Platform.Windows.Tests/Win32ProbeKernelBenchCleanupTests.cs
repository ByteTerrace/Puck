using System.Reflection;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Platform.Probes;
using Xunit;

namespace Puck.Platform.Windows.Tests;

[SupportedOSPlatform("windows10.0.10240")]
public sealed class Win32ProbeKernelBenchCleanupTests {
    // Win32ProbeKernelBench's attachment is a private nested type and OpenRingResources takes the internal
    // IProbeKernelDevice, so both the type and a real device are reached through reflection.
    private static readonly Assembly Platform = typeof(Win32RawInput).Assembly;
    private static readonly Type AttachmentType = Platform.GetType(
        name: "Puck.Platform.Windows.Win32ProbeKernelBench+Attachment",
        throwOnError: true
    )!;
    private static readonly Type VideoDeviceType = Platform.GetType(
        name: "Puck.Platform.Windows.Win32D3D11VideoDevice",
        throwOnError: true
    )!;

    // A kernel attachment whose one socket is a ring over the given shared targets and producer fence.
    private static object RingAttachment(IReadOnlyList<nint> targets, nint sharedFence) {
        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: targets.Count);

        var request = new ProbeKernelRequest(
            AccumulateBytecode: ReadOnlyMemory<byte>.Empty,
            AccumulateEntry: "",
            FinalizeBytecode: ReadOnlyMemory<byte>.Empty,
            FinalizeEntry: "",
            Constants: ReadOnlyMemory<byte>.Empty,
            ChannelCount: 0,
            RateHz: 0,
            Inputs: [new ProbeKernelInput.Ring(
                    Format: GpuPixelFormat.R8G8B8A8Unorm,
                    Height: KernelBench.FrameHeight,
                    SharedFenceHandle: sharedFence,
                    SharedTargetHandles: targets,
                    Slots: slots,
                    Width: KernelBench.FrameWidth
                )],
            Trigger: 0
        );

        return AttachmentType.GetConstructors(bindingAttr: BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single().Invoke(parameters: [request, new ProbeReadingRing()]);
    }
    // The production Direct3D 11 video device on the bench's adapter.
    private static object VideoDevice(KernelBench bench) => VideoDeviceType.GetConstructor(types: [typeof(long)])!.Invoke(parameters: [bench.AdapterLuid]);
    // What OpenRingResources throws on the device, unwrapped from reflection's invocation wrapper.
    private static Exception OpenRingResourcesRefusal(object attachment, object device) {
        var openRingResources = AttachmentType.GetMethod(
            bindingAttr: BindingFlags.Public | BindingFlags.Instance,
            name: "OpenRingResources"
        )!;
        var thrown = Assert.Throws<TargetInvocationException>(testCode: () => openRingResources.Invoke(
            obj: attachment,
            parameters: [device]
        ));

        return thrown.InnerException!;
    }
    // Holds that a refused open left nothing open: the attachment reports its ring resources closed, and every texture
    // and view of the ring socket at the index reads back zeroed, since ReleaseRingResources zeroes what it releases.
    private static void AssertRingReleased(object attachment, int index) {
        Assert.False(condition: ((bool)AttachmentType.GetProperty(
            bindingAttr: BindingFlags.Public | BindingFlags.Instance,
            name: "RingResourcesOpened"
        )!.GetValue(obj: attachment)!));

        foreach (var name in ((string[])["RingTextures", "RingViews"])) {
            var opened = ((nint[])AttachmentType.GetMethod(
                bindingAttr: BindingFlags.Public | BindingFlags.Instance,
                name: name
            )!.Invoke(
                obj: attachment,
                parameters: [index]
            )!);

            Assert.All(
                collection: opened,
                action: static resource => Assert.Equal(
                    actual: resource,
                    expected: 0
                )
            );
        }
    }

    [Fact]
    public void PartialRingAcquisition_ReleasesEveryViewBeforeEveryTexture_AndSkipsEmptySlots() {
        var released = new List<nint>();
        nint[]?[] textures = [[11, 12], null, [13, 0]];
        nint[]?[] views = [[21, 0], [22], null];
        var bench = Platform.GetType(
            name: "Puck.Platform.Windows.Win32ProbeKernelBench",
            throwOnError: true
        )!;
        var cleanup = bench.GetMethod(
            binder: null,
            bindingAttr: BindingFlags.NonPublic | BindingFlags.Static,
            modifiers: null,
            name: "ReleaseRingResources",
            types: [typeof(nint[]?[]), typeof(nint[]?[]), typeof(Action<nint>)]
        )!;

        _ = cleanup.Invoke(
            obj: null,
            parameters: [textures, views, ((Action<nint>)(resource => released.Add(item: resource)))]
        );

        Assert.Equal(
            actual: released,
            expected: [21, 22, 11, 12, 13]
        );
    }
    // A ring whose second shared handle is invalid drives the real OpenSharedResource1/CreateShaderResourceView path
    // (Win32ProbeKernelBench.Attachment.OpenRingResources, reached only through Win32D3D11 interop, so this exercises
    // real hardware) into its partial-acquisition catch: the first slot's texture and view must not remain open when
    // the second slot's open fails.
    //
    // D3D11 on this machine's driver does not identity-map OpenSharedResource1: two opens of the same NT handle on
    // the same device return distinct ID3D11Texture2D* (asserted below), so a refcount read through an
    // independently opened pointer cannot observe production's own open/release pair. OpenRingResources instead
    // publishes its acquisition arrays onto the attachment before the first native call (RingTextures/RingViews),
    // and ReleaseRingResources zeroes each entry it releases — so the released slot's own pointer, read back through
    // the attachment after the throw, proves the release ran.
    [Fact]
    public void OpenRingResources_PartialFailure_ReleasesTheOpenedTextureAndView() {
        // The production device below needs VIDEO_SUPPORT as well as ordinary D3D11 rendering.
        using var bench = KernelBench.TryCreate(requireVideoSupport: true);

        if (bench is null) {
            Assert.Skip(reason: "no D3D11 adapter with video support is available on this machine.");

            return;
        }

        var target = bench.CreateSharedTarget();
        var attachment = RingAttachment(
            sharedFence: 0,
            targets: [target.SharedHandle, ((nint)1)]
        );
        var device = VideoDevice(bench: bench);
        var openSharedTexture = VideoDeviceType.GetMethod(
            bindingAttr: BindingFlags.Public | BindingFlags.Instance,
            name: "OpenSharedTexture"
        )!;
        var releaseTexture = VideoDeviceType.GetMethod(
            bindingAttr: BindingFlags.Public | BindingFlags.Static,
            name: "ReleaseTexture"
        )!;

        try {
            var probeFirst = ((nint)openSharedTexture.Invoke(
                obj: device,
                parameters: [target.SharedHandle]
            )!);
            var probeSecond = ((nint)openSharedTexture.Invoke(
                obj: device,
                parameters: [target.SharedHandle]
            )!);

            _ = releaseTexture.Invoke(
                obj: null,
                parameters: [probeFirst]
            );
            _ = releaseTexture.Invoke(
                obj: null,
                parameters: [probeSecond]
            );
            Assert.NotEqual(
                actual: probeSecond,
                expected: probeFirst
            );

            var refusal = Assert.IsType<InvalidOperationException>(@object: OpenRingResourcesRefusal(
                attachment: attachment,
                device: device
            ));

            Assert.Contains(
                expectedSubstring: "slot 1",
                actualString: refusal.Message
            );
            AssertRingReleased(
                attachment: attachment,
                index: 0
            );
        } finally {
            ((IDisposable)device).Dispose();
        }
    }
    // THE LAW: a ring whose producer's shared fence the host's device cannot open is refused before the kernel ever
    // reads it, since nothing would then order its reads after the producer's writes. The refusal names the socket and
    // why the Direct3D 11 fence wait could not open the fence (an invalid handle here), and every target and view the
    // ring opened before the fence is released.
    [Fact]
    public void OpenRingResources_UnopenableSharedFence_RefusesTheRingAndNamesTheCause() {
        using var bench = KernelBench.TryCreate(requireVideoSupport: true);

        if (bench is null) {
            Assert.Skip(reason: "no D3D11 adapter with video support is available on this machine.");

            return;
        }

        // Two real targets, the fewest a slot ring holds, so the ring's every target and view open before its fence.
        var attachment = RingAttachment(
            sharedFence: ((nint)1),
            targets: [bench.CreateSharedTarget().SharedHandle, bench.CreateSharedTarget().SharedHandle]
        );
        var device = VideoDevice(bench: bench);

        try {
            var refusal = Assert.IsType<InvalidOperationException>(@object: OpenRingResourcesRefusal(
                attachment: attachment,
                device: device
            ));

            Assert.StartsWith(
                actualString: refusal.Message,
                expectedStartString: "ring socket 0 cannot wait on its producer's shared fence: cpu-wait ("
            );
            AssertRingReleased(
                attachment: attachment,
                index: 0
            );
        } finally {
            ((IDisposable)device).Dispose();
        }
    }
}
