using System.Globalization;
using System.Text.RegularExpressions;

namespace Puck.Cli.Bench;

/// <summary>Which of the two pinned reference lowerings an instruction was read from. The disassembly's comment
/// marker, its branch mnemonics and its scalar integer-add form all follow from this.</summary>
internal enum ReferenceArchitecture {
    /// <summary>The x86-64 lowering.</summary>
    X64,
    /// <summary>The AArch64 lowering.</summary>
    Arm64,
}
/// <summary>What one decoded instruction does to control flow.</summary>
internal enum ReferenceFlow {
    /// <summary>Falls through to the next instruction.</summary>
    Plain,
    /// <summary>Calls a subroutine and falls through.</summary>
    Call,
    /// <summary>Transfers unconditionally.</summary>
    Jump,
    /// <summary>Transfers when its condition holds and falls through otherwise.</summary>
    Conditional,
    /// <summary>Returns to the caller.</summary>
    Return,
    /// <summary>Transfers through a register, so the disassembly names no target.</summary>
    Indirect,
    /// <summary>Faults. A block of nothing but these is padding or an inline jump table's data words.</summary>
    Trap,
}
/// <summary>One decoded instruction of a reference lowering.</summary>
/// <param name="Address">Its address inside the symbol.</param>
/// <param name="Mnemonic">The mnemonic alone.</param>
/// <param name="Operands">The operands as the disassembler printed them.</param>
/// <param name="Form">The instruction form service is priced by, with absolute addresses folded away.</param>
/// <param name="Referent">The symbol the disassembler named beside a branch or call, when it named one.</param>
internal sealed record ReferenceInstruction(long Address, string Mnemonic, string Operands, string Form, string? Referent);
/// <summary>One symbol's control-flow graph: its basic blocks, the edges between them, the blocks whose transfer
/// the disassembly cannot resolve, the symbols a block tail-calls, and the symbols a block calls.</summary>
/// <param name="Order">Every block leader, ascending.</param>
/// <param name="Blocks">Each block's instructions, keyed by leader.</param>
/// <param name="Edges">Each block's resolved successors, keyed by leader.</param>
/// <param name="Unresolved">The blocks ending in a transfer with no target this graph can name.</param>
/// <param name="Tails">The symbol each block transfers to instead of returning, keyed by leader.</param>
/// <param name="Calls">The symbols each block calls, keyed by leader. A call the disassembly cannot name is
/// recorded as <see cref="ReferenceGraph.IndirectCall"/>.</param>
internal sealed record ReferenceGraph(
    IReadOnlyList<long> Order,
    IReadOnlyDictionary<long, IReadOnlyList<ReferenceInstruction>> Blocks,
    IReadOnlyDictionary<long, IReadOnlyList<long>> Edges,
    IReadOnlySet<long> Unresolved,
    IReadOnlyDictionary<long, string> Tails,
    IReadOnlyDictionary<long, IReadOnlyList<string>> Calls
) {
    /// <summary>The placeholder a call whose target the disassembly does not name is recorded under.</summary>
    public const string IndirectCall = "?";
}
/// <summary>Decodes a reference lowering: the disassembler's text into instructions, and instructions into one
/// symbol's control-flow graph. Every judgement here is a property of the printed lowering, never of the machine
/// the disassembler happened to run on.</summary>
internal static class ReferenceLowering {
    private const string Tab = "\t";

