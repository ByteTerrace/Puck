namespace Puck.State.Rules;

/// <summary>The one evaluator of a compiled postfix program: the entry a gate side, a binding, an effect source, a
/// decision's score and the compiler's own constant folder all evaluate through, so a folded constant and a live
/// evaluation of the same operation agree bit for bit.</summary>
/// <remarks>A program evaluates as the closure tree its tokens lower to (<see cref="CompiledProgram"/>), built once per
/// kind and kept: each token is a node chosen when the tree is built, so evaluation dispatches on nothing a token
/// carries, resolves no operand again and holds no value stack. Evaluation allocates nothing once the tree is built;
/// a call's arguments sit in the calling node's frame, and a fold reads its family's cells straight off the
/// arena.</remarks>
public static partial class RuleExpressions {
    /// <summary>The most arguments one shared function takes.</summary>
    public const int MaxArguments = 16;

    /// <summary>Evaluates a compiled postfix program for the evaluation a frame names. The answer is the program's
    /// <see cref="Outcome"/>: a number, an absence, or a fault (<see cref="ExpressionFault.Domain"/> or
    /// <see cref="ExpressionFault.Forever"/>). The fault is in the answer and nowhere else: whoever decides what the
    /// evaluation means reads the outcome it was handed.</summary>
    /// <param name="frame">The evaluation in flight.</param>
    /// <param name="program">The program.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <returns>What the program answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The frame names no reader.</exception>
    public static Outcome Evaluate(ref EvaluationFrame frame, CompiledProgram program, CellKind kind) {
        ArgumentNullException.ThrowIfNull(argument: program);

        _ = frame.Reader;

        return Enter(
            frame: ref frame,
            root: program.Root(kind: kind)
        );
    }
    /// <summary>Evaluates a compiled postfix program for a caller that is not a firing and decides what a fault means
    /// where it sits (a flock's affinity, the console's read-back and the browser's), through the program's kept
    /// tree.</summary>
    /// <param name="reader">The evaluation in flight, or <see langword="null"/> to fold constants: a live read
    /// refuses without one.</param>
    /// <param name="program">The program.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <returns>What the program answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    public static Outcome EvaluateDetached(IStateReader? reader, CompiledProgram program, CellKind kind) {
        ArgumentNullException.ThrowIfNull(argument: program);

        var frame = ((reader is null) ? default : new EvaluationFrame(reader: reader));

        return Enter(
            frame: ref frame,
            root: program.Root(kind: kind)
        );
    }
    /// <summary>Evaluates tokens a caller rewrites between evaluations (the constant folder's window, the bounds
    /// enumerator's substituted program), as <see cref="EvaluateDetached(IStateReader?, CompiledProgram, CellKind)"/>
    /// does, through a tree built for this evaluation and not kept.</summary>
    /// <param name="reader">The evaluation in flight, or <see langword="null"/> to fold constants.</param>
    /// <param name="program">The postfix tokens.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <returns>What the program answered.</returns>
    public static Outcome EvaluateDetached(IStateReader? reader, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind) {
        var frame = ((reader is null) ? default : new EvaluationFrame(reader: reader));

        return Enter(
            frame: ref frame,
            root: ProgramClosures.Build(kind: kind, persistent: false, tokens: program)
        );
    }

    // Every evaluation of a program's tree enters here, with a context of its own. An arithmetic exception anywhere in
    // the walk is the evaluation's domain fault, the same answer each nested body's outcome would have carried out.
    private static Outcome Enter(ref EvaluationFrame frame, ProgramNode root) {
        var context = ProgramContext.Start();

        try {
            return context.Close(value: root.Run(context: ref context, frame: ref frame));
        } catch (ArithmeticException) {
            return Outcome.Faulted(why: ExpressionFault.Domain);
        }
    }
}
