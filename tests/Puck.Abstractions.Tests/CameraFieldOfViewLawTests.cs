using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.Abstractions.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="CameraSnapshot.LookAt"/> builds a camera for every field of view from
/// <see cref="CameraSnapshot.MinFieldOfViewRadians"/> up to the float below a half turn, with a positive finite
/// <see cref="CameraSnapshot.TanHalfFieldOfView"/>, and refuses an angle outside that range by name.
/// </summary>
public sealed class CameraFieldOfViewLawTests {
    private static CameraSnapshot Look(float fieldOfViewRadians) => CameraSnapshot.LookAt(
        fieldOfViewRadians: fieldOfViewRadians,
        position: Vector3.Zero,
        target: -Vector3.UnitZ,
        viewportHeight: 240u,
        viewportWidth: 320u
    );

    [Fact]
    public void The_range_ends_build_a_camera_whose_tangent_is_a_positive_normal_float() {
        foreach (var angle in new[] { CameraSnapshot.MinFieldOfViewRadians, 1f, MathF.BitDecrement(x: MathF.PI) }) {
            var tangent = Look(fieldOfViewRadians: angle).TanHalfFieldOfView;

            Assert.True(condition: (float.IsNormal(f: tangent) && (tangent > 0f)), userMessage: $"fov {angle}: tangent {tangent}");
        }
    }
    [Fact]
    public void An_angle_outside_the_range_is_refused_naming_the_field_of_view() {
        foreach (var angle in new[] { 0f, -1f, MathF.BitDecrement(x: CameraSnapshot.MinFieldOfViewRadians), float.Epsilon, MathF.PI, 4f, float.NaN, float.PositiveInfinity }) {
            var thrown = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Look(fieldOfViewRadians: angle));

            Assert.Equal(expected: "fieldOfViewRadians", actual: thrown.ParamName);
        }
    }
}
