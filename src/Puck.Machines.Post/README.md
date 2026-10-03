# Puck.Machines.Post

The battery scaffold shared by `Puck.HumbleGamingBrick.Post`,
`Puck.AdvancedGamingBrick.Post`, and `Puck.HumbleGamingDeck.Post`: the pieces of each core's power-on self-test
that carry no machine-specific state, plus the generic pieces (`PostBattery<TContext>`,
`IPostStage<TContext>`, `HashDivergenceReport`) closed by each battery over its
own `PostContext`/snapshot types.

The [project map](../../docs/project-map.md) shows where it sits in the wider
repository; the [generated API reference](../../docs/api) owns complete member
signatures, parameters, return values, and exceptions.

## Core types

| Type | Purpose |
|---|---|
| `PostVerdict` | The outcome class of one stage: `Pass`, `Skip`, `Fail`, `Infra`. |
| `PostTier` | The ordered fast→slow tier (`A`/`B`/`C`) a stage belongs to. |
| `PostStageOutcome` | A verdict plus a one-line detail—what a stage returns. |
| `PostStageResult` | A stage's name, tier, and outcome—one report row. |
| `IPostStage<TContext>` | One battery stage: a name, a tier, and `Run(TContext)`. |
| `PostBattery<TContext>` | Runs an ordered `IPostStage<TContext>` list, isolating each stage's exceptions as `Infra`, into a `PostReport`: every `IsConcurrent` stage at once, then the rest (throughput and allocation measurements) alone on a quiet process; output keeps list order. |
| `PostProgressGuard` | The battery's hang guard; see [The hang guard](#the-hang-guard). |
| `PostReport` | The folded per-stage results, exit code, and rendered table for one battery run. |
| `HashDivergenceReport` | Snapshot-hashes two machines and, on a mismatch, prints the component/offset localization and hex windows—the loop and `DescribeDivergence` stay per-brick. |
| `CommandLineArguments` | `Value(args, name)` looks up a flag's following value; `TryValidateValues` rejects missing values for a known set of flags. |
| `CorpusManifest` | Resolves explicit corpus paths or the pinned cache; `Load(path, cacheRoot)` sets the cache used for both fetch and resolution. The runners expose `--corpus-cache`. |
| `CoreEmbeddingProbe` | Checks synchronous core input, video, audio, state replay and lookahead without host infrastructure. |

The runners locate their committed manifests with
`CorpusManifest.InRepository(projectName)`; the Humble battery resolves its
expectations ledger in the same checkout. The shared runtime locator walks to
`Puck.slnx` from the working directory first, then from the executable
directory, so CI's deterministic compiler source paths do not affect corpus
selection or ledger updates, and a runner built in one checkout and run from
another acts on the one it was run from. A runner with neither directory inside
a checkout throws `DirectoryNotFoundException`.

## Individually pinned corpus files

A manifest entry chooses one of two forms. The archive form uses `Archive`,
`Sha256`, and an optional `Root` directory within the ZIP. The file form uses
`UrlPrefix` and a nonempty `Files` array. Each file names a relative `Path` and
its own `Sha256`. The URL prefix ends in a slash; each path is appended to it.
`Name` and `Version` identify the cache directory in either form:
`corpora/<name>/<version>/`. An optional `Root` sits beneath that directory.
`License`, `Revision`, and `Hardware` describe provenance and applicability.

The file form downloads one file at a time into a temporary sibling, verifies
its complete bytes, and publishes it only after its hash matches. A partial
fetch can resume using files already verified. A missing file makes the corpus
unavailable; an incorrect cached hash raises an explicit error. Fetching and
resolving a cached file corpus both verify every present file. An existing
directory alone never proves that the file inventory is complete. Relative
paths cannot escape the corpus directory, and duplicate paths are rejected.

The archive form retains its existing download, hash-check, extract, and cache
behavior. `Fetch(HttpClient)` accepts a caller-owned transport so tests can
exercise both forms without network access. See the
[Deck CPU guide](../../docs/emulation/hgd/cpu-and-timing.md#battery-and-corpora)
for the large instruction corpus that uses the file form.

## The hang guard

A battery never cancels a stage for taking long, so a loaded or slow host
changes how long a run takes and never its verdict. `PostProgressGuard`
samples the run once a minute. A window counts as progress when any stage
finished in it or the battery process spent at least one processor-second.
A stage that is emulating, hashing or comparing always spends processor time;
a deadlocked stage spends none. After one window without progress, the guard
reports every unfinished stage as `Infra` with a `hung:` detail, and the
runner writes the report and exits 2 with those stages still running. A stage
that spins without finishing is not caught: it keeps spending processor time.

## Not shared
Each battery's `PostContext` (artifacts directory, corpus roots, BIOS image
or console model), `PostMachine`, and stage list stay in the owning project —
they diverge per machine and forcing one shape onto two genuinely different
run contexts would cost more than it saves. Each probe's `DescribeDivergence`
(and, for the Advanced brick, its bus-subregion annotation) stays per-brick for
the same reason and is handed to `HashDivergenceReport` as a delegate.

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
