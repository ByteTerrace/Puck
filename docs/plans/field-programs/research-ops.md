# One definition per field op

A relief mask puts data rows immediately before the noise displacement that
reads them. A row moves no point and writes no distance, yet it has explicit
arms in twelve dispatches or classifications today. A capsule exposed the
other failure mode. Both readers recognized the primitive, but they computed
its projection differently. An instruction catalogue can make omissions fail
the build. Sharing the primitive formula closes the second gap. The bake,
CPU queries and editor keep evaluating fields, and the frame keeps drawing
the resulting meshes. [Field programs](../field-programs.md),
[mesh-first rendering](../../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs).

## Where an instruction has meaning today

An `SdfOp` says what a stream row does. `ShapeBlend` also carries a
`SdfShapeType` and a `SdfBlendOp`. `PopField` carries a blend too. Each has its
own dispatch, so one definition per opcode leaves a hole if the shapes and
blends underneath it still have two definitions. A reset clears the point
state while the accumulated distance survives. A scope saves that field,
evaluates a child field and composes it back. Analyses that track either state
need the same events in the same order. [Instruction set](../../../src/Puck.SignedDistance/SdfOp.cs),
[point walk](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.PointWalk.cs),
[GPU walk](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-map.hlsli).

### Fixed-point evaluation and certificates

| Reader | What it needs from an op |
|---|---|
| `SdfFieldEvaluator.Compile`, `IsSupportedOp`, `IsSupportedShape`, `BoundedOps` and `BoundedShapes` | Which readings accept it, which have an interval rule, and how to decode immediate operands, scope scales and auxiliary data once. Contact and render have different admission. The CPU has no `TransformDynamic` table lookup. Shape restrictions can also depend on operands, such as the contact sweep's strand count. [Compile](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs), [admission](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.Support.cs), [bounds](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.Bounds.cs). |
| `WalkPoints`, `FoldPoint`, `WarpPoint`, `EditField`, `RenderWarpPoint` and `RenderEditField` | The point, distance correction, material delta, pending lane erosion, scope state and field updates. Primitive distance and the blend determine the candidate and winner. A relief row is a no-op in the walk. Its owning noise op reads compiled masks. Influence queries reuse the point walk and observe the compositions. [Point walk](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.PointWalk.cs), [render ops](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.RenderOps.cs), [influences](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.Influences.cs). |
| `WalkBounds`, `FoldBounds`, `EditFieldBounds`, `RenderWarpBounds` and `RenderEditFieldBounds` | Enclosures for the transformed point box and the field value, including directed rounding, seam cases, local shape and profile intervals, blend bounds and scoped composition. `BuildRotationBounds` prepares rotations once. A row has an identity interval effect. `NoiseDisplaceBounds` uses the mask's enclosed weight and can skip noise at zero weight. An unsupported or unprovable enclosure needs an explicit refusal or an unbounded result. [Bounds](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.Bounds.cs), [render bounds](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.RenderBounds.cs). |
| `DeferredScopes` and `CertifiedStepScale` | Shape and scope events, plus whether a later operation observes the magnitude of the accumulated field. Contact can defer the scale of a whole-field hard union scope when its zero set is sufficient. The certified step scale then carries the deferred factor. [Evaluator](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs). |
| `BuildShapeReaches` and `TryLocalCertificate` | Rigid placement and scale of a shape's local distance certificate, where it can be used, and which point changes end that proof. The row leaves the reach state alone. [Certificates](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.Certificates.cs). |
| `TryCertifyInstance`, `ScopeBoxReach`, `ComposesByHardUnion`, `BuildSegments` and `ClosesAtScopeEdge` | Which effects preserve a bound, which lower the field and by how much, blend margins and hard-union eligibility, and where a scope or chain prevents a skip. `ScopeBoxReach` consumes a reduced sequence of shapes and scope pops. `TryCertifyInstance` skips the row, while `ComposesByHardUnion` currently groups it with field edits at depth zero. `BuildSegments` admits a narrower transform-and-shape grammar. [CPU culling](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.Cull.cs). |
| `TryFieldGradient`, `ReadInfluenceGradient` and certified sweeps | Gradient taps and certified queries consume point and interval answers. They have no opcode derivative switch. Keep the stencil and the query procedure at their callers. [Evaluator](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs), [influences](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.Influences.cs), [certified queries](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.Certified.cs). |

