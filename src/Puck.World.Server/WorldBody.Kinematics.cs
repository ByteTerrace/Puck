using Puck.Maths;
using Puck.Networking;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>What a body's contact phase decided over its life since activation: the step-ups it took and refused and
/// the tallest it took, the ground snaps, the steps that left and met the ground, the current and longest airborne
/// runs, the current and longest runs of driven steps that did not move it, the driven steps that ended against a face
/// too steep to stand on, and the push the latest solve and the deepest solve still applied in their last pass. Every
/// count only rises and every peak only grows, so a reader takes a window by reading at both ends; the current runs and
/// the latest depth say what the latest step left.</summary>
/// <remarks>It is part of the body's motion section (<see cref="BodyMotionSection.Kinematics"/>), so a checkpoint
/// carries it, a refused sweep restores it with the rest of the step, and a rule that reads it
/// (<see cref="WorldRuleFacts.KinematicsPrefix"/>) reads the same value on every run of the same input.</remarks>
/// <param name="StepUps">The step-ups taken.</param>
/// <param name="StepRefusals">The step-ups refused (<see cref="FixedStepUpOutcomes.IsRefusal"/>).</param>
/// <param name="StepRise">The tallest riser a step-up took, in metres along the body's up.</param>
/// <param name="Snaps">The ground snaps taken.</param>
/// <param name="Leaves">The steps that left the ground.</param>
/// <param name="Landings">The steps that met the ground.</param>
/// <param name="AirborneTicks">The engine ticks of the current airborne run; zero while grounded or held on a face by a pull.</param>
/// <param name="AirborneLongest">The longest airborne run, in engine ticks.</param>
/// <param name="StuckTicks">The engine ticks of the current run of driven steps that moved the body less than
/// <see cref="WorldBody.StuckDisplacement"/>.</param>
/// <param name="StuckLongest">The longest such run, in engine ticks.</param>
/// <param name="SlopeRefusals">The driven steps that ended pressed against a face facing up too steeply to stand on.</param>
/// <param name="Penetration">The deepest push, in metres, a contact solve still applied in its last pass
/// (<see cref="ContactResolution.Residual"/>).</param>
/// <param name="Depth">The push, in metres, the latest contact solve still applied in its last pass.</param>
public readonly record struct BodyKinematicsWitness(
    long StepUps,
    long StepRefusals,
    FixedQ4816 StepRise,
    long Snaps,
    long Leaves,
    long Landings,
    ulong AirborneTicks,
    ulong AirborneLongest,
    ulong StuckTicks,
    ulong StuckLongest,
    long SlopeRefusals,
    FixedQ4816 Penetration,
    FixedQ4816 Depth
) {
    /// <summary>Writes a witness into a continuation image.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="witness">The witness.</param>
    public static void Write(WireWriter writer, in BodyKinematicsWitness witness) {
        writer.WriteInt64(value: witness.StepUps);
        writer.WriteInt64(value: witness.StepRefusals);
        writer.WriteFixed(value: witness.StepRise);
        writer.WriteInt64(value: witness.Snaps);
        writer.WriteInt64(value: witness.Leaves);
        writer.WriteInt64(value: witness.Landings);
        writer.WriteUInt64(value: witness.AirborneTicks);
        writer.WriteUInt64(value: witness.AirborneLongest);
        writer.WriteUInt64(value: witness.StuckTicks);
        writer.WriteUInt64(value: witness.StuckLongest);
        writer.WriteInt64(value: witness.SlopeRefusals);
        writer.WriteFixed(value: witness.Penetration);
        writer.WriteFixed(value: witness.Depth);
    }
    /// <summary>Reads a witness from a continuation image, in the order <see cref="Write"/> wrote it.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The witness.</returns>
    public static BodyKinematicsWitness Read(ref WireReader reader) => new(
        StepUps: reader.ReadInt64(),
        StepRefusals: reader.ReadInt64(),
        StepRise: reader.ReadFixed(),
        Snaps: reader.ReadInt64(),
        Leaves: reader.ReadInt64(),
        Landings: reader.ReadInt64(),
        AirborneTicks: reader.ReadUInt64(),
        AirborneLongest: reader.ReadUInt64(),
        StuckTicks: reader.ReadUInt64(),
        StuckLongest: reader.ReadUInt64(),
        SlopeRefusals: reader.ReadInt64(),
        Penetration: reader.ReadFixed(),
        Depth: reader.ReadFixed()
    );

    /// <summary>Reads one witness as a rule's cell holds it, in the kind <see cref="WorldKinematicsVocabulary.KindOf"/>
    /// gives it: a count as the integer itself, a time in seconds or a length in metres as the raw bits of its
    /// <see cref="FixedQ4816"/>.</summary>
    /// <param name="witness">The witness.</param>
    /// <returns>The cell's raw value.</returns>
    public long CellValue(WorldKinematicsWitness witness) => (witness switch {
        WorldKinematicsWitness.StepUps => StepUps,
        WorldKinematicsWitness.StepRefusals => StepRefusals,
        WorldKinematicsWitness.StepRise => StepRise.Value,
        WorldKinematicsWitness.Snaps => Snaps,
        WorldKinematicsWitness.Leaves => Leaves,
        WorldKinematicsWitness.Landings => Landings,
        WorldKinematicsWitness.Airborne => Seconds(ticks: AirborneTicks).Value,
        WorldKinematicsWitness.AirborneLongest => Seconds(ticks: AirborneLongest).Value,
        WorldKinematicsWitness.Stuck => Seconds(ticks: StuckTicks).Value,
        WorldKinematicsWitness.StuckLongest => Seconds(ticks: StuckLongest).Value,
        WorldKinematicsWitness.SlopeRefusals => SlopeRefusals,
        WorldKinematicsWitness.Depth => Depth.Value,
        WorldKinematicsWitness.Penetration => Penetration.Value,
        _ => 0L,
    });
    /// <summary>Describes the witness as <c>body.where</c> and <c>body.motion</c> read it back, each witness by its rule
    /// name.</summary>
    /// <returns>The <c>name=value</c> pairs, a time in seconds and a length in metres.</returns>
    public string Describe() => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
        handler: $"stepUps={StepUps} stepRefusals={StepRefusals} stepRise={((double)StepRise):0.000} snaps={Snaps} leaves={Leaves} landings={Landings} airborneLongest={((double)Seconds(ticks: AirborneLongest)):0.000} stuckLongest={((double)Seconds(ticks: StuckLongest)):0.000} slopeRefusals={SlopeRefusals} penetration={((double)Penetration):0.0000}");

    private static FixedQ4816 Seconds(ulong ticks) => FixedTickConversion.SecondsOf(ratePerSecond: FixedTickConversion.TicksPerSecond, ticks: ticks);
}

