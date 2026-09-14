# Puck.Assets.Tests

This suite checks the asset codecs and content helpers in `Puck.Assets`.
`PngCodecLawTests`, `QrCodecLawTests`, and `AutomaticSequenceCodecTests` cover
round trips, bounded and malformed input, and deterministic encoded output;
`VectorJsonConverterLawTests` covers the vector JSON contract.

## Running

```powershell
dotnet test tests/Puck.Assets.Tests/Puck.Assets.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Assets](../../src/Puck.Assets/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