### Admission, bounds and compiled plans

| Reader | What it needs from an op |
|---|---|
| `SdfProgram.LaneContract` and `ValidatePackedContract` | Which lanes are authored, derived, bit-reinterpreted or zero, their ranges and cross-lane rules, plus the nested shape, blend and relief row-kind layouts. Scope balancing and side-table ownership need structure and references. `DecodeReliefRuns` checks adjacency, count and common owner. `Excerpt` copies that run with its relief and remaps shape side tables. [Lane contract](../../../src/Puck.SignedDistance/SdfProgram.LaneContract.cs), [validation](../../../src/Puck.SignedDistance/SdfProgram.Validation.cs), [relief rows](../../../src/Puck.SignedDistance/SdfProgram.ReliefRows.cs), [excerpts](../../../src/Puck.SignedDistance/SdfProgram.Excerpt.cs). |
| Builder palette and capacity checks, `CalculateRequiredDynamicTransformCapacity` | `Build` distinguishes shape material indices and screen sentinels from other uses of the material lane. `MaxRecolorDelta` needs the fold's maximum palette offset and distinguishes a variant count from a stride. `MeasureCapacity` counts reset boundaries and shape leaves without packing. Retained emission selects glyph dependencies. Dynamic capacity needs the typed slot reference. [Builder](../../../src/Puck.SignedDistance/SdfProgramBuilder.cs), [capacity](../../../src/Puck.SignedDistance/SdfProgramBuilder.Capacity.cs), [retained emission](../../../src/Puck.SignedDistance/SdfProgramBuilder.Emission.cs), [dynamic capacity](../../../src/Puck.SignedDistance/SdfProgram.cs). |
| `SdfOpRoles.Of`, `RequireSegmentsStartAtTheWorldPoint` and `OpensUnboundedFold` | Stream role, which operations reset, read or move the point, and when a fold is unbounded. `ReliefRow` currently has field ownership and is counted by the segment's conservative point-read predicate. Audit ownership and actual point effects need separate fields. [Roles](../../../src/Puck.SignedDistance/SdfOpRoles.cs), [point state](../../../src/Puck.SignedDistance/SdfProgram.PointState.cs). |
| `AnalyzeReliefPointFactors` in `Cells` | Reset, identity, reciprocal scale, a global point-stretch factor, an explicit lack of a global bound, or a relief that consumes the factor. Reach-dependent and discontinuous warps cannot silently pass this walk as identity. `ReliefRow` is neutral. [Relief point factors](../../../src/Puck.SignedDistance/SdfProgram.Cells.cs). |
| `AnalyzeChainLipschitz` and `FoldChainLipschitz` | Candidate distance correction, point Jacobian or fold factor, and primitive reach or gradient bound. Some warp factors need the following shape's reach and the translation and scale before the warp. [Chain analysis](../../../src/Puck.SignedDistance/SdfProgram.Lipschitz.cs), [reach-dependent factors](../../../src/Puck.SignedDistance/SdfProgram.cs). |
| `AnalyzeLipschitz`, `AnalyzeStepScaleBinder`, `ReadFieldScopeClamps`, `FieldScopeExtent` | Field effects in composition order, the blend recurrence, relief slope in its sampling frame, and which scope scales are written or read. A mask adds its slope to the noise charge before the point factor. The binder reports the strongest unscoped chain or relief contributor from those facts. Scope inspection reports derived scales and nesting. [Lipschitz and binder](../../../src/Puck.SignedDistance/SdfProgram.Lipschitz.cs), [binder result](../../../src/Puck.SignedDistance/SdfStepScaleBinder.cs), [scope inspection](../../../src/Puck.SignedDistance/SdfProgram.FieldScopeClamps.cs). |
| `AnalyzeBounds`, `AnalyzeSegment`, `ShapeReachRadius`, `TryGetLocalBound` and `BuildDynamicTransformBounds` | Shape-local bounding spheres, rigid transforms or dynamic-slot references, where shape bounds cease to apply, and whether a segment can be skipped as a unit. A scope boundary blocks the segment skip while preserving point bounds. A row preserves both states. [Skip spheres](../../../src/Puck.SignedDistance/SdfProgram.SkipSpheres.cs), [shape bounds](../../../src/Puck.SignedDistance/SdfProgram.cs), [dynamic bounds](../../../src/Puck.SignedDistance/SdfProgram.DynamicBounds.cs). |
| `HasUnmaskableInfluence`, `MaxScopedFieldReach` and `MaxSmoothBlendRadius` | Unbounded geometry and global effects, how much a scoped edit can expand a surface, and how far a blend can reach. These feed instance bounds and mask eligibility. [Program analysis](../../../src/Puck.SignedDistance/SdfProgram.cs). |
| `AnalyzeInstanceDistanceBound`, `ShapeDistanceGain` and `TryCompose` | A lower bound on distance growth outside the instance, plus how far a blend can pull that bound down. The GPU uses this to decide if an instance can beat the incumbent. A row is identity. An unproved effect abandons the lower bound. [Distance gain](../../../src/Puck.SignedDistance/SdfProgram.DistanceGain.cs), [GPU consumer](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-parts.hlsli). |
| `TryCompileRigidSegment`, `TryCompilePart` and `CanTracePartsIndependently` | An explicit compact-plan eligibility rule. Rigid chains can encode a common dynamic slot or a short static fold run. Part compilation has its own scoped grammar and optional scale, axial profile or shear. Unsupported inputs retain the general walk. Shape and blend behavior still use their usual meaning on the compiled paths. [Rigid plan](../../../src/Puck.SignedDistance/SdfProgram.RigidPlan.cs), [part plan](../../../src/Puck.SignedDistance/SdfProgram.PartPrograms.cs). |

