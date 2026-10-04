using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanSmoothGradientEndpointsReturnTheDecidingGradientExactly() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyGradientEndpoints(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXSmoothGradientEndpointsReturnTheDecidingGradientExactly() {
        using var device = DirectXTestDevices.Hardware();

        VerifyGradientEndpoints(services: device.Services, extension: ".dxil");
    }

    private static void VerifyGradientEndpoints(GpuDeviceServices services, string extension) {
        var probes = new List<Vector3>();
        var parameters = new List<Vector4>();
        var expected = new List<Vector3>();
        var distances = new List<float>();
        var names = new List<string>();
        var small = new Vector3(x: 1f, y: -2f, z: 4f);
        var large = new Vector3(x: 1e8f, y: -1e8f, z: 1e8f);

        foreach (var blend in new[] { SdfBlendOp.SmoothUnion, SdfBlendOp.SmoothIntersection, SdfBlendOp.SmoothSubtraction }) {
            foreach (var endpoint in new[] { 0, 1, 2 }) {
                for (var zeroSign = 0; (zeroSign < ((endpoint == 2) ? 1 : 2)); zeroSign++) {
                    var zero = BitConverter.UInt32BitsToSingle(value: (((uint)zeroSign) << 31));
                    var subtraction = (blend == SdfBlendOp.SmoothSubtraction);
                    var current = ((endpoint == 0) ? 4f : zero);
                    var candidate = ((endpoint == 1) ? 4f : zero);

                    if (blend == SdfBlendOp.SmoothIntersection) {
                        current = ((endpoint == 0) ? -4f : zero);
                        candidate = ((endpoint == 1) ? -4f : zero);
                    }
                    if (subtraction) {
                        current = ((endpoint == 1) ? -4f : zero);
                        candidate = ((endpoint == 0) ? 4f : -zero);
                    }
                    var currentWins = (subtraction ? (endpoint == 0) : (endpoint == 1));
                    var currentGradient = (currentWins ? small : large);
                    var candidateGradient = (currentWins ? large : small);
                    var answer = (currentWins ? small : (subtraction ? -small : small));
                    var distance = zero;

                    if (endpoint == 2) {
                        current = 0f; candidate = 0f;
                        currentGradient = new Vector3(x: 2f, y: 4f, z: 8f);
                        candidateGradient = new Vector3(x: 6f, y: 10f, z: 14f);
                        answer = (subtraction ? new Vector3(x: -2f, y: -3f, z: -3f) : new Vector3(x: 4f, y: 7f, z: 11f));
                        distance = ((blend == SdfBlendOp.SmoothUnion) ? -0.5f : 0.5f);
                    }
                    probes.Add(item: new Vector3(x: ((float)blend), y: current, z: candidate));
                    parameters.Add(item: new Vector4(value: currentGradient, w: 2f));
                    parameters.Add(item: new Vector4(value: candidateGradient, w: 0f));
                    expected.Add(item: answer);
                    distances.Add(item: distance);
                    names.Add(item: $"{blend}, h={((endpoint == 2) ? "0.5" : endpoint.ToString())}, zeroSign={zeroSign}");
                }
            }
        }
        var program = Pack(emit: static (builder, material) => builder.ResetPoint().Sphere(radius: 1f, material: material));
        var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-gradient-endpoints.comp"), services: services,
            legs: [new SdfFieldLeg(Name: "smooth gradient endpoints", Words: program.Words.ToArray(), Distances: [], Materials: [])],
            points: probes.ToArray(), parameters: parameters.ToArray());

        Assert.Equal(expected: expected.Count, actual: results.Length);
        for (var index = 0; (index < results.Length); index++) {
            var result = results[index];
            var answer = expected[index];

            Assert.True(condition: ((BitConverter.SingleToUInt32Bits(value: result.X) == BitConverter.SingleToUInt32Bits(value: answer.X))
                && (BitConverter.SingleToUInt32Bits(value: result.Y) == BitConverter.SingleToUInt32Bits(value: answer.Y))
                && (BitConverter.SingleToUInt32Bits(value: result.Z) == BitConverter.SingleToUInt32Bits(value: answer.Z))),
                userMessage: $"{names[index]}: gradient {result} must select {answer} exactly, including saturated endpoints.");
            Assert.True(condition: (BitConverter.SingleToUInt32Bits(value: result.W) == BitConverter.SingleToUInt32Bits(value: distances[index])),
                userMessage: $"{names[index]}: distance must match the scalar helper and exact endpoint bits; expected {BitConverter.SingleToUInt32Bits(value: distances[index]):X8}, got {BitConverter.SingleToUInt32Bits(value: result.W):X8}.");
        }
    }
}
