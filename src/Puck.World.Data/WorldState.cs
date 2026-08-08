using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>The closed kind vocabulary a <see cref="WorldStateRow"/> declares — ONE vocabulary shared by every cell
/// the row carries, whether the row is shaped as a scalar SLOT (one implicit cell) or a keyed TABLE (many named
/// cells): a slot is a table with one key, so both read the same <see cref="WorldStateRow.Kind"/> the same way.
/// Deliberately NO float member: simulation state is float-free by the determinism contract (see <see cref="Fixed"/>
/// for how a fractional value still rides here). There is no separate "counter"/"timer" vocabulary — a counter IS a
/// <see cref="Fixed"/> cell (the same raw <c>FixedQ4816</c> bits a scalar fixed row already carried) and a timer IS
/// an <see cref="Int"/> cell with <see cref="WorldStateRow.NonNegative"/> set (the tick-count floor, never a distinct
/// kind) — see <see cref="WorldStateRow"/>'s remarks for the reconciliation this collapses.</summary>
public enum CellKind : byte {
    /// <summary>A whole 64-bit signed integer cell (a score, a round counter, an inventory count, or — with
    /// <see cref="WorldStateRow.NonNegative"/> set — a tick-count timer).</summary>
    Int,

    /// <summary>A fixed-point cell — the repo's deterministic replacement for a float in simulation state, raw
    /// <c>FixedQ4816</c> bits (what a "counter" table row meant before this kind vocabulary unified). Human-authored
    /// surfaces (document JSON, console verb arguments, validator refusal text, read-back echoes) speak DECIMAL
    /// through <c>FixedQ4816.TryParse</c>/<c>ToString</c> — never the raw bit pattern; only the addon ABI channel
    /// wire and the per-cell mutation payload stay raw (an ingress door converts before either).</summary>
    Fixed,

    /// <summary>A boolean cell (a win flag, a toggle). Carries no range — a gauge cannot bind to it.</summary>
    Bool,

    /// <summary>A short-text cell (a status label, a player name slot), bounded to
    /// <see cref="WorldStateCapacity.MaxTextValueLength"/> UTF-16 code units. Carries no range — a gauge cannot bind
    /// to it. The one kind whose value cannot ride <see cref="WorldStateCell.Value"/> (a string is not a number by
    /// any honest encoding) — see <see cref="WorldStateCell"/>'s remarks on why this is the substrate's one
    /// irreducible asymmetry, not an oversight.</summary>
    Text,
}

/// <summary>
/// One cell of the <c>state</c> section's substrate — a typed value addressed by a stable string
/// <see cref="Key"/> inside its carrying <see cref="WorldStateRow"/>. Every row is a named collection of cells
/// sharing that row's declared <see cref="WorldStateRow.Kind"/>/envelope/capacity; a SLOT (what used to be a bare
/// <c>int</c>/<c>fixed</c>/<c>bool</c>/<c>text</c> row) is simply a row with exactly one cell keyed by the reserved
/// <see cref="WorldStateRow.SlotKey"/>, and a TABLE is a row with author-chosen keys — one substrate, two shapes.
/// </summary>
/// <param name="Key">The cell's stable string key (unique within its carrying row). Keys are strings today — an
/// entity handle and an (issuer, subject) pair both render as one; richer key kinds are an open question, not
/// built.</param>
/// <param name="Value">The cell's numeric value for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/> (raw
/// <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>) or its 0/1 encoding for <see cref="CellKind.Bool"/>;
/// ignored for <see cref="CellKind.Text"/>. Raw-encoded per the carrying row's declared <see cref="CellKind"/> —
/// never a decimal/double encoding at THIS layer (the per-cell mutation wire and the addon ABI channel convention
/// both stay raw; a human-facing ingress converts before either).</param>
/// <param name="Text">The cell's text for <see cref="CellKind.Text"/>; <see langword="null"/> for every other kind.
/// A string cannot ride <see cref="Value"/> by any honest numeric encoding, so <see cref="CellKind.Text"/> is the one
/// kind carrying a second field rather than reusing the first — the substrate's one irreducible asymmetry.</param>
/// <param name="Advance">This CELL's own continuous accumulation trait, or <see langword="null"/> for an ordinary
/// cell whose value only ever changes through an explicit write — the KEYED counterpart of
/// <see cref="WorldStateRow.Advance"/>, which stays the SLOT's own trait and never this one's. <see cref="Value"/> is
/// this cell's stored BASE when present; see <see cref="WorldStateAdvance"/> for the read-side computation, and
/// <see cref="WorldStateRow"/>'s remarks for why the two never coexist on the SAME cell. Legitimate only on a cell
/// whose <see cref="Key"/> is NOT <see cref="WorldStateRow.SlotKey"/> — a scalar row's own accumulation is authored at
/// the ROW level (beside <c>value</c>), never here, so there is exactly one place to look for either shape's
/// rate.</param>
public sealed record WorldStateCell(WorldCellName Key, long Value = 0, string? Text = null, WorldStateAdvance? Advance = null);

