using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm.Tests;

// The laws read a camera sample (SdfMeshIrradiance.TrySampleCamera) as the mean luminance of its six axes; forward is the
// camera's axis, which the sample does not weight by.
internal static class SdfMeshIrradianceSampling {
    internal static bool SampleLuminance(this SdfMeshIrradiance volume, Vector3 position, Vector3 forward, out float luminance, out float skyVisibility, out SdfCameraSampleSource source, bool fresh = false) {
        _ = forward;
        Span<Vector3> light = stackalloc Vector3[6];

        luminance = 0f;
        if (!volume.TrySampleCamera(fresh: fresh, light: light, position: position, skyVisibility: out skyVisibility, source: out source)) { return false; }
        foreach (var axis in light) { luminance += (SdfPhotometry.LuminanceOf(color: axis) / 6f); }
        return true;
    }
}
