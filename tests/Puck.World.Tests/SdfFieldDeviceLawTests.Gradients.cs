using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The bake's gradient kernel walks the field once in forward mode (<c>SDF_FIELD_DUAL</c>) and takes the walk's gradient
/// in place of the four-tap stencil wherever the walk keeps every derivative. The gradient probe
/// (<c>Assets/Shaders/sdf-field-gradient.comp.hlsl</c>) walks every accepted program of the field law that way at each of
/// its points and takes the stencil there at two spacings. Where the two stencils agree (the field is smooth across them)
/// and the walk kept its derivatives, the dual gradient is the stencil's within <see cref="GradientTolerance"/>; the walk
/// keeps them on every accepted program except those through an operation with no dual rule, which the probe reports
/// uncovered. The red leg reads each dual gradient with two axes exchanged, which must miss the stencil somewhere in every
/// program, so the tolerance sees a partial read from the wrong axis.
/// </summary>
public sealed partial class SdfFieldDeviceLawTests {
    private const string GradientKernelName = "sdf-field-gradient.comp";
    // The largest disagreement between the dual gradient and the stencil at a smooth point, in field units per metre: the
    // stencil's single-precision differences resolve about a ten-thousandth of a unit gradient, its curvature error under
    // a thousandth for the probe programs' radii.
    private const float GradientTolerance = 2e-3f;
    // The most the stencil at twice the spacing may differ from it for the point to count as smooth: past it a crease
    // lies within the taps, where the stencil blends two faces and the dual takes one.
    private const float SmoothTolerance = 1e-3f;

    [Fact]
    public void AVulkanDeviceWalksEveryProgramsGradientAsItsStencilReadsIt() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyGradients(
            backend: $"vulkan ({device.Name})",
            kernel: Kernel(extension: ".spv", name: GradientKernelName),
            services: device.Services
        );
    }
    [Fact]
    public void ADirect3D12DeviceWalksEveryProgramsGradientAsItsStencilReadsIt() {
        using (var context = DirectXTestDevices.Hardware()) {
            VerifyGradients(
                backend: "directx",
                kernel: Kernel(extension: ".dxil", name: GradientKernelName),
                services: context.Services
            );
        }
    }

    private static void VerifyGradients(string backend, byte[] kernel, Puck.Abstractions.Gpu.GpuDeviceServices services) {
        var legs = AcceptedLegs().ToList();
        var results = Run(
            kernel: kernel,
            legs: legs,
            name: GradientKernelName,
            services: services
        );
        var failures = new List<string>();
        var uncovered = new List<string>();
        var compared = 0;

        for (var index = 0; (index < legs.Count); index++) {
            var leg = legs[index];
            var worst = 0f;
            var worstPoint = -1;
            var swappedMisses = false;
            var smooth = 0;
            var covered = 0;

            for (var point = 0; (point < Points.Length); point++) {
                var result = results[((index * Points.Length) + point)];

                if (result.Z != 1f) { continue; }
                covered++;
                if (!(result.Y <= SmoothTolerance)) { continue; }
                smooth++;
                if (!(result.X <= worst)) {
                    worst = result.X;
                    worstPoint = point;
                }
                swappedMisses |= (result.W > GradientTolerance);
            }

            compared += smooth;
            if (covered == 0) {
                uncovered.Add(item: leg.Name);
                continue;
            }
            if (!(worst <= GradientTolerance)) {
                failures.Add(item: $"{leg.Name}: the dual gradient misses the stencil by {worst} at {Points[worstPoint]}");
            }
            if ((smooth > 0) && !swappedMisses) {
                failures.Add(item: $"{leg.Name}: the dual gradient with x and y exchanged stayed within {GradientTolerance} of the stencil at every smooth point");
            }
        }

        Assert.True(
            condition: ((failures.Count == 0) && (compared > 0)),
            userMessage: $"{backend}: {failures.Count} of {legs.Count} program(s) disagree with their stencil ({compared} smooth points compared; uncovered: {string.Join(separator: ", ", values: uncovered)}):{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: failures)}"
        );
    }
}
