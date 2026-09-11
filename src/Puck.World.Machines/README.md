# Puck.World.Machines — the deterministic screen-machine host

This project owns the diegetic screen-machine host runtime:
- `WorldMachineHost` (`IWorldMachineHost` implementation): owns boot, per-tick stepping, cable-linking, memory peek/poke, two-phase transactional prepare/commit/finish mutations, and symbol-aware variable address resolution for every declared screen's machine.
- `WorldScreenMachineEngines`: dynamic catalog and vocabulary validator for registered `IScreenMachineEngine` instances and `ICartridgeCompiler` forge compilers.

## Architectural Decoupling and Pure Extension Packaging

`Puck.World.Machines` enforces strict separation of concerns across the engine:
1. **Zero Concrete Emulator References**: `Puck.World.Machines` depends only on `Puck.Abstractions` (`IScreenMachineEngine`, `IScreenMachine`), `Puck.GamingBricks.Forge` (`ICartridgeCompiler`), `Puck.World.Server`, `Puck.Audio`, `Puck.Assets`, and `Puck.Maths`. It references neither `Puck.HumbleGamingBrick*` nor `Puck.AdvancedGamingBrick*`.
2. **First-Class DI Extensions**: Concrete emulator cores and compilers register dynamically through extension methods in their own packages:
   - `AddHumbleGamingBrick()` (in `Puck.HumbleGamingBrick.Forge`) registers `GamingBrickEngine`, `TuneInstrumentEngine`, and `HgbCartridgeCompiler`.
   - `AddAdvancedGamingBrick()` (in `Puck.AdvancedGamingBrick.Forge`) registers `AdvancedGamingBrickEngine` and `AgbCartridgeCompiler`.
3. **Dedicated Scripting Addons**: `Puck.World.Addons` is 100% dedicated to WASM scripting guests and has zero knowledge of machines or emulator cores.

## Screen Machines Lifecycle & Two-Phase Transactions

Like WASM guests in `Puck.World.Addons`, screen machines coordinate their mutations through two-phase transactions:
- `TryPrepare(current, candidate, out plan, out reason)`: Computes the screen mutation delta, compiles cartridges, validates options, and constructs an uncommitted plan. Refusals prevent any partial state mutation.
- `Commit(plan)`: Applies changes to active slots atomically.
- `Finish(plan)`: Publishes events, disposes superseded machine instances, and cleans up temporary state.

## Cartridge Compilation and Symbol-Aware Memory Peeking

When a machine's `contentPath` ends in `.cartridge.json`, the host dynamically looks up the registered `ICartridgeCompiler` for that engine.
Cartridge compilation retains the compilation symbol map (`CartridgeCompilation.Variables`), enabling the host to resolve symbolic variable names to their active bus addresses via `TryResolveSymbol(index, symbol, out address)`. The `screen.peek` console verb resolves these symbols transparently at runtime.
