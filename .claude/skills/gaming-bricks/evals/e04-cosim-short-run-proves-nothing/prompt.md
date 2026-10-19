---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

I ran `dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --cosim <rom> --sameboy <sb-trace.exe> --boot <dir> --frames 30` on a mooneye ROM and it reports no divergence. Does that show our Game Boy PPU matches SameBoy on that ROM? What should I run instead?
