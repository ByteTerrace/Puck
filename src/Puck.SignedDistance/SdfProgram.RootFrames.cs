namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    private readonly RootFrame[]? m_rootFrames;

    internal ReadOnlyMemory<RootFrame> RootFrames => m_rootFrames;

    /// <summary>Gets the point instructions the generic walk can skip in a full, unculled evaluation.</summary>
    public int SharedRootInstructionSavings {
        get {
            var key = 0;
            var saved = 0;

            foreach (var frame in RootFrames.Span) {
                if (frame.Length == 0) { continue; }
                if (frame.Key == key) { saved += frame.Length; }
                key = frame.Key;
            }

            return saved;
        }
    }

    internal readonly record struct RootFrame(int Key, int Length);

    // The admitted tape stays the source for bounds, excerpts and bake keys. A run of segments may share a prefix
    // only when each ResetPoint reproduces the exact same sequence of similarity transforms inside one owner.
    // A skipped segment never has to initialize the frame: the first segment actually read still has its prefix.
    private RootFrame[]? FindRootFrames(int[] instructionOwners, List<(int Start, int End)> ranges) {
        RootFrame[]? frames = null;

        for (var first = 0; (first + 1 < ranges.Count); first++) {
            var (start, last) = ranges[first];
            var owner = instructionOwners[start];
            var length = SharedPrefix(start, last, ranges[first + 1], owner, instructionOwners);

            if (length == 0) { continue; }

            frames ??= new RootFrame[m_instructions.Length];
            var frame = new RootFrame(Key: (start + 1), Length: length);

            frames[start] = frame;

            do {
                frames[ranges[++first].Start] = frame;
            } while ((first + 1 < ranges.Count) &&
                (SharedPrefix(start, (start + length + 1), ranges[first + 1], owner, instructionOwners) == length));
        }

        return frames;
    }

    private int SharedPrefix(int start, int last, (int Start, int End) next, int owner, int[] instructionOwners) {
        if ((instructionOwners[next.Start] != owner) ||
            (m_instructions[start].Op != SdfOp.ResetPoint) ||
            (m_instructions[next.Start].Op != SdfOp.ResetPoint)) { return 0; }

        var limit = Math.Min(val1: (last - start - 1), val2: (next.End - next.Start - 1));
        var length = 0;

        while (length < limit) {
            var left = m_instructions[(start + length + 1)];
            var right = m_instructions[(next.Start + length + 1)];

            if ((left != right) || !IsRootFrameTransform(instruction: left)) { break; }
            length++;
        }

        return length;
    }

    private static bool IsRootFrameTransform(SdfInstruction instruction) => SdfFieldOps.RigidPlanOf(value: instruction.Op) switch {
        SdfRigidPlanAction.Translate or SdfRigidPlanAction.Rotate or SdfRigidPlanAction.TransformDynamic => true,
        SdfRigidPlanAction.IdentityScale => (MathF.Abs(x: instruction.Data0.X) == MathF.Abs(x: instruction.Data0.Y)) &&
            (MathF.Abs(x: instruction.Data0.X) == MathF.Abs(x: instruction.Data0.Z)),
        _ => false,
    };
}