### GPU evaluation, bake and other consumers

| Reader | What it needs from an op |
|---|---|
| `mapCore`, `sdfRigidFoldPoint`, `sdfComposePartProgram` | `mapCore` decodes operands and runs the point, candidate, material, lane and scope actions. The rigid fold loop reads only the five eligible fold and pose operations, and the part loop reads its three domain ops. The latter two are independent opcode dispatches even though they use `if`. Generate them from the same eligibility and operand facts as their C# producers. [Map and rigid folds](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-map.hlsli), [part reader](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-parts.hlsli). |
| `evaluateShape`, `sdfReliefRegionDistance`, `sdfMaskProduct`, `sdfComposeCandidate` | Primitive formulas and their lane decoders, the relief region subset and record layout, and blend distance and winner policy. Relief regions call the same GPU formulas through a separate subset dispatch. Point, noise, hash, ellipse and quaternion includes supply arithmetic called by the walkers. [Shapes](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli), [region reading](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-map.hlsli), [point helpers](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-point.hlsli), [blend](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-blend.hlsli). |
| `SdfIsaHlsl` and `ShaderDeclarations` | The generated `isa/sdf-isa.hlsli` already provides enum values, header accessors, common counts and layout constants. The operand use inside cases is still hand-paired. `sdf-render.hlsli` extends the vocabulary for rendering, the bake interface declares its pass records, and `sdf-bake-taps.hlsli` carries the shared stencil. They add no field dispatch. `sdf-program.hlsli` and `sdf-layout.hlsli` provide the stream and directory access. [ISA generator](../../../src/Puck.SdfVm.Model/SdfIsaHlsl.cs), [outputs](../../../src/Puck.SdfVm.Model/ShaderDeclarations.cs), [program reads](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-program.hlsli), [layout](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-layout.hlsli). |
| GPU and CPU mesh field sampling | `sdf-mesh-bake.comp.hlsl` takes a centre sample and, when requested, four gradient taps through `mapCore`. The mask kernel admits instances from bounds. `SdfMeshCpuField` and `SdfMeshPathBake`'s field levels use the CPU evaluator and shared stencil. Sampling, refinement and contouring consume distances, materials, gradients and bounds. They need no new op arm. [GPU sampling](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-bake.comp.hlsl), [instance masks](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-mesh-bake-mask.comp.hlsl), [CPU sampling](../../../src/Puck.SdfVm/SdfMeshCpuField.cs), [CPU field levels](../../../src/Puck.SdfVm/SdfMeshPathBake.FieldLevels.cs). |
| `SdfMeshGeometryBuilder.Append`, `SdfMeshBakeGroup` and `SdfMeshSkin` | The bake key includes instruction data and any external data it reads. Reset and dynamic transform change the chain slot, `LaneErode` reads its lanes, and a morph pop hashes the relevant operand lanes. Shapes can refer to glyph, sampled or profile data. Grouping recognizes drawable shape rows and consumes the program's bounds. Skinning checks smooth shape joins. A new effect that reads a resource needs a dependency declaration even when it shares an existing distance kernel helper. [Geometry key](../../../src/Puck.SdfVm/SdfMeshGeometry.cs), [groups](../../../src/Puck.SdfVm/SdfMeshBakeGroup.cs), [skin](../../../src/Puck.SdfVm/SdfMeshSkin.cs). |
| `SdfMeshAudit`, `RestScene`, `CreationShapeLaw`, `SdfWorldTables.ProgramDeclaresShape`, `WorldColliderSet.BlendGrowth` | Audit extracts point chains, field edits and owners, and replaces dynamic instructions with posed rigid instructions for a CPU read. Rest labels inspect reset, translate, shape and slot roles. The shape law formats authored geometry. Cadence detects a declared shape, and contact margin reads shape and scope blend reach. `SdfProgram.Appearance`, `Inspection`, `Path` and `SampledRegions` also select shape rows or particular shape data. These consumers need typed queries over those facts. [Audit](../../../src/Puck.SdfVm/SdfMeshAudit.cs), [rest view](../../../src/Puck.Cli.Worlds/Mesh/RestScene.cs), [shape law](../../../src/Puck.World.Authoring/Authoring/CreationShapeLaw.cs), [cadence](../../../src/Puck.SdfVm/SdfWorldTables.Cadence.cs), [contact margin](../../../src/Puck.World.Server/WorldColliderSet.Relief.cs). |

