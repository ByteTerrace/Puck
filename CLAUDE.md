# CLAUDE.md

## `src/Puck/Puck.csproj` is INSPIRATION ONLY

Never reference `Puck.csproj` from any project. All functionality is being split
out into the separate `Puck.*` projects which you can freely use: Puck.Commands,
Puck.Maths, Puck.Platform, Puck.Shaders, Puck.Storage, and Puck.Vulkan. Read the
old `Puck` project for reference only; build the real thing in the split projects.

Our main focus right now is putting together a new minimal showcase in Puck.Demo.

## After ANY code generation, format in two steps (in order)

```powershell
dotnet run tools/Tools.cs -- format   # 1. repo formatter in ./tools
dotnet format                         # 2. then dotnet format
```
