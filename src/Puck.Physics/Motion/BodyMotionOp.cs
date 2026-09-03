using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Physics.Motion;

/// <summary>Identifies a body motion program instruction from the closed domain-operation vocabulary.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyMotionOp>))]
public enum BodyMotionOp : byte {
    SenseNearestInCone,
    ProduceWanderIntent,
    ProduceAttendIntent,
    FaceSensorTarget,
    ResolveYawAttitudeAndPlanarFrame,
    IntegrateLocalAttitude,
    ComputePlanarTargetVelocity,
    ComputeLocalTargetVelocity,

    /// <summary>Converges velocity on the commanded intent through the kit's <c>shaping</c> table: the first row
    /// whose gate opens governs, running the whole-vector response law (a row with no <c>across</c> facet), the
    /// anisotropic drive decomposition (a row with one), or a named second-order follower — exactly one of the
    /// three per row.</summary>
    ShapeVelocity,
    SnapYawToPlanarIntent,
    ResolveDriveFrame,

    /// <summary>Keeps the hold the body has while its surface is still there and the same face, and otherwise takes
    /// the first hold the kit's ordered list offers. Sets the hold's frame — the tangent plane movement rides and the
    /// up axis the body's attitude and its contact walkable test stand against. Runs after the ordinary frame
    /// operation, whose heading it leaves intact.</summary>
    ResolveHold,
    RunActionTriggers,

    /// <summary>Applies the current hold's vertical law: the row's own arc for a hold gravity keeps, a rate-limited
    /// inward standoff for a grip, a fraction of that arc cancelled for a lift, nothing for a hold that holds by
    /// itself — plus the row's own thrust, in every bond, while MoveUp is non-zero. A program selecting this op
    /// without <see cref="ResolveHold"/> refuses by name, since it applies whatever row that op selected.</summary>
    ApplyHold,
    IntegratePlanarAndVerticalVelocity,
    IntegrateScratchVelocity,
    CommitPose,
    SetVerticalVelocity,
    ScaleVerticalVelocity,
    PlanarImpulse,
    SetState,
    AddState,
    StartTimer,
    Designate,
    Generate,
    Judge,
}