/// <summary>
/// One row of the world's genre-neutral <c>state</c> section — a named cell OR a named collection of cells, addressed
/// by its stable <see cref="Name"/> (the <c>UpsertStateRow</c>/<c>RemoveStateRow</c> whole-row key and the
/// <c>state:&lt;name&gt;</c> grant subject; a SLOT-shaped row's single cell is also the <c>state.&lt;name&gt;</c> HUD
/// binding token's value). The engine never interprets a row's name, key, or value — exactly as game-flavored as a
/// scene-row id or a kit name, never a taxonomy the engine branches on.
/// </summary>
/// <remarks>
/// <para><b>A slot is a table with one key — and there is ONE authored spelling for both.</b> Before this shape, a
/// scalar row and a keyed table were two independent concepts: two grant-subject kinds
/// (<c>state:&lt;name&gt;</c>/<c>table:&lt;name&gt;</c>), two mutation-kind pairs (whole-row upsert/remove vs.
/// per-key upsert/remove), two C# types, and two <c>$type</c> discriminators in the document. They are now ONE
/// substrate: every row is a named collection of <see cref="WorldStateCell"/>s sharing this row's
/// <see cref="Kind"/>/<see cref="Min"/>/<see cref="Max"/>/<see cref="Capacity"/>/<see cref="NonNegative"/>, and ONE
/// authored shape carries both (<c>Puck.World.WorldStateRowJsonConverter</c>): a row names itself, declares its
/// <see cref="Kind"/>, and carries EITHER a bare <c>value</c> — sugar for one cell keyed by the reserved
/// <see cref="SlotKey"/> — OR a <c>cells</c> array of author-keyed cells. Two optional fields, never two
/// discriminators; a row carrying both, or a <c>value</c> beside a <see cref="Capacity"/>, is refused by name. A
/// SLOT is simply a row whose <see cref="Cells"/> holds exactly one cell keyed <see cref="SlotKey"/> and declares no
/// <see cref="Capacity"/> (<see cref="IsSlot"/>) — the shape the <c>value</c> sugar authors and the shape the
/// canonical writer emits <c>value</c> back for. Every mechanism beneath the syntax is likewise single: one grant
/// subject (<c>state:&lt;name&gt;</c>, <c>GrantSubjectKind.Table</c> is retired), one whole-row mutation pair
/// (<c>UpsertStateRow</c>/<c>RemoveStateRow</c>), and one per-cell mutation pair (<c>UpsertStateCell</c>/
/// <c>RemoveStateCell</c>, ordinals 49/50 — the same ordinals <c>UpsertTableEntry</c>/<c>RemoveTableEntry</c> held,
/// renamed and widened to cover a slot's own cell rather than retired-then-reallocated, since it is the SAME
/// conceptual per-cell write, only no longer restricted to author-declared tables).</para>
/// <para><b>The kind-vocabulary reconciliation.</b> Scalar rows spoke <c>int</c>/<c>fixed</c>/<c>bool</c>/<c>text</c>;
/// the table primitive spoke a SEPARATE vocabulary, <c>ActionStateKind.Counter</c>/<c>Timer</c>. These are not two
/// vocabularies — a counter is exactly a <see cref="CellKind.Fixed"/> value (the identical raw <c>FixedQ4816</c> bit
/// pattern), and a timer is exactly a <see cref="CellKind.Int"/> value with an always-enforced non-negative floor.
/// <see cref="NonNegative"/> promotes that floor from a table-only special case to a per-row trait ANY numeric row
/// may declare, enforced regardless of any authored <see cref="Min"/>. It is the row's OWN declaration that carries
/// the floor everywhere it is enforced: document validation walks it here, and the cross-document write-back channel
/// (<c>Server.WorldOwnedWorlds.Decide</c>) READS THIS TRAIT off the row it is writing rather than assuming a floor of
/// its own — a door admitting what the validator would refuse would persist a document that stops loading.
/// <see cref="CellKind"/> is therefore the ONE kind vocabulary over cells, spelled once as the row's own
/// <c>kind</c> field — there is no more "valueKind counter|timer" to reconcile with, because
/// <c>counter</c>/<c>timer</c> were never a fifth and sixth kind, only two of these four spelled differently.</para>
/// </remarks>
/// <param name="Name">The row's stable string name (unique within the section).</param>
/// <param name="Kind">Which cell kind every cell in this row carries.</param>
/// <param name="Min">The row-wide declared lower bound every cell's <see cref="WorldStateCell.Value"/> must satisfy,
/// raw-encoded per <see cref="Kind"/> (raw <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>), or
/// <see langword="null"/> for none. Present only together with <see cref="Max"/> — a range is authored as a pair or
/// not at all. Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>. Omitted from the wire
/// when null.</param>
/// <param name="Max">The row-wide declared upper bound, raw-encoded per <see cref="Kind"/>, or <see langword="null"/>
/// for none. Present only together with <see cref="Min"/>. Omitted from the wire when null.</param>
/// <param name="Capacity">The row's own cell-count ceiling (<c>1..</c><see cref="WorldStateCapacity.MaxCellsPerRow"/>),
/// or <see langword="null"/> to fall back to the implicit ceiling. A row declaring <see cref="Capacity"/> can never
/// be a slot (<see cref="IsSlot"/>), even if it happens to carry exactly one cell — declaring a capacity is
/// declaring table intent. Omitted from the wire when null.</param>
/// <param name="NonNegative">Whether every cell's value must be non-negative — enforced regardless of any authored
/// <see cref="Min"/>. Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>; this is what a
/// "timer" meant before the kind vocabularies reconciled (see this type's remarks) — <see cref="CellKind.Int"/> +
/// <see cref="NonNegative"/> IS a timer, never a third kind.</param>
/// <param name="GatesDrive">Whether this row is a DRIVE-ADMISSION GATE — a keyed row (see <see cref="IsKeyed"/>)
/// whose per-body cell, addressed by the body's 0-based entity index as the cell's KEY (the SAME entity-addressing
/// convention <c>WorldRuleFacts.ArgMaxPrefix</c>/<c>ArgMinPrefix</c> and <c>WorldStateReader.ArgExtremum</c> parse a
/// keyed cell key against), the intent-admission door consults BEFORE admitting a Drive intent for that body: a
/// nonzero cell refuses the body's drive/action intents until the cell reads zero again (never latched — checked
/// fresh every tick the body submits). The engine still does not interpret this row's NAME (a world may call it
/// <c>stunned</c>, <c>dead</c>, <c>silenced</c>, or anything else — several independently-named gate rows may exist
/// at once, any one of which refuses); this field is the sole, explicit, authored opt-in that makes a row a gate,
/// exactly the deny-by-default-is-inert-by-default posture every other opt-in trait here follows. Legitimate only on
/// a row declaring <see cref="Capacity"/> (a slot has no ONE body to gate) and only for
/// <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>/<see cref="CellKind.Bool"/> (a text cell has no honest
/// zero/nonzero reading). Default <see langword="false"/> — an ordinary row never gates admission.</param>
/// <param name="Evicts">Whether this row is a BOUNDED, FIFO-EVICTING table — a keyed row (see <see cref="IsKeyed"/>)
/// whose overflow policy is DROP-OLDEST rather than refuse. Ordinarily a <see cref="Capacity"/> is a hard ceiling: a
/// write that would grow <see cref="Cells"/> past it is refused BY NAME at validation. With this set, the SAME write
/// instead succeeds and, if it added a brand-new key past capacity, evicts the row's OLDEST surviving cell — an
/// append-only chat log, an activity feed, a kill-log, anything meant to stay bounded by forgetting its earliest
/// entries rather than refusing its newest one. Eviction runs inside the <c>UpsertStateCell</c> compose arm itself
/// (<c>WorldServer.TryCompose</c>) as a PURE function of the candidate cells and whether this write minted a new key,
/// so <c>world.undo</c>'s journal replay reproduces the identical victim on every re-composition, and the dropped
/// key names itself on the mutation's apply echo — a silent eviction is never acceptable here.
/// <para><b>FIFO by insertion POSITION, not recency of touch.</b> A brand-new key is always appended to the END of
/// <see cref="Cells"/> (see <c>WorldServer.Upsert</c>'s replace-in-place-else-append rule) and eviction always drops
/// index 0 — the row's longest-standing cell. Re-writing an EXISTING key updates it in place and does
/// <c>NOT</c> move it to the back: this is true insertion-order FIFO, never LRU, so a hot key written every tick
/// is exactly as evictable as one never touched again, keyed purely by when it first entered the row.</para>
/// <para><b>Why this earns its keep rather than costing nothing.</b> Before this trait, a bounded FIFO log was
/// INEXPRESSIBLE at any contraption cost: the rule effect vocabulary has no <c>removeStateCell</c> effect (only
/// <c>setState</c>/<c>addState</c>/<c>generate</c>/<c>hud</c>/<c>placement</c>/<c>save</c>), so nothing could author
/// "drop the oldest entry" — the row could only grow until capacity refused every further write. Unlike a saturating
/// write mode over signed HP (refused elsewhere in this substrate because a signed cell already carries the
/// information for free — the mode would have bought nothing), preserving EVERY entry forever costs UNBOUNDED
/// memory; a deliberate, named, per-row choice to forget is the feature, not a shortcut around one. Legitimate only
/// together with a declared <see cref="Capacity"/> (the bound to evict against — a slot has no ONE cell to make room
/// beside, so <see cref="Evicts"/> without <see cref="Capacity"/>, which also covers the slot case, is refused BY
/// NAME). Default <see langword="false"/> — an ordinary keyed row still refuses over capacity, exactly as before this
/// trait existed.</para></param>
/// <param name="Cells">The row's current cells (default empty). Refused past its effective capacity, and on a
/// duplicate key, BY NAME (naming the row) — UNLESS <see cref="Evicts"/> is set, in which case a write that would
/// grow past capacity evicts the oldest cell instead of refusing (see <see cref="Evicts"/>). A slot-shaped row (see
/// <see cref="IsSlot"/>) holds exactly one cell keyed <see cref="SlotKey"/>; a keyed row may hold any author-chosen
/// keys except <see cref="SlotKey"/> itself, which is reserved for the <c>value</c> sugar and refused as an authored
/// cell key.</param>
/// <param name="Advance">The row's own (SLOT-cell) continuous accumulation trait, or <see langword="null"/> for an
/// ordinary row whose slot value only ever changes through an explicit write. See <see cref="WorldStateAdvance"/>.
/// Legitimate only for <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>, only on a SCALAR (slot-eligible) row —
/// declares no <see cref="Capacity"/> and holds at most its one <see cref="SlotKey"/> cell — and never together with
/// <see cref="Draw"/>: a row is an authored-randomness DRAW SITE or a continuous accumulator, never both. A KEYED
/// row's own cells accumulate independently through <see cref="WorldStateCell.Advance"/> instead — this field and
/// that one never both name the SAME cell (the slot cell may carry only this one), so "which advance governs this
/// cell" is never an open question.</param>
/// <param name="Draw">The row's authored-randomness facet, or <see langword="null"/> for an ordinary row. A row
/// carrying one is a DRAW SITE (see <see cref="WorldDraw"/> and <see cref="IsDraw"/>): its slot cell's value is
/// DRAWN — at first fill, and at every later <c>generate</c> its <see cref="WorldDraw.Timing"/> admits — from the
/// stochastic SOURCE the facet either names (<see cref="WorldDraw.Source"/>, a row of the document's
/// <c>generators</c> section) or inlines (<see cref="WorldDraw.Generator"/>). The site's <see cref="Kind"/> must be
/// one the source can write (<c>WorldGeneratorEngine.TryCheckTargetKind</c>: <see cref="CellKind.Text"/> for a
/// Markov source, <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/> for a numeric one), it may declare no
/// <see cref="Capacity"/> (a draw site is scalar — a keyed row has no ONE cell for a draw to fill), and it is
/// mutually exclusive with <see cref="Advance"/>. Sits BESIDE <see cref="Cells"/> rather than instead of it (the
/// same shape <see cref="Advance"/> already follows): the cell holds the row's CURRENT value, and this decides
/// it.</param>
/// <param name="DrawCursor">How many SAMPLES this site's <see cref="Draw"/> has ever consumed — ENGINE-MINTED
/// bookkeeping, and the position the seekable engine re-seeks to
/// (<c>WorldGeneratorEngine.AdvancesPerSample</c> scales it into <c>Pcg32XshRr</c> advances, so resuming is an exact
/// O(1) <c>Advance</c> and never a replay of the earlier draws). Living in the document rather than in server-side
/// runtime state is what makes <c>world.undo</c>, <c>world.save</c>, and replay rewind a site's draw position for
/// free, bit-identically, by the same whole-document restore that already rewinds an ordinary counter. Zero when
/// <see cref="Draw"/> is <see langword="null"/>; refused negative.</param>
/// <param name="DrawDecks">This site's per-context DEALT masks, by the source's context declaration ordinal —
/// ENGINE-MINTED bookkeeping a Markov source under a deck <see cref="WorldGeneratorMode"/> carries, and the one part
/// of a draw's position the cursor cannot express (which alternatives are gone is not a function of how many samples
/// were taken). Bit <c>i</c> is set when alternative <c>i</c> of that context has been dealt. Lives at the SITE, not
/// on the source row, which is what lets two sites reference ONE deck source and deal independently. Null or empty
/// for every site whose source never deals.</param>
public sealed record WorldStateRow(
    WorldCellName Name,
    CellKind Kind,
    long? Min = null,
    long? Max = null,
    int? Capacity = null,
    bool NonNegative = false,
    bool GatesDrive = false,
    bool Evicts = false,
    IReadOnlyList<WorldStateCell>? Cells = null,
    WorldStateAdvance? Advance = null,
    WorldDraw? Draw = null,
    long DrawCursor = 0,
    IReadOnlyList<long>? DrawDecks = null
) {
    /// <summary>The prefix EVERY engine-minted row/cell name carries, and the one an AUTHOR may never spell. A row
    /// name starting with it is refused outright (nothing mints a row); a cell key starting with it is refused unless
    /// it is exactly the engine-minted key legitimate for that row's shape — <see cref="SlotKey"/> on a slot. The
    /// rule lives in
    /// <c>WorldDefinitionValidator</c>, which runs at BOOT, at every live mutation, and on every undo-replay entry —
    /// so a hand-authored document and a console verb are refused by the same code, rather than by a door one of them
    /// can walk around.</summary>
    public const string ReservedNamePrefix = "$";

    /// <summary>The reserved cell key a SLOT-shaped row's one implicit cell carries — the address the authored
    /// <c>value</c> sugar writes to, and never a legal author-chosen cell key (see <see cref="IsSlot"/> and this
    /// type's remarks). Chosen to be visually distinct from any string a game would plausibly choose as its own key
    /// — legal as a <see cref="WorldCellName"/> like any other (<c>'$'</c> is neither reserved nor a dot), the one
    /// reserved exception the substrate mints rather than authors.</summary>
    public static readonly WorldCellName SlotKey = WorldCellName.Parse(candidate: "$value");

    /// <summary>Gets a value indicating whether this row is shaped as a scalar SLOT — no declared <see cref="Capacity"/> and exactly one cell
    /// keyed <see cref="SlotKey"/>. Drives whether <c>Puck.World.WorldStateRowJsonConverter</c> writes the row's one
    /// cell back as the bare <c>value</c> sugar or as a <c>cells</c> array, and which read-backs (HUD
    /// <c>state.&lt;name&gt;</c> binding, <c>world.state</c>'s value column) resolve a live value for — a keyed row
    /// has no single value to show, the same unbound-gauge precedent every other HUD binding already follows, never a
    /// new refusal. A DRAW SITE is an ordinary slot: its one cell holds the drawn value, and its own bookkeeping
    /// (<see cref="DrawCursor"/>/<see cref="DrawDecks"/>) lives in row FIELDS rather than in cells, so nothing about
    /// being drawn changes how the row reads.</summary>
    public bool IsSlot => (Capacity is null) && (Cells is { Count: 1 } cells) && (cells[0].Key == SlotKey);

    /// <summary>Gets a value indicating whether this row is positively KEYED — it declares a <see cref="Capacity"/>, carries more than one
    /// cell, or carries its single cell under an author-chosen key. Such a row has no ONE cell, so an omitted key
    /// beside it addresses nothing: the (row, key) pair rule every consumer of the pair shares — a world rule's
    /// <c>compareState</c>/<c>setState</c>/<c>addState</c>, a <c>generate</c> effect's destination at either scope,
    /// and the <c>Generate</c> mutation's own target — refuses BY NAME here rather than silently reading the row's
    /// first cell.</summary>
    /// <remarks>This is NOT <c>!<see cref="IsSlot"/></c>, and the difference is the whole reason it is stated once. A
    /// row carrying NO cells at all is not a slot yet IS legitimately slot-addressable — the first write mints its
    /// slot cell, exactly as <c>world.state.cell.set</c> does. <see cref="IsSlot"/> asks whether a single value
    /// exists to READ; this asks whether an omitted key can ADDRESS one.</remarks>
    public bool IsKeyed => (Capacity is not null) || (Cells is { Count: > 1 }) || ((Cells is { Count: 1 } cells) && (cells[0].Key != SlotKey));

    /// <summary>Gets a value indicating whether this row declares a <see cref="WorldDraw"/> — whether it is a DRAW SITE.</summary>
    public bool IsDraw => (Draw is not null);

    /// <summary>Gets a value indicating whether this row declares a <see cref="WorldStateAdvance"/> continuous-accumulation trait.</summary>
    public bool IsAdvancing => (Advance is not null);

    /// <summary>Answers whether this row declares a cell under <paramref name="key"/> — the (row, key) existence half
    /// of the pair rule, asked by the rule compiler's operand walk, the HUD binding validator, and the
    /// <c>world.hud</c> read-back alike, so an undeclared cell refuses the same way at every door.</summary>
    /// <remarks>Allocation-free and ordinal, like <see cref="WorldDefinitionRows.FindStateRow"/> its callers reach it
    /// through: the HUD path runs this per frame.</remarks>
    /// <param name="key">The cell key to look for.</param>
    /// <returns><see langword="true"/> when the row declares a cell under that key.</returns>
    public bool HasCell(string key) {
        foreach (var cell in (Cells ?? [])) {
            if (string.Equals(a: cell.Key, b: key, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Projects <paramref name="value"/> onto this row's declared numeric envelope —
    /// <see cref="NonNegative"/>'s floor first, then an authored <see cref="Min"/>/<see cref="Max"/> pair — answering
    /// "what is the nearest value a cell of this row may actually hold".</summary>
    /// <remarks>This is the row's envelope stated ONCE, for the two callers that need to know where a value would
    /// land rather than whether it is legal. It is NOT a write-side clamp and does not soften the settled envelope
    /// duality (a computed value clamps, an explicit write refuses):
    /// <see cref="WorldStateAdvance.ComputeCurrentValue"/> uses it for the READ clamp it has always applied, and
    /// <c>WorldServer.FireWorldRuleEffect</c> uses it only to decide whether a rule's write could move the
    /// destination at all — never to change the value that write submits, which stays exactly what the rule
    /// composed and is still refused BY NAME by <c>WorldDefinitionValidator</c> when it falls outside.</remarks>
    /// <param name="value">The raw value to project, encoded per this row's <see cref="Kind"/>.</param>
    /// <returns>The projected raw value; <paramref name="value"/> unchanged when this row declares no envelope, as a
    /// <see cref="CellKind.Bool"/>/<see cref="CellKind.Text"/> row never may.</returns>
    public long ClampToEnvelope(long value) {
        var clamped = ((NonNegative && (value < 0L)) ? 0L : value);

        if ((Min is { } lo) && (Max is { } hi)) {
            clamped = ((clamped < lo) ? lo : ((clamped > hi) ? hi : clamped));
        }

        return clamped;
    }
}

/// <summary>
/// A <see cref="WorldStateRow"/>'s CONTINUOUS accumulation trait — the row's stored cell is a BASE value, and the
/// row's READ value ADVANCES with elapsed ticks at an exact per-tick rational RATE from the tick it was last
/// explicitly set (<see cref="EpochTick"/>). Regen, fractional accumulation, a day/night clock: anything whose value
/// should move on its own between observations, with no observation required for the movement to be exact.
/// </summary>
/// <remarks>
/// <para><b>Lazy, journal-silent.</b> Nothing per-tick materializes and nothing per-tick journals — the computed
/// value (<see cref="ComputeCurrentValue"/>) is a pure function of (base, <see cref="EpochTick"/>, rate, the tick
/// asked about). An explicit write — <c>UpsertStateRow</c> re-authoring the row, or a slot-cell <c>UpsertStateCell</c>
/// — RE-BASES: the written value becomes the new base and <see cref="EpochTick"/> becomes the tick the write applied
/// at, unconditionally overwriting whatever <see cref="EpochTick"/> the write's own payload carried (an authored
/// epoch is honored only on the LOADED document a live write has not yet touched).</para>
/// <para><b>One read seam, both sides.</b> <see cref="ComputeCurrentValue"/> has exactly one caller —
/// <see cref="WorldStateReader.TryRead"/>, the section's one (row, key) read — so every read-back, every rule gate,
/// every HUD binding AND every arithmetic write resolves an advancing row through the same code and can never
/// disagree about what the row currently holds. In particular an <c>add</c> against an advancing row adds to what a
/// reader sees, never to the base the accumulation runs from.</para>
/// <para><b>The rules boundary.</b> <c>rules</c> (see <c>WorldRules.cs</c>) already owns the PERIODIC/every-N-ticks
/// and COOLDOWN vocabulary — a discrete crossing an author schedules, or a countdown an author decrements. This
/// trait owns the complementary CONTINUOUS half: a value that moves on its own between whatever ticks anything
/// happens to look at it, needing no schedule row and firing no per-tick write. The two compose rather than
/// duplicate: a rule's <c>compareState</c> reads an advancing row's LIVE computed value exactly as it reads any
/// other row, so "fire once health regenerates past half" is an ordinary Edge rule gated on a row this trait — not
/// the rule — knows how to advance. A rule's OWN <c>setState</c>/<c>addState</c> effect against an advancing row's
/// slot cell is itself an explicit write, so it re-bases exactly like a console verb would — a Level-mode rule that
/// writes the SAME row every tick therefore overrides the trait's own accumulation with its own, the identical
/// footgun <c>WorldRule.Mode</c>'s remarks already name for journal spam; want the trait to run, write the row from
/// elsewhere or not every tick.</para>
/// <para><b>Units.</b> <see cref="RateNumerator"/>/<see cref="RateDenominator"/> is an exact fraction of the row's
/// own DISPLAYED unit per tick — the unit its <c>value</c>, <see cref="WorldStateRow.Min"/> and
/// <see cref="WorldStateRow.Max"/> are authored in, not its raw storage. For <see cref="CellKind.Int"/> the two
/// coincide (a plain integer per tick). For <see cref="CellKind.Fixed"/> they do not: <c>1/1</c> is <c>1.0</c> per
/// tick, and <see cref="ComputeCurrentValue"/> scales the numerator by <c>2^FixedQ4816.FractionBitCount</c> before
/// allocating, so a rate far slower than one raw <c>FixedQ4816</c> tick still accumulates EXACTLY (at
/// 240 Hz, <c>1/240</c> is one displayed unit per second, allocated as 65536/240 raw per tick with no rounding
/// drift) via <see cref="Puck.Maths.DiscreteMeasure"/>'s exact rational allocation, this trait's computation
/// core.</para>
/// <para><b>Sign.</b> The rate MAY be negative (decay/drain), and a negative rate is the exact MIRROR of its
/// positive twin rather than a floor of the signed affine function: <see cref="Puck.Maths.DiscreteMeasure"/>
/// accepts only a non-negative rate, so this type floors the MAGNITUDE and negates it. Over the same elapsed span
/// a rate of <c>-n/d</c> therefore loses exactly what <c>+n/d</c> would gain (rate <c>-1/3</c> over 44 ticks
/// subtracts 14, not the 15 a floor of <c>-44/3</c> would); decay and regen at equal magnitude stay symmetric,
/// which is the property an author tuning a pair of them relies on.</para>
/// <para><b>The envelope.</b> A declared <see cref="WorldStateRow.Min"/>/<see cref="WorldStateRow.Max"/> or
/// <see cref="WorldStateRow.NonNegative"/> floor bounds the COMPUTED value on every read — CLAMPS it, never rewrites
/// the stored base/epoch. That is the read side of the settled envelope duality (a computed value clamps, an
/// explicit write refuses), not a local choice. Because one row's rate never changes sign mid-flight (only an
/// explicit Set redeclares it), a stateless read-time clamp and a stateful "saturate once and stop accumulating"
/// scheme compute the identical value on every later tick; this trait takes the simpler of the two rather than
/// tracking a saturation flag nothing could ever observe differently. There is no WRAP/modulo mode (a day/night
/// phase that loops instead of capping at its max) — an open question, not built.</para>
/// </remarks>
/// <param name="RateNumerator">The per-tick rate's signed numerator, in the row's own DISPLAYED unit (see this
/// type's remarks). Negative accumulates downward (decay); zero is declared but inert.</param>
/// <param name="RateDenominator">The per-tick rate's denominator. Refused at zero or below.</param>
/// <param name="EpochTick">The server tick the rate starts accumulating from — the tick the row's base value was
/// last explicitly set, or the LOADED document's own authored value for a row never set since. A negative value is
/// refused, which in practice is a BOOT-document refusal: every live write is rebased to the applying tick before
/// validation sees it, so an authored negative epoch can only survive as far as the validator on a document being
/// loaded.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateAdvance(long RateNumerator, long RateDenominator, long EpochTick = 0) {
    /// <summary>Computes <paramref name="row"/>'s current value: <paramref name="baseValue"/> plus the exact
    /// accumulation between <see cref="EpochTick"/> and <paramref name="currentTick"/>, clamped into the row's
    /// declared envelope (see this type's remarks). <paramref name="currentTick"/> preceding <see cref="EpochTick"/>
    /// — the server's own tick counter resets to zero on reboot while a persisted row's epoch does not, the same
    /// characteristic every <c>$tick</c> rule schedule already has — reads as zero elapsed, never a negative
    /// accumulation.</summary>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/> and envelope).</param>
    /// <param name="baseValue">The row's stored raw cell value.</param>
    /// <param name="currentTick">The tick to compute the value as of.</param>
    /// <returns>The computed, envelope-clamped raw value.</returns>
    public long ComputeCurrentValue(WorldStateRow row, long baseValue, ulong currentTick) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var elapsed = BigInteger.Max(left: BigInteger.Zero, right: ((BigInteger)currentTick - EpochTick));
        var delta = BigInteger.Zero;

        if ((RateNumerator != 0) && !elapsed.IsZero) {
            var scale = ((row.Kind == CellKind.Fixed) ? (1L << FixedQ4816.FractionBitCount) : 1L);
            var magnitude = DiscreteMeasure
                .Rational(numerator: (BigInteger.Abs(value: (BigInteger)RateNumerator) * scale), denominator: RateDenominator)
                .AmountBetween(start: BigInteger.Zero, end: elapsed);

            delta = ((RateNumerator < 0) ? -magnitude : magnitude);
        }

        var raw = (baseValue + delta);

        // Saturating into long first and clamping after is the same answer as clamping the BigInteger first: every
        // envelope bound is itself a long, so saturation can only move a value that is already outside every bound,
        // and it moves it to the same side. Stating the envelope once (WorldStateRow.ClampToEnvelope) is what keeps
        // this read clamp and the rule-effect write's "could this move the cell" test from drifting apart.
        return row.ClampToEnvelope(value: ((raw > long.MaxValue) ? long.MaxValue : ((raw < long.MinValue) ? long.MinValue : (long)raw)));
    }
}

/// <summary>How a <see cref="WorldGenerator"/> context's alternatives are consumed — the DECK vocabulary. Authored,
/// never inferred: exhaustion behaviour is a declaration, not a fallback the engine picks.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldGeneratorMode>))]
public enum WorldGeneratorMode : byte {
    /// <summary>Every sample leaves the context's alternatives unchanged — the ordinary weighted Markov transition.</summary>
    WithReplacement,

    /// <summary>Each alternative may be dealt at most once per context; a context whose alternatives are all dealt
    /// REFUSES the whole emission by name (never a silent stall, never a re-deal).</summary>
    WithoutReplacement,

    /// <summary>Each alternative may be dealt at most once per context; a context whose alternatives are all dealt
    /// clears its deck and deals again from the full set, deterministically, in the same emission.</summary>
    ReshuffleOnExhaustion,
}

/// <summary>One weighted alternative of a <see cref="WorldGeneratorContext"/>: the token it emits, its relative
/// weight, and the context the walk moves INTO after it is picked. The authored <see cref="Next"/> is what makes this
/// a real Markov process rather than a bag of independent draws — the context key IS the process state, so an author
/// folds exactly as much history into it as the chain needs.</summary>
/// <param name="Token">The opaque game-authored token this alternative emits. The engine never interprets it; it is
/// space-joined with the emission's other tokens and written into the target text cell. Bounded by
/// <see cref="WorldGeneratorCapacity.MaxTokenLength"/>.</param>
/// <param name="Weight">The alternative's positive relative weight. At least one alternative in a context must carry
/// a non-zero weight.</param>
/// <param name="Next">The context the walk moves into after this alternative is picked. Must name a declared
/// <see cref="WorldGeneratorContext.Key"/>; naming a context that declares NO alternatives ends the emission (a
/// terminal is a context with nothing to say, never a reserved token spelling).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGeneratorAlternative(string Token, ulong Weight, WorldCellName Next);

/// <summary>One named context of a <see cref="WorldGenerator"/> — the state the walk may be sitting in and the
/// weighted alternatives it may pick while there. A context declaring NO alternatives is TERMINAL: reaching it ends
/// the emission.</summary>
/// <param name="Key">The stable context key, unique within the generator.</param>
/// <param name="Alternatives">The weighted alternatives out of this context, or empty for a terminal context.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGeneratorContext(WorldCellName Key, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGeneratorAlternative>? Alternatives = null);

/// <summary>The closed vocabulary of a <see cref="WorldGenerator"/>'s draw shape — which of its fields are read, and
/// what ONE emission produces. This is the whole stochastic-source vocabulary the document has: a Markov text walk, a
/// deck deal, a uniform range, a weighted numeric table, and a raw stream draw are SOURCES of one family, never
/// parallel primitives with their own seeding, cursoring, and refusal stories.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldGeneratorSource>))]
public enum WorldGeneratorSource : byte {
    /// <summary>The weighted-transition walk over <see cref="WorldGenerator.Contexts"/> — reads
    /// <see cref="WorldGenerator.Start"/>/<see cref="WorldGenerator.Bound"/>/<see cref="WorldGenerator.Contexts"/>/
    /// <see cref="WorldGenerator.Mode"/>, writes TEXT. The only source that deals (see
    /// <see cref="WorldGeneratorMode"/>) and the only one whose emission costs more than one sample.</summary>
    Markov,

    /// <summary>One draw over the closed integer range
    /// <c>[<see cref="WorldGenerator.RangeMin"/>, <see cref="WorldGenerator.RangeMax"/>]</c> — reads only those two
    /// fields, writes an INT or FIXED value. One FIXED-COST advance per draw: a multiply-high map of a
    /// <c>UnitFraction32</c>, never the rejection-sampled <c>Pcg32XshRr.NextUInt32(min, max)</c>, which is what keeps
    /// a cursor SEEKABLE. That trade is why the map is uniform rather than EXACTLY uniform: with <c>n</c> = the
    /// range's value count each outcome claims either <c>⌊2³²/n⌋</c> or <c>⌈2³²/n⌉</c> of the 2³² fractions, a
    /// relative deviation of at most <c>n/2³²</c> — and exactly zero whenever <c>n</c> divides 2³².</summary>
    UniformRange,

    /// <summary>One alias-table draw over <see cref="WorldGenerator.Weighted"/>'s numeric outcomes — reads only that
    /// field, writes an INT or FIXED value. Exactly two advances per draw, the same fixed alias-table cost the Markov
    /// walk pays per token.</summary>
    WeightedNumeric,

    /// <summary>One raw, unshaped 32-bit draw off the site's own stream — no range, no weights — widened into the
    /// target's raw value as-is. One fixed-cost advance per draw. The "give me entropy, no distribution"
    /// primitive.</summary>
    StreamDraw,
}

/// <summary>One numeric outcome of a <see cref="WorldGeneratorSource.WeightedNumeric"/> source: the raw value it
/// writes and its relative weight — the numeric twin of <see cref="WorldGeneratorAlternative"/>, minus
/// <c>Token</c> (nothing to join into text) and <c>Next</c> (a numeric draw is one terminal pick, never a
/// walk).</summary>
/// <param name="Value">The raw value this outcome writes on selection (a plain integer for
/// <see cref="CellKind.Int"/>, raw <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>).</param>
/// <param name="Weight">The outcome's relative weight, fed straight to <c>Puck.Maths.WeightedSampler</c>'s exact
/// <c>ulong</c> overload. At least one outcome must carry a non-zero weight.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGeneratorWeightedNumeric(long Value, ulong Weight);

/// <summary>
/// An authored STOCHASTIC SOURCE — the ONE randomness vocabulary the document has. A name generator, a dialogue line,
/// a loot roll, a flat weighted draw, a card deal, a random census, and a drawn host backend all reduce to a source
/// of this family sampled at an authored moment into an authored SITE (see <see cref="WorldDraw"/>).
/// </summary>
/// <remarks>
/// <para><b>A source is a pure declaration — it holds no position.</b> Nothing here is stateful: the CURSOR and the
/// dealt DECKS live on the SITE that draws (<see cref="WorldStateRow.DrawCursor"/>/
/// <see cref="WorldStateRow.DrawDecks"/>), which is exactly what lets two sites reference ONE declared source and
/// draw independent sequences from it. A source may be declared once in the document's <c>generators</c> section
/// (see <see cref="WorldGeneratorRow"/>) and referenced by name, or inlined at a single site as sugar — the two
/// spellings compile to the identical record, so no capability is reachable one way and not the other.</para>
/// <para><b>Markov: one emission is one walk.</b> A walk begins at <see cref="Start"/> and repeats — sample the
/// current context's alternatives, append the picked token, move to its
/// <see cref="WorldGeneratorAlternative.Next"/> — until it reaches a TERMINAL context (one declaring no
/// alternatives). A walk that has emitted <see cref="Bound"/> tokens without terminating REFUSES the whole emission
/// BY NAME rather than truncating it. A single self-terminating context with <see cref="Bound"/> 1 is the degenerate
/// flat weighted TEXT draw — a named pick among tokens, which is how an enum-valued site (the host backend) draws BY
/// NAME rather than by an unnamed ordinal.</para>
/// <para><b>The other three sources are numeric and always exactly ONE draw</b> — <see cref="Bound"/> and
/// <see cref="Mode"/> are meaningless beside them and must be left at their defaults. Each source's fields are
/// BOTH-OR-NEITHER against the fields the others own: declaring <see cref="Contexts"/> beside <see cref="RangeMin"/>
/// is refused by name rather than silently ignored.</para>
/// <para><b>The deck.</b> <see cref="Mode"/> is Markov-only, per-context, and persists across emissions in the
/// drawing SITE's own <see cref="WorldStateRow.DrawDecks"/> masks — so dealing one card per invocation is the
/// ordinary case, and the eleventh deal from a ten-card deck either refuses by name or reshuffles, by declaration.
/// The numeric sources never deal.</para>
/// </remarks>
/// <param name="Source">Which draw shape this source fires.</param>
/// <param name="Start">Markov only: the context every emission begins from. Must name a declared context.</param>
/// <param name="Bound">Markov only: the maximum tokens ONE emission may draw before refusing by name,
/// <c>1..</c><see cref="WorldGeneratorCapacity.MaxEmissionBound"/>. Left at <see cref="DefaultBound"/> by a numeric
/// source.</param>
/// <param name="Contexts">Markov only: the declared contexts, at least one, uniquely keyed.</param>
/// <param name="Mode">Markov only: how alternatives are consumed (see <see cref="WorldGeneratorMode"/>).</param>
/// <param name="RangeMin"><see cref="WorldGeneratorSource.UniformRange"/> only: the closed range's inclusive lower
/// bound — both bounds present or neither. RAW-encoded per the destination site's <see cref="CellKind"/> (raw
/// <c>FixedQ4816</c> bits for a <c>fixed</c> site), UNLIKE a site row's own <c>min</c>/<c>max</c>, which a
/// <c>fixed</c> row authors as decimal text: a source is not bound to one site, so this declaration cannot know the
/// kind it will write and has no honest decimal spelling to offer.</param>
/// <param name="RangeMax"><see cref="WorldGeneratorSource.UniformRange"/> only: the inclusive upper bound, same
/// encoding as <see cref="RangeMin"/>.</param>
/// <param name="Weighted"><see cref="WorldGeneratorSource.WeightedNumeric"/> only: the weighted numeric outcomes, at
/// least one, at least one carrying a non-zero weight.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGenerator(
    WorldGeneratorSource Source = WorldGeneratorSource.Markov,
    // Each source reads a disjoint field set, so the canonical writer omits the ones this source does not own rather
    // than emitting a wall of nulls a reader has to discount.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCellName? Start = null,
    int Bound = WorldGenerator.DefaultBound,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGeneratorContext>? Contexts = null,
    WorldGeneratorMode Mode = WorldGeneratorMode.WithReplacement,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? RangeMin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? RangeMax = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGeneratorWeightedNumeric>? Weighted = null
) {
    /// <summary>The <see cref="Bound"/> an undeclared source carries — one emitted token. <see cref="Bound"/> and
    /// <see cref="Mode"/> are the two Markov-only fields that are NOT nullable, so "left at its default" is the only
    /// reading of "not declared" available to them; a numeric source carrying anything else is refused against this
    /// constant rather than left to parse and then be ignored.</summary>
    public const int DefaultBound = 1;
}

/// <summary>One row of the document's <c>generators</c> section: a stochastic source DECLARED under a name, so that
/// any number of <see cref="WorldDraw"/> sites may reference it (<see cref="WorldDraw.Source"/>). Declaring a source
/// once and referencing it is what makes an NPC-bark site and a loot site able to share one authored table while
/// still drawing independent sequences — the source carries the shape, each site carries its own position.</summary>
/// <param name="Name">The source's name, unique within the section, and the spelling a site's
/// <see cref="WorldDraw.Source"/> resolves against.</param>
/// <param name="Generator">The source itself.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGeneratorRow(WorldCellName Name, WorldGenerator Generator);

/// <summary>The RATIFIED <see cref="WorldGenerator"/> caps — read by <see cref="WorldDefinitionValidator"/>.</summary>
/// <remarks>The context cap and the alternative cap are load-bearing together, not decorative: a deck mode records
/// one <see cref="WorldStateRow.DrawDecks"/> mask per context, and each such mask is a 64-bit dealt lane (so a
/// context can hold no more alternatives than a <c>ulong</c> has bits).</remarks>
public static class WorldGeneratorCapacity {
    /// <summary>A source's context-count ceiling.</summary>
    public const int MaxContexts = 32;

    /// <summary>A context's alternative-count ceiling — one bit per alternative in its deck mask.</summary>
    public const int MaxAlternativesPerContext = 64;

    /// <summary>The declared <see cref="WorldGenerator.Bound"/> ceiling.</summary>
    public const int MaxEmissionBound = 64;

    /// <summary>One emitted token's length ceiling, in UTF-16 code units (the JOINED emission is separately bounded
    /// by <see cref="WorldStateCapacity.MaxTextValueLength"/>).</summary>
    public const int MaxTokenLength = 64;

    /// <summary>The document's declared-source count ceiling.</summary>
    public const int MaxDeclaredSources = 64;

    /// <summary>A <see cref="WorldGeneratorSource.WeightedNumeric"/> source's outcome-count ceiling — matches
    /// <see cref="MaxAlternativesPerContext"/> since both build an alias table over an authored entry list.</summary>
    public const int MaxWeightedOutcomes = 64;

    /// <summary>The greatest value a <see cref="WorldGeneratorSource.UniformRange"/> bound may hold. The draw is a
    /// single fixed-cost multiply-high map whose span must fit a <c>uint</c> without truncation; this bound is what
    /// keeps it there.</summary>
    public const long MaxRangeBound = int.MaxValue;

    /// <summary>The least value a <see cref="WorldGeneratorSource.UniformRange"/> bound may hold — see
    /// <see cref="MaxRangeBound"/>.</summary>
    public const long MinRangeBound = int.MinValue;
}

/// <summary>
/// The ONE rule for a <see cref="WorldStateRow.ReservedNamePrefix"/>-prefixed cell: which reserved keys a row's shape
/// legitimately MINTS.
/// </summary>
/// <remarks>
/// <para>Stated once, here, because two doors ask it and a second reading is how they drift: the whole-document walk
/// in <c>WorldDefinitionValidator</c> (which runs at BOOT, at every live mutation, and on every undo-replay entry)
/// and the <c>WorldMutation.UpsertStateCell</c> compose arm, which refuses the same shape BY NAME at the verb rather
/// than letting the operator read a whole-document validation error for a cell they just typed.</para>
/// <para><b>Why the rule is now one line.</b> It used to also police VALUES, because a generator row's draw position
/// (<c>$cursor</c>) and dealt decks (<c>$deck&lt;n&gt;</c>) were CELLS an author could hand-write — a negative
/// cursor seeks to a position no draw could have reached, a deck bit past a context's alternative count is inert.
/// Draw bookkeeping now lives in typed row FIELDS at the drawing SITE
/// (<see cref="WorldStateRow.DrawCursor"/>/<see cref="WorldStateRow.DrawDecks"/>) rather than in the cell namespace,
/// so those shapes are refused by the field's own type and range check instead of by a string-keyed exception
/// carved out here. What remains is the original rule: the slot key is the only reserved key any row mints.</para>
/// </remarks>
public static class WorldStateReservedCells {
    /// <summary>Validates one <see cref="WorldStateRow.ReservedNamePrefix"/>-prefixed cell against the row that
    /// carries it.</summary>
    /// <param name="row">The row the cell lives on.</param>
    /// <param name="key">The cell's key (assumed to carry the reserved prefix — an ordinary key is always admitted).</param>
    /// <param name="reason">Why the cell was refused, in the author's own vocabulary, or empty on success.</param>
    /// <returns><see langword="true"/> when the row mints a cell by that key.</returns>
    public static bool TryValidateReservedCell(WorldStateRow row, WorldCellName key, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: row);
        reason = string.Empty;

        if (!key.Value.StartsWith(value: WorldStateRow.ReservedNamePrefix, comparisonType: StringComparison.Ordinal) || (key == WorldStateRow.SlotKey)) {
            return true;
        }

        reason = $"carries the reserved prefix '{WorldStateRow.ReservedNamePrefix}' — reserved cell keys are engine-minted, and this row mints none by that name";

        return false;
    }
}

