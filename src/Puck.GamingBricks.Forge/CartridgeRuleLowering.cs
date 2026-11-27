using Puck.State;

namespace Puck.GamingBricks.Forge;

/// <summary>
/// The target-independent half of lowering a rule: the branch structure of a gate's all, any and not, and the control
/// flow of a body's if, repeat and break. Every native compiler walks a document through this one walker and supplies
/// only what its processor spells differently: a label, a jump, a comparison, a counted loop's three moves, and the
/// steps that touch its own hardware.
/// </summary>
/// <remarks>
/// <para>A gate branches to its fail label when it misses and falls through when it holds, so composition costs only
/// jumps: all chains its arms to the one fail label, any tries each arm against its own next label and jumps to the
/// holding join on the first that holds, and not swaps which label its arm misses to.</para>
/// <para>A counted loop writes its index variable with the iteration number: zero before the first test, the count after
/// normal completion, and the iteration that broke when a break leaves it.</para>
/// </remarks>
/// <param name="newLabel">Allocates a fresh, unmarked label.</param>
/// <param name="mark">Marks a label at the current position.</param>
/// <param name="jump">Jumps unconditionally to a label.</param>
/// <param name="compare">Lowers one comparison: branches to the label when it misses, falls through when it holds.</param>
/// <param name="assign">Lowers one <c>set</c> step.</param>
/// <param name="step">Lowers any other step that touches the target's own hardware: every kind but set, if, repeat and
/// break.</param>
/// <param name="repeatEnter">Writes zero to the loop's index variable.</param>
/// <param name="repeatTest">Branches to the label once the index variable has reached the count.</param>
/// <param name="repeatAdvance">Adds one to the index variable.</param>
/// <param name="settle">Runs after each if and repeat closes, for a processor that must place pooled literals between
/// blocks; null for one that needs nothing there.</param>
public sealed class CartridgeRuleLowering(
    Func<int> newLabel,
    Action<int> mark,
    Action<int> jump,
    Action<ActionPredicate.CompareValue, int> compare,
    Action<CartridgeStatement> assign,
    Action<CartridgeStatement> step,
    Action<string> repeatEnter,
    Action<string, int, int> repeatTest,
    Action<string> repeatAdvance,
    Action? settle = null) {
    /// <summary>Lowers a gate: branches to <paramref name="fail"/> when it misses and falls through when it holds. An
    /// absent gate always holds and emits nothing.</summary>
    /// <param name="predicate">The validated gate, or null.</param>
    /// <param name="fail">The label taken when the gate misses.</param>
    public void Gate(ActionPredicate? predicate, int fail) {
        switch (predicate) {
            case null:
                return;
            case ActionPredicate.All all:
                foreach (var inner in all.Predicates) {
                    Gate(
                        fail: fail,
                        predicate: inner
                    );
                }

                return;
            case ActionPredicate.Any any: {
                    var holds = newLabel();

                    foreach (var inner in any.Predicates) {
                        var next = newLabel();

                        Gate(
                            fail: next,
                            predicate: inner
                        );
                        jump(obj: holds);
                        mark(obj: next);
                    }

                    jump(obj: fail);
                    mark(obj: holds);

                    return;
                }
            case ActionPredicate.Not not: {
                    // The inner gate branches away when it MISSES, which is exactly when this one holds.
                    var holds = newLabel();

                    Gate(
                        fail: holds,
                        predicate: not.Predicate
                    );
                    jump(obj: fail);
                    mark(obj: holds);

                    return;
                }
            default:
                compare(
                    arg1: ((ActionPredicate.CompareValue)predicate),
                    arg2: fail
                );

                return;
        }
    }
    /// <summary>Lowers each rule of a document in declaration order: its gate, then its body.</summary>
    /// <param name="rules">The validated rules.</param>
    public void Rules(IEnumerable<CartridgeRule> rules) {
        ArgumentNullException.ThrowIfNull(argument: rules);

        foreach (var rule in rules) {
            var end = newLabel();

            Gate(
                fail: end,
                predicate: rule.When
            );
            Statements(
                breakLabel: -1,
                statements: rule.Body
            );
            mark(obj: end);
            settle?.Invoke();
        }
    }
    /// <summary>Lowers a body's steps in order.</summary>
    /// <param name="statements">The validated steps.</param>
    /// <param name="breakLabel">The enclosing loop's exit, or -1 outside any loop; validation has already refused a
    /// stray break.</param>
    public void Statements(CartridgeStatement[] statements, int breakLabel) {
        ArgumentNullException.ThrowIfNull(argument: statements);

        foreach (var statement in statements) {
            switch (statement.Kind) {
                case "set":
                    assign(obj: statement);

                    break;
                case "if": {
                        var otherwise = newLabel();

                        Gate(
                            fail: otherwise,
                            predicate: statement.When!
                        );
                        Statements(
                            breakLabel: breakLabel,
                            statements: statement.Then!
                        );
                        if (statement.Else is { } alternative) {
                            var joined = newLabel();

                            jump(obj: joined);
                            mark(obj: otherwise);
                            Statements(
                                breakLabel: breakLabel,
                                statements: alternative
                            );
                            mark(obj: joined);
                        } else {
                            mark(obj: otherwise);
                        }

                        settle?.Invoke();

                        break;
                    }
                case "repeat": {
                        var top = newLabel();
                        var exit = newLabel();
                        var index = statement.Index!;

                        repeatEnter(obj: index);
                        mark(obj: top);
                        repeatTest(
                            arg1: index,
                            arg2: statement.Count!.Value,
                            arg3: exit
                        );
                        Statements(
                            breakLabel: exit,
                            statements: statement.Body!
                        );
                        repeatAdvance(obj: index);
                        jump(obj: top);
                        mark(obj: exit);
                        settle?.Invoke();

                        break;
                    }
                case "break":
                    jump(obj: breakLabel);

                    break;
                default:
                    step(obj: statement);

                    break;
            }
        }
    }
}
