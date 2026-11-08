# Art and visual design

This directory contains art briefs, character model specifications, concept art
packages, and visual design guidance for the reference game. Art pages describe
the intended experience and review evidence; the authored world and shader
documents remain the implementation sources.

## Design hubs and concept packages

| Document or package | Scope | Description |
|---|---|---|
| [Caldera water pack](caldera-water-pack/README.md) | C3 water and visible melt | Six B-based references for glacier/lake, falls, river/bridge/pool, reservoir gauge and a matched-camera three-hour end state. |
| [Canyon discovery gallery](canyon-discovery-pack/README.md) | Major demo layout and interaction design | B-based portal court, sixteen-station shape workshop, Wonderland/link lab, six-game commons and studio/trail. |
| [Canyon workshop environment pack](canyon-workshop-pack/README.md) | Selected valley and gallery direction | B-based environment views, interior scenes, construction details, and modeller handoff. |
| [Valley and gallery visual references](valley-and-gallery.md) | Environment direction and real-life references | Source references, initial comparisons, and the selected B direction for a painterly, cartoony futuristic valley and gallery. |
| [Armored chibi hero implementation brief](armored-chibi-hero-brief.md) | Character design and motion | Aesthetic brief defining silhouette, chunky chibi anime armor, materials, and movement dynamics for the hero avatar within Puck's signed-distance renderer. |
| [Moth concept pack](moth-concept-pack/README.md) | Concept art and model sheets | The Moth's visual standard: the mech-suit turnaround, construction details, poses and expressions, flight, the off-suit Moth with her scale and fit inside the suit, the handheld at true scale, the flight-pack references and prompt provenance. |

## Concept art review

Concept art a modeller builds from carries no glaring mistake (the owner's
ruling). Every panel passes a written audit by both partners, the artist and the
checking partner, covering:

- attachment and continuity;
- joint range;
- physical coherence of the pose (thrust against motion, weight over the feet);
- agreement with the turnaround;
- the camera's perspective;
- compositing seams;
- the character's fixed facts.

A panel that fails is regenerated or removed, never kept with a caveat.

For the [caldera water pack](caldera-water-pack/review.md), the owner explicitly
assigns the primary artist as the deciding judge. Its written audit records
that scoped arrangement and does not claim checking-partner approval.

## The visual bar

The owner's ruling: "I need you to ensure that some bar is defined and adhered
to." The bar stands beside the frame bar, and a place meets both at once.

**The direction.** Painterly, with The Wind Waker and quality toon animation as
the character of the treatment; cartoony and slightly futuristic; Study B's
warm layered canyon cliffs, cream and ochre modules and deep teal
([valley and gallery](valley-and-gallery.md)). The concept packs here are the
references, and where a place has none, real-world reference and what a modern
stylized game shows supply it.

**The bar.** Every place in the debug world:

- reads at first sight as what it is: ice as ice, water as water, rock as rock,
  a cloud as a cloud, a glacier as a glacier;
- meets its reference panel's material hierarchy and contacts (the caldera
  pack's builder handoff is the model: grounded ice with crevasses and sun
  planes, transparent shallows over coloured depth, foam and spray where water
  breaks, wet stone where it falls, banks the water meets);
- carries the treatment: stylized shading with a held palette, never smooth
  generic shading over flat colour;
- has no unexplained shape, colour or object, and no seam, cut-out, blown-out
  or clipped region;
- holds across the day: night is a night sky with its moons and a mood, not a
  void over a lit model.

**Who decides.** Look intent is the lead's and the lanes' artistic freedom
(the owner's ruling): they draft it from these references and their own
knowledge and hold work to it; the owner corrects a specific miss and never has
to point out the obvious. Each place's guide (`worlds/debug/<place>.md` or its
section of `worlds/debug/debug.md`) names its reference panels and its look
intent in a sentence or two.

**How it is judged.** A look change is captured at its place's reference
views, in the scene-read order (position, place guide, `world.view.pick`
across the frame, the authored intent, then the pixels), and judged against
the reference by a reader that did not build it, under the headings above and
the [concept art review](#concept-art-review)'s attachment, physical coherence,
perspective and compositing checks. It lands when it passes; a failure is a
defect fixed at its cause, including the rendering feature the place needs.
The lead puts current captures beside their references before the owner on a
regular cadence ([orchestration](../../../.claude/skills/orchestration/SKILL.md#land-in-batches)).

## Authored avatars

Live avatar implementations, authored SDF primitives, and in-game controls live directly alongside the world assets:

- [Moth flight studio](../../../worlds/debug/avatars/moth.md)—Authored body geometry, vanity slots, controls, and live iteration recipes.

- [Moth courtyard](../../../experimental/src/Puck.World/Assets/worlds/moth-courtyard.md)—Quarantined prior art: a playable environment combining authored terrain, lighting, and avatar locomotion.

## Rendering aesthetics

For the mathematical principles, lighting models, material palettes, and signed-distance rendering techniques used to present this art, see:

- [SDF handbook](../../rendering/sdf/handbook/README.md)—A nine-chapter guide to signed-distance evaluation and shader pipelines.

- [SDF technical reference](../../rendering/sdf/reference/README.md)—Focused references on distance bounds, blending, and lighting formulations.
