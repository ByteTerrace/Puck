using Puck.Maths;
using Puck.Networking;
using Puck.Physics;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>The complete continuation of a body-relative move after its launch frame has been resolved.</summary>
public readonly record struct WorldTimedBodyMove(FixedTimedMove Motion, FixedQ4816 StartYaw, ulong ElapsedTicks) {
    internal bool IsActive => (Motion.DurationTicks != 0);

    internal (FixedVector3 Displacement, FixedQ4816 RelativeYaw, ulong Elapsed) SampleStep(ulong stepTicks) {
        var end = ElapsedTicks + Math.Min(stepTicks, Motion.DurationTicks - ElapsedTicks);
        var next = Motion.Sample(end);
        return (next.Displacement - Motion.Sample(ElapsedTicks).Displacement, next.Yaw, end);
    }

    internal static void Write(WireWriter writer, in WorldTimedBodyMove move) {
        writer.WriteBoolean(value: move.IsActive);
        if (!move.IsActive) { return; }

        writer.WriteFixedVector(value: move.Motion.Displacement);
        writer.WriteFixed(value: move.Motion.Yaw);
        writer.WriteFixed(value: move.StartYaw);
        writer.WriteUInt64(value: move.Motion.DurationTicks);
        writer.WriteUInt64(value: move.ElapsedTicks);
        writer.WriteByte(value: (byte)move.Motion.Curve);
    }

    internal static WorldTimedBodyMove Read(ref WireReader reader) {
        if (!reader.ReadBoolean()) { return default; }

        var displacement = reader.ReadFixedVector();
        var yaw = reader.ReadFixed();
        var startYaw = reader.ReadFixed();
        var duration = reader.ReadUInt64();
        var elapsed = reader.ReadUInt64();
        var curve = (BodyMotionCurve)reader.ReadByte();
        if ((elapsed >= duration) || !Enum.IsDefined(curve)) {
            reader.Fail(detail: "invalid timed move continuation", refusal: WireRefusal.PayloadMalformed);
            return default;
        }
        return new(Motion: new(displacement, yaw, duration, curve), StartYaw: startYaw, ElapsedTicks: elapsed);
    }
}

public sealed partial class WorldBody {
    private WorldTimedBodyMove m_timedMove;

    internal bool CanStartTimedMove => (m_rigid is null) && m_bodyMotionProgram.SupportsTimedMove;

    private bool HasTimedYaw => m_timedMove.IsActive && (m_timedMove.Motion.Yaw != FixedQ4816.Zero);

    private static bool TimedYawUsesTransport(FixedVector3 up, FixedVector3 attitudeUp) =>
        (up != FixedVector3.UnitY) && ((attitudeUp == up) || (attitudeUp == FixedVector3.Zero));

    private FixedQuaternion TimedYawFrame(FixedVector3 up, FixedVector3 attitudeUp) =>
        (TimedYawUsesTransport(up, attitudeUp)
            ? m_frame
            : FixedQuaternion.FromTo(from: FixedVector3.UnitY, to: (attitudeUp == FixedVector3.Zero ? up : attitudeUp)));

    internal bool CanPrepareTimedMove(in CompiledBodyInstruction instruction) =>
        TryPrepareTimedMove(in instruction, m_orientation, m_up, m_attitudeUp, out _);

    private bool TryPrepareTimedMove(in CompiledBodyInstruction instruction, FixedQuaternion orientation, FixedVector3 up,
        FixedVector3 attitudeUp, out WorldTimedBodyMove move) {
        move = default;
        if (!CanStartTimedMove) { return false; }

        var right = orientation.Rotate(vector: FixedVector3.UnitX).RejectFrom(unitAxis: up);
        var back = orientation.Rotate(vector: FixedVector3.UnitZ).RejectFrom(unitAxis: up);
        FixedVector3 displacement;
        try {
            displacement = checked(((right * instruction.Direction.X) + (back * instruction.Direction.Z)) * m_scale);
        } catch (OverflowException) {
            return false;
        }

        var startYaw = (TimedYawFrame(up, attitudeUp).Conjugate() * orientation).Yaw();
        move = new(Motion: new(displacement, instruction.Value, instruction.DurationTicks, instruction.Curve), StartYaw: startYaw, ElapsedTicks: 0);
        return true;
    }

    // This is still a proposal. Advance restores the whole move clock and pose when the contact phase refuses it.
    private bool ProposeTimedMove(ref BodyMotionScratch scratch) {
        if (!m_timedMove.IsActive) { return true; }

        var sample = m_timedMove.SampleStep(scratch.StepTicks);
        if (!FixedVector3.TryAdd(scratch.NextPosition, sample.Displacement, out var nextPosition)
            || !FixedVector3.TrySubtract(nextPosition, m_position, out _)) {
            NoteSweepRefusal(refusal: ContactRefusal.UnrepresentableSweep);
            return false;
        }
        scratch.NextPosition = nextPosition;

        if (HasTimedYaw) {
            var relative = FixedQ4816.WrapAngle(radians: sample.RelativeYaw);
            m_yaw = FixedQ4816.WrapAngle(radians: (m_timedMove.StartYaw + relative));
            var rotation = FixedQuaternion.FromAxisAngle(angle: m_yaw, axis: FixedVector3.UnitY);
            scratch.Orientation = TimedYawFrame(scratch.Up, scratch.AttitudeUp) * rotation;
        }
        m_timedMove = ((sample.Elapsed == m_timedMove.Motion.DurationTicks)
            ? default
            : m_timedMove with { ElapsedTicks = sample.Elapsed });
        return true;
    }
}