I searched opcode names, switches and predicates on instruction `.Op`, both
packed opcode accessors, the row types that feed the packer, and the call sites
of `mapCore`. The device probes call that same GPU interpreter. Builders and
authoring emit instructions and remain callers of the encoding, including
the `SdfDomainOp` lowering. `SdfEncodingProbe` is a manually assembled set of
builder calls used for the encoding description and some device inputs. Its
coverage law checks opcode membership, which can hold while a walker drops a
certificate.
[Builder emission](../../../src/Puck.SignedDistance/SdfProgramBuilder.Emission.cs),
[domain lowering](../../../src/Puck.SignedDistance/SdfDomainExpansion.cs),
[encoding probes](../../../src/Puck.SignedDistance/SdfEncodingProbe.cs),
[coverage law](../../../tests/Puck.SignedDistance.Tests/SdfEncodingProbeLawTests.cs),
[device probe](../../../tests/Puck.World.Tests/Assets/Shaders/sdf-field.comp.hlsl),
[masked device probe](../../../tests/Puck.World.Tests/Assets/Shaders/sdf-instance-field.comp.hlsl).

`SdfShadeOp` dispatches a separate shading language. A shade program's shape
helper and material paints also call `evaluateShape` at a mesh hit. They use
the same primitive definitions as the bake while evaluating individual
shapes without walking `SdfOp`. `CapturePaintShape` reads the emitted
primitive, scale, rotation and referenced profile data for that path.
[Shade emission](../../../src/Puck.SdfVm.Model/SdfShadeHlsl.cs),
[paint shader](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/shade/shade-layers.hlsli),
[paint shape capture](../../../src/Puck.SignedDistance/SdfProgramBuilder.Paint.cs).

## Make every op declare its effects

