# Content-search complete reference

This preserves the full command, pattern, engine, and troubleshooting contract.
Read the relevant section when the compact skill routes here.

## Contents

- [Command](#command)
- [Flags](#flags-as-built)
- [Engine semantics](#engine-semantics-that-change-how-you-write-patterns)
- [Syntax extensions](#syntax-extensions-the-reason-to-use-this-engine)
- [Cookbook](#cookbook--real-intents-to-patterns)
- [Translating grep/ripgrep habits](#translating-grepripgrep-habits)
- [Shell escaping](#shell-escaping)
- [Known limits](#known-limits-verified-honest)

Content searches go through the **`search` verb** whenever its clean-checkout
bootstrap is available. A host search facility is a disclosed degraded fallback
only when neither documented .NET command can run. The search verb is a
ripgrep-shaped interface over a
non-backtracking symbolic-derivatives regex engine (credit:
RE# in the repository's `ACKNOWLEDGMENTS.md`, referred to below as "the engine"):
linear-time, leftmost-longest, with intersection, complement, and lookaround.
What stays outside it:

- **Glob** (the tool) for pure filename/path patterns — still correct.
- **symbol-analysis** (`puck references`, `puck declarations`) for semantic C#
  questions (who references a symbol, implementers, dead code, safe rename).
  Text search answers those wrong.

## Command

```text
dotnet src/Puck.Cli/publish/Puck.Cli.dll search <pattern> [path] [options]
```

Run it from the repo root; every path below is repo-relative. If the portable
published assembly is absent, bootstrap from the tracked project:

```text
dotnet run --project src/Puck.Cli/Puck.Cli.csproj -c Release -- search <pattern> [path] [options]
```

Every argument after `search` is the invocation documented below. The rest of
this file writes just `search <pattern>` for short. Never rely exclusively on
the ignored Windows launcher `src/Puck.Cli/publish/puck.exe`. Prefer the
published framework-dependent invocation and one search over per-file loops.
Refresh published output after tool changes with
`dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish`.

## Flags (as built)

```
search <pattern> [path ...]     search contents recursively (default path: cwd)
  -i               case-insensitive (DEFAULT is case-SENSITIVE)
  -F               literal string (escape the pattern; no metacharacters)
  -l               files-with-matches only (WINS over -c when both are passed)
  -c               per-file matching-LINE counts
  -n / -N          line numbers on (default) / off
  -A n             n context lines after
  -B n             n context lines before
  -C n             n context lines before and after
  -g <glob>        include glob, FILES only (repeatable). No '/' -> matches basename;
                   with '/' -> matches the path relative to cwd. ** = any, * = any-but-'/'
  --not <glob>     exclude glob (repeatable). No '/' -> matches a file OR DIRECTORY
                   basename, and a matching directory is pruned; with '/' -> matches
                   the cwd-relative path of a file
  -s               span mode: run over whole-file text, print start-end line ranges
  -M <n>           max results (default 250, 0 = unlimited) — truncation is SILENT
  --files          enumerate the files that would be searched — takes NO pattern;
                   in --files mode EVERY positional is a path
  -q               quiet: exit code only (--files included)
  --               end of options: everything after it is pattern/paths
  -h / --help      usage
```

**A pattern starting with `-`** is parsed as an option, with two outcomes
(verified): an unrecognized one (`->`, `--exit-after-seconds`) fails loudly —
exit 2 `unknown option` — but a pattern that IS a search flag (`-l`, `-i`, `-s`,
…) is silently consumed as that flag, the next positional becomes the pattern,
and the search runs wrong with no diagnostic (`-A`/`-B`/`-C`/`-M`/`-g`
additionally eat the following argument). Either way, put `--` before it:
`search -- '-l'` (`--help` lists it). `-F` alone does NOT help (option parsing
runs first); with `-F` the `--` is still needed: `search -F -- '-Something'`. In
regex mode escaping the dash also works: `'\->'`.

**`--files` takes no pattern.** `search PATTERN --files` treats PATTERN as a
path, which normally does not exist: `search: no such file or directory: PATTERN`
and exit 2. Write `search --files [path ...] [-g ...] [--not ...]`. `--files -q`
is the bare existence probe (verified): no listing, exit 0 if any file would be
searched and 1 if none.

Defaults: the recursive walk skips `.git`, `artifacts`, `bin`, `obj`,
`node_modules`, `publish`, `BenchmarkDotNet.Artifacts`, agent worktrees under
`.claude/worktrees`, and binary files (NUL-byte sniff of the first 4 KiB). The
build-artifact names are pruned because this repo publishes the tool into
`src/Puck.Cli/publish/`, whose generated `.xml` doc files are duplicates of the
sources that would otherwise drain the `-M` cap before the walk reaches
`src/Puck.Maths/`. `.claude/worktrees` is pruned because it holds live duplicate
checkouts of this same repository: their copies answer a tree-wide query as if
they were live consumers, which poisons find-usages and safe-to-delete sweeps
(verified: a repo-root `namespace Puck.Maths` sweep returned 78 worktree files
before the prune and zero after it). Explicitly named paths partly override this
(verified): a named directory IS searched, even `bin`, `publish`, `.git` or
`.claude/worktrees` — though skip-named subdirectories inside it are still
pruned, and `-g`/`--not` still apply — and so is a file named inside one. A named
file bypasses `-g` include globs entirely but still honors `--not` and the binary
sniff, so naming a NUL-containing file (UTF-16, `.spv`, `.dxil`) silently exits 1
with no warning. UTF-8 with permissive fallback; a leading byte-order mark is
stripped before matching. Forward and back slashes both accepted. Colors off.
**Exit codes: 0 matched, 1 no match, 2 usage/pattern error** (the engine's own
parse message is printed verbatim — read it, the wording is precise).

`puck declarations` walks the same tree with the same skip list (verified), so
the two verbs paired on one question — a text sweep plus a declaration inventory
— cover exactly the same files.

**Overlapping paths are searched once (verified):** `search X src src/Puck.Maths`
and `search X . .` enumerate each file a single time, so `-c` counts, `-l`
listings, line output and the `-M` budget all see one entry per file however many
roots cover it. Paths differing only in case are the same file here (Windows).

**Globs (verified):** the syntax is ONLY `**` (any), `*` (any-but-`/`), `?`
(one non-`/`). rg-style brace sets `{cs,md}` and classes `[abc]` are LITERAL
characters: as `-g` they match no file (silent exit 1), as `--not` they exclude
nothing — repeat the flag instead; `-g "*.cs" -g "*.md"` (includes OR
together). Globs are case-sensitive even on Windows, and `-i` does not apply to
them — it only affects the search pattern. `**` also differs from ripgrep: the
`/`s around it stay literal, so `a/**/b` needs at least one intermediate
directory — `src/**/*.cs` silently misses `src/foo.cs`, and `**/*.cs` misses
top-level `.cs` files (exit 0 either way). For an extension filter use the
basename form `-g "*.cs"`; for a whole subtree use `-g "src/**"` or
`-g "src/**.cs"` — both cover every depth.

**Excluding a directory (verified):** the two `--not` forms do different jobs.
The basename form — no `/` anywhere in the glob — matches a DIRECTORY name as
well as a file name, and a matching directory is pruned at any depth, so
`--not Assets` and `--not "*.Post"` drop those whole subtrees wherever they sit.
The path form — any `/` in the glob — is a file filter matched against the
cwd-relative path and anchored at the cwd: `--not "src/Puck.SdfVm/Assets/**"`
excludes those files, while `--not "Assets/**"` matches nothing under
`src/Puck.SdfVm/Assets/`. Directory pruning is `--not`-only: `-g` filters files
in both forms and never selects a subtree (`-g Assets` looks for files NAMED
`Assets` and finds none — write `-g "src/Puck.SdfVm/Assets/**"`). A directory
named as a search ROOT is walked even when `--not` matches it, the same override
the default skip list gets.

## Engine semantics that change how you write patterns

**`_` is ANY CHARACTER, and POSIX classes are not a thing here.** Both bite
hardest when you are building a word-boundary lookaround, which is exactly when
a wrong pattern over-matches silently instead of erroring. `a_b` matches `aXb`
as well as `a_b`, so `(?!_)` does not mean "not followed by an underscore" — it
means "not followed by anything", i.e. end of input. And `[[:alnum:]]` is read
as the literal set `[:alnum]`, so a lookaround built on it silently fails to
exclude what you meant. **The working boundary spelling is an explicit class:**

```sh
# Exactly the token, never a prefix or suffix of a longer one:
puck search '(?<![.A-Za-z0-9_-])world[.]row[.]set(?![.A-Za-z0-9_-])' src
```

Verify any boundary pattern against a control file before trusting a sweep.
On this control:

```
world.row.set
world.row.setx
xworld.row.set
```

the spelling above matches the first line only, while the `[[:alnum:]]` form
matches all three. Note `[.]` rather than `\.` for a literal dot: both work,
but a backslash has to survive your shell and a bracket does not.

**An unescaped `.` is any character, like any regex.** Worth restating beside
the above because a dotted verb or file name (`world.row.set`,
`common.schema.json`) is the common case here, and `world.row.set` happily
matches `world row set`. Bracket every literal dot.

**Matching is leftmost-longest, not greedy/lazy backtracking.** Alternation
order does not decide the winner — the longest match at the leftmost start does.
`a|ab` on `xabx` matches `ab`, not `a`. Write the alternatives in any order; the
engine takes the longest. There is no lazy matching, and the engine does NOT
reliably reject the habit (verified): class-shaped lazies (`\d+?`, `[abc]*?`,
`_*?`) exit 2 with `Resharp does not support lazy loops`, and under `-i`
literal lazies (`a+?`, `a{1,2}?`) join them (folding turns the literal into a
class) — but dot and group lazies (`.*?`, `(ab)+?`) are silently ACCEPTED with
or without `-i`, and literal lazies pass without `-i`; in every accepted case
the `?` is ignored — same language as the greedy form.
Line-mode results are unchanged by that, but in span mode `A.*?B` still matches
leftmost-longest to the LAST `B` and undercounts: `-s -c 'x.*?y'` on `x1y x2y`
gives 1, not 2. Strip the `?`; for shortest-interior intent use a
complement-bounded interior, `A(~(_*B_*))B` — verified:
`<a>(~(_*</a>_*))</a>` yields two matches on `<a>1</a><a>2</a>`.

**Linear-time guaranteed — no catastrophic backtracking.** Big alternations and
nested quantifiers that would blow up a PCRE engine are fine. `(a+)+$`,
50-way `foo|bar|...` unions, `.*x.*y.*z.*` — all run in time linear in the input.
Do not hand-optimize a pattern to dodge backtracking; there is none. The engine
is in fact fastest on exactly the patterns backtracking engines fear. The one
exception is COMPILE time (see Known limits): large counted repetitions like
`a{20000}` or `.{4000,}` stall for seconds-to-forever, silently.

**Matching is UNANCHORED (substring), exactly like grep.** `search cat` hits any
line containing `cat`. This interacts with complement (see the cookbook): a bare
`~(...)` is satisfied by almost any short substring, so a "does NOT contain"
screen must anchor the whole search string — `^(...)$` in line mode,
`\A(...)\z` in span mode.

**Anchors (verified):** `^` and `$` are **line** anchors — start/end of a line —
in BOTH modes: in default line mode the pattern runs per line so they frame that
line; in span mode (`-s`) they still match at every line boundary inside the
file. `\A` and `\z` anchor the start/end of the whole search string — same as
`^`/`$` in line mode, but in span mode they are the only way to anchor the
whole FILE. `\b` word boundary works: `\bcat\b` matches `a cat b`, rejects
`scatter` — but `\b` cannot touch an alternation: `\b(foo|bar)\b`, one-sided
`\b(foo|bar)`, and mixed unions like `\bfoo|bar` are all exit-2 errors
("unconstrained word borders" / "Lookarounds inside union"), and the union
error's suggested rewrite just triggers the word-border error. For whole-word
alternations write `\b` per alternative — `\bfoo\b|\bbar\b` — or intersect:
`\b\w+\b&(foo|bar|baz)` (both verified).

**Span-mode trailing-newline trap (verified):** in span mode the search string
is the raw file text, trailing newline included — so `X\z` matches only files
that do NOT end with a newline. For "file ends with X" write `X(\r?\n)?\z`
(verified on LF, CRLF, and no-EOL files). `\Z` matches before a final `\n` but
NOT before a final `\r\n` — skip it. The whole-file `\A…_*\z` recipes below are
unaffected (`_*` absorbs the newline) and correctly handle empty files.

**CRLF caveat (verified):** line mode strips `\r` before matching, so `^...$`
is CRLF-safe there. Span mode searches the raw file text: `$` matches only
immediately before `\n` (or at end of input), so on CRLF files — ~96% of this
checkout's `.cs`/`.md` — `X$` silently never matches in `-s`, and a
literal-`\n` join (`A\nB`) fails because `\r` intervenes. A single `_` is
exactly one character and won't span `\r\n` either. In `-s`, write `X\r?$` and
`A\r?\nB`, or join with `_*`/`_+` — the cookbook's `_*` recipes are CRLF-safe.
`^` is unaffected.

**No backreferences.** `\1`, named-group backrefs — unsupported (exit 2,
`UndefinedNumberedReference`). Groups `(...)` exist purely for grouping — they
capture nothing and there is no "same text again" operator. When you reached
for a backreference: "the two sides are equal" is inexpressible — fall back to
reading the candidates the verb returns. For most real intents (balanced-ish
tokens, repeated words) an intersection or an explicit alternation replaces it.

**Lookaround is supported at pattern boundaries.** Lookahead `(?=...)` /
`(?!...)` and lookbehind `(?<=...)` / `(?<!...)`, in the normalized form
`(?<=R1)R2(?=R3)` — lookbehind leading, lookahead trailing. `cat(?=s)` matches
`cat` only before `s`; `(?<!s)cat` matches `cat` not preceded by `s` (both
verified). Intersections of such normalized patterns are also supported
(verified): `(?<=A)B(?=C)&(?<=D)E(?=F)` — e.g. `(?<=author).*&.*and.*` finds
text after `author` that contains `and`, and `(?<!s)cat&cat(?!s)` combines two
negative constraints in one pass. Lookaround unions (`(?<=a)b|(?<=c)d`) and
midstream shapes like `a(?=bb)b` are rejected at compile time with exit 2 and a
precise message ("lookarounds are only supported at the start or end of the
pattern"). Nested lookarounds are officially unsupported but only
inconsistently rejected — `(?=(?<=a)b)` compiles and matches correctly while
`(?<=(?=ab)a)b` exits 2 — and a few simple midstream uses are likewise accepted
(`a(?=b)b` verified matching). Treat acceptance of any non-boundary shape as
luck, not support: write the boundary form.

**Verified character-class behavior:** in this build `\d` and `\w` are
ASCII-only (`\d` = `[0-9]`), while their negations `\D`/`\W` exclude the FULL
Unicode class — so a non-ASCII digit or letter (`٣`, `é`) matches NEITHER the
class NOR its negation, and a `\d`/`\w` search silently misses it. For
non-ASCII text use literals or explicit ranges. `\s`/`\S` are Unicode-aware and
complementary (NBSP matches `\s`; `é` matches `\S`). `.` matches any character
except newline. Non-ASCII BMP characters (e.g. the em-dash `—`) are matched by
`.`, `_`, `[^...]`, `\D`, `\W`, and `.*` normally. Astral-plane characters
(emoji, U+10000+) count as TWO `.`/`_` positions — UTF-16 units, not code
points: `a.b` does not match `a😀b`; `a..b` and `a.{2}b` do (verified);
`.*`/`_*` are unaffected. Literal emoji in a pattern match fine, with or
without `-F`, and `-i` folds non-ASCII case (`é` matches `É`).

## Syntax extensions (the reason to use this engine)

- `_` — any character **including newline** (the engine's `[\s\S]`). `.`
  excludes newline; `_` does not.
- `&` — intersection: `A&B` matches where both `A` and `B` match. Chains:
  `.*a.*&.*b.*&.*c.*` requires all three. Binds tighter than `|`.
- `~(...)` — complement: matches text the inner pattern does not. The
  parentheses are MANDATORY — a `~` not followed by `(` is silently dropped,
  never an error (verified): `~foo` matches exactly like `foo`, the opposite
  of the intended "not foo", and `x~y` ≡ `xy`. A literal tilde is `\~`.

**Inside a complement, prefer `_` over `.`** — `~(_*xyz_*)` excludes `xyz`
anywhere in the search string; `~(.*xyz.*)` only excludes it when no newline
intervenes. Irrelevant in line mode (a line has no newlines), decisive in span
mode.

**The `_` gotcha (verified):** `_` is a metacharacter. A literal underscore must
be escaped as `\_`. `a_c` matches `abc`; `a\_c` matches `a_c`. In `-F` literal
mode the verb escapes it for you.

## Cookbook — real intents to patterns

**Reading this file's tables:** `\|` inside a table cell is markdown escaping
for a bare `|` — write the actual pattern with a plain pipe. In a real pattern
`\|` matches a LITERAL pipe character (verified: `foo\|bar` silently
misses `foo`).

| Intent | Pattern |
|---|---|
| contains A **and** B, either order | `.*A.*&.*B.*` (one pass, unanchored — the whole line is the witness substring) |
| contains A but **not** B | `^(.*A.*&~(.*B.*))$` — the `^...$` is REQUIRED; without it the complement is met by a short substring and the line still matches |
| line is exactly A (whole-line) | `^A$` |
| a **block / multiline** span, e.g. an XML doc summary that wraps lines | `-s "<summary>(_*TOKEN_*&~(_*</summary>_*))</summary>"` — `_` crosses newlines; the `&~(...)` confines the match to ONE block so each matching block gets its own range. The naive `<summary>_*TOKEN_*</summary>` is a trap in any multi-block file: leftmost-longest bleeds one span from the file's first opener to its LAST closer, and it matches — even under `-l` — when TOKEN merely sits BETWEEN blocks (both verified). Add `-l` for just the files |
| any of many tokens (no blowup) | `foo\|bar\|baz\|...` — linear time, spell out as many as needed |
| any of many tokens, **whole-word** | `\bfoo\b\|\bbar\b` (`\b` per alternative), or `\b\w+\b&(foo\|bar\|baz)` — never `\b(foo\|bar)\b` (exit 2) |
| files **without** a pattern | `-s -l "\A~(_*TOKEN_*)\z"` — whole-file complement, one pass (verified); or per file check exit code 1 |
| file contains A but **not** B (file-level, not line-level) | `-s -l "\A(_*A_*&~(_*B_*))\z"` |
| literal string with metacharacters | `-F "a.b*c"` |
| token not followed / preceded by another | boundary lookaround: `cat(?!alog)` finds `cat` that is not `catalog`; `(?<!wild)cat` finds `cat` not preceded by `wild` |

### The A-and-B vs A-not-B asymmetry (the #1 trap)

"A and B" works **unanchored**: `.*cat.*&.*dog.*` — because the whole line (no
newline in it) is a single substring that must satisfy both sides. Verified:
on lines `alpha cat`, `beta dog`, `gamma cat dog`, it returns exactly the
`gamma cat dog` line, matching a line-by-line oracle.

"A but not B" does **not** work unanchored. `.*cat.*&~(.*dog.*)` on the line
`gamma cat dog` still matches, because the substring `cat` alone satisfies
`.*cat.*` and does not contain `dog`. Anchor it: `^(.*cat.*&~(.*dog.*))$`
forces the witness to be the entire line, so a line containing `dog` anywhere
is correctly rejected (verified: the anchored form returns only `alpha cat`;
the bare form wrongly also returns `gamma cat dog`). The span-mode analog uses
`\A...\z` instead of `^...$`.

## Translating grep/ripgrep habits

| grep / rg | `puck search` |
|---|---|
| `grep -i` | `-i` |
| `grep -l` / `rg -l` | `-l` |
| `grep -c` | `-c` (matching lines) |
| `grep -A/-B/-C n` | `-A/-B/-C n` |
| `rg -g '*.cs'` / `--glob` | `-g "*.cs"` — but see Globs above: no braces/classes, and `**` needs an intermediate dir |
| `rg -g '!*.md'` (exclude) | `--not "*.md"` |
| `rg -g '!dir/'` (exclude a directory) | `--not "dir"` — the basename form prunes the subtree; `--not "dir/**"` only works when `dir` sits directly under the cwd |
| `grep -rn PATTERN` | `search -M 0 PATTERN` (recursive + line numbers are the default; `-M 0` because the verb otherwise stops at 250, silently) |
| `grep -F` | `-F` |
| `rg -U` / `grep -Pz` multiline, `(?s)` dotall | `-s` (and `_` for any-incl-newline; mind the CRLF caveat for `$` and `\n`) |
| `grep -L` (files without match) | `-s -l "\A~(_*PATTERN_*)\z"` |
| `rg --files` | `--files` |
| `grep -q` | `-q` — stops at the first matching file, as grep does; works with `--files` too |
| `grep -o` (only-matching) | not provided; the verb prints whole lines (line mode) or line ranges (`-s`) |

## Shell escaping

- **PowerShell**: `&` is a PowerShell metacharacter and `$`, `` ` ``, `(`, `)` are
  too. **Always single-quote the pattern** in PowerShell:
  `search '.*A.*&.*B.*' src`. For `$` (the `$` anchor) single quotes are
  mandatory — double quotes let PowerShell eat it.
- **Bash tool**: single-quote as well — `&`, `$`, `~`, `(`, `)` are shell-active.
  `~(...)` at the start of an unquoted word triggers tilde expansion; quote it.
- In both, `\` inside single quotes is passed through literally, which is what the
  engine wants (`\d`, `\_`, `\A`, `\.`).

## Known limits (verified, honest)

- **No backreferences, no lazy matching, no capturing** (hard engine limits);
  no balancing groups or conditionals either. Lazy syntax is only sometimes
  rejected — see the lazy note under Engine semantics before trusting a
  `*?`/`+?` pattern that ran clean.
- **Lookarounds only at pattern boundaries** (normalized `(?<=R1)R2(?=R3)`, or
  intersections of such patterns). Unions fail loudly with exit 2; midstream
  and nested shapes are only inconsistently rejected — a non-boundary pattern
  that compiles is luck, not support (see the lookaround note under Engine
  semantics).
- **Case-sensitive by default** — pass `-i` (pattern only; never applies to
  `-g` globs). A stray case mismatch is the most common "why no results".
- **The default `-M` 250 cap truncates SILENTLY** — no warning, no marker, and
  the exit code stays 0. A "result" is mode-dependent: matching lines in
  default mode (context lines are free), files with `-l`, spans with `-s`. Any
  exhaustive pass (find-all-usages, `-l` inventories, piping to `wc -l`) must
  pass `-M 0`. `-c` and `--files` are never capped, so `-c` is the safe way to
  count matches per file.
- **A nonexistent path is a usage error** (verified) — `search: no such file or
  directory: <path>` on stderr and exit 2, with every bad path named before the
  run gives up. One typo fails the whole run rather than quietly searching the
  paths that did resolve, so a partial result is never mistaken for a complete
  one. When a result still surprises you, confirm the set with
  `search --files <paths>` — it lists exactly what would be searched.
- **An unreadable path is reported, not hidden** (verified). A file held under
  an exclusive lock or denied by ACL drops out of both results and `--files`,
  but writes one `search: cannot read <path>: <reason>` line to stderr; a
  directory that cannot be enumerated reports the same way and its subtree is
  skipped. The search continues and the exit code still reflects only whether
  anything matched, so check stderr on any pass you need to be exhaustive.
  Binary files are dropped SILENTLY by contrast — that is the NUL sniff, not a
  failure.
- **UTF-8 only.** A leading byte-order mark is stripped before matching
  (verified), so `^` anchors the first real character of line 1 and span mode's
  raw text starts there too; no emitted line carries a stray U+FEFF.
  Decoding replaces invalid bytes with U+FFFD, so an accented
  Latin-1/CP1252 literal never matches (ASCII on the same line still does).
  UTF-16 files are full of NUL bytes, so the binary sniff silently drops them —
  even when named explicitly — exit 1, indistinguishable from "no match";
  `--files <file>` printing nothing is the tell. Re-encode such files to UTF-8
  before searching (pwsh 7 `>` already writes UTF-8; Windows PowerShell 5.1 `>`
  and `.reg` exports write UTF-16).
- **Large counted repetitions stall at COMPILE time, silently.** Matching is
  linear, but compile time grows roughly quadratically with `{n}`/`{n,}`/`{n,m}`
  bounds. Counts in the low thousands can take seconds; much larger bounds can
  sit silent past a practical timeout with no output or error. Keep counter
  bounds low; if the verb sits silent before printing, suspect a large counter
  in the pattern.
- The engine builds its DFA lazily and is **not safe under concurrent access to a
  single compiled pattern**; the verb handles this internally (one engine
  instance per worker) and guards each file scan, so a rare engine fault degrades
  to a `search: engine fault on <file>` line on stderr and the search continues
  rather than aborting. If you see that line, the results for that one file may
  be incomplete; re-run that file alone to confirm.
- `-c` counts matching **lines** (same as grep `-c`) — except under `-s`, where
  it counts match occurrences (one per span) instead. `-M` never caps `-c`.
  **`-l` wins over `-c`** when both are passed, as in grep (verified): you get
  bare paths with no `:n` suffix and no diagnostic. `-q` in turn silences both.
- **A 0-byte file counts as one empty line in line mode**: `^$` matches it,
  `-c` reports 1, `-l` lists it — grep reports nothing for a file with no
  lines. A file holding just a newline is one empty line for both tools; only
  the 0-byte case diverges.
- Span mode prints `path:start-end` locators, not the matched text.