    private static readonly Regex Address = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"(?<![$#\w])0x[0-9a-f]{4,}"
    );
    private static readonly Regex Arm64Conditional = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"^(b\.[a-z]+|cbn?z|tbn?z)$"
    );
    private static readonly Regex Arm64Comment = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"\s+//.*$"
    );
    private static readonly Regex Line = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"^\s*([0-9a-f]+):\s+(.*)$"
    );
    // A transfer into another section leaves the object with a relocation where its displacement will go, and the
    // disassembler prints that relocation, naming the symbol, on the line after the instruction it patches.
    private static readonly Regex Relocation = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"^IMAGE_REL_\w+\s+(\S+)\s*$"
    );
    private static readonly Regex Symbolic = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"\s*<(.*)>\s*$"
    );
    private static readonly Regex TargetAddress = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"^(?:0x)?([0-9a-f]+)\b"
    );
    // A managed throw ends a permitted path: the reference kernels refuse by returning, never by throwing, so the
    // throw's own object construction, stack walk and unwind are a named exclusion rather than a silent zero. A
    // fail-fast ends the process, and is excluded the same way.
    private static readonly Regex Throw = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"Throw|Exception___ctor|RhpRethrow|__ThrowHelper|FailFast"
    );
    private static readonly Regex X64Comment = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"\s+#.*$"
    );
    private static readonly Regex X64Conditional = new(
        options: RegexOptions.CultureInvariant,
        pattern: @"^j(?!mp)[a-z]+$"
    );

    /// <summary>Returns the instruction form service is priced by: the mnemonic and its operands, with the absolute
    /// addresses of a branch, a call or an address materialization folded to zero, because service depends on the
    /// form and not on where the target happens to sit.</summary>
    /// <param name="architecture">The lowering the instruction was read from.</param>
    /// <param name="body">The instruction as the disassembler printed it, comment and symbol name already removed.</param>
    public static string CanonicalForm(ReferenceArchitecture architecture, string body) {
        var separator = body.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: Tab
        );
        var mnemonic = ((separator < 0)
            ? body
            : body[..separator]);
        var operands = ((separator < 0)
            ? string.Empty
            : body[(separator + 1)..]);
        var flow = Classify(
            architecture: architecture,
            mnemonic: mnemonic
        );

        if ((flow is ReferenceFlow.Call or ReferenceFlow.Jump or ReferenceFlow.Conditional) || (mnemonic is "adr" or "adrp")) {
            operands = Address.Replace(
                input: operands,
                replacement: "0"
            );
        }
        return ((mnemonic + Tab) + operands).TrimEnd();
    }
    /// <summary>Returns what one mnemonic does to control flow in the named lowering.</summary>
    /// <param name="architecture">The lowering the mnemonic was read from.</param>
    /// <param name="mnemonic">The mnemonic alone.</param>
    public static ReferenceFlow Classify(ReferenceArchitecture architecture, string mnemonic) {
        if (architecture == ReferenceArchitecture.X64) {
            return mnemonic switch {
                "call" or "callq" => ReferenceFlow.Call,
                "int3" or "ud2" => ReferenceFlow.Trap,
                "jmp" or "jmpq" => ReferenceFlow.Jump,
                "ret" or "retq" => ReferenceFlow.Return,
                _ => (X64Conditional.IsMatch(input: mnemonic)
                    ? ReferenceFlow.Conditional
                    : ReferenceFlow.Plain),
            };
        }
        return mnemonic switch {
            "b" => ReferenceFlow.Jump,
            "bl" or "blr" => ReferenceFlow.Call,
            "br" => ReferenceFlow.Indirect,
            "brk" or "udf" => ReferenceFlow.Trap,
            "ret" => ReferenceFlow.Return,
            _ => (Arm64Conditional.IsMatch(input: mnemonic)
                ? ReferenceFlow.Conditional
                : ReferenceFlow.Plain),
        };
    }
    /// <summary>Returns whether any of the symbols a block calls is a managed throw.</summary>
    /// <param name="names">The symbols the block calls.</param>
    public static bool ContinuesIntoAThrow(IEnumerable<string> names) => names.Any(predicate: static name => (!string.Equals(
        a: name,
        b: ReferenceGraph.IndirectCall,
        comparisonType: StringComparison.Ordinal
    ) && Throw.IsMatch(input: name)));
    /// <summary>Reads the disassembler's text into instructions, or returns an empty list when the symbol's body is
    /// absent from the object.</summary>
    /// <param name="architecture">The lowering the text was printed from.</param>
    /// <param name="disassembly">The disassembler's standard output.</param>
    public static IReadOnlyList<ReferenceInstruction> Decode(ReferenceArchitecture architecture, string disassembly) {
        var comment = ((architecture == ReferenceArchitecture.X64)
            ? X64Comment
            : Arm64Comment);
        var rows = new List<ReferenceInstruction>();

        foreach (var line in disassembly.Split(separator: '\n')) {
            var match = Line.Match(input: line.TrimEnd(trimChar: '\r'));

            if (!match.Success) { continue; }

            var text = match.Groups[2].Value;
            var relocation = Relocation.Match(input: text.Trim());

            // The relocation's symbol is what the transfer before it reaches once the object is linked; until then
            // the disassembler can only annotate that transfer with the offset of its own next instruction.
            if (relocation.Success) {
                if ((rows.Count > 0) && (Classify(
                    architecture: architecture,
                    mnemonic: rows[^1].Mnemonic
                ) is ReferenceFlow.Call or ReferenceFlow.Jump) && (Named(referent: rows[^1].Referent) is null)) {
                    rows[^1] = (rows[^1] with { Referent = relocation.Groups[1].Value });
                }
                continue;
            }

            string? referent = null;
            var symbolic = Symbolic.Match(input: text);

            if (symbolic.Success) {
                referent = symbolic.Groups[1].Value;
                text = text[..symbolic.Index];
            }

            var body = comment.Replace(
                input: text,
                replacement: string.Empty
            ).TrimEnd();

            if (body.Length == 0) { continue; }

            var separator = body.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: Tab
            );

            rows.Add(item: new(
                Address: long.Parse(
                    provider: CultureInfo.InvariantCulture,
                    s: match.Groups[1].Value,
                    style: NumberStyles.HexNumber
                ),
                Form: CanonicalForm(
                    architecture: architecture,
                    body: body
                ),
                Mnemonic: ((separator < 0)
                    ? body
                    : body[..separator]),
                Operands: ((separator < 0)
                    ? string.Empty
                    : body[(separator + 1)..].Trim()),
                Referent: referent
            ));
        }
        return rows;
    }
    /// <summary>Returns the address a direct branch names, or null when its operand is not one.</summary>
    /// <remarks>The target is the last operand: a compare-and-branch or a test-bit-and-branch names the register it
    /// tests, and for the latter the bit, before the address it transfers to.</remarks>
    /// <param name="operands">The branch's operands.</param>
    public static long? DirectTarget(string operands) {
        var text = operands[(operands.LastIndexOf(value: ',') + 1)..].Trim().TrimStart(trimChar: '*').TrimStart(trimChar: '$');
        var match = TargetAddress.Match(input: text);

        return (match.Success
            ? long.Parse(
                provider: CultureInfo.InvariantCulture,
                s: match.Groups[1].Value,
                style: NumberStyles.HexNumber
            )
            : null);
    }
    /// <summary>Returns the symbol a disassembler annotation names, or null when it names an offset inside one: an
    /// offset is a position, not a callable body, and treating it as one would price the wrong code.</summary>
    /// <param name="referent">The annotation, when the disassembler printed one.</param>
    public static string? Named(string? referent) => ((!string.IsNullOrEmpty(value: referent) && !referent.Contains(
        comparisonType: StringComparison.Ordinal,
        value: "+"
    ))
        ? referent
        : null);
    /// <summary>Builds one symbol's control-flow graph from its decoded instructions.</summary>
    /// <param name="architecture">The lowering the instructions were read from.</param>
    /// <param name="rows">The symbol's instructions, ascending by address.</param>
    public static ReferenceGraph Build(ReferenceArchitecture architecture, IReadOnlyList<ReferenceInstruction> rows) {
        if (rows.Count == 0) { throw new ArgumentException(message: "A reference lowering's control-flow graph needs at least one instruction.", paramName: nameof(rows)); }

        var index = rows.Select(selector: static row => row.Address).ToHashSet();
        var leaders = new HashSet<long> { rows[0].Address };

        for (var position = 0; (position < rows.Count); ++position) {
            var row = rows[position];
            var flow = Classify(
                architecture: architecture,
                mnemonic: row.Mnemonic
            );

            if (flow is ReferenceFlow.Conditional or ReferenceFlow.Jump) {
                var target = DirectTarget(operands: row.Operands);

                if ((target is { } address) && index.Contains(item: address)) { leaders.Add(item: address); }
            }
            if ((flow is ReferenceFlow.Conditional or ReferenceFlow.Jump or ReferenceFlow.Return or ReferenceFlow.Indirect or ReferenceFlow.Trap) && ((position + 1) < rows.Count)) {
                leaders.Add(item: rows[(position + 1)].Address);
            }
        }

        var ordered = leaders.Order().ToArray();
        var blocks = new Dictionary<long, IReadOnlyList<ReferenceInstruction>>();
        var calls = new Dictionary<long, IReadOnlyList<string>>();
        var edges = new Dictionary<long, IReadOnlyList<long>>();
        var tails = new Dictionary<long, string>();
        var unresolved = new HashSet<long>();

        for (var position = 0; (position < ordered.Length); ++position) {
            var start = ordered[position];
            var stop = (((position + 1) < ordered.Length)
                ? ordered[(position + 1)]
                : ((long?)null));

            blocks.Add(
                key: start,
                value: [.. rows.Where(predicate: row => ((row.Address >= start) && ((stop is not { } end) || (row.Address < end))))]
            );
        }
        for (var position = 0; (position < ordered.Length); ++position) {
            var start = ordered[position];
            var body = blocks[start];
            var successors = new List<long>();
            var inner = new List<string>();

            foreach (var row in body) {
                if (Classify(
                    architecture: architecture,
                    mnemonic: row.Mnemonic
                ) == ReferenceFlow.Call) {
                    inner.Add(item: (Named(referent: row.Referent) ?? ReferenceGraph.IndirectCall));
                }
            }
            if (inner.Count > 0) {
                calls.Add(
                    key: start,
                    value: inner
                );
            }
            edges.Add(
                key: start,
                value: successors
            );

            var last = body[^1];
            var flow = Classify(
                architecture: architecture,
                mnemonic: last.Mnemonic
            );
            var follow = (((position + 1) < ordered.Length)
                ? ordered[(position + 1)]
                : ((long?)null));

            if (flow is ReferenceFlow.Return or ReferenceFlow.Trap) { continue; }
            if (flow == ReferenceFlow.Indirect) {
                unresolved.Add(item: start);
                continue;
            }
            if (flow is ReferenceFlow.Conditional or ReferenceFlow.Jump) {
                var target = DirectTarget(operands: last.Operands);
                var named = Named(referent: last.Referent);

                // A transfer the disassembler names another symbol for leaves this body, even when its unlinked
                // displacement happens to print as an address inside it; a body's own entry is the one name that
                // stays a branch.
                if ((target is { } address) && blocks.ContainsKey(key: address) && ((named is null) || (address == rows[0].Address))) {
                    successors.Add(item: address);
                } else if (named is { } symbol) {
                    tails.Add(
                        key: start,
                        value: symbol
                    );
                } else {
                    unresolved.Add(item: start);
                }
                if ((flow == ReferenceFlow.Conditional) && (follow is { } next)) { successors.Add(item: next); }
                continue;
            }
            if (follow is { } fallthrough) { successors.Add(item: fallthrough); }
        }
        return new(
            Blocks: blocks,
            Calls: calls,
            Edges: edges,
            Order: ordered,
            Tails: tails,
            Unresolved: unresolved
        );
    }
}