public sealed partial class WorldBody {
    /// <summary>Gets the distance, in metres, a driven step moves the body under which it counts as stuck: a millimetre
    /// in a step, a few centimetres a second at the slowest simulation rate.</summary>
    public static FixedQ4816 StuckDisplacement { get; } = FixedQ4816.FromRawBits(value: (1L << (FixedQ4816.FractionBitCount - 10)));
    // The least share of its unit normal a face's rise along up has for a pressed face to count as a slope rather than a
    // wall: a sixteenth, about four degrees off vertical.
    private static readonly FixedQ4816 SlopeFacing = FixedQ4816.FromRawBits(value: (1L << (FixedQ4816.FractionBitCount - 4)));

    private BodyKinematicsWitness m_kinematics;

    /// <summary>Gets what this body's contact phase decided over its life (<see cref="BodyKinematicsWitness"/>).</summary>
    public BodyKinematicsWitness Kinematics => m_kinematics;

    // Whether a step's intent drives the body: either planar move role past the authored idle threshold. The obstruction
    // latch releases on its negation, and the stuck witness counts only driven steps.
    private bool DrivenBy(in Puck.World.Protocol.PlayerIntent intent) => (
        (FixedQ4816.Abs(value: Role(intent: in intent, role: ChannelRole.MoveAdvance)) > ObstructionLatchIdleThreshold) ||
        (FixedQ4816.Abs(value: Role(intent: in intent, role: ChannelRole.MoveStrafe)) > ObstructionLatchIdleThreshold)
    );

