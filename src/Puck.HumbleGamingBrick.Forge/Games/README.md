# Games

Hand-authored SM83 cartridges built directly on `Puck.HumbleGamingBrick.Forge.Framework
.GameFramework` — the same facade `Puck.HumbleGamingBrick.Forge.Tune.TuneGame` uses (the
smallest existing worked example; read it first). Unlike `Tune/` (built at
runtime by `Puck.World`'s audio host), a cart here is built once and its
32 KiB image committed as a binary asset — see each game's own doc comment
for where.

| Game | Facade | Committed ROM |
|---|---|---|
| arcade-quest | `ArcadeQuestRom` (`Build`/`Verify`) | `src/Puck.World/Assets/roms/arcade-quest.gbc` |

## `ArcadeQuestRom` — the blank-slate campaign's winnable proof ROM

One state, one mechanic: press RIGHT three times to walk a WRAM counter
(`ArcadeQuestProtocol.Position`, `0xC200`) from 0 to `WinPosition` (3);
reaching it latches a WRAM win flag (`ArcadeQuestProtocol.WinFlag`,
`0xC201`) and prints "YOU WIN" — a single-shot latch this cart never
clears again. Both bytes live in `FrameworkMemoryMap.GameRam` (`0xC200+`),
which `FrameworkKernel.EmitBootPrologue`'s boot block-fill zero-fills every
boot (split only around the reserved victory-share slot at
`0xC0F0..0xC0FF`, below `GameRam`) — the flag can never read stale-set at
boot without the game writing it there itself, which this cart never does
outside the win edge; `ArcadeQuestGame.EmitPlayEnter` restates the clear
explicitly anyway, defense in depth.

This was the `arcade.world.json` cabinet's cart: a `puck.world.def.v1` world
rule's `$machine:0:49665` reserved `compareState` channel read the win-flag
byte live off the booted machine every tick (`WorldServer.Machines.TryPeek`),
so the document's own `rules` section reacted to the win edge with no addon
involved. `arcade.world.json` was retired under the 2026-08-06 four-world
charter (`play`/`dive`/`kart`/`jump` are the whole shipped roster now); the
ROM and its `WorldRule`-driven reaction-ladder pattern are unaffected; the pattern
this cart demonstrated survives in git history. No shipped world hosts it today.

Build + self-verify (no committed runner — see `ArcadeQuestVerify`, which
mirrors `Puck.HumbleGamingBrick.Forge.Tune.TuneVerify`'s shape: a real `Puck.HumbleGamingBrick`
core, `VerifyMachineSettle.SettleOutOfOamDma` after every frame batch):

```csharp
var rom = Puck.HumbleGamingBrick.Forge.Games.ArcadeQuestRom.Build();   // 32 KiB image
Puck.HumbleGamingBrick.Forge.Games.ArcadeQuestRom.Verify(rom: rom);     // throws on any violation
File.WriteAllBytes("arcade-quest.gbc", rom);
```

`ArcadeQuestRom`'s `Build`/`Verify` pair is the intended entry point, so a
throwaway build runner outside this assembly needs nothing more than a
reference to `Puck.HumbleGamingBrick.Forge` (the
`ByteTerrace.Puck.HumbleGamingBrick.Forge` package);
`tests/Puck.HumbleGamingBrick.Forge.Tests` runs the same pair as the standing
gate.
