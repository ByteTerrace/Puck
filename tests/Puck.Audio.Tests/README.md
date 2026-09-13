# Puck.Audio.Tests

This suite exercises the deterministic mixer and voice simulation in
`Puck.Audio`. `AudioMixerTests` covers block mixing, while
`VoiceSynthTests`, `VoiceSimulationTests`, and `MusicSimulationTests` cover
voice state, seeded triggers, music clocks, transitions, layers, and
embellishments using deterministic block sources.

## Running

```powershell
dotnet test tests/Puck.Audio.Tests/Puck.Audio.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Audio](../../src/Puck.Audio/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
