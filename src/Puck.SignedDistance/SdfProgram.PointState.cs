using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // Whether an instruction opens a fold whose lattice has no edge. Repeat, CellJitter (a cell lattice at zero jitter
    // too) and LogSphere (shells at every scale) never end; a RepeatLimited or WallpaperFold ends only at its limit.
    // The one test the classifier reads.
    private static bool OpensUnboundedFold(SdfInstruction instruction) => instruction.Op switch {
        SdfOp.Repeat or SdfOp.CellJitter or SdfOp.LogSphere => true,
        SdfOp.RepeatLimited => SdfDomainOps.IsUnboundedRepeat(limit: new Vector3(x: instruction.Data1.X, y: instruction.Data1.Y, z: instruction.Data1.Z)),
        SdfOp.WallpaperFold => SdfWallpaperFold.IsUnbounded(limit: new Vector2(x: instruction.Data1.X, y: instruction.Data1.Y)),
        _ => ((SdfOpRoles.Of(op: instruction.Op) == SdfOpRole.Lattice) ? throw new InvalidOperationException(message: $"SDF lattice op {instruction.Op} has no unbounded-limit rule.") : false),
    };
    // A segment (the unit the directory skips, and an instance's compiled part program evaluates alone) starts from the
    // world point. ResetPoint says so where the stream splits at one, but the stream also splits at every instance's first
    // and end instruction (an empty instance included, per SegmentRanges), and a segment that does not begin with a ResetPoint inherits whatever point the
    // segments before it left. A segment the directory or the instance mask can skip passes the state before it along
    // unchanged, so a moved point can reach a later segment through any number of ResetPoints. A stream whose point can
    // be moved when a segment that reads it begins without a ResetPoint refuses by name: the inherited point would depend
    // on which segments ran. The classifier and the segment analysis then start from the world point because the program
    // guarantees it, not because they assume it.
    //
    // The walk keeps one fact: whether, in some history of skips, the point entering the next segment is moved. A segment
    // that begins with ResetPoint leaves it moved only when a point op follows the reset; one that does not leaves it
    // moved when it was moved on entry or a point op follows. A skippable segment (an instance's, or a world segment with a
    // shape, the only kind the directory gives a bound) also leaves whatever entered it. A world segment with no shape is
    // always evaluated.
    private void RequireSegmentsStartAtTheWorldPoint(int[] instructionOwners, string paramName) {
        var moved = false;
        var mover = -1;

        foreach (var (first, last) in SegmentRanges()) {
            var startsWithReset = (m_instructions[first].Op == SdfOp.ResetPoint);
            var firstMover = -1;
            var hasShape = false;
            var readsPoint = false;

            for (var index = first; (index <= last); index++) {
                var op = m_instructions[index].Op;

                hasShape |= (op == SdfOp.ShapeBlend);
                readsPoint |= (op is not (SdfOp.ResetPoint or SdfOp.PushField or SdfOp.PopField));

                if (
                    (firstMover < 0) &&
                    (SdfOpRoles.Of(op: op) is SdfOpRole.Point or SdfOpRole.Lattice)
                ) {
                    firstMover = index;
                }
            }

            if (
                moved &&
                !startsWithReset &&
                (first > 0) &&
                readsPoint
            ) {
                throw new ArgumentException(
                    message: $"Instruction {first} ({m_instructions[first].Op}) begins {((instructionOwners[first] < 0) ? "a world segment" : $"a segment of instance {instructionOwners[first]}")} without a ResetPoint, and instruction {mover} ({m_instructions[mover].Op}) may have moved the point it would inherit. A segment starts from the world point, since it can be culled or compiled apart from its neighbours and a skipped one passes the state before it along, so begin it with a ResetPoint.",
                    paramName: paramName
                );
            }

            var skippable = ((instructionOwners[first] >= 0) || hasShape);
            var leavesMoved = ((firstMover >= 0) || (!startsWithReset && moved));

            if (firstMover >= 0) {
                mover = firstMover;
            }

            moved = (leavesMoved || (skippable && moved));
        }
    }
}
