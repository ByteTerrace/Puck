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
    ComputeSwimTargetVelocity,
    ShapePlanarVelocity,
    SnapYawToPlanarIntent,
    ResolveVehicleFrame,
    ShapeVehicleVelocity,
    RunActionTriggers,
    ApplyVerticalGravity,
    ApplyVerticalDecay,
    ApplyBuoyancyAndSurface,
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
