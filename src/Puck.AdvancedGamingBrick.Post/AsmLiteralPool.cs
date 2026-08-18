namespace Puck.AdvancedGamingBrick.Post;

// The literal-pool resolution shared by MicroRoms and OracleProbes' independent private Asm assemblers: each
// LdrConst/LdrLabel left a placeholder PC-relative LDR and a (instr, rd, value-or-label) entry; this dedups the
// resolved 32-bit constants into a pool appended after the code and rewrites each placeholder's PC-relative offset.
internal static class AsmLiteralPool {
    /// <summary>Resolves every pending load against a deduplicated literal pool, rewriting each placeholder
    /// instruction word in <paramref name="code"/> in place.</summary>
    /// <param name="code">The assembled code words; the placeholder word at each load's instruction index is
    /// overwritten.</param>
    /// <param name="loads">Each pending load: the placeholder's index into <paramref name="code"/>, the destination
    /// register, and either an immediate value or a label to resolve through <paramref name="labels"/>.</param>
    /// <param name="labels">Label name to code-word index.</param>
    /// <param name="romBase">The ROM's base address, added to a label's word index (times 4) to form its address.</param>
    /// <param name="poolBase">The pool's starting word index — the code length at the point Finish builds it.</param>
    /// <returns>The deduplicated pool, in first-use order, to append after <paramref name="code"/>.</returns>
    public static List<uint> Resolve(List<uint> code, List<(int Instr, int Rd, uint Value, string? Label)> loads, IReadOnlyDictionary<string, int> labels, uint romBase, int poolBase) {
        var pool = new List<uint>();

        foreach (var (instr, rd, value, label) in loads) {
            var resolved = ((label is null)
                ? value
                : (romBase + (((uint)labels[label]) * 4u)));
            var poolIndex = pool.IndexOf(item: resolved);

            if (poolIndex < 0) {
                poolIndex = pool.Count;
                pool.Add(item: resolved);
            }

            var literalWord = (poolBase + poolIndex);
            var offsetBytes = ((literalWord - (instr + 2)) * 4); // pc = instr*4 + 8

            code[instr] = 0xE59F0000u | (((uint)rd) << 12) | (((uint)offsetBytes) & 0xFFFu);
        }

        return pool;
    }
}
