# The affordance coverage check, specified against a placeholder

**Written 2026-08-02 while the binding model is under owner review.** It
deliberately does NOT propose an affordance vocabulary or a binding shape —
those are the owner's, and a check written against a guess at them would be a
fourth set of specifics wearing a verification costume. It is written against a
PLACEHOLDER: some machine-readable set **A** of world affordances, some set of
human surfaces that reference members of **A**, and some set of addon channel
verbs that also reference members of **A**.

Everything below survives any shape those take, because every predicate is
derived from a defect that already happened rather than from a design.

## Why this exists

Five defects were found in the current binding surface. They look like five and
they are one:

| Defect | Artifact | State |
|---|---|---|
| `gamepad.buttonNorth` unbound in the base play page | well-formed | unreachable in play |
| The modifier gate | well-formed, live code | never once fired |
| Linux input | well-formed | never worked |
| The shipped addon's dead-reckon target | well-formed | aimed at a deleted room's furniture |
| The addon's proximity interact | well-formed | silently produced nothing |

**Every artifact was well-formed. Every one was inert. Nothing anywhere could
say so.** That is the defect — not the individual binds, which are taste, and
not their number, which is scale. A redesign producing a better set of binds
with the same absence of a coverage check decays the same way, and faster,
because it will be larger.

## The three predicates

Each is traced to an instance above. A check that fired on all three would have
caught the first, second and fifth before any of them shipped.

### A — Dangling reference

*A surface references a name that **A** does not declare.*

The easy half, and the one every schema validator already suggests. It catches
the deleted-room case in its general form: an artifact naming something that has
since stopped existing. It would not have caught North.

### B — Unreachable in context

*An affordance declared available in a context that no surface in that
context's set binds.*

**The context qualifier is the whole predicate, and it is the correction the
measurement forces.** North was not unreachable — it was bound in the editor
group and absent from the base play page. A check asking "can any page reach
this affordance" passes North with a clean sheet. The question that catches it
is per-context: *this affordance claims to be available while playing, and
nothing a player can press while playing reaches it.*

This is the same shape as the architecture gate's narrow-versus-wide lesson. The
wide form of a reachability rule is trivially satisfiable and therefore silent
forever; the narrow form yields findings.

### C — Unenterable precondition

*A surface whose precondition — a modifier, a page, a chord prefix — cannot be
produced by anything in that context.*

The subtlest, and the one that catches the modifier gate. It is not a question
about the affordance list at all; it is reachability over the PRECONDITION
graph. A chord requiring a modifier that nothing in the context can enter is
live code that can never run, and it is indistinguishable from working code by
reading.

## What the check must not do

**It must not be satisfiable by widening.** An implementation tempted to treat
"declared available in a context" as optional metadata, or to default it
permissively, converts B into A and re-creates the silence. The declaration of
where an affordance is available is load-bearing input, not annotation.

**It must not be silenced by exemption without a reason.** Some affordances are
legitimately unbound — reserved, developer-only, or awaiting a surface. Those
need a NAMED exemption carrying why, in the shape the backend quarantine already
uses: one named exception with its argument beats a rule quietly relaxed to
admit it. An exemption list that grows without reasons is the check being
switched off one line at a time.

**It must not report a caveat it has not measured.** If a context has no
surfaces at all, say that; do not emit "0 unreachable" for a context that was
never examined. Empty-because-clean and empty-because-unexamined are
indistinguishable in a report and opposite in meaning.

## Where it runs, which is the half that decides whether it is real

A generator nobody runs is a hand-maintained file with extra steps, and a check
nobody runs is a report that happens to have an exit code. Both of those are
findings this campaign has already made, in three separate places.

So the check needs a runner, and its home follows from what it reads. Unlike the
architecture gate, this one reads DOCUMENTS rather than project structure, which
puts it in two places for two audiences:

- **The document gate**, so a world carrying an unreachable affordance is
  refused at parse rather than discovered on somebody else's machine. This is
  the same argument that put the per-lane request vocabulary at document parse.
- **A `puck` verb**, so an author can ask the question while authoring rather
  than only at load. Reporting surface, explicitly not the authority.

Both, or the check is decorative in the way three prior findings have already
described.

## What it needs from whatever shape lands

Stated as requirements on the design rather than as a design:

1. **A** must be machine-readable and singular. Two declarations of what the
   world can do is the second-source defect this check exists to police, and a
   check cannot police the thing it is written against.
2. An affordance must declare **where it is available**, or predicate B has no
   input and degenerates into A.
3. Preconditions must be enumerable, or predicate C cannot be evaluated at all.
4. Both surfaces — human bindings and addon channel verbs — must reference **A**
   by name rather than restating it. The check compares references against a
   declaration; it cannot compare two declarations and know which is right.

Requirement 4 is worth reading twice: it is the reason this document does not
propose a vocabulary. **The human surface and the addon surface are one question
being answered twice** — a `(Channel, Verb)` opcode space is an affordance list
under another name — and the check is cheap when they share a source and
impossible when they do not.
