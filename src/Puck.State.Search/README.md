# Puck.State.Search

Puck.State.Search holds the game-tree search half of `Puck.State`: `ArenaSearch`
runs a set of `ArenaSearchPlan` jobs over one `StateArena`, and
`RuleArenaSearchJudge` binds each job's verdict and score to the compiled rules
`Puck.State.Rules` evaluates. A job restarts whenever its input rows change and
lands its answer as `ArenaSearchWrite`s, which a caller installs through
whatever mutation door it owns.

Four properties hold for every job:

- **A candidate is a scope, not a copy.** `ArenaSearchCandidate` opens a journal
  scope on the arena, the candidate's writes and its judge's writes land inside
  it, and the walk rewinds it on the way back up. A ply below the root is one
  more open scope, so no position is ever copied and a step leaves the arena
  byte-for-byte what it was.
- **A candidate judges what the world would judge.** The judge is
  `RuleEvaluator` over the job's compiled judge rules through an
  `IEffectHost`. Admission (`RuleNeeds.Admit`) is asked once when a job set is
  installed, so a plan whose rules need a facet the host does not serve is
  refused by name before any candidate runs. An arm the firing cannot rewind is
  queued and dropped rather than fired, because the candidate it belongs to is
  rewound.
- **A position is what the job can read.** The transposition key folds the rows
  the plan addresses and the rows the judge reads or writes, so two positions
  differing only outside that reach transpose.
- **Nothing is drawn from the store.** A tree job's playout draws advance a
  SplitMix64 stream seeded by an authored plan field.

Negamax (iterative deepening, alpha-beta, transpositions), UCB1 tree search,
chance nodes (`ArenaSearchChancePlan`), and per-seat max-n scores are all
carried over those scopes, and `ArenaSearchCheckpoint` captures a job mid-
descent by the candidates that opened its scopes rather than by a column
snapshot.

## Documentation

- [State and rules](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state.md) — the row, cell, and rule model a search judges over.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.State.Search.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md) and [commercial licensing](https://github.com/ByteTerrace/Puck/blob/main/LICENSING.md).
