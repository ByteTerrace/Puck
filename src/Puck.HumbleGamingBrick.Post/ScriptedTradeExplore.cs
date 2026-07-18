using Puck.Capture;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The interactive cross-gen-cart trade explorer — the investigative tool (mirroring <see cref="LinkExplore"/>) that
/// authors and debugs the crafted-save + scripted-trade harness. It boots one or two Cgb trade-cart machines with crafted
/// <see cref="TradeSaveFactory"/> saves already in SRAM, drives them under text <see cref="LinkInputScript"/>s
/// (lone, or linked through a <see cref="SerialLinkSession"/>), and dumps each side's framebuffer + a peek panel
/// (<c>$FFCD</c> connection status, SC, and the lead party species from the live SRAM) every N frames — so an operator
/// can watch the CONTINUE acceptance, the receptionist walk, the rendezvous, and the block exchange progress. It is a
/// diagnostic, not a self-checking stage.
/// </summary>
internal static class ScriptedTradeExplore {
    /// <summary>Dispatches <c>--trade-explore</c>. Usage:
    /// <c>--trade-explore &lt;rom&gt; [--linked] [--scriptA path] [--scriptB path] [--frames N] [--dump-every M]
    /// [--out DIR]</c>. Without <c>--linked</c> a lone side-A machine is driven; with it, side A and side B are linked.
    /// Returns false (battery runs) when the flag is absent.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="exitCode">The exit code (0).</param>
    /// <returns><see langword="true"/> when the flag was handled.</returns>
    public static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;

        // --trade-export [--out DIR]: write the two crafted trade saves (side A RATTATA, side B PIDGEY) + a README to an
        // artifacts location the demo can point per-cabinet saves at. No ROM is needed because the save is a pure
        // function of the crafted trainers.
        if (Array.IndexOf(array: args, value: "--trade-export") >= 0) {
            ExportSaves(outDir: (StringArg(args: args, name: "--out") ?? Path.Combine(path1: "artifacts", path2: "gb-post", path3: "trade-saves")));

            return true;
        }

        // --trade-run <rom> [--frames N] [--dump-every M] [--out DIR]: drive the full peek-gated scripted trade
        // (ScriptedTradeDriver) with per-frame phase logging + periodic framebuffer/peek dumps — the tool that authors and
        // debugs the trade phase conditions.
        // --trade-pc <rom>: continue to the overworld, then hold DOWN and histogram the CPU program counter over many
        // instructions — a liveness probe distinguishing "overworld loop running" (wide PC spread) from "stuck in a tight
        // halt/loop" (a handful of PCs).
        // --trade-talk <rom>: continue, tap UP to face the crafted receptionist, then mash A — logging facing, the
        // receptionist object, and script/link state each step to see whether the interaction fires.
        var talkIndex = Array.IndexOf(array: args, value: "--trade-talk");

        if (talkIndex >= 0) {
            var talkRom = (((talkIndex + 1) < args.Length) ? args[(talkIndex + 1)] : null);

            if ((talkRom is null) || !File.Exists(path: talkRom)) {
                Console.WriteLine(value: "  --trade-talk needs a trade-cart ROM path");

                return true;
            }

            ProbeTalk(rom: File.ReadAllBytes(path: talkRom));

            return true;
        }

        // --trade-warp <rom>: test the self-contained WARP-reload trick — set wBackupMap to POKECENTER_2F itself so the
        // (0,7) down-stairs (dest POKECENTER_2F / warp -1, the dynamic "return to backup" warp) re-enters POKECENTER_2F via
        // MapSetupScript_Warp (SpawnPlayer + LoadMapObjects), spawning the receptionists the CONTINUE path skips. Walk the
        // player to (0,7), step the warp, and report whether wObject1Struct (the receptionist) came alive.
        var warpIndex = Array.IndexOf(array: args, value: "--trade-warp");

        if (warpIndex >= 0) {
            var warpRom = (((warpIndex + 1) < args.Length) ? args[(warpIndex + 1)] : null);

            if ((warpRom is null) || !File.Exists(path: warpRom)) {
                Console.WriteLine(value: "  --trade-warp needs a trade-cart ROM path");

                return true;
            }

            ProbeWarp(rom: File.ReadAllBytes(path: warpRom));

            return true;
        }

        // --trade-capture <rom>: craft a save with wSpawnAfterChampion=SPAWN_LANCE (SRAM 0x2043), which routes the
        // CONTINUE through the post-E4 WARP entry path (MapSetupScript_Warp -> SpawnPlayer + LoadMapObjects) so the game
        // fully populates wObjectStructs + wMapObjects from ROM — a clean source to capture a real, active standing player
        // object struct (and NPC structs) to bake into the crafted save (the CONTINUE path never spawns them).
        var capIndex = Array.IndexOf(array: args, value: "--trade-capture");

        if (capIndex >= 0) {
            var capRom = (((capIndex + 1) < args.Length) ? args[(capIndex + 1)] : null);

            if ((capRom is null) || !File.Exists(path: capRom)) {
                Console.WriteLine(value: "  --trade-capture needs a trade-cart ROM path");

                return true;
            }

            ProbeCapture(rom: File.ReadAllBytes(path: capRom));

            return true;
        }

