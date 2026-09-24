# Puck.State.Rules

Puck.State.Rules compiles authored rules against a state catalog and runs them
over a state arena. It holds the rule compiler, the evaluator, rule groups, the
latch, every state transform, and rule analysis and work budgets.

## Documentation

- [Rules and firing](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/rules.md) — gates, modes, atomic firing, and refusals.
- [State transforms](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/transforms.md) — structured changes such as transfers, shuffles, and rays.
- [Rule groups and turn undo](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/rule-groups.md) — fixpoint and staged groups, and rewinding completed turns.
- [Host and extend the state engine](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/hosting.md) — what a host implements and how it adds its own vocabulary.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.State.Rules.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
