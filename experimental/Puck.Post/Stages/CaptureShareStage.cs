using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.Platform;
using Puck.Platform.Windows;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. The compositor-capture ZERO-COPY transport end to end: a Windows Graphics Capture feed copies its
/// WGC Direct3D 11 frames into consumer-provisioned Direct3D 12 shared simultaneous-access textures (the path the
/// Direct3D 12 World host samples directly), and the Direct3D 12 side reads the latest slot back to prove real content
/// crossed. The shared Tier-C Direct3D 12 device stands in for the World host: it owns the adapter the capture must
/// target (its LUID is passed to <see cref="INativeImageCaptureService.TryCreateWindowCapture"/> so WGC's decode device
/// is on the same adapter), provisions three round-robin shared targets via
/// <see cref="DirectXGpuSurfaceExportFactory.CreateSimultaneousAccessStorageImage"/>, and reads back the slot the feed
/// reports last copied. Environment-lenient like the CPU <c>capture</c> stage: a pre-19041 build, an unavailable
/// capture service, an unopenable probe window, or a headless compositor that copies no frame all return
/// <see cref="PostVerdict.Skip"/>. Only a slot with no real content (frames flowed but the read-back texture is flat)
/// hard-fails.
/// </summary>
internal sealed class CaptureShareStage : IPostStage {
    private const int MinDistinctColors = 16; // a shaded window frame has hundreds; a flat/blank buffer one
    private const double ProbeRefreshRateHz = 5.0; // a slow producer bounds the GPU-frame wait without racing paints
    private const int TargetCount = 3; // round-robin shared targets, matching the feed's provisioning contract
    private static readonly TimeSpan GpuFrameTimeout = TimeSpan.FromSeconds(value: 2.0);