        // --trade-diff <rom>: continue to overworld, then report which HRAM/WRAM bytes change over one idle frame (liveness)
        // and which change when a button is held (input reach) — address-agnostic freeze diagnosis.
        var diffIndex = Array.IndexOf(array: args, value: "--trade-diff");

        if (diffIndex >= 0) {
            var diffRom = (((diffIndex + 1) < args.Length) ? args[(diffIndex + 1)] : null);

            if ((diffRom is null) || !File.Exists(path: diffRom)) {
                Console.WriteLine(value: "  --trade-diff needs a trade-cart ROM path");

                return true;
            }

            ProbeDiff(rom: File.ReadAllBytes(path: diffRom));

            return true;
        }

        var pcIndex = Array.IndexOf(array: args, value: "--trade-pc");

        if (pcIndex >= 0) {
            var pcRom = (((pcIndex + 1) < args.Length) ? args[(pcIndex + 1)] : null);

            if ((pcRom is null) || !File.Exists(path: pcRom)) {
                Console.WriteLine(value: "  --trade-pc needs a trade-cart ROM path");

                return true;
            }

            ProbePc(rom: File.ReadAllBytes(path: pcRom), hold: (StringArg(args: args, name: "--hold") ?? "Down"));

            return true;
        }

        var tradeIndex = Array.IndexOf(array: args, value: "--trade-run");

        if (tradeIndex >= 0) {
            var tradeRom = (((tradeIndex + 1) < args.Length) ? args[(tradeIndex + 1)] : null);

            if ((tradeRom is null) || !File.Exists(path: tradeRom)) {
                Console.WriteLine(value: "  --trade-run needs a trade-cart ROM path");

                return true;
            }

            RunTrade(
                rom: File.ReadAllBytes(path: tradeRom),
                dumpEvery: IntArg(args: args, name: "--dump-every", fallback: 60),
                outDir: (StringArg(args: args, name: "--out") ?? Path.Combine(path1: Path.GetTempPath(), path2: "trade-run"))
            );

            return true;
        }

        var index = Array.IndexOf(array: args, value: "--trade-explore");

        if (index < 0) {
            return false;
        }

        var romPath = (((index + 1) < args.Length) ? args[(index + 1)] : null);

        if ((romPath is null) || !File.Exists(path: romPath)) {
            Console.WriteLine(value: "  --trade-explore needs a trade-cart ROM path");

            return true;
        }

        var rom = File.ReadAllBytes(path: romPath);
        var frames = IntArg(args: args, name: "--frames", fallback: 1200);
        var dumpEvery = IntArg(args: args, name: "--dump-every", fallback: 120);
        var outDir = (StringArg(args: args, name: "--out") ?? Path.Combine(path1: Path.GetTempPath(), path2: "trade-explore"));
        var linked = (Array.IndexOf(array: args, value: "--linked") >= 0);

        s_spawnOverride = StringArg(args: args, name: "--spawn");

        var bootRomPath = StringArg(args: args, name: "--bootrom");

        s_bootRom = (((bootRomPath is not null) && File.Exists(path: bootRomPath)) ? File.ReadAllBytes(path: bootRomPath) : null);
        s_model = (StringArg(args: args, name: "--model")?.ToLowerInvariant()) switch {
            "dmg" => ConsoleModel.Dmg,
            "agb" => ConsoleModel.Agb,
            _ => ConsoleModel.Cgb,
        };

        var scriptA = LoadScript(path: StringArg(args: args, name: "--scriptA"));
        var scriptB = LoadScript(path: StringArg(args: args, name: "--scriptB"));

        Directory.CreateDirectory(path: outDir);

        if (linked) {
            RunLinked(rom: rom, scriptA: scriptA, scriptB: scriptB, frames: frames, dumpEvery: dumpEvery, outDir: outDir);
        } else {
            RunLone(rom: rom, script: scriptA, frames: frames, dumpEvery: dumpEvery, outDir: outDir);
        }

