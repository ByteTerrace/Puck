using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Tests;

internal static class LightingSnapshot {
    internal static byte[] Bytes(SdfLighting lighting) {
        var upload = new SdfLightingUpload();

        upload.Pack(environment: lighting);
        return [.. upload.LightFrameBytes, .. upload.LightBytes, .. upload.SkyFrameBytes,
            .. upload.StopBytes, .. upload.SoftboxBytes];
    }
}
