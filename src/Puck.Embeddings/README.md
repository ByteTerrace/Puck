# Puck.Embeddings

`Puck.Embeddings` defines embedding generators, batching, and quantization
for deterministic simulation state in Puck.

The simulation never runs a model directly:
- **Authored text** is compiled into a committed lock file by `puck embed`.
- **Runtime text** is embedded outside the tick by an operator-approved host service.
- **Vectors** enter state as deterministic signed 8-bit components normalized on radius 127.

## Embedding identity

Every generator is built for one `Puck.State.EmbeddingIdentity`: the model name
(for example, `puck-fixture` or `text-embedding-3-small`), its revision, and a
dimension count from 8 to 1,024. `Puck.State` owns the identity and its limits,
so this package references `Puck.State`. See
[Vectors and embedding spaces](../../docs/reference/state/vectors.md#declare-a-space-and-vector-rows).

## Embedding generators

Embedding generation standardizes on Microsoft's provider-agnostic
`IEmbeddingGenerator<string, Embedding<float>>` (`Microsoft.Extensions.AI`).

Two generators are provided:
1. `FixtureEmbeddingGenerator`: offline reference generator answering model `puck-fixture`.
   Component `i` is signed byte `i mod 32` of
   `SHA-256(model ‖ 0 ‖ revision ‖ 0 ‖ dimensions ‖ 0 ‖ text ‖ 0 ‖ ⌊i/32⌋)`, with `-128 → -127`, then normalized.
2. `OpenAiEmbeddingGeneratorFactory`: factory constructing `IEmbeddingGenerator<string, Embedding<float>>`
   backed by `Azure.AI.OpenAI` and `Microsoft.Extensions.AI.OpenAI` using passwordless identity
   authentication via `TokenCredential` (`DefaultAzureCredential`).

## Quantization

`VectorQuantizer.TryQuantizeUnit` maps floating-point embeddings through
`Puck.Maths.SignedByteVectorFunctions.TryQuantizeUnit` into normalized 8-bit signed integer
components admitted into `Puck.State.StateVector`.
