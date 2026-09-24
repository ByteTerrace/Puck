using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The position-integration remainders <see cref="WorldBodyTransferState"/> deliberately drops (see its own
/// remarks) because a transfer's destination frame makes them meaningless — captured here instead for a
/// checkpoint restore, which is CONTINUOUS: the restored body keeps advancing in the identical frame it was
/// captured in, so a dropped remainder would round the very next step's swept segment differently and the
/// restored trajectory would diverge from the uninterrupted one by one raw unit within a few ticks.</summary>
/// <param name="PreviousPosition">The position at the top of the most recently completed <see cref="WorldBody.Advance"/> —
/// mirrors <see cref="WorldBody.FixedPreviousPosition"/>, carried here so a restore can set it directly.</param>
/// <param name="PositionRemainderX">The position-rate accumulator's X-axis remainder.</param>
/// <param name="PositionRemainderY">The position-rate accumulator's Y-axis remainder.</param>
/// <param name="PositionRemainderZ">The position-rate accumulator's Z-axis remainder.</param>
/// <param name="RotationRemainderX">The rotation-rate accumulator's X-axis remainder.</param>
/// <param name="RotationRemainderY">The rotation-rate accumulator's Y-axis remainder.</param>
/// <param name="RotationRemainderZ">The rotation-rate accumulator's Z-axis remainder.</param>
/// <param name="VerticalVelocityRemainder">The vertical-velocity-rate accumulator's remainder.</param>
/// <param name="Up">The latched contact-surface up normal — a solver fact ordinarily re-derived by the very next
/// grounded <see cref="WorldBody.Advance"/>, carried here so a checkpoint taken and restored between the same two ticks
/// (before any <see cref="WorldBody.Advance"/> re-derives it) reads identically to the uninterrupted server.</param>
/// <param name="Grounded">The latched grounded state, for the identical reason as <paramref name="Up"/>.</param>
/// <param name="Engaged">The screen-engagement route latch (<see cref="Engaged"/>/<see cref="EngagedIntent"/>) —
/// a <see cref="WorldBody"/> field, not a <see cref="Puck.World.Server.WorldEngagement"/> field, so it belongs
/// here rather than being re-derived by that subsystem's own restore.</param>
/// <param name="EngagedIntent">The intent resolved on the most recent <see cref="WorldBody.Advance"/>, carried alongside
/// <paramref name="Engaged"/>.</param>
/// <param name="OrdinaryAdvanceAdmitted">Whether this body's most recent step advanced under the ordinary
/// (non-continuum) path.</param>
/// <param name="ContinuumConsumedThroughEngineTick">The latest engine-time boundary this body has already
/// consumed from an inbound continuum trajectory, or <see langword="null"/> when none is in flight.</param>
/// <param name="AffectingSubject">The entity index that most recently pushed this body during contact
/// resolution, or <c>-1</c> — reset to <c>-1</c> at the tail of every <see cref="WorldBody.Advance"/>, so this is a
/// one-tick image carried here purely so a checkpoint taken mid-tick-window reads identically on restore.</param>
/// <param name="Frame">The carried rotation from world +Y into <paramref name="Up"/>. It is not safely
/// reconstructible from the two axes at their antipodal point and also carries the body's tangent-frame twist.</param>
/// <param name="UpNeedsReseat">Whether the next usable solved-gravity direction must reseat the body's up axis
/// after a teleport instead of steering continuously toward it.</param>
/// <param name="FieldUpTurnRemainder">The solved-field up-turn rate accumulator's signed remainder.</param>
/// <param name="ContactUpTurnRemainder">The measured-contact-normal turn accumulator's signed remainder.</param>
/// <param name="AsleepSinceTick">The simulation tick at which the body fell asleep, or 0 while awake.</param>
/// <param name="SleepIdleTicks">The consecutive idle engine ticks accumulated toward the authored sleep floor.</param>
/// <param name="LastContactFieldVersion">The contact-field version observed by the body's latest advance.</param>
/// <param name="ContactFieldObservationCurrent">Whether that observation matched the population's field at
/// checkpoint capture. Versions are process-local, so this semantic edge is what a restore preserves.</param>
/// <param name="PlanarFollowerSeeded">Whether the planar dynamics follower has consumed its first target.</param>
/// <param name="VerticalFollowerSeeded">Whether the vertical dynamics follower has consumed its first target.</param>
/// <param name="Tether">The body-local tether continuation state.</param>
/// <param name="HoldIndex">The index into the kit's ordered hold list of the hold this body holds, or <c>-1</c>
/// when nothing holds it.</param>
/// <param name="HoldAnchor">The held surface point, or <see cref="FixedVector3.Zero"/> for a free (or absent)
/// hold.</param>
/// <param name="HoldNormal">The held surface's unit normal, on the same terms as
/// <paramref name="HoldAnchor"/>.</param>
/// <param name="HoldSpendRemainder">The hold spend rate accumulator's signed remainder.</param>
/// <param name="AttitudeUp">The axis the body is drawn standing on, carried so a grip's lean is turned into rather than snapped to.</param>
/// <param name="AttitudeTurnRemainder">The drawn-axis turn accumulator's signed remainder.</param>
/// <param name="AttitudeLeaned">Whether a surface hold has leaned the drawn axis, which decides whether leaving
/// a hold turns the axis back or seats it outright.</param>
/// <param name="Home">The position this body was activated at — the anchor its producer steers against. Not
/// re-derivable after the fact (a teleport never moves it), so a checkpoint carries it.</param>
/// <param name="RigidVelocity">A rigid kit's linear velocity; zero for a locomotion kit.</param>
/// <param name="RigidAngularVelocity">A rigid kit's angular velocity; zero for a locomotion kit.</param>
/// <param name="RigidResting">A rigid kit's resting latch; always <see langword="false"/> for a locomotion kit.</param>
/// <param name="RigidRestingHoldTicks">A rigid kit's elapsed engine ticks under the resting thresholds so far,
/// carried across a checkpoint so a restore does not re-arm the hold window from zero mid-settle.</param>
/// <param name="RigidGroundContacting">A rigid kit's ground-channel restitution edge latch — whether the
/// previous substep already had a walkable contact, so a restore does not read a genuine mid-rest tick as a
/// fresh impact.</param>
/// <param name="RigidObstructionContacting">A rigid kit's obstruction-channel restitution edge latch, on the
/// same terms as <paramref name="RigidGroundContacting"/> but for the last non-walkable (wall) contact.</param>
/// <param name="RigidGroundMissStreak">The ground-channel contact's consecutive-miss run —
/// <see cref="WorldBody.RigidGroundMissStreak"/> — carried so a restore does not grant a fresh grace window a
/// checkpoint interrupted mid-run.</param>
/// <param name="RigidObstructionMissStreak">The obstruction-channel contact's consecutive-miss run, on the same
/// terms as <paramref name="RigidGroundMissStreak"/>.</param>
/// <param name="Carrying">The population index of the body this one is carrying, or <c>-1</c>.</param>
/// <param name="CarriedBy">The population index of the body carrying this one, or <c>-1</c>.</param>
public readonly record struct WorldBodyIntegrationResidue(
    FixedVector3 PreviousPosition,
    long PositionRemainderX,
    long PositionRemainderY,
    long PositionRemainderZ,
    long RotationRemainderX,
    long RotationRemainderY,
    long RotationRemainderZ,
    long VerticalVelocityRemainder,
    FixedVector3 Up,
    bool Grounded,
    bool Engaged,
    PlayerIntent EngagedIntent,
    bool OrdinaryAdvanceAdmitted,
    ulong? ContinuumConsumedThroughEngineTick,
    int AffectingSubject,
    FixedQuaternion Frame,
    bool UpNeedsReseat,
    long FieldUpTurnRemainder,
    long ContactUpTurnRemainder,
    ulong AsleepSinceTick,
    ulong SleepIdleTicks,
    ulong LastContactFieldVersion,
    bool ContactFieldObservationCurrent,
    bool PlanarFollowerSeeded,
    bool VerticalFollowerSeeded,
    WorldBodyTetherResidue Tether,
    int HoldIndex,
    FixedVector3 HoldAnchor,
    FixedVector3 HoldNormal,
    long HoldSpendRemainder,
    FixedVector3 AttitudeUp,
    long AttitudeTurnRemainder,
    bool AttitudeLeaned,
    FixedVector3 Home,
    FixedVector3 RigidVelocity,
    FixedVector3 RigidAngularVelocity,
    bool RigidResting,
    ulong RigidRestingHoldTicks,
    bool RigidGroundContacting,
    bool RigidObstructionContacting,
    int RigidGroundMissStreak,
    int RigidObstructionMissStreak,
    int Carrying,
    int CarriedBy
);
