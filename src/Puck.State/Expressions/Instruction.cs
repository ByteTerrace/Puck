namespace Puck.State;

/// <summary>One instruction of an <see cref="ExpressionProgram"/>: an <see cref="ExpressionOp"/> and, for the
/// operations that address something the stack cannot carry, the payload that operation needs.</summary>
/// <remarks>An operation's arity, kind signature, cost, and spelling come from
/// <see cref="ExpressionOperators"/> rather than from the instruction, so an operation is described in exactly one
/// place. <see cref="ExpressionOperators.PayloadOf(ExpressionOp)"/> says which payload case an operation requires,
/// and a program carrying any other case is refused when it compiles.</remarks>
/// <param name="Operation">The operation.</param>
/// <param name="Payload">What the operation addresses, or <see langword="null"/> for one that addresses nothing.</param>
public sealed record Instruction(ExpressionOp Operation, InstructionPayload? Payload = null) {
    /// <summary>Creates an instruction pushing a call's argument.</summary>
    /// <param name="index">The argument's position in the call's operand list, counted from zero.</param>
    /// <returns>The instruction.</returns>
    public static Instruction Argument(int index) => new(
        Operation: ExpressionOp.Argument,
        Payload: new InstructionPayload.Argument(Index: index)
    );
    /// <summary>Creates an instruction reading a topology-bound board operation.</summary>
    /// <param name="operation">BoardShift, BoardRay, or BoardImage.</param>
    /// <param name="topology">The discrete topology's name.</param>
    /// <param name="index">The direction's name, or the symmetry element's for BoardImage.</param>
    /// <returns>The instruction.</returns>
    public static Instruction Board(ExpressionOp operation, string topology, string index) => new(
        Operation: operation,
        Payload: new InstructionPayload.Board(
            Index: index,
            Topology: topology
        )
    );
    /// <summary>Creates an instruction calling a subprogram.</summary>
    /// <param name="subprogram">The subprogram's index in <see cref="ExpressionProgram.Subprograms"/>.</param>
    /// <returns>The instruction.</returns>
    public static Instruction Call(int subprogram) => new(
        Operation: ExpressionOp.Call,
        Payload: new InstructionPayload.Call(Subprogram: subprogram)
    );
    /// <summary>Creates an instruction pushing an exact decimal literal.</summary>
    /// <param name="value">The literal.</param>
    /// <returns>The instruction.</returns>
    public static Instruction Constant(decimal value) => new(
        Operation: ExpressionOp.Constant,
        Payload: new InstructionPayload.Constant(Value: value)
    );
    /// <summary>Creates an instruction folding a family through a subprogram.</summary>
    /// <param name="operation">All, Any, Count, or Sum.</param>
    /// <param name="family">The family or keyed row whose members are folded.</param>
    /// <param name="binder">The name the subprogram reads the current member by.</param>
    /// <param name="subprogram">The subprogram's index in <see cref="ExpressionProgram.Subprograms"/>.</param>
    /// <returns>The instruction.</returns>
    public static Instruction Fold(ExpressionOp operation, string family, string binder, int subprogram) => new(
        Operation: operation,
        Payload: new InstructionPayload.Fold(
            Binder: binder,
            Family: family,
            Subprogram: subprogram
        )
    );
    /// <summary>Creates an instruction with no payload.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>The instruction.</returns>
    public static Instruction Of(ExpressionOp operation) => new(Operation: operation);
    /// <summary>Creates an instruction reading a live state cell or reserved channel.</summary>
    /// <param name="name">The state row or reserved-channel name.</param>
    /// <param name="key">The keyed-row cell, or <see langword="null"/>.</param>
    /// <returns>The instruction.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="key"/> is spelled by nothing the
    /// expression spelling can write: empty, or holding a backquote.</exception>
    public static Instruction Operand(string name, string? key = null) => Operand(
        key: StateChannelRef.OfNullable(spelling: key),
        name: StateChannelRef.Parse(spelling: name)
    );
    /// <summary>Creates an instruction reading an already typed state reference.</summary>
    /// <param name="name">The state row, pool field or reserved channel.</param>
    /// <param name="key">The keyed-row cell, or <see langword="null"/>.</param>
    /// <returns>The instruction.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="key"/> is spelled by nothing the
    /// expression spelling can write: empty, or holding a backquote.</exception>
    public static Instruction Operand(StateChannelRef name, StateChannelRef? key = null) {
        ArgumentNullException.ThrowIfNull(argument: name);

        RequireSpelled(parameter: nameof(name), reference: name);
        RequireSpelled(parameter: nameof(key), reference: key);

        return new(
            Operation: ExpressionOp.Operand,
            Payload: new InstructionPayload.State(Key: key, Name: name)
        );
    }

