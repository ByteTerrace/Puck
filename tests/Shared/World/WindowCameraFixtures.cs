using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Testing;

/// <summary>The camera a seat sees through a window, and the tolerances its laws compare with.</summary>
internal static class WindowCameraFixtures {
    // The seat camera the presenter frames a body with: the seat's compiled chase rig, posed at the body.
    internal static (Vector3 Eye, Vector3 Target) Camera(WorldSeatViewState view, WorldDefinition definition, WorldStateMirror mirror, Vector3 position, Quaternion orientation) {
        var rig = view.ResolveChase(
            bodyOrientation: orientation,
            definition: definition,
            domains: new WorldValueDomainGuard(),
            mirror: mirror,
            views: definition.Views
        );
        var anchor = new SdfAnchor(
            Orientation: orientation,
            Position: position
        );
        var clock = new SdfCameraClock(
            AuthoritativeTick: 0UL,
            PresentationSeconds: 0f
        );

        var (eye, target, _) = rig.Resolve(
            anchor: in anchor,
            clock: in clock
        );

        return (eye, target);
    }
    internal static Quaternion Heading(FixedQ4816 yaw) => Quaternion.CreateFromAxisAngle(
        angle: ((float)((double)yaw)),
        axis: Vector3.UnitY
    );
    internal static void Near(Vector3 expected, Vector3 actual, string what) => Assert.True(
        condition: (Vector3.Distance(value1: expected, value2: actual) <= Tolerance),
        userMessage: $"{what}: expected {expected}, actual {actual}"
    );

    internal const float Tolerance = 1e-3f;
}