    /// <inheritdoc/>
    public string Name => "capture-share";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) {
            return PostStageOutcome.Skip(detail: "the GPU capture transport (Windows Graphics Capture into shared textures) requires Windows 10 2004 (build 19041) or newer");
        }

        var service = new Win32NativeImageCaptureService();
        if (!service.IsSupported) {
            return PostStageOutcome.Skip(detail: "Windows Graphics Capture is unavailable in this desktop session");
        }

        return RunCore(context: context, service: service);
    }

    [SupportedOSPlatform("windows10.0.19041")]
    private static PostStageOutcome RunCore(PostContext context, Win32NativeImageCaptureService service) {
        // The shared Tier-C Direct3D 12 device stands in for the World's Direct3D 12 host: it provisions the shared
        // targets WGC copies into and reads the latest slot back. The capture must run on this adapter, so its LUID is
        // handed to the feed (WGC's Direct3D 11 decode device opens the shared handles on the same adapter).
        var directX = context.RequireDirectXDevice();
        var adapterLuid = new DirectXNativeDeviceApi().GetAdapterLuid(deviceHandle: directX.DeviceContext.DeviceHandle);

        using var probe = new CaptureProbeWindow();

        if (!service.TryCreateWindowCapture(windowTitleFragment: probe.Title, width: 320, height: 240, refreshRateHz: ProbeRefreshRateHz, feed: out var feed, adapterLuid: adapterLuid)) {
            return PostStageOutcome.Skip(detail: "the interactive Windows capture service could not open the probe window");
        }

        using (feed) {
            var width = feed.SourceWidth;
            var height = feed.SourceHeight;
            if ((width <= 0) || (height <= 0)) {
                return PostStageOutcome.Skip(detail: $"the capture feed reported no source extent ({width}x{height})");
            }

            var images = new IGpuExportableStorageImage[TargetCount];
            try {
                AllocateTargets(directX: directX, feed: feed, height: (uint)height, images: images, width: (uint)width);

                // Poll (bounded) for the first GPU copy. No frame → skip (headless/CI compositors publish nothing),
                // matching the CPU capture stage's leniency.
                if (!TryWaitForGpuFrame(feed: feed, slot: out var slot)) {
                    return PostStageOutcome.Skip(detail: $"the compositor copied no frame into the shared textures within {GpuFrameTimeout.TotalSeconds:0.#}s ({width}x{height}, revision {feed.GpuRevision})");
                }

                // Read back the latest-copied slot on the Direct3D 12 host and assert real content survived the
                // zero-copy transport — a shaded window frame has many distinct colors; a flat/blank buffer one.
                var (distinctColors, firstPixel) = ReadbackDistinctColors(directX: directX, height: (uint)height, image: images[slot], width: (uint)width);

                if (distinctColors < MinDistinctColors) {
                    return PostStageOutcome.Fail(detail: $"the Direct3D 12 host read back GPU slot {slot} but it lacks real content ({distinctColors} distinct colors < {MinDistinctColors}, px0=0x{firstPixel:X8}) — no frame crossed the shared texture");
                }

                return PostStageOutcome.Pass(detail: $"{width}x{height} B8G8R8A8 | WGC copied a window frame into {TargetCount} shared simultaneous-access textures (adapter LUID 0x{adapterLuid:X}); the Direct3D 12 host read back slot {slot} zero-copy ({distinctColors} distinct colors, px0=0x{firstPixel:X8}, GpuRevision {feed.GpuRevision})");
            } finally {
                for (var index = 0; (index < TargetCount); index++) {
                    images[index]?.Dispose();
                }
            }
        }
    }

    // Provision the round-robin shared targets on the Direct3D 12 host and attach them to the feed, whose WGC device
    // opens the shared handles and copies each frame into the next slot.
    [SupportedOSPlatform("windows10.0.10240")]
    private static void AllocateTargets(PostDirectXDevice directX, INativeImageCaptureFeed feed, IGpuExportableStorageImage[] images, uint width, uint height) {
        var factory = new DirectXGpuSurfaceExportFactory();
        var handles = new nint[TargetCount];

        for (var index = 0; (index < TargetCount); index++) {
            images[index] = factory.CreateSimultaneousAccessStorageImage(
                deviceContext: directX.DeviceContext,
                format: GpuPixelFormat.B8G8R8A8Unorm,
                height: height,
                width: width
            );
            handles[index] = images[index].SharedHandle;
        }

        feed.AttachGpuTargets(new NativeImageGpuCaptureTargets(SharedTargetHandles: handles, Width: (int)width, Height: (int)height));
    }

    // The feed pumps its own WGC copies; poll LatestGpuSlot until the first completed revision lands or the bounded
    // deadline passes (a losing/ended feed yields no slot). No sim clock is waited — only the compositor.
    private static bool TryWaitForGpuFrame(INativeImageCaptureFeed feed, out int slot) {
        var deadline = Stopwatch.GetTimestamp() + (long)(GpuFrameTimeout.TotalSeconds * Stopwatch.Frequency);

        while (Stopwatch.GetTimestamp() < deadline) {
            if ((feed.GpuRevision > 0) && (feed.LatestGpuSlot >= 0)) {
                slot = feed.LatestGpuSlot;

                return true;
            }

            if (feed.IsEnded) {
                break;
            }

            Thread.Sleep(millisecondsTimeout: 5);
        }

        slot = -1;

        return false;
    }

    // Read the Direct3D 12-owned shared target back on Direct3D 12 (its own device) and count distinct colors — the
    // content signature that separates a real captured frame from a flat/blank buffer.
    [SupportedOSPlatform("windows10.0.10240")]
    private static (int DistinctColors, uint FirstPixel) ReadbackDistinctColors(PostDirectXDevice directX, IGpuExportableStorageImage image, uint width, uint height) {
        using var readback = directX.Services.GetRequiredService<IGpuSurfaceTransferFactory>().CreateReadback(deviceContext: directX.DeviceContext);
        var pixels = readback.Read(bytesPerPixel: 4, deviceContext: directX.DeviceContext, format: GpuPixelFormat.B8G8R8A8Unorm, height: height, sourceImageHandle: image.ImageHandle, width: width).Span;
        var pixels32 = MemoryMarshal.Cast<byte, uint>(span: pixels);
        var firstPixel = pixels32.IsEmpty ? 0u : pixels32[0];
        var distinct = new HashSet<uint>();

        for (var index = 0; (index < pixels32.Length); index++) {
            _ = distinct.Add(item: pixels32[index]);
        }

        return (distinct.Count, firstPixel);
    }
}