/// <summary>The RATIFIED <c>state</c> section schema caps — read by <see cref="WorldDefinitionValidator"/>.</summary>
/// <remarks>
/// <para><b>The gauge range story.</b> A HUD gauge element (see <c>WorldHudElementKind.Gauge</c>) may bind to
/// <c>state.&lt;name&gt;</c> — legitimate only for a SLOT-shaped row (see <see cref="WorldStateRow.IsSlot"/>). The
/// simplest honest range a row can declare is BOTH-OR-NEITHER: a <see cref="CellKind.Int"/>/<see cref="CellKind.Fixed"/>
/// row either carries no <see cref="WorldStateRow.Min"/>/<see cref="WorldStateRow.Max"/> at all, or carries both
/// together with <c>Min &lt; Max</c> and every cell's own value inside <c>[Min, Max]</c> — a half-declared range (one
/// bound present, the other absent) is refused rather than guessed. A gauge bound to a row with no declared range, to
/// a <see cref="CellKind.Bool"/>/<see cref="CellKind.Text"/> row (which carry no range at all), or to a keyed row
/// (no single value to show) draws EMPTY at render time — the same "an unbound gauge draws empty" precedent every
/// other HUD gauge already follows, never a validation-time refusal (only the binding's EXISTENCE is validated,
/// matching the rest of <c>HudBindingVocabulary</c>'s refuse-unknown-by-name discipline).</para>
/// </remarks>
public static class WorldStateCapacity {
    /// <summary>The section's row-count ceiling.</summary>
    public const int MaxRows = 128;

