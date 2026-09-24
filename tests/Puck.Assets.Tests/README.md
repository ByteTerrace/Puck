# Puck.Assets.Tests

This suite checks the asset codecs and content helpers in `Puck.Assets`.
`PngCodecLawTests`, `QrCodecLawTests`, and `AutomaticSequenceCodecTests` cover
round trips, bounded and malformed input, and deterministic encoded output;
`TextureCodecLawTests` and `TextureMipChainLawTests` cover the block codecs
against their decoders, their pinned bytes, and tile-aware mip chains;
`ChunkContainerLawTests` covers the shared chunk container and the canonical
binary primitives it writes with;
`VectorJsonConverterLawTests` covers the vector JSON contract; and
`ContentPinLawTests` covers the `sha256/` and `sha256-64/` pin grammar, including
the refusal of uppercase hex, and the content-addressed store's object layout.

## Running

```powershell
dotnet test tests/Puck.Assets.Tests/Puck.Assets.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Assets](../../src/Puck.Assets/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
