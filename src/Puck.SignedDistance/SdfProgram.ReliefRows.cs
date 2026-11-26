namespace Puck.SignedDistance;

// A relief's masks travel as data rows directly before it (SdfOp.ReliefRow, SdfReliefMask). The door below is the one
// place a run is decoded; it holds, for every program:
//   1. A row stands only in a run closed by the NoiseDisplace that reads it, and that relief's Material lane counts the
//      run exactly, so a run and its relief cannot be separated by a rewrite without the program refusing.
//   2. A run and its relief share one instruction owner. Since a run is contiguous rows, no ResetPoint, scope edge or
//      instance boundary falls inside it, so the run lies in its relief's segment and field scope too.
//   3. A run is bands each followed by one or more pose and primitive pairs, within SdfReliefMask's caps; each row's
//      lanes already passed the lane contract (RequireReliefRowLanes).
// The analyses and both evaluators read the decoded masks (ReliefMasksAt); every other reader of the stream steps over
// a row, which moves no point, writes no field and takes no owner.
public sealed partial class SdfProgram {
    // Per instruction, the masks decoded from the run before each masked relief; null for a program with none.
    private SdfReliefMask[]?[]? m_reliefMasks;

    /// <summary>Returns the masks of the relief at <paramref name="instructionIndex"/>, decoded once by the program's door
    /// from the rows directly before it.</summary>
    /// <param name="instructionIndex">The instruction's index in <see cref="Instructions"/>.</param>
    /// <returns>The masks; empty for an unmasked relief and for every other instruction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="instructionIndex"/> names no instruction.</exception>
    public IReadOnlyList<SdfReliefMask> ReliefMasksAt(int instructionIndex) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: instructionIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: instructionIndex, other: InstructionRows.Length);

        return (m_reliefMasks?[instructionIndex] ?? []);
    }

    // The door (facts 1 to 3 above). Runs after the lane contract and RequireBalancedFieldScopes.
    private void DecodeReliefRuns(int[] instructionOwners, string paramName) {
        var runStart = -1;

        for (var index = 0; (index < InstructionRows.Length); index++) {
            var instruction = InstructionRows[index];
            var stream = SdfFieldOps.StreamOf(value: instruction.Op);

            if (stream == SdfReliefStreamRole.ReliefRow) {
                if (runStart < 0) { runStart = index; }
                continue;
            }

            var runLength = ((runStart < 0) ? 0 : (index - runStart));

            if (stream == SdfReliefStreamRole.ReliefOwner) {
                var rows = ((int)instruction.Material);

                if (rows != runLength) {
                    throw new ArgumentException(message: $"Relief {index} counts {rows} mask rows in its Material lane, but {runLength} stand directly before it; a relief's count is the length of the run it reads.", paramName: paramName);
                }

                if (rows > 0) {
                    for (var row = runStart; (row < index); row++) {
                        if (instructionOwners[row] != instructionOwners[index]) {
                            throw new ArgumentException(message: $"Relief mask row {row} and the relief {index} that reads it stand in different instruction owners; a run and its relief share one.", paramName: paramName);
                        }
                    }

                    (m_reliefMasks ??= new SdfReliefMask[]?[InstructionRows.Length])[index] = SdfReliefMask.Read(firstIndex: runStart, paramName: paramName, run: InstructionRows.Slice(start: runStart, length: rows));
                }
            } else if (runLength > 0) {
                throw new ArgumentException(message: $"Relief mask rows {runStart}..{(index - 1)} are followed by instruction {index} ({instruction.Op}); a row stands only in a run closed by the NoiseDisplace that reads it.", paramName: paramName);
            }

            runStart = -1;
        }

        if (runStart >= 0) {
            throw new ArgumentException(message: $"Relief mask rows from instruction {runStart} close the stream with no relief after them; a row stands only in a run closed by the NoiseDisplace that reads it.", paramName: paramName);
        }
    }
    // Per instruction, the summed slope of each masked relief's masks (SdfReliefMask.SlopeOf); null when none is masked.
    private float[]? ReliefMaskSlopes() {
        if (m_reliefMasks is not { } masks) { return null; }

        var slopes = new float[masks.Length];

        for (var index = 0; (index < masks.Length); index++) {
            if (masks[index] is { } held) { slopes[index] = SdfReliefMask.SlopeOf(masks: held); }
        }

        return slopes;
    }
}
