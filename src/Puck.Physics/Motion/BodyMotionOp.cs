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
    ShapePlanarVelocity,
    SnapYawToPlanarIntent,
    ResolveDriveFrame,

    /// <summary>Keeps the hold the body has while its surface is still there and the same face, and otherwise takes
    /// the first hold the kit's ordered list offers. Sets the hold's frame — the tangent plane movement rides and the
    /// up axis the body's attitude and its contact walkable test stand against. Runs after the ordinary frame
    /// operation, whose heading it leaves intact.</summary>
    ResolveHold,
    ShapeDriveVelocity,
    RunActionTriggers,
    ApplyVerticalGravity,
    ApplyVerticalDecay,

    /// <summary>Applies the current hold's vertical law: gravity for a hold gravity keeps, a rate-limited inward
    /// standoff for a grip, a fraction of gravity cancelled for a lift, nothing for a hold that holds by itself.
    /// Replaces <see cref="ApplyVerticalGravity"/> in a program authoring holds; a program selecting both is
    /// refused.</summary>
    ApplyHold,
    /// <summary>While MoveUp is non-zero, drives vertical velocity directly at MoveSpeed and suspends the ballistic
    /// channel. Releasing MoveUp returns vertical ownership to gravity, so authored jump actions and ordinary ground
    /// contact remain coherent in the same program.</summary>
    ApplyVerticalDrive,
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