    // Folds one resolved contact step into the witness and the process's physics.bodies counts. Runs once per contact
    // phase that resolved (a refused sweep restores the witness with the rest of the step), after the grounded verdict.
    private void WitnessKinematics(in ContactResolution resolution, FixedStepUpOutcome stepOutcome, FixedQ4816 stepRise, bool snapped, bool attemptedStep, in BodyMotionScratch scratch) {
        var witness = m_kinematics;
        var ticks = scratch.StepTicks;
        var wasAirborne = (witness.AirborneTicks > 0UL);
        var driven = DrivenBy(intent: in scratch.Intent);
        var moved = (scratch.NextPosition - m_position);
        var stuck = (driven && ((moved.LengthSquared) <= (StuckDisplacement * StuckDisplacement)));
        var obstruction = resolution.ObstructionNormal;
        var facing = FixedVector3.Dot(left: obstruction, right: scratch.Up);
        var slope = (driven && (obstruction != FixedVector3.Zero) && (facing > SlopeFacing));
        var stepped = (attemptedStep && (stepOutcome == FixedStepUpOutcome.Taken));
        var refused = (attemptedStep && stepOutcome.IsRefusal());
        // A body a pull holds on a face (a climb) is supported by its hold, not airborne.
        var airborne = ((m_grounded || HoldOwnsVerticalChannel) ? 0UL : (witness.AirborneTicks + ticks));
        var stuckRun = (stuck ? (witness.StuckTicks + ticks) : 0UL);

        m_kinematics = witness with {
            StepUps = (witness.StepUps + (stepped ? 1L : 0L)),
            StepRefusals = (witness.StepRefusals + (refused ? 1L : 0L)),
            StepRise = (stepped ? FixedQ4816.Max(x: witness.StepRise, y: stepRise) : witness.StepRise),
            Snaps = (witness.Snaps + (snapped ? 1L : 0L)),
            Leaves = (witness.Leaves + ((!wasAirborne && (airborne > 0UL)) ? 1L : 0L)),
            Landings = (witness.Landings + ((wasAirborne && m_grounded) ? 1L : 0L)),
            AirborneTicks = airborne,
            AirborneLongest = Math.Max(val1: witness.AirborneLongest, val2: airborne),
            StuckTicks = stuckRun,
            StuckLongest = Math.Max(val1: witness.StuckLongest, val2: stuckRun),
            SlopeRefusals = (witness.SlopeRefusals + (slope ? 1L : 0L)),
            Penetration = FixedQ4816.Max(x: witness.Penetration, y: resolution.Residual),
            Depth = resolution.Residual,
        };

        var work = WorldBodyWork.Process;

        if (stepped) {
            work.Add(amount: 1L, kind: WorldBodyWork.StepUps);
            work.Raise(kind: WorldBodyWork.StepRisePeak, value: WorldBodyWork.Micrometres(metres: stepRise));
        }
        if (refused) { work.Add(amount: 1L, kind: WorldBodyWork.StepRefusals); }
        if (snapped) { work.Add(amount: 1L, kind: WorldBodyWork.Snaps); }
        if (m_kinematics.Leaves != witness.Leaves) { work.Add(amount: 1L, kind: WorldBodyWork.Leaves); }
        if (m_kinematics.Landings != witness.Landings) { work.Add(amount: 1L, kind: WorldBodyWork.Landings); }
        if (airborne > 0UL) { work.Raise(kind: WorldBodyWork.AirbornePeak, value: WorldBodyWork.Milliseconds(engineTicks: airborne)); }
        if (stuck) {
            work.Add(amount: 1L, kind: WorldBodyWork.StuckSteps);
            work.Raise(kind: WorldBodyWork.StuckPeak, value: WorldBodyWork.Milliseconds(engineTicks: stuckRun));
        }
        if (slope) { work.Add(amount: 1L, kind: WorldBodyWork.SlopeRefusals); }
        if (resolution.Residual > FixedQ4816.Zero) {
            work.Add(amount: 1L, kind: WorldBodyWork.ResidualSteps);
            work.Raise(kind: WorldBodyWork.PenetrationPeak, value: WorldBodyWork.Micrometres(metres: resolution.Residual));
        }
    }
}
