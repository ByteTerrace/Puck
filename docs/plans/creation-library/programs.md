# Authoring shapes as programs at scale

## Summary

**[inference]** Puck can take more from a library of adjustable recipes than from
a warehouse of finished objects. Its source geometry already is a program. The
useful additions are ways to find a family, understand its controls, compose its
parts, retain its source and ancestry, and establish its cost in the receiving
world. Generation can propose those programs; it cannot certify their meaning,
usefulness or admission merely by producing an attractive image.

**[inference]** Five lessons most change the options: publish meaningful design
families; compose through relationships; keep source and provenance alongside
derived previews; generate through execution and inspection; and evaluate edits,
contact and population cost separately from visual resemblance. These are
research recommendations, not a decision to build a service or adopt a format.
The round's ruling is “Research, no copying”: no external content, implementation,
training set or format is proposed for incorporation.

Evidence labels apply to the paragraph or table cell they introduce:
**[opened]** means an opened primary page or repository file, with a short pasted
quotation and a link or file location; **[summary]** means an unopened search
result and cannot support a decision; **[inference]** means this memo's reasoning
or proposed experiment. Reported research results are the authors' results, not
measurements reproduced in Puck. No generation model, editor or external asset
was run for this writing lane.

## Puck's starting point

| Evidence | Consequence for this research |
|---|---|
| **[opened]** The [SDF authoring skill](../../../.claude/skills/sdf-authoring/SKILL.md), `.claude/skills/sdf-authoring/SKILL.md:9`, says “Shapes are signed distance fields composed by blends, not meshes”. | **[inference]** A geometry library should preserve an editable field construction. A mesh reconstruction result is not automatically a reusable creation. |
| **[opened]** The [authoring guide](../../authoring/README.md#source-document-and-running-state), `docs/authoring/README.md:19`, says “The DSL is an authoring language that lowers to JSON”; decompilation “cannot reconstruct the author's original templates or comments”. The [DSL skill](../../../.claude/skills/puck-dsl/SKILL.md#grammar-that-bites), `.claude/skills/puck-dsl/SKILL.md:62`, names a template as “a parametric block”. | **[inference]** Retain authored source, parameter bindings and imports as well as the canonical compiled document. Reverse-generating source from the compiled rows loses the family design. |
| **[opened]** [AGENTS.md](../../../AGENTS.md#puck-is-pre-release), `AGENTS.md:50`, says an author “imports, overrides by id, or replaces”; `AGENTS.md:179` says derived products “are caches, never the truth”. | **[inference]** Concrete standard designs belong in `worlds/standard/`; debug exhibits exercise them. Variants should not become unrelated copied meshes or engine defaults. |
| **[opened]** The [hero brief](../../game/art/armored-chibi-hero-brief.md#3-implementation-route), `docs/game/art/armored-chibi-hero-brief.md:122`, excludes a “mesh import route, or separate character renderer”. | **[inference]** CAD and mesh-generation research is evidence about authoring mechanisms, not authorization for a second asset pipeline. |
| **[opened]** The [world model](../../architecture/worlds.md#the-idea-in-plain-terms), `docs/architecture/worlds.md:33`, says “Every world is the same kind of document, compiled and simulated the same way.” [Prototype delivery](../../architecture/worlds.md#engine-flow-and-boundaries), `docs/architecture/worlds.md:915`, requires that “its bytes must match the pin”; `docs/architecture/worlds.md:922` says “A content pin grants no access.” | **[inference]** Content identity, permission to fetch, permission to execute and permission to remix are separate questions. Existing prototype delivery is not evidence of a complete hosted source-library service. |
| **[opened]** [AGENTS.md](../../../AGENTS.md#core-rules), `AGENTS.md:358`, says “Same document and same input give bit-identical state”; `AGENTS.md:220` attributes cost to “what the author wrote in `world.budget`”. | **[inference]** An offline stochastic generator may propose source; accepted simulation still follows Puck's deterministic contract. A small source file is not proof of cheap expansion, bake, contact or residency. |

**[inference]** The working invariant is one authored source of truth through
Puck's ordinary compilation and admission machinery. The authoring guide and
`WorldCompiler` contract describe that route; this research adds no enforcement.
Its guarantee stops before author intent, aesthetic quality, safe parameter
ranges and arbitrary cross-authority reuse. Those need evidence of their own.

## Parametric libraries and controls for non-experts

### OpenSCAD and BOSL2: reuse includes a placement language

**[opened]** OpenSCAD's [documentation entry point](https://openscad.org/documentation.html)
routes users to modules, multiple scripts and libraries. Its linked
[Customizer manual](https://en.wikibooks.org/wiki/OpenSCAD_User_Manual/Customizer)
says “one does not need to edit the code”; it exposes sliders, choices and other
controls, and saves parameter values as variants. This is the project's linked
community manual, rather than an independent usability study.

**[opened]** BOSL2's [attachment overview](https://github.com/BelfrySCAD/BOSL2/wiki/Tutorial-Attachment-Overview)
introduces “attachables” that can “Position or attach shapes relative to parent
objects”. Anchors, orientation, spin and tags make placement and Boolean intent
part of the reusable interface. Its [wiki](https://github.com/BelfrySCAD/BOSL2/wiki)
provides “Function Index” and “Topics Index” discovery routes. The attachment
tutorial itself warns that the features can initially seem complex.

**[inference]** The transferable mechanism is a callable family with defaults,
examples and a relational interface. For Puck, an authored bench could expose
seat height, span, slat count and palette roles; its legs follow the seat and
ground through named relationships. A beginner chooses a variant and adjusts
those controls; an expert opens the same `.puck` source. A preview grid showing
the range is more informative than one thumbnail. Neither OpenSCAD syntax nor
BOSL2's implementation is needed. Exposing every primitive transform as a slider
would merely relocate the expert task into a crowded form.

### CadQuery and build123d: design intent survives in relationships

**[opened]** [CadQuery's introduction](https://cadquery.readthedocs.io/en/latest/intro.html)
aims to “Create parametric models that can be very easily customized by end
users”. It separates the library from GUIs and demonstrates selecting a face,
creating a workplane, then adding holes relative to it. Its model kernel is
OpenCascade. [build123d's concepts](https://build123d.readthedocs.io/en/latest/key_concepts.html)
describe topology as the “hierarchical structure of geometric elements”; its
[examples](https://build123d.readthedocs.io/en/latest/introductory_examples.html)
show the same designs in “Builder mode” and “Algebra mode”, including functions,
loops, placements, selectors, fillets and sweeps.

**[inference]** These systems show how a program can retain construction intent
instead of a fixed result. But their edges, faces and workplanes belong to a
boundary representation (B-rep), not to an SDF source. In Puck, author-named
parts, attachment frames and profile parameters are the promising equivalents.
“Put the handle on this authored mount” is a better candidate than “select the
third baked face”: tessellation is derived and can change. Before proposing any
new anchor mechanism, an implementation lane must search the existing world and
creation vocabularies; this memo has not established that such machinery is
missing. Multiple ergonomic spellings are useful only if they lower to the same
canonical meaning and counted work, as Puck already requires.

### Houdini digital assets: the library item can be a generator

**[opened]** SideFX's [digital asset introduction](https://www.sidefx.com/docs/houdini/assets/intro.html)
says “reusable custom nodes with their own user interfaces” and describes
“promoting parameters”. An asset packages its internal network and can include
support files. Its examples include a building workflow exposing architectural
style, floor count and windows, and character controls hiding implementation
detail. Assets can be distributed centrally; the
[asset documentation index](https://www.sidefx.com/docs/houdini/assets/index.html)
explicitly distinguishes “Two types of asset versioning”.

**[inference]** The important split is the expert's generator and the user's
small, intentional control surface. A Puck library can contain a railing recipe
or foliage family, not only a particular railing or tree. Controls, units,
relationships and representative presets belong with its authored definition.
The generated field, material and behaviour must still be ordinary Puck
programs, inspected and costed through the same route as hand-authored content.
An HDA runtime, external Python execution or central mutable update mechanism
does not follow from this lesson. Across Puck authorities, a pinned dependency
and an explicit author update are preferable to an upstream edit silently
changing every zone's accepted geometry.

## SDF catalogues, sculpting and sharing

### Quilez and Shadertoy: runnable explanations are a library surface

**[opened]** Quilez's [distance-function catalogue](https://iquilezles.org/articles/distfunctions/)
connects primitives, operators, explanations and runnable Shadertoy examples.
It labels “Ellipsoid - lower bound” separately from exact primitives and labels
smooth combinations “bound, bound, bound”. Its repetition discussion warns that
the simple expression needs symmetry at tile boundaries. The page was retrieved
directly as HTML when the web reader could not fetch it; no formulas are copied
here.

**[inference]** A catalogue should teach what a shape operation guarantees, not
just what it resembles. Puck already exposes field classifications and contact
admission through its [SDF authoring workflow](../../../.claude/skills/sdf-authoring/SKILL.md#verifying).
A reusable creation should make those distinctions visible for its selected
parameters. A field useful for rendering need not be an exact Euclidean
distance, and neither implies deterministic contact support. A screenshot
cannot establish any of these properties.

**[opened]** Shadertoy co-creator Pol Jeremias's
[publication page](https://www.poljeremias.com/publication/siggraph2013/) describes
live coding and says creations are “shared with the community”, for
“inspiration, learning, and teaching”. **[summary]** A
[search result describing the platform](https://resources.proceduralpixels.com/resource/r048/)
calls out forking and remixing; that secondary page was not opened. Shadertoy's
own about and terms pages were inaccessible to the web reader, so this memo does
not assert a platform-wide reuse licence or a verified present-day fork UI.

**[inference]** The useful cultural mechanism is seeing a result beside its
executable explanation, modifying it, and sharing a new explanation. In Puck,
show the creation, its recipe and an editable instance in a world. Discovery
could follow a technique, an authored part or a remix lineage as well as an
object name. A complete shader scene with its camera, lighting and timed effects
is not automatically an isolated shape. Repeated copying without dependency or
attribution records would also fight Puck's single-implementation rule.

### Dreams: field source, approachable tools and visible ancestry

**[opened]** Media Molecule's
[Learning from Failure talk abstract](https://advances.realtimerendering.com/s2015/index.html)
describes “Operationally Transformed CSG trees”, evaluated into SDFs and then
“dense multi-resolution point clouds”. It describes several abandoned rendering
approaches and an eventual renderer shaped by aesthetic as well as technical
choices. This is evidence about that talk's pipeline, not a claim about every
detail of the final shipped implementation. The linked slide PDF exceeded the
web reader's size limit; its slides were not reviewed.

**[opened]** Media Molecule's [art tips](https://docs.indreams.me/en-US/create/top-tips/art-top-tips)
include “Make Use of Kits” and “Use Genealogy to Find Out How People Made
Things”. Its [remix guide](https://docs.indreams.me/en-US/create/resources/videos/tools-and-tips/remix-creations-and-use-genealogy)
says “A remix is a new version of a creation”; genealogy exposes contents and
remixes. That guide carries its own warning that some details may be outdated.

**[inference]** Dreams is particularly relevant because source construction and
presentation representation need not be the same. Puck already takes that
separation through field-derived mesh caches; borrowing point rendering is not
necessary. The authoring lesson is to make insertion, sculpting, adjustment,
inspection and learning from another creation one continuous activity. An MMO
library could be encountered as a furnished pavilion or garden in the debug
world, with examples drawn from the standard package. Genealogy becomes a
dependency and attribution graph that survives copies across hosts; it cannot
be mistaken for a signature granting access or gameplay authority. Puck should
not inherit Dreams' painterly treatment as an engine default.

### Clayxels and MagicaCSG: direct manipulation can preserve field structure

**[opened]** The [Clayxels manual](https://www.clayxels.com/uploads/4/7/2/7/47277855/clayxels_documentation.pdf),
pages 2–4, introduces a container, nested objects, shape and blend controls, and
advises “Split your sculpt into many containers”. Ordering changes composition.
Its [FAQ](https://www.clayxels.com/faq.html) says it “uses SDF and voxels” and is
“a GPU hungry system”; it describes point-cloud or mesh output rather than
heavy per-pixel marching, and mesh freezing as a deployment option.

**[opened]** Ephtracy's [MagicaCSG page](https://ephtracy.github.io/magicacsg.html)
calls it a “Nondestructive SDF Modeler” and lists “Independent Volume Resolution”,
instancing, subgroup Booleans, arrays and editable stroke controls. Its update
notes describe dragging strokes, gizmos, snapping and duplication. The page was
retrieved directly as HTML after the web reader failed; product claims and UI
descriptions were read, not tested.

**[inference]** Both demonstrate a route from small selectable parts to a
complex field without demanding that a beginner write every formula. For Puck,
direct manipulation should edit the same authored source or its explicit
parameter bindings, retaining names, grouping and order. Generated rows need a
clear route back to their template: the
[existing save workflow](../../../.claude/skills/sdf-authoring/SKILL.md#the-loop)
already distinguishes literal rows from rows generated by loops or templates.
Neither mesh export as the new master nor a second proprietary sculpt format
is warranted. Container decomposition is also a cost and seam choice, not a
promise that any number of interacting fields will be cheap.

## Generating shapes as programs

### What the evidence actually reaches

The systems below solve different tasks. Reconstruction fits an observed shape;
unconditional generation samples a family; text-conditioned generation follows
a description; an agent iteratively edits and checks a program. Their scores
cannot be ranked on one common scale.

| System and evidence | Mechanism and demonstrated quality | Limit and meaning for Puck |
|---|---|---|
| **[opened]** [CSGNet paper](https://arxiv.org/pdf/1712.08290), sections 3–4 and table 4: “compact and easy-to-interpret sequence of modeling instructions”. | **[opened]** Predicts primitive/Boolean programs; supervised synthetic training, beam search and policy-gradient refinement. Its synthetic 3D experiment executes on a 64 × 64 × 64 grid with spheres, cubes and cylinders. Table 4 reports 89.2% IoU at beam size 10, versus 73.2% for nearest-neighbour retrieval. | **[inference]** This establishes constrained program induction, not arbitrary sculpt reconstruction or correct design intent. Puck could search a bounded subset of its own field vocabulary; adopting the voxel output or this restricted grammar is unnecessary. |
| **[opened]** [CSG-Stump abstract](https://arxiv.org/abs/2108.11305): “outputs of a constant dimensionality”. | **[opened]** Learns from point clouds without ground-truth programs, using complement, intersection and union layers. The authors establish representational equivalence to CSG and report reconstruction improvements over prior CSG methods. | **[inference]** A regular search representation can help learning. Equivalence in representability says nothing about equal program length, execution cost, or preservation of Puck's smooth operators. Treat it as a synthesis strategy, not a replacement source or VM. |
| **[opened]** [ShapeAssembly project](https://rkjones4.github.io/shapeAssembly.html): “hierarchical connected assembly of part proxies cuboids”. [Paper](https://rkjones4.github.io/pdf/shapeAssembly.pdf), sections 7–8. | **[opened]** Generates programs with cuboid parts, attachments, hierarchy and symmetry; a differentiable interpreter fits continuous parameters. Evaluates geometric fit, distributional quality, rootedness and stability under simulated forces. Parameters support explicit edits and interpolation. | **[opened]** Its extraction rejects some part orientations and programs with more than 12 cuboid declarations. **[inference]** The result is structural scaffolding, not finished detailed surfaces. Puck's field programs could fill the parts; relationships are more valuable than importing cuboids as a visual style. |
| **[opened]** [ShapeCoder abstract](https://arxiv.org/abs/2305.05661): “capture common patterns (both structural and parametric)”. | **[opened]** Jointly discovers abstraction functions and programs explaining primitive-decomposed shapes. A recognition network and conditional e-graph rewriting support abstraction use. Evaluation includes dataset compression and downstream usefulness; functions remove unnecessary degrees of freedom. | **[inference]** This suggests learning reusable families from authored Puck examples, with author review. Compression does not itself prove friendly controls, valid extremes or visual quality. Do not substitute a learned latent vector for understandable parameters. |
| **[opened]** [Text2CAD proceedings abstract](https://proceedings.nips.cc/paper_files/paper/2024/hash/0e5b96f97c1813bb75f6c28532c2ecc7-Abstract-Conference.html): “visual quality, parametric precision, and geometrical accuracy”. | **[opened]** Autoregressively generates parametric CAD sequences from prompts at different expertise levels. About 170K models and 660K annotations are produced through a DeepCAD annotation pipeline using language and vision-language models. | **[inference]** Non-expert descriptions and expert procedural instructions deserve separate evaluation. Synthetic text annotations are not evidence of success with messy author intent. Puck needs descriptions paired with its own programs, not a sketch/extrusion token format. Only the abstract was reviewed here; no numerical win is inferred. |
| **[opened]** [CAD-Recode project](https://cad-recode.github.io/) and [paper](https://arxiv.org/html/2412.14042v2): “sketch-extrude sequences as Python code”. | **[opened]** Point clouds become editable CadQuery programs, trained on one million procedurally generated examples. The project table reports DeepCAD IoU 92.0 and invalid ratio 0.4, and Fusion360 IoU 87.8 and invalid ratio 0.5, in its percentage columns. Paper section 5.2 separately demonstrates LLM edits and question answering. | **[inference]** Native code output plus synthetic paired observations is promising. These benchmark results concern sketch/extrude reconstruction, not unrestricted use of every operation Python or CadQuery can express. A scanner-to-CAD-to-mesh route would add no necessary capability to Puck's field authoring pipeline. |
| **[opened]** [CAD-Coder paper](https://arxiv.org/html/2505.14646v1), metrics, table 2 and limitations: “generalization remains heavily prompt-dependent”. | **[opened]** A fine-tuned vision-language model writes CadQuery from images. On 100 GenCAD-Code test samples it reports 100% valid execution and 0.675 best-aligned IoU. The IoU metric normalizes translation and scale, chooses the best principal-axis alignment, and excludes non-executing scripts. | **[opened]** The authors report difficulty with perspective, dimensions and proportions. **[inference]** Correct code is plainly weaker than correct geometry. This metric cannot establish true size or orientation in a Puck world, let alone collision and usable parameterization. |
| **[opened]** [CAD-Assistant paper](https://arxiv.org/html/2412.13810v3), sections 3–4: “iteratively executed on a Python interpreter”. | **[opened]** A vision-language planner uses FreeCAD and tools for rendering, constraints and cross-sections. Human review of 200 trajectories reports 98.5% tool-use validity. The failure analysis of 100 misclassified question-answering trajectories attributes most failures to reasoning or misreading renders. | **[inference]** Tool feedback helps beyond one-shot code completion. The 98.5% is tool use, not a rate of correct finished designs. For Puck the tools should compile, inspect fields, capture multiple views, exercise edits and report cost through existing machinery. |

**[opened]** The newer [Text2CAD-Bench abstract](https://arxiv.org/abs/2605.18430)
describes 600 human-curated cases spanning primitive geometry, advanced topology,
freeform surfaces and applications beyond mechanical parts, with paired expert
and non-expert prompts. It reports that performance “degrade substantially on
complex topology and advanced features”. Only the abstract was reviewed; its
model rankings and quantitative results are unconfirmed here.

**[inference]** That warning qualifies the narrow benchmark successes above;
it does not negate them. A library can first make common parameter changes
reliable, then evaluate composition and new program synthesis separately.
There is no evidence here that a general LLM reliably writes arbitrary,
efficient, contact-correct Puck SDF programs from one image.

### An implicit function is not necessarily an authored program

**[opened]** [Shap-E's abstract](https://arxiv.org/abs/2305.02463) generates
“parameters of implicit functions” using an encoder and conditional diffusion,
with mesh and neural-radiance-field rendering. It reports quality comparable
to or better than Point-E on its task.

**[inference]** This is a useful boundary case: being implicit does not make
output a short, named, composable source program. A learned field's weights do
not automatically expose “seat height” or “petal count”, nor prove a distance
bound suitable for Puck's contact or field execution. Neural-field work can
inform proposals and evaluation without becoming Puck's source representation.
Likewise, optimizing parameters inside a fixed program differs from discovering
the program structure. Both are useful, but their failure modes and authorship
costs differ.

### How to judge quality without confusing the tasks

**[inference]** A Puck evaluation should retain the papers' useful distinctions
and add the properties of a world. No single similarity score should decide
acceptance, and no new recorded gate or fingerprint is proposed.

| Question | Evidence to collect in a bounded Puck trial | Failure it exposes |
|---|---|---|
| Does it execute and enter the engine? | Compile and validate ordinary source; inspect diagnostics, `creation stats` and `creation shape`. Count failures among all attempts, not just renderable outputs. | Valid-looking text, unsupported contact operations, scope refusals and expansion blowups. |
| Does it mean the requested thing? | Several views, silhouettes and field slices; verify dimensions, units and named part relationships without aligning away world scale or orientation. | A convincing front image with the wrong back, hidden holes, inverted axes or an unusable size. |
| Does it remain editable? | Ask for held-out edits: widen without changing height, add a slat without drifting spacing, swap a material role, change the handle side. Compare the requested relationships before and after. | Literal coordinate soup, accidental couplings, copied variants and spurious controls. |
| Is the field suitable for use? | Inspect field guarantees and deterministic contact admission; exercise the creation in its intended physical role. | A rendered recess that contact ignores, a loose distance estimate, or a moving blend that stretches. |
| Does the family cover useful variety? | Sample within the authored domain, including boundaries and invalid requests; separate new topology from parameter variation and compare with retrieval plus parameter editing. | Memorized examples, collapsed variation, implausible extremes and needless full regeneration. |
| Does it scale in a world? | Attribute expanded instructions, bake groups, actual meshes, contact work, resident bytes and repeated-instance cost through existing reports and `world.budget`. | A tiny recipe expanding into expensive unique content across thousands of bodies or zones. |
| Is it useful to a person? | Observe completion and correction effort for novices and experienced authors; keep failures and requested edits visible. | A higher IoU with more repair work or controls that users cannot understand. |

**[inference]** Geometry metrics remain helpful as diagnostics. IoU measures
overlap; Chamfer distance measures sampled surface proximity. Neither alone
establishes topology, dimensions, part relationships or parameter semantics.
ShapeAssembly's physical checks extend geometric resemblance, but a proxy-cuboid
simulation is not a check of the final Puck contact field. CAD-Coder's normalized
alignment is reasonable for its image task but would conceal some world-placement
errors. An LLM judge can suggest corrections; it should not be the sole oracle
for a geometric property that Puck can inspect directly.

## Lessons for Puck

### 1. Publish design families with meaningful controls

**[inference]** General mechanism: useful abstraction removes irrelevant degrees
of freedom while retaining the changes people want. OpenSCAD Customizer, HDAs
and ShapeCoder approach this from UI, packaging and learning respectively.
In Puck it becomes one authored template family with named, unit-aware controls,
palettes, representative variants and source access. A novice chooses a bench;
an expert can replace its program. The strongest case against templates is that
a single fixed object is quicker to publish. Offer fixed creations too, but do
not flatten a family to many unrelated results merely to fill a catalogue.
The author's control over concrete design, recorded in AGENTS.md, settles where
these decisions live: authored packages, not a C# catalogue of object types.

### 2. Compose through intent rather than output topology

**[inference]** General mechanism: relationships survive parameter changes better
than independently adjusted coordinates. BOSL2 and ShapeAssembly make that
explicit; CAD systems express a related idea through topology selectors.
In Puck it becomes author-named attachment frames and relationships in source,
lowered through existing generic mechanisms. This closes a class of drifting
parts when a variant changes size. The objection is extra interface work for
small one-off props; retain direct transforms there. Do not require an assembly
language for every sculpt, or expose baked mesh faces as stable handles.

### 3. Share the recipe and its ancestry; derive the presentation

**[inference]** General mechanism: runnable examples teach, dependency graphs
explain reuse, and derived representations serve presentation. Shadertoy,
Dreams and the SDF sculptors supply complementary evidence. In Puck, source,
bindings, dependency identities and attribution are distinct from canonical
creation bodies, preview images and mesh caches. Equivalent compiled meaning
may share derived geometry even when sources differ; source ancestry must not
be inferred from a content hash. Central hosting makes discovery convenient,
but Puck's per-world trust model requires independent fetch authorization and
receiving-world admission. A library entry should not grant ambient rights to
execute behaviour or alter the receiving world's state.

### 4. Generate proposals through the ordinary authoring loop

**[inference]** General mechanism: choosing a program structure, fitting its
parameters, executing it and observing failure are complementary operations.
CSGNet, CAD-Recode and CAD-Assistant support different pieces. In Puck, begin
with retrieval of an authored family and parameter edits, then compose existing
parts, then attempt new source where needed. Compile, inspect, capture and revise
with the existing tools. Preserve the editable result and diagnostic context;
generation has no privileged runtime path. A specialised trained generator may
eventually outperform retrieval, but the evidence does not justify building one
before measuring retrieval plus editing on real Puck author tasks.

### 5. Evaluate the whole family in a populated world

**[inference]** General mechanism: a successful sample can hide failed attempts,
bad edits, unsupported use cases and expensive deployment. In Puck, judge useful
parameter regions and task success, not just a hero render. Check source
equivalence, contact suitability and counted work, then exercise repeated
instances and deliberate unique variants in the debug world. One shared template
does not imply one shared bake after its parameters change. This closes the
temptation to equate compact source with cheap MMO content. Quiet-machine frame
measurements belong to the lead's scheduled window; measured cost informs the
author instead of forcing a single visual style or banning expressive spellings.

## What Puck should not borrow

- **[inference]** No external content, code or dataset ingestion from this round.
  The owner's inspirational scope is narrower than an asset or training-data
  acquisition project; sharing on a website is not proof of permission to reuse.
- **[inference]** No CAD kernel, mesh converter or proprietary sculpt format as
  an intermediate master. Those would duplicate the existing field pipeline.
  Learn parameter, relationship and UI patterns while keeping Puck programs.
- **[inference]** No claim that every visually successful “SDF” is an exact
  distance or a supported contact field. Quilez's own classifications qualify
  that shorthand. Preserve Puck's explicit guarantees and admission checks.
- **[inference]** No fixed voxel resolution, cuboid-only grammar, twelve-part
  ceiling or regular CSG learning layout as an authoring restriction. They help
  particular research tasks; they do not define Puck's creative range.
- **[inference]** No generic Python or shader execution permission attached to
  a downloaded shape. Generated content enters ordinary Puck compilation and
  receiving-world policy; a pin authenticates bytes, not their goodness or rights.
- **[inference]** No global “latest” mutation, opaque latent-only controls,
  obligatory painterly look, or similarity leaderboard treated as acceptance.
  Each would discard useful author control or hide a property Puck must expose.

## Open questions and bounded next steps

**[inference]** These are research questions for the creation-library lead to
assign after synthesis, not unimplemented promises or blockers for this memo.
No source or engine change was authorized in this lane. Each trial should use
authored content in the standard package and prove its composition in the debug
world; no experimental world needs improvement or execution.

| Question | Owner and trial | Evidence that decides the option |
|---|---|---|
| What is the smallest useful family interface? | Library lead with an authoring lane: one bench, one railing and one foliage family, using existing mechanisms before adding any. | A newcomer makes named variants and one relational edit; invalid requests explain the authored constraint. Record corrections and whether source editing was needed. Choose controls from those tasks, not every available scalar. |
| Can direct manipulation preserve source intent? | DSL/authoring lane: trace edits to a literal row, a template instance, a loop output and an imported override. | Each edit either changes the correct owning source/binding or explicitly explains why it cannot. Recompile and compare intended meaning; never silently flatten the source. This is an investigation of the existing save contract before a new editor design. |
| How much generation is needed beyond retrieval? | Library lead: compare manual selection, text retrieval plus parameter editing, and an execution-feedback code agent on the same Puck tasks. | Count useful completed tasks, all failures, correction steps, unsupported requests and retained editable structure. Include organic and structural forms and held-out composition; paper CAD scores do not decide this. |
| What must travel across authorities? | Federation and library leads: map source recipe, expanded creation, dependencies, rights and preview delivery against existing prototype fetches. | An authorized pinned fetch succeeds; missing permission, changed bytes or a denied dependency is refused without silently substituting content. Distinguish the future source library from the already documented prototype body cache. |
| How do useful variants affect MMO cost? | Rendering/physics lead, in a scheduled measurement window: compare repeated identical creations with many distinct parameter sets and moving parts in the debug world. | Separate shared-program work, unique bakes, residency, contact and frame work. Decide preview/residency policy from these results, keeping the source program intact. A repeated-field formula alone cannot predict total world cost. |

## Source map and limits

The inline links identify the exact pages supporting each finding. The main
primary-source groups are:

- Author controls and sharing: [OpenSCAD documentation](https://openscad.org/documentation.html),
  its linked [Customizer manual](https://en.wikibooks.org/wiki/OpenSCAD_User_Manual/Customizer),
  [BOSL2 attachments](https://github.com/BelfrySCAD/BOSL2/wiki/Tutorial-Attachment-Overview),
  [CadQuery](https://cadquery.readthedocs.io/en/latest/intro.html),
  [build123d](https://build123d.readthedocs.io/en/latest/introductory_examples.html)
  and [Houdini digital assets](https://www.sidefx.com/docs/houdini/assets/intro.html).
- Fields, tools and remix: [Quilez's catalogue](https://iquilezles.org/articles/distfunctions/),
  [the Shadertoy creators' publication](https://www.poljeremias.com/publication/siggraph2013/),
  [Media Molecule's rendering abstract](https://advances.realtimerendering.com/s2015/index.html),
  [Dreams remix guidance](https://docs.indreams.me/en-US/create/resources/videos/tools-and-tips/remix-creations-and-use-genealogy),
  [Clayxels documentation](https://www.clayxels.com/uploads/4/7/2/7/47277855/clayxels_documentation.pdf)
  and [MagicaCSG](https://ephtracy.github.io/magicacsg.html).
- Program structure: [CSGNet](https://arxiv.org/pdf/1712.08290),
  [CSG-Stump](https://arxiv.org/abs/2108.11305),
  [ShapeAssembly](https://rkjones4.github.io/pdf/shapeAssembly.pdf)
  and [ShapeCoder](https://arxiv.org/abs/2305.05661).
- Conditional generation and agents: [Text2CAD](https://proceedings.nips.cc/paper_files/paper/2024/hash/0e5b96f97c1813bb75f6c28532c2ecc7-Abstract-Conference.html),
  [CAD-Recode](https://arxiv.org/html/2412.14042v2),
  [CAD-Coder](https://arxiv.org/html/2505.14646v1),
  [CAD-Assistant](https://arxiv.org/html/2412.13810v3),
  [Text2CAD-Bench](https://arxiv.org/abs/2605.18430)
  and [Shap-E](https://arxiv.org/abs/2305.02463).

**[inference]** Apparent disagreements mostly reflect different targets. Exact
SDF mathematics, practical sculpting fields and learned implicit functions are
not interchangeable guarantees. A learning representation can be equivalent to
CSG without being equally economical. A model can execute every test script yet
only moderately match geometry. A strong reconstruction score on procedural
mechanical parts and weaker advanced-topology performance can both be true.
None of the opened studies measures Puck source ergonomics, its deterministic
contact path, federated permission boundaries or its MMO population costs.
Those transfers remain explicit inferences for the bounded trials above.
