# Strings, Text & Searching — .NET 10 Performance Reference

**TL;DR for this domain.** Searching is a solved primitive: funnel every "find char/byte/substring" through `SearchValues` / `IndexOfAny*` / `ContainsAny*` and it inherits each release's growing algorithm repertoire for free; hand-rolled scan loops inherit nothing. The regex optimizer now converts what you naturally write into what an expert would write (auto-atomicity, dead-construct elimination, anchor exploitation) — most regex micro-syntax folklore is dead, but timeouts on untrusted input are not. UTF-8 is a first-class parsing citizen (`IUtf8SpanParsable` on `Guid`/`Version`/`Char`/`Rune`): parse bytes directly, never transcode to UTF-16 first. The dominant optimization shape everywhere is *work elimination* — skip the index you never read, the capture nobody observes, the string you never mutate, the allocation the span version avoids.

## Contents

1. [Find chars/bytes/substrings: SearchValues & IndexOf family](#1-find-charsbytessubstrings-searchvalues--indexof-family)
2. [Containment checks: ContainsAny\* over IndexOfAny\* >= 0](#2-containment-checks-containsany-over-indexofany--0)
3. [Search from generic code: comparer-accepting MemoryExtensions](#3-search-from-generic-code-comparer-accepting-memoryextensions)
4. [Regex: pattern authoring — what the optimizer does for you now](#4-regex-pattern-authoring--what-the-optimizer-does-for-you-now)
5. [Regex: call the right API (CA1874/CA1875)](#5-regex-call-the-right-api-ca1874ca1875)
6. [Validate / encode / decode text](#6-validate--encode--decode-text)
7. [Parse primitives from UTF-8 (IUtf8SpanParsable)](#7-parse-primitives-from-utf-8-iutf8spanparsable)
8. [Format primitives & interpolated strings](#8-format-primitives--interpolated-strings)
9. [Tokenize without allocating: span-based Split](#9-tokenize-without-allocating-span-based-split)
10. [Folklore to delete](#folklore-to-delete)

---

## 1. Find chars/bytes/substrings: SearchValues & IndexOf family

The core rule: cache `SearchValues` instances in `static readonly` fields and route all multi-target searches through them. The internal strategy selection (chosen at `SearchValues.Create` time) keeps growing; cached instances pick up new algorithms on every runtime upgrade automatically.

### SearchValues<byte>: nibble-shuffle for 4–5 non-contiguous ASCII targets
- **What:** dotnet/runtime#106900. An existing ASCII shuffle specialization (previously >5 targets only) now covers 4–5 targets when each target byte's low nibble is unique — one shuffle + one equality check per input vector instead of four per-target vector comparisons.
- **Mechanism:** algorithm selection at `Create` time; table-lookup shuffle replaces N comparisons in the hot vectorized loop.
- **Magnitude:** `IndexOfAny` with needle `"\0\r&<"u8` over a large corpus: 3.704 ms → 2.668 ms (~28%).
- **Adoption:** FREE if already on `SearchValues<byte>`; PATTERN if hand-rolling.
- **Caveat:** requires all-ASCII targets with unique low nibbles; SIMD hardware.

### SearchValues<char>: AVX512 probabilistic-map (Bloom filter) tightening
- **What:** dotnet/runtime#107798. The vectorized probabilistic-map fallback (bitmap keyed per byte of the char; any bit clear → definitely absent) uses better shuffle/shift/permute instruction selection on AVX512.
- **Magnitude:** `IndexOfAny` over 10k chars, needle `"ßäöüÄÖÜ"`: 437.7 ns → 404.7 ns (~8%).
- **Adoption:** FREE. **Guidance:** non-ASCII char sets still get real vectorized treatment — don't special-case them yourself. **Caveat:** AVX512-only win.

### SearchValues<string> (Teddy multi-substring): instruction fusion
- **What:** dotnet/runtime#107819 (AVX512: `PermuteVar8x64x2`+`AlignRight` → single `PermuteVar64x8x2`), #118110 (Arm64: `ExtractNarrowingSaturateUpper` → cheaper `UnzipEven`).
- **Adoption:** FREE. **Guidance:** `SearchValues.Create(["a","b"], StringComparison...)` is the primitive for "find any of these strings"; `Regex` alternations like `(?i)hello|world` (Compiled/source-generated) use it under the covers. **Caveat:** architecture-specific micro-wins.

### SearchValues<string> single needle: cheaper/skipped verification
- **What:** dotnet/runtime#108368 extends "false positives impossible by construction" (skip candidate verification) from case-sensitive to some case-insensitive uses; #108365 specializes needles up to 16 chars (was 8) and precomputes more for faster verification.
- **Magnitude:** flows into `Regex.Count` (`IgnoreCase | Compiled`) over a big corpus: "the" 9.881 → 7.799 ms; "something" 2.466 → 2.027 ms.
- **Adoption:** FREE (also via `Regex`, which uses `SearchValues<string>` for literal-prefix finding).
- **Guidance:** for repeated searches — now including case-insensitive — `SearchValues<string>` beats `IndexOf(string, StringComparison)` because it amortizes up-front analysis.

### SearchValues<string> single needle: packed ASCII variant (2x vector density)
- **What:** dotnet/runtime#118108. Packed variant drops each char's upper zero byte for common cases like ASCII, fitting twice the characters per vector.
- **Magnitude:** `ContainsAny` with one `OrdinalIgnoreCase` needle over a ~120k-char near-miss haystack: 58.41 µs → 16.32 µs (~3.6x). One of the biggest searching wins in the release.
- **Adoption:** FREE.

### C# 14 first-class spans: MemoryExtensions directly on string
- **What:** C# 14 span conversions surface many `MemoryExtensions` methods directly on `string` without `.AsSpan()`.
- **Adoption:** PATTERN (C# 14). Call span helpers on strings directly; fewer `.AsSpan()` incantations.

---

## 2. Containment checks: ContainsAny\* over IndexOfAny\* >= 0

- **What:** dotnet/runtime#112065 — the regex source generator itself switched fixed-repeater validation (e.g. `[0-9a-f]{32}`) from `slice.IndexOfAnyExcept(...) >= 0` to `slice.ContainsAnyExcept(...)`.
- **Mechanism:** `IndexOfAny*` must compute the exact index of the hit (extra instructions); `ContainsAny*` answers yes/no. Comparing `>= 0` throws the index away.
- **Adoption:** FREE for source-generated regexes; PATTERN in your own code:

```csharp
// Before:
if (span.IndexOfAnyExcept(validChars) >= 0) return false;
// After:
if (span.ContainsAnyExcept(validChars)) return false;
```

- **Caveat:** only when the index is genuinely unused.

---

## 3. Search from generic code: comparer-accepting MemoryExtensions

- **What:** dotnet/runtime#110197 adds ~30 `MemoryExtensions` overloads (`IndexOf`, `Contains`, `Count`, …) WITHOUT the `IEquatable<T>`/`IComparable<T>` constraint, taking optional `IEqualityComparer<T>?`/`IComparer<T>`. Null or `EqualityComparer<T>.Default` falls through to the same vectorized logic. #112951 adds `CountAny` and `ReplaceAny`. LINQ's `Enumerable.Contains` now delegates to vectorized `MemoryExtensions.Contains<T>` when the source is span-able (`T[]`, `List<T>`).
- **Mechanism:** removes the *type-system* blocker — generic code with unconstrained `T` previously couldn't reach the vectorized span helpers at all.
- **Magnitude:** `Enumerable.Contains(int.MaxValue, EqualityComparer<int>.Default)` over 1M ints: 213.94 µs → 67.86 µs (~3.2x).
- **Adoption:** FREE for LINQ callers; new API/PATTERN for generic library code.

```csharp
// Before (unconstrained T — manual loop was the only option):
static bool Has<T>(ReadOnlySpan<T> span, T value, IEqualityComparer<T>? cmp)
{
    cmp ??= EqualityComparer<T>.Default;
    foreach (T item in span) if (cmp.Equals(item, value)) return true;
    return false;
}
// After (.NET 10 — vectorized when comparer is default):
static bool Has<T>(ReadOnlySpan<T> span, T value, IEqualityComparer<T>? cmp) =>
    span.Contains(value, cmp);
```

- **Caveat:** a custom (non-default) comparer necessarily disables vectorized comparison; the win is the default path.

---

## 4. Regex: pattern authoring — what the optimizer does for you now

Background: the backtracking engines (interpreter, `RegexOptions.Compiled`, `[GeneratedRegex]`) all share one node-tree optimizer. Since .NET 5 it auto-converts provably-safe loops to atomic ("auto-atomicity": `a*b` → `(?>a*)b` when disjoint). .NET 10 widens the proofs dramatically. All items below are FREE on all three backtracking engines. Verify what the optimizer did by inspecting the source generator's emitted C#/XML comments ("greedily" vs "atomically").

Of ~20,000 real-world nuget-sourced patterns, only ~100 hand-write an atomic group, yet on .NET 10 over 70% of those patterns get at least one construct auto-upgraded to atomic.

### Auto-atomicity learns Unicode categories
- **What:** dotnet/runtime#117869 — disjointness proofs now cover category-based classes (`\d`, `\w`, `\p{...}`), not just explicit chars/ranges. `\w*\p{Sm}`: `\w` is eight Unicode categories, none `Sm`, so the loop goes atomic.
- **Mechanism:** atomic loops carry no backtracking state; a greedy loop that matched N chars can force N re-evaluations of everything after it. Disjointness (loop's last element can't match what follows) makes the transform behavior-preserving.
- **Magnitude:** `\s+\S+` (Compiled) over 100 spaces: 183.31 → 69.23 ns (~2.6x).
- **Caveat:** overlapping sets (`[abc]+[cef]`) must stay backtrackable — the proof correctly fails there.

### Word-boundary atomicity generalized to any subset of \w
- **What:** dotnet/runtime#117892 — previously only `\w+\b`/`\d+\b`; now any loop over a provable *subset* of `\w` before `\b` (e.g. `[A-Za-z]+\b`) goes atomic.
- **Mechanism:** `\b` guarantees the wordness flips, so backtracking into the loop is fruitless.
- **Magnitude:** `^[A-Za-z]+\b` (Compiled) on a 35-char near-miss: 116.57 → 21.74 ns (~5.4x).

### Multi-character loop bodies (groups/alternations) can go atomic
- **What:** dotnet/runtime#117943 — auto-atomicity was single-char-loops only; now loops like `([a-z]+ )+` qualify under a stricter three-way proof: (1) loop *end* disjoint from what follows, (2) loop *start* disjoint from what follows (counter-example: `([a-z][0-9])+a1` on `"b2a1"`), (3) loop start and end disjoint from each other, since the "next thing" may be another iteration (counter-example: `^(a|ab)+$` on `"aba"`).
- **Magnitude:** `([a-z]+ )+[A-Z]` (Compiled) over the full Twain corpus: 573.4 → 504.6 ms (~12%); >7% of ~20,000 real-world patterns positively impacted by this one PR.
- **Guidance:** nested-quantifier landmines `(x+y)+z` are now frequently defused automatically — but only where disjointness is provable, so **keep `Regex` timeouts for untrusted patterns/input**.
- **Caveat:** overlapping alternation branches inside the loop (`(a|ab)+`) block it by necessity.

### Boundaries stop forcing conservative look-past
- **What:** dotnet/runtime#118191 — `CanBeMadeAtomic` no longer walks past `\b`/`\B` the way it must for genuinely nullable constructs (`b*` in `a*b*\w`): a boundary, though zero-width, *guarantees* wordness flips, which is itself a usable disjointness fact.
- **Adoption:** FREE; more boundary-adjacent patterns get atomicity with no benchmark of its own.

### Useless captures inside negative lookarounds eliminated
- **What:** dotnet/runtime#118084 — capture groups inside `(?!...)`/`(?<!...)` not referenced by a backreference *within the same lookaround* are removed. Captures can never escape a negative lookaround, so the bookkeeping is pure waste.
- **Adoption:** FREE — but PATTERN advice stands everywhere else: use `(?:...)` or `RegexOptions.ExplicitCapture` for grouping-only intent, because captures anywhere else must remain observable via `Match.Groups` and cannot be auto-elided.
- **Caveat:** internal backreference use (e.g. `^(?!(ab)\1cd)...`) keeps the capture.

### Redundant zero-width constructs removed
- **What:** #118091: positive lookaround around zero-width → unwrapped (`(?=$)` → `$`). #118079/#118111: loops around zero-width assertions collapsed (`\b?` is a nop and disappears; `(?=abc)*`-style loops reduce to at most one evaluation). #118083: adjacent duplicate boundaries condensed (`\b\b` ≡ `\b{2}` ≡ `\b`).
- **Mechanism:** zero-width assertions are deterministic at a fixed position; re-evaluating one can never change the answer.
- **Magnitude:** degenerate `(?=.*\bTwain\b.*\bConnecticut\b)*.*Mark` (Compiled): 3,226 ms → 6.6 ms (~488x). Tool/template-generated patterns are the realistic beneficiaries.
- **Caveat:** loop-around-assertion collapse requires **no capture groups inside** — .NET records all iterations' captures (`Group.Captures`), so lowering the bound would be observable.

### Boundary checks halved against surrounding context
- **What:** dotnet/runtime#118105 — for `\b\w+\b`, instead of two full `IsBoundary` calls (each testing both neighbors), the compiler/source generator emit `IsPreWordCharBoundary` before the loop and `IsPostWordCharBoundary` after, each doing half the checks because the adjacent `\w+` already proved the other side.
- **Magnitude:** `\ba\b` (Compiled | IgnoreCase) over Twain: 20.58 → 19.25 ms (~6%); adds up across many boundary tests.
- **Caveat:** per TR18, boundary-word-char is `\w` plus two zero-width chars, so the remaining half must stay.

### Negated-set alternation merging: `(?:.|\n)` collapses to one always-true set
- **What:** dotnet/runtime#118109 — alternation coalescing (already `a|e|i|o|u` → `[aeiou]`) now merges *negated* sets: `(?:.|\n)` / `\n|[^\n]` become a single constant-true set check instead of a two-branch alternation.
- **Magnitude:** C-comment pattern `/\*(?:.|\n)*?\*/` (Compiled): 344.80 → 93.59 ns (~3.7x; also aided by the #118373 fix below).
- **Guidance:** `RegexOptions.Singleline` remains the most direct "any char", but the `(?:.|\n)`/`[\s\S]` idioms no longer carry a penalty.

### Compiled-engine bug fix: lazy-loop-over-any-set emitted the wrong IndexOf
- **What:** dotnet/runtime#118373 (one-word fix) — the Compiled engine's fast-forward for a lazy any-set loop (search for what *follows*, e.g. `IndexOf("*/")`) was handed the wrong node, emitting the equivalent of `IndexOfAnyInRange((char)0, '￿')` — matches every position, degrading the SIMD skip into per-position work. Functionally correct, silently slow.
- **Adoption:** FREE. Bug was Compiled-only (source generator unaffected). Meta-lesson: investigate benchmark numbers that don't match expectations.

### Empty alternation branches canonicalized to `?`/`??`
- **What:** dotnet/runtime#118087 — `X|` → `X?`, `|X` → `X??` (order preserved). Empty branches commonly arise from other transforms: `\r\n|\r` → prefix-factored `\r(?:\n|)` → `\r\n?` → auto-atomicized `\r(?>\n?)`.
- **Magnitude:** `ab|a` (Compiled) over Twain: 23.35 → 18.73 ms (~20%).
- **Guidance:** write the natural alternation; no hand-rewriting to `\r\n?` needed.

### Leading positive lookaheads inform the find phase; anchors factored from alternations
- **What:** dotnet/runtime#112107 — `TryFindNextPossibleStartingPosition` now explores positive lookaheads at pattern start: `(?=^)hello` used to vector-search the whole input for "hello" repeatedly; now it emits `if (pos == 0) ...` — one candidate ever. Same PR factors shared anchors out of alternation branches: `^abc|^abd` → `^ab[cd]` (previously anchors blocked prefix factoring entirely).
- **Magnitude:** `(?=^)hello` (Compiled) over Twain: 2,383,785 ns → 17.43 ns (deliberately tantalizing micro-benchmark; the mechanism — anchors collapse the candidate set — generalizes).

---

## 5. Regex: call the right API (CA1874/CA1875)

- **What:** analyzers in the .NET 10 SDK (dotnet/roslyn-analyzers#7547), both with fixers. CA1874: `Regex.Match(...).Success` → `Regex.IsMatch(...)`. CA1875: `Regex.Matches(...).Count` → `Regex.Count(...)`.
- **Mechanism:** `Match` allocates a `Match` + supporting structures on success and computes full extents/captures; `IsMatch` reuses an internal `Match` and — especially under `RegexOptions.NonBacktracking` — exploits pay-for-play: *whether* a match exists is cheaper than *where*, which is cheaper than *all captures*. `Matches(...).Count` allocates every `Match` just to count.
- **Magnitude:** `\b\w+\b` (NonBacktracking) over Twain: `Matches(...).Count` 680.4 ms / ~665.5 MB alloc vs `Count(...)` 219.0 ms / 0 B (~3.1x, alloc-free).
- **Adoption:** PATTERN (call-site change; analyzers automate discovery).

```csharp
// Before:
if (regex.Match(input).Success) { ... }
int n = regex.Matches(input).Count;
// After:
if (regex.IsMatch(input)) { ... }
int n = regex.Count(input);
```

- **Caveat:** biggest wins on NonBacktracking, but all engines save the allocations.

---

## 6. Validate / encode / decode text

The shared shape: an encoder/validator's hot path *is* a search ("find the first char needing attention, bulk-copy the literal run"). Delegate the search to `SearchValues`/`IndexOfAny*`; allocate output only when a change is actually needed.

### string.Normalize argument validation vectorized
- **What:** dotnet/runtime#110574 — char-by-char surrogate scan replaced with `IndexOfAnyInRange`.
- **Magnitude:** ~104.93 → 88.94 ns (~15%) on mostly-ASCII input. **Adoption:** FREE.

### HttpUtility.UrlDecode: vectorized scan + no-op returns the input
- **What:** dotnet/runtime#110478 — vectorized `IndexOfAnyInRange` scan, and when nothing needs decoding, returns the original string instead of allocating a copy.
- **Magnitude:** 59.42 → 54.26 ns (~9%) with an encoded tail; the alloc-free path helps the common fully-literal case more. **Adoption:** FREE.
- **Guidance:** copy the "scan first, allocate only if a change is needed" pattern in your own decode/escape routines.

### System.Text.Encodings.Web encoders use SearchValues
- **What:** dotnet/runtime#114494 — `OptimizedInboxTextEncoder` (core of `JavaScriptEncoder`, `HtmlEncoder`, `UrlEncoder`) now uses `SearchValues` for the find step. **Adoption:** FREE.

### Convert: UTF-8 hex overloads
- **What:** dotnet/runtime#117965 adds `byte`-based (UTF-8) overloads of `Convert.FromHexString` / `TryToHexStringLower` and friends (previously UTF-16 only). **Adoption:** new API.
- **Guidance:** hex to/from UTF-8 buffers no longer requires transcoding through `string`/`char`. (Related: on Wasm, the workhorse methods behind `Convert` hex/base64 routines like `FromBase64String` gained `PackedSimd` vectorization, dotnet/runtime#115062 — FREE for Blazor/Wasm.)

---

## 7. Parse primitives from UTF-8 (IUtf8SpanParsable)

Byte-oriented pipelines (JSON, HTTP headers, wire formats) should parse primitives directly from bytes. The transcode-to-UTF-16 hop is now both slower *and* unidiomatic.

### Guid implements IUtf8SpanParsable
- **What:** dotnet/runtime#105654 — `Guid.Parse`/`TryParse` over `ReadOnlySpan<byte>` (UTF-8); `Guid` usable under an `IUtf8SpanParsable` generic constraint.
- **Mechanism:** eliminates the transcode; also beats `Utf8Parser.TryParse`, which is less optimized than `Guid.TryParse`.
- **Magnitude:** vs transcode+parse: 24.72 → 16.47 ns; vs `Utf8Parser.TryParse`: 19.34 → 16.47 ns.
- **Adoption:** new API.

```csharp
// Before:
Span<char> scratch = stackalloc char[64];
Encoding.UTF8.TryGetChars(utf8, scratch, out int n);
Guid g = Guid.Parse(scratch[..n]);
// After (.NET 10):
Guid g = Guid.Parse(utf8); // ReadOnlySpan<byte> overload
```

- **Caveat:** keep `Utf8Parser.TryParse` only for parsing a Guid off the *front* of a longer buffer — the new overloads don't do that.

### Char, Rune, Version implement IUtf8SpanParsable
- **What:** dotnet/runtime#105773, #109252. For `char`/`Rune` mainly generic-code consistency; `Version` gains real UTF-8 parse overloads with a genuine win.
- **Magnitude:** `Version.Parse(utf8)` vs transcode-then-parse: 46.48 → 35.75 ns (~23%).
- **Adoption:** new API. **Guidance:** use `IUtf8SpanParsable` as the constraint when writing byte-oriented parsing helpers — `Guid`, `Char`, `Rune`, `Version` all satisfy it now.

---

## 8. Format primitives & interpolated strings

### String interpolation: null-check removal
- **What:** dotnet/runtime#114497 removes a variety of null checks on nullable inputs in the interpolation handler paths.
- **Magnitude:** `$"{s} {s} {s} {s}"`: 34.21 → 29.47 ns (~14%). **Adoption:** FREE.

### DefaultInterpolatedStringHandler.Text property
- **What:** dotnet/runtime#112171 — `.Text` exposes the already-formatted buffered text as `ReadOnlySpan<char>`.
- **Mechanism:** consumers (custom handlers, formatting helpers built atop `DefaultInterpolatedStringHandler`) can read the stack/ArrayPool-backed buffer without forcing the terminal `string` allocation of `ToStringAndClear()`.
- **Adoption:** new API. **Guidance:** read `.Text` and copy/consume when a `string` isn't the required output.

### Guid "X" format ~4x faster by deleting pointer code
- **What:** dotnet/runtime#110923 removed pointer-based code from `Guid` formatting; side effect: `TryFormat(..., "X")` 3.0584 → 0.7873 ns (~3.9x).
- **Mechanism:** safer span-based code turned out more optimizable than the old unsafe pointer code.
- **Adoption:** FREE. **Caveat:** only "X" moved measurably; other formats were already fast.

### TypeConverter parsing (Size/SizeF/Point/Rectangle)
- **What:** dotnet/runtime#111349 — these `TypeConverter`s use span-based `Split` and string interpolation, cutting parse overhead. **Adoption:** FREE.

---

## 9. Tokenize without allocating: span-based Split

- **What:** dotnet/runtime#118288 — the BCL converted `EnumConverter` from `string.Split` to `MemoryExtensions.Split`, killing both the `string[]` and the per-element `string` allocations. The pattern is general:

```csharp
// Before:
foreach (string part in value.Split(',')) Process(part.AsSpan());
// After (allocation-free):
foreach (Range r in value.AsSpan().Split(',')) Process(value.AsSpan()[r]);
```

- **Adoption:** FREE inside the BCL; PATTERN in your code — use span `Split` returning `Range`s for transient tokenization; reserve `string.Split` for when the tokens must outlive the parse as strings.

---

## Folklore to delete

- **"Hand-wrap regex loops in `(?>...)` atomic groups / possessive quantifiers for performance."** The optimizer auto-atomicizes single-char loops before disjoint constructs, `\w`-subset loops before `\b`, and many multi-character group loops — provably-safely. Hand-written atomic groups are only useful where disjointness is unprovable, and there they change semantics, so they were never pure optimizations.
- **"Avoid `(?:.|\n)` / `\n|.` — alternation is slow, use `[\s\S]` or `Singleline`."** The negated-set merge collapses these to a constant-true set check. Penalty gone.
- **"Write `\r\n?` instead of `\r\n|\r`."** Empty-branch canonicalization rewrites the alternation into the loop form for you.
- **"`regex.Match(input).Success` is fine for boolean checks."** Analyzer-flagged (CA1874). It allocates and computes information you throw away. Same for `Matches(...).Count` vs `Count(...)` (CA1875) — 665 MB of pure-waste allocation in the benchmark.
- **"`span.IndexOfAny...(...) >= 0` is the containment idiom."** Use `ContainsAny*`; computing an index you never read costs instructions. The regex source generator itself switched.
- **"Hand-rolled char-by-char scan loops in validators/encoders/decoders are fine."** The BCL keeps replacing its own with `SearchValues`/`IndexOfAnyInRange` and winning (`string.Normalize`, `UrlDecode`, web encoders, `Regex` internals). Manual loops also never inherit future SIMD improvements; a cached `static readonly SearchValues<T>` inherits every one.
- **"Passing an explicit comparer (even `EqualityComparer<T>.Default`) to Contains/search kills vectorization"** / **"unconstrained generic code can't use MemoryExtensions."** The ~30 new comparer-accepting overloads route default comparers to the vectorized paths (~3x on arrays via LINQ `Contains`).
- **"Transcode UTF-8 to chars before parsing Guid/Version"** and **"use `Utf8Parser.TryParse` for UTF-8 GUIDs."** `Guid.Parse(ReadOnlySpan<byte>)` / `Version.Parse(utf8)` are direct and faster. Keep `Utf8Parser` only for parse-off-the-front-of-a-buffer.
- **"`string.Split` for transient tokenization."** Span-based `MemoryExtensions.Split` returning `Range`s removes the `string[]` and per-token strings; the BCL converted its own call sites.
- **"Unsafe pointer code beats span code for formatting."** `Guid` "X" formatting got ~4x faster by *removing* the pointers.
- **"Non-ASCII character sets need custom search code because they miss the fast paths."** The probabilistic-map fallback is genuinely vectorized and keeps improving; use `SearchValues<char>` regardless of alphabet.
- **Superstition NOT deleted:** timeouts on regexes over untrusted patterns/input are still warranted. Auto-atomicity only fires where disjointness is provable; catastrophic backtracking remains possible outside those cases.
