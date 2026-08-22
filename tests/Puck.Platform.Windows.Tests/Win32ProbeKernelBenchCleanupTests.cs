using System.Reflection;
using System.Runtime.Versioning;
using Puck.Abstractions.Presentation;
using Puck.Platform.Probes;
using Xunit;

namespace Puck.Platform.Windows.Tests;

[SupportedOSPlatform("windows10.0.10240")]
public sealed class Win32ProbeKernelBenchCleanupTests {
    [Fact]
    public void PartialRingAcquisition_ReleasesEveryViewBeforeEveryTexture_AndSkipsEmptySlots() {
        var released = new List<nint>();
        nint[]?[] textures = [[11, 12], null, [13, 0]];
        nint[]?[] views = [[21, 0], [22], null];
        var bench = typeof(Win32RawInput).Assembly.GetType(
            name: "Puck.Platform.Windows.Win32ProbeKernelBench",
            throwOnError: true
        )!;
        var cleanup = bench.GetMethod(
            name: "ReleaseRingResources",
            bindingAttr: BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(nint[]?[]), typeof(nint[]?[]), typeof(Action<nint>)],
            modifiers: null
        )!;

        _ = cleanup.Invoke(
            obj: null,
            parameters: [textures, views, (Action<nint>)(resource => released.Add(item: resource))]
        );

        Assert.Equal(expected: [21, 22, 11, 12, 13], actual: released);
    }
    // A ring whose second shared handle is invalid drives the real OpenSharedResource1/CreateShaderResourceView path
    // (Win32ProbeKernelBench.Attachment.OpenRingResources, reached only through Win32D3D11 interop, so this exercises
    // real hardware) into its partial-acquisition catch: the first slot's texture and view must not remain open when
    // the second slot's open fails. Attachment is a private nested type and OpenRingResources takes the internal
    // IProbeKernelDevice, so both the type and a real device are reached through reflection.
    //
    // D3D11 on this machine's driver does not identity-map OpenSharedResource1: two opens of the same NT handle on
    // the same device return distinct ID3D11Texture2D* (asserted below), so a refcount read through an
    // independently opened pointer cannot observe production's own open/release pair. OpenRingResources instead
    // publishes its acquisition arrays onto the attachment before the first native call (RingTextures/RingViews),
    // and ReleaseRingResources zeroes each entry it releases — so the released slot's own pointer, read back through
    // the attachment after the throw, proves the release ran.
    [Fact]
    public void OpenRingResources_PartialFailure_ReleasesTheOpenedTextureAndView() {
        using var bench = KernelBench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");

            return;
        }

        var target = bench.CreateSharedTarget();
        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: 2);

        var request = new ProbeKernelRequest(
            KernelSource: "",
            AccumulateEntry: "",
            FinalizeEntry: "",
            Constants: ReadOnlyMemory<byte>.Empty,
            ChannelCount: 0,
            RateHz: 0,
            Inputs: [new ProbeKernelInput.Ring(
                Width: KernelBench.FrameWidth,
                Height: KernelBench.FrameHeight,
                Format: SurfaceFormat.R8G8B8A8Unorm,
                SharedTargetHandles: [target.SharedHandle, ((nint)1)],
                Slots: slots
            )],
            Trigger: CameraSensor.Color
        );
        var ring = new ProbeReadingRing();
        var assembly = typeof(Win32RawInput).Assembly;
        var attachmentType = assembly.GetType(name: "Puck.Platform.Windows.Win32ProbeKernelBench+Attachment", throwOnError: true)!;
        var attachmentConstructor = attachmentType.GetConstructors(bindingAttr: (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)).Single();
        var attachment = attachmentConstructor.Invoke(parameters: [request, ring]);
        var deviceType = assembly.GetType(name: "Puck.Platform.Windows.Win32D3D11VideoDevice", throwOnError: true)!;
        var device = deviceType.GetConstructor(types: [typeof(long)])!.Invoke(parameters: [bench.AdapterLuid]);
        var openSharedTexture = deviceType.GetMethod(name: "OpenSharedTexture", bindingAttr: BindingFlags.Public | BindingFlags.Instance)!;
        var releaseTexture = deviceType.GetMethod(name: "ReleaseTexture", bindingAttr: BindingFlags.Public | BindingFlags.Static)!;

        try {
            var probeFirst = ((nint)openSharedTexture.Invoke(obj: device, parameters: [target.SharedHandle])!);
            var probeSecond = ((nint)openSharedTexture.Invoke(obj: device, parameters: [target.SharedHandle])!);

            _ = releaseTexture.Invoke(obj: null, parameters: [probeFirst]);
            _ = releaseTexture.Invoke(obj: null, parameters: [probeSecond]);
            Assert.NotEqual(expected: probeFirst, actual: probeSecond);

            var openRingResources = attachmentType.GetMethod(name: "OpenRingResources", bindingAttr: BindingFlags.Public | BindingFlags.Instance)!;
            var thrown = Assert.Throws<TargetInvocationException>(testCode: () => openRingResources.Invoke(obj: attachment, parameters: [device]));

            Assert.IsType<InvalidOperationException>(@object: thrown.InnerException);
            Assert.Contains(expectedSubstring: "slot 1", actualString: thrown.InnerException!.Message);

            var ringResourcesOpenedProperty = attachmentType.GetProperty(name: "RingResourcesOpened", bindingAttr: BindingFlags.Public | BindingFlags.Instance)!;

            Assert.False(condition: ((bool)ringResourcesOpenedProperty.GetValue(obj: attachment)!));

            var ringTexturesMethod = attachmentType.GetMethod(name: "RingTextures", bindingAttr: BindingFlags.Public | BindingFlags.Instance)!;
            var ringViewsMethod = attachmentType.GetMethod(name: "RingViews", bindingAttr: BindingFlags.Public | BindingFlags.Instance)!;
            var slot0Textures = ((nint[])ringTexturesMethod.Invoke(obj: attachment, parameters: [0])!);
            var slot0Views = ((nint[])ringViewsMethod.Invoke(obj: attachment, parameters: [0])!);

            Assert.All(collection: slot0Textures, action: texture => Assert.Equal(expected: 0, actual: texture));
            Assert.All(collection: slot0Views, action: view => Assert.Equal(expected: 0, actual: view));
        } finally {
            ((IDisposable)device).Dispose();
        }
    }
}