    // Every operand prints and reads back as itself, so a name no spelling can write is refused where the operand is
    // built rather than printed as text that reads back as nothing or as another name.
    private static void RequireSpelled(StateChannelRef? reference, string parameter) {
        if ((reference is not null) && !ExpressionSpelling.IsSpelledName(name: reference.Spelling)) {
            throw new ArgumentException(
                message: $"an operand's name is nonempty and holds no backquote, which '{reference.Spelling}' does not",
                paramName: parameter
            );
        }
    }

    /// <summary>Creates an instruction calling a vector function over two vector operands.</summary>
    /// <param name="operation">Dot, Similarity, or Identical.</param>
    /// <param name="left">The left vector operand.</param>
    /// <param name="right">The right vector operand.</param>
    /// <returns>The instruction.</returns>
    public static Instruction Vector(ExpressionOp operation, VectorOperand left, VectorOperand right) => new(
        Operation: operation,
        Payload: new InstructionPayload.Vector(
            Left: left,
            Right: right
        )
    );
}
/// <summary>What an <see cref="Instruction"/> addresses beyond its operation — the closed set of payloads the
/// expression IR carries.</summary>
[Union]
public abstract record InstructionPayload {
    private InstructionPayload() { }

    /// <summary>One of the operand values the call in flight consumed, read inside a subprogram.</summary>
    /// <param name="Index">The argument's position in the call's operand list, counted from zero.</param>
    public sealed record Argument(int Index) : InstructionPayload;
    /// <summary>A topology and one of its directions or symmetry elements, for a board mask operation.</summary>
    /// <param name="Topology">A discrete topology of <c>state.lattices</c> with at most 64 cells.</param>
    /// <param name="Index">A direction of that topology, or a symmetry element for <see cref="ExpressionOp.BoardImage"/>.</param>
    public sealed record Board(string Topology, string Index) : InstructionPayload;
    /// <summary>A call site into the program's shared subprogram table.</summary>
    /// <param name="Subprogram">The subprogram's index in <see cref="ExpressionProgram.Subprograms"/>.</param>
    public sealed record Call(int Subprogram) : InstructionPayload;
    /// <summary>An exact authored decimal, converted to the destination row's numeric kind at compile time.</summary>
    /// <param name="Value">The exact decimal literal.</param>
    public sealed record Constant(decimal Value) : InstructionPayload;
    /// <summary>A family reduced in one pass by a subprogram evaluated per member.</summary>
    /// <param name="Family">The family or keyed row whose members are folded.</param>
    /// <param name="Binder">The name the subprogram reads the current member by, kept so the fold prints back
    /// in the spelling it was authored in.</param>
    /// <param name="Subprogram">The subprogram's index in <see cref="ExpressionProgram.Subprograms"/>.</param>
    public sealed record Fold(string Family, string Binder, int Subprogram) : InstructionPayload;
    /// <summary>A live state cell or reserved rule channel.</summary>
    /// <param name="Name">The state row or reserved-channel name.</param>
    /// <param name="Key">The keyed-row cell, or <see langword="null"/>.</param>
    public sealed record State(StateChannelRef Name, StateChannelRef? Key = null) : InstructionPayload;
    /// <summary>The two operands of a vector function.</summary>
    /// <param name="Left">The left vector operand.</param>
    /// <param name="Right">The right vector operand.</param>
    public sealed record Vector(VectorOperand Left, VectorOperand Right) : InstructionPayload;
}
/// <summary>One operand to a vector expression call (dot, similarity, identical).</summary>
[Union]
public abstract record VectorOperand {
    private VectorOperand() { }

    /// <summary>A vector state cell operand.</summary>
    /// <param name="Name">The state row name.</param>
    /// <param name="Key">The key or key indirection, or <see langword="null"/>.</param>
    public sealed record Cell(StateChannelRef Name, StateChannelRef? Key = null) : VectorOperand;
    /// <summary>An unlowered authored text embedding literal operand.</summary>
    /// <param name="Text">The text to embed.</param>
    /// <param name="Space">The space name, or <see langword="null"/> for the document's default.</param>
    public sealed record Embed(string Text, string? Space = null) : VectorOperand;
    /// <summary>A base64url encoded vector literal operand.</summary>
    /// <param name="Value">The base64url encoded component bytes.</param>
    public sealed record Literal(string Value) : VectorOperand;
}
/// <summary>Which payload case an <see cref="ExpressionOp"/> requires of its instruction.</summary>
public enum PayloadShape : byte {
    /// <summary>The operation addresses nothing; its instruction carries no payload.</summary>
    None,
    /// <summary><see cref="InstructionPayload.Argument"/>.</summary>
    Argument,
    /// <summary><see cref="InstructionPayload.Board"/>.</summary>
    Board,
    /// <summary><see cref="InstructionPayload.Call"/>.</summary>
    Call,
    /// <summary><see cref="InstructionPayload.Constant"/>.</summary>
    Constant,
    /// <summary><see cref="InstructionPayload.Fold"/>.</summary>
    Fold,
    /// <summary><see cref="InstructionPayload.State"/>.</summary>
    State,
    /// <summary><see cref="InstructionPayload.Vector"/>.</summary>
    Vector,
}