Use a typed C# catalogue in the field model. Define one concrete entry per
opcode and companion entries per shape, blend and data-row kind. Generate the
opcode roster from the entries, including the C# enum, so declaring a new
numeric id separately is impossible. Give each entry required facets for
encoding and admission, point and field action, interval behavior, slope and
reach, distance gain, permitted compact plans, and external reads and row
ownership. Use abstract members without default implementations, or an
equivalent typed constructor with no optional fields. Each facet returns a
closed choice, such as a point action, an explicit identity, and an explicit
unbounded or unsupported result where the consumer permits it. Derive
inspection lists and the binder's contributors from those same choices.
An isolated SDK 11 C# compiler probe accepted a concrete row with its abstract
`Reach` property implemented and rejected the same row without it (`CS0534`).
That establishes the language check on a complete row. Roster generation and
use by every consumer remain implementation work. [C# abstract members](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/abstract).

Keep the traversal algorithms in their current owners. They know how to walk
segments, compose scopes, preserve conservative bounds, and schedule samples.
They should consume a generated opcode dispatch or a typed event such as
point reset, field edit, or scope close. For example, a row's point and field
actions are identity, its interval action is identity, and its ownership is
the next relief. The relief's own entry declares that it reads the row run,
edits distance, contributes slope and outward reach, and carries those bytes
into the bake dependency list. Encode data-row ownership separately from
execution effects. Preserve current conservative policy initially, including
the hard-union and segment-read cases in the inventory, until a scoped and
unscoped comparison shows which restrictions the owning relief already
imposes. A failed proof converts to generic evaluation or an unbounded
answer, depending on the pass. An unknown opcode is invalid at admission.

Generate static C# switches for hot dispatch and HLSL cases and operand
decoders alongside the existing declarations. Keep the C# build independent
of the shader project. A C# generator can discover the definitions and emit
the roster and CPU dispatch into the field assembly. The shader generator
then consumes the compiled catalogue and publishes HLSL before DXC runs. Use
one semantic emitter for the generic interpreter and any later per-program
specialization. Keep one dispatch per hot pass where the generated output
would otherwise add nested switches or duplicate shape functions. At build
time, require one valid definition per generated id, verify that each emitted
reader covers its admitted actions, and route model consumers through typed
facets rather than raw opcode dispatch. The invariant is that every admitted
row has an explicit outcome for every reader. C# checks the supplied facets,
the emitters own the set of arms, and admission enforces the stream contract.
These checks cannot prove that a chosen identity is correct or that a bound
truly contains the field. [Shader generation host](../../../src/Puck.Shaders.Generator/Program.cs),
[Puck shading decision](../../decisions/rendering.md#authored-shading).

For arithmetic common to fixed-point queries and the float GPU, start with
one typed formula for capsule and superellipsoid. Give the formula named
operands and operations for projection, norm, clamp and selection. Lower the
same expression and branches to fixed-point C# and `precise` HLSL. Keep
target arithmetic in narrow primitives with explicit units and rounding.
For example, retain widening for fixed-point projection and let the GPU use
float dot products. A fixed-point interval rule may use a stronger geometric
enclosure than evaluating that expression over a box. Keep that proof
hand-written and required by the shape entry. Keep texture and buffer reads,
vector and interval primitives, sweep and path machinery, and target-specific
precision and saturation code hand-written behind typed inputs. A handwritten
target escape needs an explicit support decision and a numerical witness.
This guarantees shared control and operands for the formulas that use the
emitter. It leaves rounding, discrete decisions near ties, and approximation
quality to numerical evidence on each carrier. [Fixed-point formulas](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs),
[GPU formulas](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli),
[HLSL precision modifier](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-variable-syntax),
[DXC SPIR-V mapping](https://github.com/microsoft/DirectXShaderCompiler/blob/main/docs/SPIR-V.rst).

### Alternatives and cost

| Approach | What it buys and where it stops |
|---|---|
| Table-driven catalogue with runtime calls | Abstract facets give compile-time completeness and work well for cold analysis. A dispatch through runtime objects in every sample is a separate performance choice that this lane did not measure. A table beside a hand-maintained enum can still omit an entire entry. [C# abstract members](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/abstract). |
| A separate declaration language and generator, as in LLVM TableGen and MLIR ODS | LLVM puts instruction facts in records and lets each backend choose their meaning. MLIR adds operation constraints, traits and generated verification, with custom code for semantics beyond the schema. Those mechanisms support the catalogue approach. They give no reason to introduce another authoring language for engine definitions that can be checked in C#. [LLVM TableGen](https://www.llvm.org/docs/TableGen/ProgRef.html), [MLIR ODS](https://mlir.llvm.org/docs/DefiningDialects/Operations/). |
| Roslyn incremental generation and Puck shader generation together | Roslyn can discover attributed definitions and add C# output. Its cookbook recommends extracting a small equatable model and putting diagnostics in an analyzer. Puck's shader generator already owns files consumed by DXC. The tools serve different consumers, and HLSL generation can stay in the shader build. Avoid a general C#-to-HLSL translator. Ordinary C# contains host operations whose meaning in a shader would have to be invented. Emit a small typed formula representation and call existing primitives. [Roslyn cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md), [Puck declarations](../../../src/Puck.SdfVm.Model/ShaderDeclarations.cs). |

The sources describe different ownership choices. TableGen delegates meaning
to each backend. ODS gives specific operation facts shared meaning. Puck's
shading code already generates interpreter and specialized calls from
`SdfShadeOps` through `SdfShadeHlsl`, while leaving helper arithmetic in HLSL.
Reuse that model for emitting calls, with fixed-point and interval contracts
added for the field. A specialist bake kernel can later use the same catalogue
if `field-measure` shows a gain. In the meantime, generic evaluation handles
every admitted program. An eventual specialized kernel should cover its whole
program, with generic fallback selected by the baker while compilation is
pending or on a cache miss. Key the derived product from its inputs and the
actual shader include closure. The exact code size, build latency, register
use and sample throughput of this design are unmeasured. [Shade table](../../../src/Puck.SignedDistance/Shading/SdfShadeOps.cs),
[shade emitter](../../../src/Puck.SdfVm.Model/SdfShadeHlsl.cs),
[interpret first, specialize behind](../../decisions/rendering.md#authored-shading).

### Count the edits

The opcode enum has 28 members and `mapCore` has 28 `case SDF_OP_*` labels.
In the current tree `ReliefRow` appears in 12 hand-written arms or pattern
alternatives. These are contact support, two CPU cull classifications, CPU
point and interval walks, shape reach certificates, op roles, relief point
factors, distance gain, skip spheres, lane admission and `mapCore`. A
`BoundedOps` list entry and a run decoder predicate make 14 consumer edit
sites that explicitly name the op. This excludes the enum declaration,
instruction constructors, generated define, comments, examples and tests.
The count came from `rg -n 'SdfOp\.ReliefRow\b|case SDF_OP_RELIEF_ROW:' src -g '*.cs' -g '*.hlsli'`,
classifying each result by its enclosing method. A new executable op also
needs an arm in the main GPU walk. The number of other arms varies with its
effects and the fallback a reader currently takes. An existing enum entry or
a shared `default` conceals those obligations from the compiler.

Under the catalogue a new op adds one hand-written definition, zero
per-reader opcode arms and zero parallel support-list entries. A relief row
still has a payload schema and a consumer for its run. A genuinely new effect
needs arithmetic or an analysis rule written once, and may require a new
required facet for every catalogue entry if no existing facet can express it.
For a capsule or superellipsoid, the present pair is two point formulas and
two GPU shape dispatch entries per primitive (ordinary shape and relief
region), in addition to the CPU point, interval, certificate and lane rules.
The proposed end state is one definition per primitive, one shared point
formula and an explicit enclosure and certificate decision. The generated
dispatches still contain the arms their readers need.

## What the two merges establish

| Defect | What generation would catch | What still needs a numerical or behavioral witness |
|---|---|---|
| **Superellipsoid**. The relief-a merge, including its grid-rounding follow-up, aligns the admitted radii and exponent, raises the minimum radius, uses metre-scaled ratios and agrees on the clamp for `2 < e ≤ 3`. | Shared lane preparation and emitted arithmetic remove independent choices about units, the clamp branch and the operands it reads. An opcode catalogue with names of two separate helpers still permits the defect. | Small and eccentric radii, exponents at and between the clamp boundaries, and far samples. Both carriers can execute the same formula and still disagree because of rounding, power approximation or saturation. [CPU](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs), [GPU](../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-shapes.hlsli), [device cases](../../../tests/Puck.World.Tests/SdfFieldDeviceLawTests.ShapeFormulas.cs). |
| **Capsule**. Relief-a removes the packed reciprocal of endpoint length squared and has both readers project from the endpoint. Lane admission refuses length squared that is nonfinite in float. | A shared operand layout and formula prevent one reader from quietly consuming a reciprocal that the other ignores. Removing a named lane breaks any generated formula that still reads it. | Degenerate, short and long endpoints, off-axis samples, and range edges for float length and fixed-point intermediates. [Lane admission](../../../src/Puck.SignedDistance/SdfProgram.LaneContract.cs), [device cases](../../../tests/Puck.World.Tests/SdfFieldDeviceLawTests.ShapeFormulas.cs). |
| **Relief walker omissions**. Relief-a supplies missing row handling in `TryCertifyInstance`, `AnalyzeSegment`, distance gain, shape reaches and point factors. It also updates owner-sensitive extraction and charges the mask slope in the relief's sampling frame. | A required effect or explicit neutral outcome for each analysis keeps a row from falling into an error or a conservative fallback by accident. Run ownership and extraction follow the same declaration. | An author can still choose the wrong facet, or bound the mask in the wrong units. Compare masked and row-stripped programs with the same relief, at scale one and a quarter, and observe the documented slope change and retained skip opportunities. [Review findings](../terrain-pass/relief-mask-design.md#step-a-as-built-and-the-step-back-reviews-findings), [twin law](../../../tests/Puck.SignedDistance.Tests/SdfReliefMaskLawTests.cs). |

Relief-gpu records a separate disagreement in the device law. The GPU scales
a scoped candidate immediately. The contact evaluator can defer that scale
and apply it to the certified step, so the law now accounts for the ratio of
the step scales. A shared formula would leave that valid difference in place.+The merge also records empty sectors caused by the scratch world's far
distance, a bake-planning issue outside opcode generation. The recorded
device-law pass is from that lane. I inspected its source and commit evidence
without rerunning either GPU backend. [Device comparison](../../../tests/Puck.World.Tests/SdfFieldDeviceLawTests.Legs.cs),
[scope rule](../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs),
[bake planning](../../../src/Puck.SdfVm/SdfMeshBakeGroup.cs).

## Recommended implementation order

1. **Let the field model own the whole roster.** Implement required facets and
   generated admission and dispatch for `ReliefRow` and its owning relief.
   The field-model implementer should remove a required facet from a scratch
   entry and see compilation fail, then compare the scoped and unscoped row
   variants through both query modes, step scale, bake key and skip plans.
   Move the remaining readers to typed access before removing handwritten
   opcode lists. Derive eligible plan cases on both sides from the same rule.
2. **Share the two primitive formulas next.** The field and shader implementer
   should replace the capsule and superellipsoid twins and use the existing
   formula cases on both DXIL and SPIR-V. Add samples only where needed to
   exercise the new lowering's rounding, interval containment or an omitted
   branch. Require matching discrete outcomes away from stated ties and a
   distance tolerance in the chosen reading. Keep the relief twin as the
   composition witness. The formula emitter's performance and the full
   numeric range remain for this implementation check.
3. **Measure specialization separately.** The bake lead should compare the
   generated generic kernel with the current generic kernel on the debug
   world, using build latency, shader size, registers, samples and bake time.
   Use the field-measure result to decide whether a specialized kernel earns
   a second evaluation path. If it does, compare the same creation and
   sampling budget on both backends and exercise a cache miss through the
   generic baker. The frame continues to consume the meshes.
