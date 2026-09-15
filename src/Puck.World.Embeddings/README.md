# Puck.World.Embeddings

This engine services extension connects runtime text embedding generation to
Puck worlds through the host's declarative service extension composition.

It registers two provider types into the host extension registry:

- **`embedding.fixture`**: Offline, deterministic embedding generator using SHA-256
  for repeatable tests, development, and air-gapped execution.
- **`embedding.azure-openai`**: Live Azure OpenAI embedding generator using
  passwordless `TokenCredential` / `DefaultAzureCredential` authentication.

## Layering and Dependencies

`Puck.World.Embeddings` lives in the `Engine services` layer alongside `Puck.World.Azure`.
It references `Puck.World.Server` and `Puck.Embeddings`. It is dynamically loaded or
referenced by host composition roots (`Puck.World.Silo`, `Puck.World.Console`, `Puck.Cli`).

## Configuration

Embeddings are configured in the host's extension configuration file under `embeddings`:

```json
{
  "schema": "puck.world.extensions.v1",
  "world": "my-world",
  "lineage": "00000000-0000-0000-0000-000000000001",
  "providers": [
    {
      "name": "local",
      "type": "embedding.fixture",
      "settings": {
        "model": "puck-fixture",
        "revision": "1",
        "dimensions": 256
      }
    }
  ],
  "embeddings": [
    {
      "name": "chat",
      "provider": "local",
      "client": "addon:embedder",
      "space": "lore",
      "requests": "said",
      "results": "saidVectors",
      "status": "saidStatus",
      "maximumItems": 64,
      "batchSize": 64,
      "retryTicks": 1200,
      "cacheEntries": 4096
    }
  ]
}
```

The simulation tick never calls an external model. Generated vectors are committed as
recorded `WorldMutation.Batch` contributions so that replay reads the recorded bytes
and reproduces identical state hashes without network access.
