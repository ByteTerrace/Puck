using System.Buffers;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Tests;

internal static class LightingSnapshot {
    internal static byte[] Bytes(SdfLighting lighting) {
        var upload = new SdfLightingUpload();

        upload.Pack(environment: lighting);
        var bytes = new ArrayBufferWriter<byte>();
        bytes.Write(upload.LightFrameBytes);
        bytes.Write(upload.LightBytes);
        bytes.Write(upload.SkyFrameBytes);
        bytes.Write(upload.StopBytes);
        bytes.Write(upload.SoftboxBytes);
        if (lighting.Sky is { } sky) {
            if (sky.Common is { } common) { bytes.Write(common.Bytes); }
            if (sky.Stops is { } stops) { bytes.Write(stops.Bytes); }
            for (var index = 0; index < sky.TableCount; index++) { bytes.Write(sky.Table(index).Bytes); }
        }
        return bytes.WrittenSpan.ToArray();
    }
}