        return true;
    }

    private static string? s_spawnOverride;
    private static byte[]? s_bootRom;

    private static void ProbeTalk(byte[] rom) {
        using var machine = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideA);

        var joypad = machine.GetRequiredService<IJoypad>();
        var bus = machine.GetRequiredService<ISystemBus>();
        var script = ScriptedTradeHarness.ContinueScript();

        void Run(JoypadButtons b, int frames) {
            for (var f = 0; (f < frames); ++f) {
                joypad.SetButtons(pressed: b);
                machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
            }
        }

        void Log(string tag) =>
            Console.WriteLine(value: (($"  {tag}: yx={bus.ReadByte(address: 0xDA02):X2},{bus.ReadByte(address: 0xDA03):X2} pDir={bus.ReadByte(address: 0xD205):X2} pFacing={bus.ReadByte(address: 0xD20A):X2} "
                + $"rcpSprite={bus.ReadByte(address: 0xD225):X2} rcpMapY={bus.ReadByte(address: (0xD225 + 17)):X2} rcpMapX={bus.ReadByte(address: (0xD225 + 16)):X2} ")
                + $"SC={bus.ReadByte(address: 0xFF02):X2} linkMode={bus.ReadByte(address: 0xD042):X2} scriptVar={bus.ReadByte(address: 0xD173):X2} scriptBank={bus.ReadByte(address: 0xD08C):X2} scriptRunning={bus.ReadByte(address: 0xD160):X2}"));

        for (var frame = 0; (frame < 600); ++frame) {
            joypad.SetButtons(pressed: script.ButtonsAt(frame: frame));
            machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
        }

        Log(tag: "spawned");
        Run(b: JoypadButtons.Up, frames: 2);
        Run(b: JoypadButtons.None, frames: 4);
        Log(tag: "faced-up");

        var cpu = machine.GetRequiredService<Puck.HumbleGamingBrick.Interfaces.ICpu>();
        var fb = machine.GetRequiredService<IFramebuffer>();

        void DumpFb(string name) {
            var pixels = fb.Pixels;
            var rgba = new byte[(pixels.Length * 4)];

            for (var p = 0; (p < pixels.Length); ++p) {
                rgba[(p * 4)] = (byte)(pixels[p] >> 16);
                rgba[((p * 4) + 1)] = (byte)(pixels[p] >> 8);
                rgba[((p * 4) + 2)] = (byte)pixels[p];
                rgba[((p * 4) + 3)] = 0xFF;
            }

            PngEncoder.Write(path: Path.Combine(path1: Path.GetTempPath(), path2: name), rgba: rgba, width: fb.Width, height: fb.Height);
        }

        for (var tap = 0; (tap < 10); ++tap) {
            Run(b: JoypadButtons.A, frames: 2);
            Run(b: JoypadButtons.None, frames: 6);
            Log(tag: $"A#{tap} PC={cpu.ProgramCounter:X4} SP={cpu.StackPointer:X4}");
            DumpFb(name: $"trade-talk-{tap}.png");
        }
    }
    private static void ProbeWarp(byte[] rom) {
        var save = TradeSaveFactory.CreateSaveFile(trainer: TradeSaveFactory.SideA);

        // wBackupWarpNumber 0x285D / wBackupMapGroup 0x285E / wBackupMapNumber 0x285F: point the dynamic return warp back
        // at POKECENTER_2F (group 20 / map 1), warp 0 = the (0,7) stairs.
        save[0x285D] = 0;
        save[0x285E] = 20;
        save[0x285F] = 1;

        // Spawn the player at (X=1, Y=7), one tile right of the (0,7) down-stairs, so a single LEFT step triggers the warp.
        // Patch wXCoord/wYCoord AND the player object struct's Map/Last/Init X/Y (map coords = tile + 4).
        save[0x286A] = 7; // wYCoord
        save[0x286B] = 1; // wXCoord
        byte mx = (1 + 4), my = (7 + 4);

        save[0x2075] = mx; save[0x2076] = my; // MapX/MapY
        save[0x2077] = mx; save[0x2078] = my; // LastMapX/LastMapY
        save[0x2079] = mx; save[0x207A] = my; // InitX/InitY
        TradeSaveFactory.RewriteChecksum(saveFile: save);

        using var machine = ScriptedTradeHarness.BuildFromSave(rom: rom, save: save);

        var joypad = machine.GetRequiredService<IJoypad>();
        var bus = machine.GetRequiredService<ISystemBus>();
        var script = ScriptedTradeHarness.ContinueScript();

        byte X() => bus.ReadByte(address: 0xDA03);
        byte Y() => bus.ReadByte(address: 0xDA02);
        void Hold(JoypadButtons b, int frames) {
            for (var f = 0; (f < frames); ++f) {
                joypad.SetButtons(pressed: b);
                machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
            }
        }

        for (var frame = 0; (frame < 600); ++frame) {
            joypad.SetButtons(pressed: script.ButtonsAt(frame: frame));
            machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
        }

        Console.WriteLine(value: $"  spawned: map={bus.ReadByte(address: 0xDA00):X2}/{bus.ReadByte(address: 0xDA01):X2} yx={Y():X2},{X():X2}");

        // Walk toward the (0,7) stairs. Movement is a continuous hold (~16 frames/step); log the path in 16-frame slices.
        void WalkLog(JoypadButtons b, string tag, int steps) {
            for (var s = 0; (s < steps); ++s) {
                Hold(b: b, frames: 16);
                Console.WriteLine(value: $"    {tag}[{s}] yx={Y():X2},{X():X2} map={bus.ReadByte(address: 0xDA00):X2}/{bus.ReadByte(address: 0xDA01):X2}");
            }
        }

        WalkLog(b: JoypadButtons.Left, tag: "LEFT", steps: 6);

        Hold(b: JoypadButtons.None, frames: 60);

        var recSprite = bus.ReadByte(address: 0xD225); // wObject1Struct sprite ($D225).
        var recSprite2 = bus.ReadByte(address: (ushort)(0xD225 + 40)); // wObject2Struct.

        Console.WriteLine(value: $"  after warp: map={bus.ReadByte(address: 0xDA00):X2}/{bus.ReadByte(address: 0xDA01):X2} yx={Y():X2},{X():X2} obj1Sprite=0x{recSprite:X2} obj2Sprite=0x{recSprite2:X2} playerSprite=0x{bus.ReadByte(address: 0xD1FD):X2}");
    }
    private static void ProbeCapture(byte[] rom) {
        var save = TradeSaveFactory.CreateSaveFile(trainer: TradeSaveFactory.SideA);

        // wSpawnAfterChampion (SRAM 0x2043) = SPAWN_LANCE (1): route CONTINUE through the WARP entry path (spawns at
        // NEW_BARK with full object loading). Recompute the primary checksum so the save still validates.
        save[0x2043] = 1;
        TradeSaveFactory.RewriteChecksum(saveFile: save);

        using var machine = ScriptedTradeHarness.BuildFromSave(rom: rom, save: save);

        var joypad = machine.GetRequiredService<IJoypad>();
        var bus = machine.GetRequiredService<ISystemBus>();
        var script = ScriptedTradeHarness.ContinueScript();

        for (var frame = 0; (frame < 900); ++frame) {
            joypad.SetButtons(pressed: script.ButtonsAt(frame: frame));
            machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
        }

        Console.WriteLine(value: $"  after WARP continue: map={bus.ReadByte(address: 0xDA00):X2}/{bus.ReadByte(address: 0xDA01):X2} yx={bus.ReadByte(address: 0xDA02):X2},{bus.ReadByte(address: 0xDA03):X2} pState={bus.ReadByte(address: 0xD682):X2}");

        // Dump the live object structs region (wObjectStructs $D1FD, 13 * 40 bytes) and the map objects (wMapObjects
        // $D445, 16 * 16 bytes) as hex, plus a per-object one-liner (sprite/mapObjectIndex/direction/action/mapX/mapY).
        static byte[] Read(ISystemBus b, ushort start, int length) {
            var buffer = new byte[length];

            for (var index = 0; (index < length); ++index) {
                buffer[index] = b.ReadByte(address: (ushort)(start + index));
            }

            return buffer;
        }

        var structs = Read(b: bus, start: 0xD1FD, length: (13 * 40));
        var mapObjects = Read(b: bus, start: 0xD445, length: (16 * 16));

        for (var obj = 0; (obj < 13); ++obj) {
            var s = structs.AsSpan(start: (obj * 40), length: 40);

            if (s[0] == 0) {
                continue; // sprite 0 = inactive slot.
            }

            Console.WriteLine(value: $"  objStruct[{obj}] sprite={s[0]:X2} mapObjIdx={s[1]:X2} moveType={s[3]:X2} dir={s[8]:X2} action={s[10]:X2} facing={s[12]:X2} mapX={s[15]:X2} mapY={s[16]:X2}  {Convert.ToHexString(inArray: s.ToArray())}");
        }

        for (var obj = 0; (obj < 16); ++obj) {
            var m = mapObjects.AsSpan(start: (obj * 16), length: 16);

            if (m[1] == 0) {
                continue;
            }

            Console.WriteLine(value: $"  mapObject[{obj}] structId={m[0]:X2} sprite={m[1]:X2} y={m[2]:X2} x={m[3]:X2} movement={m[4]:X2}  {Convert.ToHexString(inArray: m.ToArray())}");
        }
    }
    private static void ProbeDiff(byte[] rom) {
        using var machine = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideA);

        var joypad = machine.GetRequiredService<IJoypad>();
        var bus = machine.GetRequiredService<ISystemBus>();
        var script = ScriptedTradeHarness.ContinueScript();

        for (var frame = 0; (frame < 620); ++frame) {
            joypad.SetButtons(pressed: script.ButtonsAt(frame: frame));
            machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
        }

        static byte[] Snap(ISystemBus b) {
            var buffer = new byte[0x10000];

            for (var address = 0xC000; (address <= 0xFFFE); ++address) {
                buffer[address] = b.ReadByte(address: (ushort)address);
            }

            return buffer;
        }

        static void Diff(string tag, byte[] before, byte[] after) {
            var changes = new List<string>();

            for (var address = 0xC000; (address <= 0xFFFE); ++address) {
                if (before[address] != after[address]) {
                    changes.Add(item: $"{address:X4}:{before[address]:X2}->{after[address]:X2}");
                }
            }

            Console.WriteLine(value: $"  {tag}: {changes.Count} changed  {string.Join(separator: " ", values: changes.Take(count: 40))}");
        }

        // OAM sprite population: count non-blank sprites ($FE00-$FE9F, 40 * 4 bytes; a sprite with Y in 1..159 is on screen).
        var oamSprites = 0;

        for (var sprite = 0; (sprite < 40); ++sprite) {
            var y = bus.ReadByte(address: (ushort)(0xFE00 + (sprite * 4)));

            if ((y > 0) && (y < 160)) {
                ++oamSprites;
            }
        }

        // wPlayerStruct object region ($D1FD..) — the player's own object struct; wPlayerState $D682; wVramState.
        var playerStruct = new byte[16];

        for (var offset = 0; (offset < 16); ++offset) {
            playerStruct[offset] = bus.ReadByte(address: (ushort)(0xD1FD + offset));
        }

        Console.WriteLine(value: $"  OAM on-screen sprites: {oamSprites}");
        Console.WriteLine(value: $"  wPlayerStruct $D1FD: {Convert.ToHexString(inArray: playerStruct)}");
        Console.WriteLine(value: $"  wPlayerState $D682={bus.ReadByte(address: 0xD682):X2} wPlayerDir $D205={bus.ReadByte(address: 0xD205):X2} wPlayerFacing $D20A={bus.ReadByte(address: 0xD20A):X2}");

        // Liveness: one idle frame with no input.
        var idle0 = Snap(b: bus);

        joypad.SetButtons(pressed: JoypadButtons.None);
        machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);

        Diff(tag: "idle-frame", before: idle0, after: Snap(b: bus));

        // Input reach: hold DOWN for 4 frames from the settled state.
        var pre = Snap(b: bus);

        for (var frame = 0; (frame < 4); ++frame) {
            joypad.SetButtons(pressed: JoypadButtons.Down);
            machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
        }

        Diff(tag: "hold-down-4f", before: pre, after: Snap(b: bus));
    }

    // Continue to the overworld, hold a button, and histogram the CPU PC over a long instruction window to see whether the
    // overworld main loop is actually running.
    private static void ProbePc(byte[] rom, string hold) {
        using var machine = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideA);

        var joypad = machine.GetRequiredService<IJoypad>();
        var cpu = machine.GetRequiredService<Puck.HumbleGamingBrick.Interfaces.ICpu>();
        var script = ScriptedTradeHarness.ContinueScript();

        for (var frame = 0; (frame < 620); ++frame) {
            joypad.SetButtons(pressed: script.ButtonsAt(frame: frame));
            machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
        }

        Console.WriteLine(value: $"  after continue: map={ScriptedTradeHarness.LiveMapGroup(machine: machine):X2}/{ScriptedTradeHarness.LiveMapNumber(machine: machine):X2} yx={ScriptedTradeHarness.Peek(machine: machine, address: 0xDA02):X2},{ScriptedTradeHarness.Peek(machine: machine, address: 0xDA03):X2}");

        static string StateLine(MachineInstance m) =>
            ((($"yx={ScriptedTradeHarness.Peek(machine: m, address: 0xDA02):X2},{ScriptedTradeHarness.Peek(machine: m, address: 0xDA03):X2} "
            + $"pState={ScriptedTradeHarness.Peek(machine: m, address: 0xD682):X2} pDir={ScriptedTradeHarness.Peek(machine: m, address: 0xD205):X2} pFacing={ScriptedTradeHarness.Peek(machine: m, address: 0xD20A):X2} ")
            + $"linkMode={ScriptedTradeHarness.Peek(machine: m, address: 0xD042):X2} scriptVar={ScriptedTradeHarness.Peek(machine: m, address: 0xD173):X2} ")
            + $"vblank={ScriptedTradeHarness.Peek(machine: m, address: 0xFF8C):X2} hJoypadDown={ScriptedTradeHarness.Peek(machine: m, address: 0xFF9C):X2} hJoyDown={ScriptedTradeHarness.Peek(machine: m, address: 0xFFA0):X2}");

        Console.WriteLine(value: $"  no-input : {StateLine(m: machine)}");

        var held = LinkInputScript.ParseButtons(text: hold);

        for (var frame = 0; (frame < 24); ++frame) {
            joypad.SetButtons(pressed: held);
            machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);

            if (frame < 4) {
                Console.WriteLine(value: $"  hold[{frame}] : {StateLine(m: machine)}");
            }
        }

        Console.WriteLine(value: $"  after24  : {StateLine(m: machine)}");

        joypad.SetButtons(pressed: held);

        var histogram = new Dictionary<ushort, int>();
        var haltCount = 0;
        const int steps = 400_000;

        for (var step = 0; (step < steps); ++step) {
            var pc = cpu.ProgramCounter;

            histogram[pc] = (histogram.TryGetValue(key: pc, value: out var count) ? (count + 1) : 1);

            if (cpu.IsHalted) {
                ++haltCount;
            }

            machine.Machine.StepInstruction();
        }

        Console.WriteLine(value: $"  holding {hold}: {steps} instructions, {histogram.Count} distinct PCs, halted at {haltCount} samples ({((100.0 * haltCount) / steps):F1}%)");
        Console.WriteLine(value: $"  after window: map={ScriptedTradeHarness.LiveMapGroup(machine: machine):X2}/{ScriptedTradeHarness.LiveMapNumber(machine: machine):X2} yx={ScriptedTradeHarness.Peek(machine: machine, address: 0xDA02):X2},{ScriptedTradeHarness.Peek(machine: machine, address: 0xDA03):X2}");

        foreach (var entry in histogram.OrderByDescending(keySelector: e => e.Value).Take(count: 20)) {
            Console.WriteLine(value: $"    PC 0x{entry.Key:X4}: {entry.Value}");
        }
    }

    // Drives the full peek-gated scripted trade, dumping each side's framebuffer + peek panel every dumpEvery frames and
    // logging every phase transition + the resolved rendezvous roles, so an operator can watch the receptionist walk, the
    // rendezvous, the block exchange, the mon selection, and the post-trade CANCEL exit.
    private static void RunTrade(byte[] rom, int dumpEvery, string outDir) {
        Directory.CreateDirectory(path: outDir);
        Console.WriteLine(value: $"== trade-run (linked Cgb↔Cgb scripted trade) dump every {dumpEvery}, out {outDir} ==");

        object? lastPhase = null;

        var result = ScriptedTradeDriver.Run(
            rom: rom,
            onFrame: frame => {
                if (!Equals(objA: frame.Phase, objB: lastPhase)) {
                    Console.WriteLine(value: $"  [{frame.Frame:D5}] -> phase {frame.Phase} (A status=0x{ScriptedTradeHarness.ConnectionStatus(machine: frame.Driver.MachineA):X2} B status=0x{ScriptedTradeHarness.ConnectionStatus(machine: frame.Driver.MachineB):X2} A map={ScriptedTradeHarness.LiveMapGroup(machine: frame.Driver.MachineA):X2}/{ScriptedTradeHarness.LiveMapNumber(machine: frame.Driver.MachineA):X2} B map={ScriptedTradeHarness.LiveMapGroup(machine: frame.Driver.MachineB):X2}/{ScriptedTradeHarness.LiveMapNumber(machine: frame.Driver.MachineB):X2})");
                    lastPhase = frame.Phase;
                }

                var logEvery = (string.Equals(a: frame.Phase.ToString(), b: "Approach", comparisonType: StringComparison.Ordinal) ? 16 : 80);

                if (((frame.Frame + 1) % logEvery) == 0) {
                    var a = frame.Driver.MachineA;
                    var b = frame.Driver.MachineB;

                    Console.WriteLine(value: $"    [{frame.Frame:D5}] {frame.Phase} statA={ScriptedTradeHarness.ConnectionStatus(machine: a):X2} statB={ScriptedTradeHarness.ConnectionStatus(machine: b):X2} linkA={ScriptedTradeHarness.Peek(machine: a, address: 0xD042):X2}/{ScriptedTradeHarness.Peek(machine: b, address: 0xD042):X2} ayx={ScriptedTradeHarness.Peek(machine: a, address: 0xDA02):X2},{ScriptedTradeHarness.Peek(machine: a, address: 0xDA03):X2} aDir={ScriptedTradeHarness.Peek(machine: a, address: 0xD205):X2} byx={ScriptedTradeHarness.Peek(machine: b, address: 0xDA02):X2},{ScriptedTradeHarness.Peek(machine: b, address: 0xDA03):X2} mapA={ScriptedTradeHarness.LiveMapGroup(machine: a):X2}/{ScriptedTradeHarness.LiveMapNumber(machine: a):X2}");
                }

                if (((frame.Frame + 1) % dumpEvery) == 0) {
                    Dump(machine: frame.Driver.MachineA, outDir: outDir, tag: "A", frame: (frame.Frame + 1));
                    Dump(machine: frame.Driver.MachineB, outDir: outDir, tag: "B", frame: (frame.Frame + 1));
                }
            }
        );

        Console.WriteLine(value: $"  == result: completed={result.Completed} rolesResolved={result.RolesResolved} (A=0x{result.RoleA:X2} B=0x{result.RoleB:X2})");
        Console.WriteLine(value: $"  A: lead=0x{result.LeadA:X2} checksumOk={result.ChecksumOkA} masterSends={result.TrafficA.MasterSends} completions={result.TrafficA.Completions} traffic=0x{result.TrafficA.TrafficHash:X16}");
        Console.WriteLine(value: $"  B: lead=0x{result.LeadB:X2} checksumOk={result.ChecksumOkB} masterSends={result.TrafficB.MasterSends} completions={result.TrafficB.Completions} traffic=0x{result.TrafficB.TrafficHash:X16}");
    }
    private static void RunLone(byte[] rom, LinkInputScript script, int frames, int dumpEvery, string outDir) {
        using var machine = BuildSideA(rom: rom);

        var joypad = machine.GetRequiredService<IJoypad>();

        Console.WriteLine(value: $"== trade-explore (lone Cgb, side A) {frames} frames, dump every {dumpEvery} ==");

        for (var frame = 0; (frame < frames); ++frame) {
            joypad.SetButtons(pressed: script.ButtonsAt(frame: frame));
            machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);

            if ((((frame + 1) % dumpEvery) == 0) || ((frame + 1) == frames)) {
                Dump(machine: machine, outDir: outDir, tag: "A", frame: (frame + 1));
            }
        }

        ScanForMapSpawn(machine: machine);
    }

    // Empirically locate wMapGroup in live WRAM by scanning 0xC000..0xDFFF for the crafted spawn signature
    // (group 20 / map 1, i.e. 0x14 0x01) — resolves "did the map actually load?" and pins the absolute WRAM address
    // of the map-position block for later peek-gated phases (this cart's WRAM addresses are specific to it).
    private static void ScanForMapSpawn(MachineInstance machine) {
        var bus = machine.GetRequiredService<ISystemBus>();

        for (var address = 0xC000; (address <= 0xDFFE); ++address) {
            if ((bus.ReadByte(address: (ushort)address) == 0x14) && (bus.ReadByte(address: (ushort)(address + 1)) == 0x01)) {
                var y = bus.ReadByte(address: (ushort)(address + 2));
                var x = bus.ReadByte(address: (ushort)(address + 3));

                Console.WriteLine(value: $"  wram 0x{address:X4}: 0x14 0x01 then 0x{y:X2} 0x{x:X2} (candidate wMapGroup/wMapNumber/wYCoord/wXCoord)");
            }
        }
    }
    private static void RunLinked(byte[] rom, LinkInputScript scriptA, LinkInputScript scriptB, int frames, int dumpEvery, string outDir) {
        using var machineA = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideA);
        using var machineB = ScriptedTradeHarness.Build(rom: rom, trainer: TradeSaveFactory.SideB);

        Console.WriteLine(value: $"== trade-explore (linked Cgb↔Cgb) {frames} frames, dump every {dumpEvery} ==");

        var result = LinkReplay.Run(
            first: machineA, firstScript: scriptA, second: machineB, secondScript: scriptB, frames: frames,
            onFrame: frame => {
                if ((((frame + 1) % dumpEvery) == 0) || ((frame + 1) == frames)) {
                    Dump(machine: machineA, outDir: outDir, tag: "A", frame: (frame + 1));
                    Dump(machine: machineB, outDir: outDir, tag: "B", frame: (frame + 1));
                }
            }
        );

        Console.WriteLine(value: $"  A: masterSends={result.First.MasterSends} completions={result.First.Completions} status=0x{ScriptedTradeHarness.ConnectionStatus(machine: machineA):X2} lead=0x{TradeSaveFactory.ReadLeadSpecies(sram: ScriptedTradeHarness.ExportSram(machine: machineA)):X2}");
        Console.WriteLine(value: $"  B: masterSends={result.Second.MasterSends} completions={result.Second.Completions} status=0x{ScriptedTradeHarness.ConnectionStatus(machine: machineB):X2} lead=0x{TradeSaveFactory.ReadLeadSpecies(sram: ScriptedTradeHarness.ExportSram(machine: machineB)):X2}");
    }
    private static void Dump(MachineInstance machine, string outDir, string tag, int frame) {
        var framebuffer = machine.GetRequiredService<IFramebuffer>();
        var pixels = framebuffer.Pixels;
        var rgba = new byte[(pixels.Length * 4)];

        for (var pixel = 0; (pixel < pixels.Length); ++pixel) {
            var offset = (pixel * 4);
            var value = pixels[pixel];

            rgba[offset] = (byte)(value >> 16);
            rgba[(offset + 1)] = (byte)(value >> 8);
            rgba[(offset + 2)] = (byte)value;
            rgba[(offset + 3)] = 0xFF;
        }

        var path = Path.Combine(path1: outDir, path2: $"{tag}_{frame:D5}.png");

        PngEncoder.Write(path: path, rgba: rgba, width: framebuffer.Width, height: framebuffer.Height);

        var status = ScriptedTradeHarness.ConnectionStatus(machine: machine);
        var control = ScriptedTradeHarness.Peek(machine: machine, address: 0xFF02);
        var lead = TradeSaveFactory.ReadLeadSpecies(sram: ScriptedTradeHarness.ExportSram(machine: machine));
        var group = ScriptedTradeHarness.Peek(machine: machine, address: 0xDA00);
        var map = ScriptedTradeHarness.Peek(machine: machine, address: 0xDA01);
        var yCoord = ScriptedTradeHarness.Peek(machine: machine, address: 0xDA02);
        var xCoord = ScriptedTradeHarness.Peek(machine: machine, address: 0xDA03);
        var lcdc = ScriptedTradeHarness.Peek(machine: machine, address: 0xFF40);
        var ly = ScriptedTradeHarness.Peek(machine: machine, address: 0xFF44);
        var key1 = ScriptedTradeHarness.Peek(machine: machine, address: 0xFF4D);
        var iflag = ScriptedTradeHarness.Peek(machine: machine, address: 0xFF0F);
        var ienable = ScriptedTradeHarness.Peek(machine: machine, address: 0xFFFF);

        Console.WriteLine(value: $"    [{frame:D5}] {tag} -> {Path.GetFileName(path: path)} fb=0x{HashPixels(pixels: pixels):X16} status=0x{status:X2} SC=0x{control:X2} lead=0x{lead:X2} map={group:X2}/{map:X2} yx={yCoord:X2},{xCoord:X2} LCDC={lcdc:X2} LY={ly:X2} KEY1={key1:X2} IF={iflag:X2} IE={ienable:X2}");
    }
    private static ulong HashPixels(ReadOnlySpan<uint> pixels) {
        var hash = 14_695_981_039_346_656_037ul;

        foreach (var pixel in pixels) {
            hash = ((hash ^ pixel) * 1_099_511_628_211ul);
        }

        return hash;
    }

    internal static ConsoleModel s_model = ConsoleModel.Cgb;

    // Builds side A, honoring a debug --spawn group:map:y:x override (decimal) and --model when present.
    private static MachineInstance BuildSideA(byte[] rom) {
        var save = TradeSaveFactory.CreateSaveFile(trainer: TradeSaveFactory.SideA);

        if (s_spawnOverride is { } spawn) {
            var parts = spawn.Split(separator: ':');

            TradeSaveFactory.PatchSpawn(saveFile: save, group: byte.Parse(s: parts[0]), map: byte.Parse(s: parts[1]), y: byte.Parse(s: parts[2]), x: byte.Parse(s: parts[3]));
            Console.WriteLine(value: $"  [debug spawn override -> group {parts[0]} map {parts[1]} y {parts[2]} x {parts[3]}]");
        }

        return ScriptedTradeHarness.BuildFromSave(rom: rom, save: save, model: s_model, bootRom: s_bootRom);
    }

    // Writes the two crafted trade saves + a README to outDir (created if absent). Each .sav is [32 KiB SRAM][48-byte MBC3
    // RTC footer] — exactly what the demo's GamingBrickChildNode battery import consumes.
    private static void ExportSaves(string outDir) {
        Directory.CreateDirectory(path: outDir);

        var sideA = TradeSaveFactory.CreateSaveFile(trainer: TradeSaveFactory.SideA);
        var sideB = TradeSaveFactory.CreateSaveFile(trainer: TradeSaveFactory.SideB);
        var pathA = Path.Combine(path1: outDir, path2: "trade-side-a-rattata.sav");
        var pathB = Path.Combine(path1: outDir, path2: "trade-side-b-pidgey.sav");

        File.WriteAllBytes(path: pathA, bytes: sideA);
        File.WriteAllBytes(path: pathB, bytes: sideB);
        File.WriteAllText(path: Path.Combine(path1: outDir, path2: "README.md"), contents: ReadmeText());

        Console.WriteLine(value: $"  --trade-export -> {pathA} ({sideA.Length} bytes)");
        Console.WriteLine(value: $"  --trade-export -> {pathB} ({sideB.Length} bytes)");
        Console.WriteLine(value: $"  --trade-export -> {Path.Combine(path1: outDir, path2: "README.md")}");
    }
    private static string ReadmeText() =>
        """
        # Crafted cross-gen trade-cart saves

        Two byte-exact battery saves for the cross-gen trade cart, produced by `TradeSaveFactory`
        (`src/Puck.HumbleGamingBrick.Post`). Each file is `[32 KiB SRAM][48-byte MBC3 RTC footer]`
        (32 816 bytes total) — the straight concatenation `Mbc3Cartridge` imports/exports, i.e. exactly what the
        demo's per-cabinet battery load (`GamingBrickChildNode`) consumes. Point two trade-cart cabinets at these two
        files and each boots a distinct, CONTINUE-able trainer.

        | File | Trainer | ID | Lead |
        |---|---|---|---|
        | `trade-side-a-rattata.sav` | GOLD | 0x1234 | RATTATA (Lv.5) |
        | `trade-side-b-pidgey.sav` | SILVER | 0x5678 | PIDGEY (Lv.5) |

        Both spawn on the shared POKECENTER_2F Cable Club floor (map group 20 / map 1) one tile below the Trade
        Center receptionist, with the `EVENT_GAVE_MYSTERY_EGG_TO_ELM` gate flag set (the sole Cable-Club-access
        requirement), current HP == max HP, a valid primary checksum + check bytes, and a clean (halt/carry-clear)
        RTC footer so no clock-reset prompt appears. Regenerate with:

            dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --trade-export --out <dir>

        The crafted saves are CONTINUE-accepted, the loaded overworld renders + is navigable, and the fully-scripted
        two-machine Cable Club trade runs end-to-end on them: the `link-lock` Post stage drives both sides through the
        rendezvous, the TRADE_CENTER warp, the mon-selection menus, the species swap (auto-saved on both sides), and the
        CANCEL exit back to the overworld.
        """;
    private static LinkInputScript LoadScript(string? path) =>
        (((path is not null) && File.Exists(path: path)) ? LinkInputScript.Load(path: path) : new LinkInputScript());
    private static string? StringArg(string[] args, string name) {
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return args[(index + 1)];
            }
        }

        return null;
    }
    private static int IntArg(string[] args, string name, int fallback) {
        var value = StringArg(args: args, name: name);

        return (((value is not null) && int.TryParse(s: value, result: out var parsed)) ? parsed : fallback);
    }
}