    /// <summary>A <see cref="CellKind.Text"/> cell's value-length ceiling, in UTF-16 code units.</summary>
    public const int MaxTextValueLength = 256;

    /// <summary>The greatest value a <see cref="CellKind.Int"/> cell may carry — <see cref="FixedQ4816"/>'s own
    /// integer ceiling, because every engine READ of an int cell lifts it to fixed point
    /// (<c>WorldServer.ReadStateCell</c> feeds a world rule's gate and its live copy operand through
    /// <see cref="FixedQ4816.FromInteger(long)"/>, which THROWS outside this band). Stated as a document invariant so
    /// every ingress — boot file, console verb, addon decode, rule write-back, undo replay — refuses an
    /// unrepresentable cell BY NAME at the one validator, rather than admitting a value that kills the process on the
    /// first tick a rule reads it. A <see cref="CellKind.Fixed"/> cell carries RAW <see cref="FixedQ4816"/> bits and
    /// legitimately spans the whole <see cref="long"/>, so this band is an INT-cell rule alone.</summary>
    public const long MaxIntCellValue = (long.MaxValue >> FixedQ4816.FractionBitCount);

    /// <summary>The least value a <see cref="CellKind.Int"/> cell may carry — see <see cref="MaxIntCellValue"/>.</summary>
    public const long MinIntCellValue = (long.MinValue >> FixedQ4816.FractionBitCount);

    /// <summary>The IMPLICIT per-row cell-count ceiling — applies to EVERY <see cref="WorldStateRow.Cells"/>,
    /// slot-shaped or keyed alike (a slot never approaches it: exactly one cell), even when the author omits
    /// <see cref="WorldStateRow.Capacity"/>, so a row can never state no bound at all (unbounded growth is refused by
    /// construction, never by author diligence). An authored <see cref="WorldStateRow.Capacity"/> may only NARROW
    /// this, never widen it.</summary>
    public const int MaxCellsPerRow = 128;
}
