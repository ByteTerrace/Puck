# Puck.ShaderVm

A backend-neutral packed program model over four-lane values, and two
interpreters of it — one on the host, one on the GPU — that must agree lane for
lane.

**Nothing consumes this project.** The world render path is `Puck.SdfVm`'s and
is unchanged; no kernel includes `shader-vm.hlsli` yet, and no project outside
this one and its tests references the assembly. Read it as a successor under
construction, not a live collaborator.

The VM knows nothing about skyboxes, suns, stars, clouds, or signed-distance
fields. It evaluates generic execution inputs, parameters, constants,
arithmetic, interpolation, forward-only control flow, and deterministic field
operations. Vocabularies built on top compose those into something an author
recognizes.

## The packed layout

Four header words — magic, ISA version, instruction count, constant count —
then one word per instruction, then four words per constant. An instruction
carries its opcode in bits 0–7 and its unsigned operand in bits 8–31.

`ShaderIsa`, `ShaderInput`, `ShaderOp`, `ShaderInstruction` and
`ShaderInterpreter` are one half of the contract;
`Assets/Shaders/ShaderVm/shader-vm.hlsli` is the other. Change either with its
partner. `ShaderInterpreter` is the reference semantics of every opcode.

Jumps are forward-only, so a single linear pass over the stream is a complete
stack-depth dataflow and the GPU interpreter's work is bounded by the
instruction count. There are no loops and no calls; a vocabulary inlines.

## What is built on it

| Piece | What it is |
|---|---|
| `ShaderExpression` / `ShaderExpressionCompiler` | An operator-overloaded value graph and its compiler. Sharing is by reference; a node used twice is evaluated once into a local register, and the register is reclaimed at its last read. |
| `ShaderMath` | The value-graph spelling of every operation that is not an operator. |
| `ShaderSdf` | The signed-distance vocabulary — shapes, point transforms, symmetry and wallpaper folds, blends — as pure functions of a point, so there is no accumulator ordering hazard. |
| `Programs/SkyProgram` | A gradient, sun disc, star field and domain-warped cloud deck, driven by the `SkyParameters` rows a host packs per frame. |
| `ShaderProgramStatistics` | What a program demands of an interpreter: stack depth, live registers, branch count. A GPU kernel provisions `PUCK_SHADER_VM_STACK_DEPTH`/`PUCK_SHADER_VM_LOCALS` from these rather than paying the ISA ceilings, which cost 2 KB of scratch per lane. |

## Verification

`dotnet test tests/Puck.ShaderVm.Tests -c Release` covers the ISA laws, the
host interpreter, and register reuse; it also writes host renders of the sky and
of `null.world.json`'s geometry to `PUCK_SKY_PREVIEW_DIR` when that is set, and
a throughput report beside them.

The two interpreters have never been run against each other. The HLSL half
compiles to SPIR-V and DXIL and nothing more has been established about it.
