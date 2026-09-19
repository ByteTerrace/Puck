# Puck.Embeddings

`Puck.Embeddings` defines embedding generators, embedding identity, and quantization
for deterministic simulation state in Puck.

The simulation never runs a model directly:
- **Authored text** is compiled into a committed lock file by `puck embed`.
- **Runtime text** is embedded outside the tick by an operator-approved host service.
- **Vectors** enter state as deterministic signed 8-bit components normalized on radius 127.

## Embedding identity

An `EmbeddingIdentity` describes the configuration of an embedding space:
- `Model`: the model name (e.g. `puck-fixture` or `text-embedding-3-small`).
- `Revision`: model revision string.
- `Dimensions`: dimensionality in `[8, 1024]`.

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
