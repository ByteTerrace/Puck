# The learn panel

A learn panel opens the maths behind what a terminal's screen shows, on that
screen. It comes in layers: a plain sentence first, then, on a tap, the formula
the engine runs, the same formula with its live value written in, its terms one
a row and its step by step. A tap on a term highlights it wherever it appears
and says what it means. Dragging the scrub bar changes a value, and the subject
and the formula's numbers follow at once. The standard package authors the
behaviour (`worlds/standard/learn-panel.puck`). A world places the panel's
regions, draws it with its own paints and says what each subject shows. The
debug world's shape stations are the worked use: each console's LEARN button
opens the maths of the exhibit on its turntable (`worlds/debug/shapes.puck`'s
`learnPaints` and `learnExhibits`, `worlds/debug/shape-station.puck`).

## The maths is the engine's

A formula on the panel is never typed by an author. A math text paint's
`@formula <reference>` line brings in the engine's own formula
(`SdfFormulaTypesetting`), typeset from the formula tree the query evaluator
and the kernels both run, so a change to a formula changes what every panel
shows ([the decision](../decisions/rendering.md#the-maths-a-screen-shows-is-the-engines-own-formula-tree)).
A reference is `shape:` or `blend:` and the catalogue entry's name:
`shape:Sphere`, `shape:Box`, `blend:SmoothUnion`. An author's own shapes use
the same primitives and blends, so their formulas come with them. `world.formula`
lists every reference, and `world.formula <reference>` prints one formula's
equation, terms and steps as markup.

The panel's texts are math text paints ([math text](../rendering/sdf/reference/text-and-glyphs.md#math-text)):
`learnMathsText(key)` writes the equation, the equation with values and the
steps, scrolled by `learnScrollText(key)`; `learnTermsText(key, glossary)`
writes a term a row and is set with `pitch: true` on the rows of the panel's
`terms` region. `learnGlossary` holds what each engine term is in plain words,
by the term's name in the formula; a row already shows the term's symbol, so a
meaning never repeats it or the code's name for the term. A world passes its own
glossary to replace it.

## Placing a panel

A world expands three things, all keyed by its panels' keys:

- `learnSubjectsState(subjects)` and `learnPanelState(keys)` in its
  `state.world`. A subject is `{ formula, sentence, scrub }`: the formula
  reference (or `""`), the sentence, and `scrub` either null or
  `{ term, value, least }`, the term's name in the formula, its value at full
  size and the least share of it the scrub reaches.
- `learnPanel(key, surface, terminal, drag, scrubFrom, scrubTo)` once a panel,
  with a rule of its own that writes the subject's index to the panel's
  `learnSubject` cell.
- A screen that responds to `learnOpen` with the panel's look (the scrub bar is
  `learnBarText(key)` set with `markup: true`, its ticks lit by the panel's
  rules), and a pointer
  surface whose regions are named `learn` (opens the panel), `back`, `layer`
  (the maths on and off), `terms` (a list region, a row a term), `steps` (the
  scrolling step by step) and `scrub` (the bar). `scrubFrom` and `scrubTo` are
  the bar's edges as shares of the surface's width; `drag` turns a drag's
  travel down the surface into scroll.

What a panel shows follows two cells, `learnSubject` (the subject's index) and
`learnOpen` (0 closed, 1 the sentence, 2 the maths), whatever writes them: a
tap, a state write or a world's own rule. One rule (`learn-view`) copies the
subject's sentence, scrub term and formula, names the layer button and resets
the term and scroll whenever either moves, and the formula is only held while
`learnOpen` is 2. The value and the scrub bar are derived by a second rule from
the subject's `learnSize`, so a drag, a state write and a reset all show the
true number.

The wheel scrolls a panel for a seat engaged at its terminal. It arrives as the
standard's `scroll` channel, which the terminal page and pointer capture bind
to the mouse wheel; a world lists `scroll` in its `inputModes` to make it live.

## Scrubbing reshapes without a bake

A subject's scrubbed size is the share in its `learnSize[<index>]` cell, which
its creation's `sizeBy` reads, and `sizeAbout` names the point the size scales
about, so an exhibit on a stand grows and shrinks about its own centre. Only
the root matrix changes, so nothing bakes and the drawn subject follows the
finger. The term's value written into the formula, the scrub's reading and the
bar's lit ticks all follow the share, so they stay true when a state write moves
it. A term that changes the contour itself, such as a blend's softness,
would need a new mesh; until a blend's smooth radius can bind state the panel
shows its value and offers no scrub for it
([open items](../plans/open-items.md#learn-panel)).

## Reading it back

`world.state learnOpen`, `learnTerm`, `learnScroll` and `learnValue` read a
panel's state. `world.texts` reads each math text as the last frame laid it
out: the formulas it brings in, the highlighted term, the values it shows, the
scroll asked and the offset shown against the most it can scroll, its lines and
its glyphs.
