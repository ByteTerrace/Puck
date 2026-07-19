#:project ../Puck.World.csproj
#:project ../../Puck.Forge/Puck.Forge.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property EnforceCodeStyleInBuild=false
#:property AnalysisLevel=none
// audio-device — the AP3 live device smoke, one .NET 10 file-based app:
//
//   dotnet run src/Puck.World/scripts/audio-device.cs [--no-build] [--width W] [--height H]
//
// STRUCTURAL LIVENESS ONLY — sample content is the offline hash proof's job (audio-mix.cs). Three batteries:
//   (a) failure paths, in-process (the overlay-envelope pattern): the null platform factory parks the service
//       'unsupported'; a declining factory degrades silent, counts rebind attempts on the documented cadence, and
//       stops cleanly without a throw; a mock device that faults mid-stream detaches the mixer, rebinds into a
//       SECOND device generation (attach observed again), and folds its delivered-frame count into the totals.
//   (b) the real endpoint, in-process: the platform factory opens the default render device and delivers frames
//       (>0 within ~1.5 s) with no fault; disposal is bounded. SKIPPED honestly (never failed) when the platform
//       has no backend or no endpoint — battery (c) then asserts the silent posture instead.
//   (c) the full session: boot Puck.World, read audio.state over the pipe. With a device: frames advance and the
//       untouched default world mixes SILENCE (peak stays 0). Then the machine-audio path end to end IN THE
//       SELF-HEAL ORDER — the speaker row lands FIRST (emitters derive, sources stay 0), the cartridge boots
//       SECOND (screen.insert), and the director's per-frame reconcile binds the late machine without any further
//       verb: sources goes to 1 and the running peak goes nonzero — the first sound World has ever made. A
//       screen.boot CUE row (AP4) authored before the insert fires on the boot and reads in speaker.state's live
//       transient tail — the one cue producer that needs a real cartridge. Without a device: audio.state reads
//       silent/rebinding with counted rebinds and the session survives.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Puck.Authoring;
using Puck.Forge.Tune;
using Puck.Platform.Audio;
using Puck.World;
using Puck.World.Audio;
using Puck.World.Client;

var noBuild = args.Contains("--no-build");
var width = ArgInt("--width", 640);
var height = ArgInt("--height", 480);
var failures = 0;

int ArgInt(string name, int fallback) {
    var index = Array.IndexOf(args, name);

    return (((index >= 0) && (index < (args.Length - 1)) && int.TryParse(args[index + 1], out var value)) ? value : fallback);
}

bool Check(string name, bool ok, string detail) {
    Console.WriteLine($"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

    if (!ok) {
        failures++;
    }

    return ok;
}

var repoRoot = Directory.Exists(Path.Combine("docs", "examples", "tunes"))
    ? Path.GetFullPath(".")
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", ".."));

// ---- (a) failure paths, in-process --------------------------------------------------------------------------------
Console.WriteLine("[proof] === audio-device (a): failure paths — unsupported, declining factory, mid-stream fault ===");

{
    // A headless director with one published (empty) snapshot, so the mock generation exercises TryMixBlock's TRUE
    // path (mix-of-nothing = silence) rather than the not-yet-published false path only.
    var director = new WorldAudioDirector(client: null, animator: null);

    director.ReconcileSpeakers(WorldDefinition.Default);
    director.Publish(transforms: [], seats: [new WorldSeatCameraPose(Joined: true, Eye: default, Forward: new(0f, 0f, -1f))]);

    // a1: the null factory (the non-Windows posture) parks as unsupported and never starts a thread.
    var unsupported = new WorldAudioRenderService(director: director, factory: null);

    await unsupported.StartAsync(CancellationToken.None);
    Check("null-factory-parks-unsupported", (unsupported.StateToken == "unsupported"), $"state={unsupported.StateToken}");
    await unsupported.StopAsync(CancellationToken.None);
    Check("null-factory-stops-clean", (unsupported.StateToken == "stopped"), $"state={unsupported.StateToken}");

    // a2: a factory that always declines — silent state, rebind attempts on the cadence, a clean bounded stop.
    var declining = new DecliningFactory();
    var silent = new WorldAudioRenderService(director: director, factory: declining, rebindPeriodMilliseconds: 50);

    await silent.StartAsync(CancellationToken.None);
    Thread.Sleep(400);
    Check("declining-factory-runs-silent", (silent.StateToken == "silent"), $"state={silent.StateToken}");
    Check("declining-factory-counts-rebinds", (silent.RebindAttempts >= 3), $"rebindAttempts={silent.RebindAttempts} after ~8 periods");
    Check("declining-factory-surfaces-reason", ((silent.Fault ?? "").Contains("no endpoint (mock)")), $"fault={silent.Fault}");
    Check("declining-factory-delivers-nothing", (silent.FramesDelivered == 0), $"frames={silent.FramesDelivered}");
    await silent.StopAsync(CancellationToken.None);
    Check("declining-factory-stops-clean", (silent.StateToken == "stopped") && !director.MixerAttached, $"state={silent.StateToken} attached={director.MixerAttached}");

    // a3: a mock device that faults mid-stream — attach observed, frames delivered, detach + rebind into a second
    // generation, totals folded across generations.
    var mock = new MockDeviceFactory(faultAfterQuanta: 12);
    var service = new WorldAudioRenderService(director: director, factory: mock, rebindPeriodMilliseconds: 50);

    await service.StartAsync(CancellationToken.None);

    var attachedDuringFirst = SpinUntil(() => director.MixerAttached, 2000);

    Check("mock-device-attaches-mixer", attachedDuringFirst, $"attached={director.MixerAttached}");
    Check("mock-device-reaches-second-generation", SpinUntil(() => (mock.OpenCount >= 2), 5000), $"generations opened={mock.OpenCount}");
    Check("mock-device-counts-rebind-on-loss", (service.RebindAttempts >= 1), $"rebindAttempts={service.RebindAttempts}");
    Check("mock-device-reattaches", SpinUntil(() => director.MixerAttached, 2000), $"attached={director.MixerAttached}");
    Check("mock-device-frames-accumulate", (service.FramesDelivered > 0), $"frames={service.FramesDelivered}");
    Check("mock-device-fills-clean", (service.FillFaults == 0), $"fillFaults={service.FillFaults}");
    await service.StopAsync(CancellationToken.None);
    Check("mock-device-stops-clean", (service.StateToken == "stopped") && !director.MixerAttached && mock.AllDisposed, $"state={service.StateToken} attached={director.MixerAttached} allDisposed={mock.AllDisposed}");
}

static bool SpinUntil(Func<bool> condition, int timeoutMilliseconds) {
    var deadline = Environment.TickCount64 + timeoutMilliseconds;

    while (Environment.TickCount64 < deadline) {
        if (condition()) {
            return true;
        }

        Thread.Sleep(10);
    }

    return condition();
}

// ---- (b) the real endpoint, in-process ----------------------------------------------------------------------------
Console.WriteLine("[proof] === audio-device (b): the real endpoint opens and delivers frames ===");

var endpointPresent = false;

{
    var factory = AudioRenderPlatform.CreateFactory();

    if (factory is null) {
        Console.WriteLine("[proof]   SKIP real-endpoint: no platform render backend (non-Windows)");
    } else {
        long fillCalls = 0;
        var device = factory.TryOpen(sampleRate: WorldAudioMixer.SampleRate, maxQuantumFrames: WorldAudioMixer.MaxBlockFrames, fill: block => { Interlocked.Increment(ref fillCalls); block.Clear(); }, reason: out var reason);

        if (device is null) {
            Console.WriteLine($"[proof]   SKIP real-endpoint: {reason} (battery (c) asserts the silent posture instead)");
        } else {
            endpointPresent = true;
            Thread.Sleep(1500);
            Check("endpoint-delivers-frames", (device.FramesDelivered > 0), $"frames={device.FramesDelivered} in ~1.5s (buffer {device.BufferFrames} frames @ {device.SampleRate} Hz)");
            Check("endpoint-pumps-fill", (Interlocked.Read(ref fillCalls) > 0), $"fill callbacks={Interlocked.Read(ref fillCalls)}");
            Check("endpoint-healthy", (device.Fault is null) && (device.FillFaults == 0), $"fault={device.Fault ?? "none"} fillFaults={device.FillFaults}");

            var sw = Stopwatch.StartNew();

            device.Dispose();
            Check("endpoint-dispose-bounded", (sw.ElapsedMilliseconds < 3000), $"dispose join {sw.ElapsedMilliseconds}ms");
        }
    }
}

// ---- (c) the full session -----------------------------------------------------------------------------------------
Console.WriteLine("[proof] === audio-device (c): the full session — audio.state over the pipe, then the first audible cabinet ===");

var exe = BuildAndFindExe();

if (exe is null) {
    return 2;
}

var romPath = Path.Combine(Path.GetTempPath(), $"puck-audio-device-{Environment.ProcessId}.gb");
var tunesRoot = Path.Combine(repoRoot, "docs", "examples", "tunes");
var tuneDocument = AudioDocumentStore.Load(Path.Combine(tunesRoot, "tune.audio.json"), tunesRoot);

if (tuneDocument is null) {
    Console.Error.WriteLine($"[proof] FAIL: tune fixture not found under '{tunesRoot}'");

    return 1;
}

File.WriteAllBytes(romPath, TuneRom.Build(tuneDocument));

var stateRegex = new Regex(pattern: @"\[audio\.state: device=(\w+) frames=(\d+) rebinds=(\d+) fillFaults=(\d+) sources=(\d+) voices=(\d+) peak=(\d+) droppedTriggers=(\d+) emitters=(\d+) fault=(.*)\]", options: RegexOptions.Compiled);
var process = Launch(exe);
var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();

Collect(process.StandardOutput);
Collect(process.StandardError);

try {
    _ = Check("simulation-ready", (SendAwait("player.stop 1", "[player.stop:", 60.0) is not null), "console verbs applying");

    var first = ReadState();

    if (first is null) {
        _ = Check("audio-state-echoes", false, "(no audio.state echo)");
    } else if (first.Value.Device == "playing") {
        Check("device-playing", true, $"frames={first.Value.Frames} rebinds={first.Value.Rebinds}");
        Check("device-playing-matches-inproc", endpointPresent, "battery (b) also saw the endpoint");
        Thread.Sleep(1000);

        var second = ReadState();

        Check("frames-advance", ((second is { } s) && (s.Frames > first.Value.Frames) && (s.Frames > 0)), $"frames {first.Value.Frames} -> {second?.Frames}");
        Check("default-world-mixes-silence", ((second is { } s2) && (s2.Peak == 0) && (s2.Fault == "none")), $"peak={second?.Peak} fault={second?.Fault}");

        // THE SELF-HEAL ORDER: the speaker row FIRST (the machine slot is still empty — honest silence)...
        _ = Check("speaker-applies", (SendAwait("""world.speaker.set {"$type":"bed","name":"smoke","center":[0,0,0],"radius":100,"innerRadius":50,"feed":{"source":{"$type":"machine","screenIndex":0},"channel":"mix","gain":1}}""", "[world.mutation: UpsertSpeaker 'smoke' applied]", 20.0) is not null), "bed speaker over machine:0");

        var derived = ReadState();

        Check("speaker-derives-before-machine", ((derived is { } d) && (d.Emitters >= 1) && (d.Sources == 0) && (d.Peak == 0)), $"emitters={derived?.Emitters} sources={derived?.Sources} peak={derived?.Peak}");

        // The screen.boot CUE row (AP4), authored data-first: a looping patch (the 2 s transient cap keeps the
        // polling window honest) through the hash-pin boundary — the in-process canonical hash IS the verb's, so
        // the pin lands in one submission — then the cue row bound to the boot event.
        var cueDrone = SynthPatchCanonicalizer.Canonicalize(new SynthPatchDocument(
            Schema: SynthPatchDocument.CurrentSchema, Name: null, Oscillator: SynthOscillator.Noise,
            DutyThousandths: null, Polynomial: 40, AttackFrames: null, DecayFrames: null, SustainThousandths: null,
            ReleaseFrames: null, PitchMillihertz: 1_000));

        _ = Check("cue-patch-applies", (SendAwait($$$"""world.patch.set {"id":"cue-drone","document":{"schema":"puck.synth.v1","oscillator":"Noise","polynomial":40,"pitchMillihertz":1000},"hash":"{{{cueDrone.Hash}}}"}""", "[world.mutation: UpsertPatch 'cue-drone' applied]", 20.0) is not null), "the drone patch through the hash pin");
        _ = Check("boot-cue-applies", (SendAwait("""world.audio.set {"masterGain":1,"defaultSpeakerRadius":8,"defaultCurve":"smoothstep","defaultBedFadeSeconds":0.5,"listener":"focus","cues":[{"event":"screen.boot","patchId":"cue-drone","placement":"listener"}]}""", "[world.mutation: SetAudioDefaults applied]", 20.0) is not null), "the screen.boot cue row");

        // ...the cartridge SECOND: the per-frame reconcile binds the late machine with no further verb.
        _ = Check("cartridge-boots", (SendAwait($"screen.insert 0 {romPath} gaming-brick cgb", "[screen.insert: screen 0 booted", 30.0) is not null), "tune cart on screen 0");
        _ = Check("boot-cue-fires", (SendAwait("speaker.state", "cue:screen.boot=cue-drone", 5.0) is not null), "cue:screen.boot live in speaker.state");

        var audible = AwaitState(state => ((state.Sources >= 1) && (state.Peak > 0)), 20.0);

        Check("late-machine-self-heals-and-sounds", (audible is not null), ((audible is { } a) ? $"sources={a.Sources} peak={a.Peak} frames={a.Frames}" : "(sources/peak never went live within 20s)"));
        Check("stream-stays-healthy", ((audible is { } h) && (h.Rebinds == first.Value.Rebinds) && (h.FillFaults == 0) && (h.Fault == "none")), $"rebinds={audible?.Rebinds} (boot {first.Value.Rebinds}) fillFaults={audible?.FillFaults} fault={audible?.Fault}");
    } else {
        // No endpoint on this machine: the honest silent posture — alive, counting rebinds, never crashing.
        Check("device-absent-degrades-silent", ((first.Value.Device is "silent" or "rebinding") && (first.Value.Rebinds >= 1)), $"device={first.Value.Device} rebinds={first.Value.Rebinds} fault={first.Value.Fault}");
        Console.WriteLine("[proof]   SKIP audible-cabinet: no render endpoint on this machine");
    }

    // The fault sweep: the whole session, both streams, no unhandled faults.
    var faults = lines.Count(line => (line.Contains("Unhandled exception") || line.Contains("Fatal error.")));

    Check("no-session-faults", (faults == 0), $"{faults} fault line(s)");
} finally {
    try {
        process.Kill(entireProcessTree: true);
    } catch {
        // Already exited.
    }

    File.Delete(romPath);
}

Console.WriteLine($"[proof] audio-device {((failures == 0) ? "PASS" : $"FAIL ({failures})")}");

return ((failures == 0) ? 0 : 1);

// ---- session helpers ----------------------------------------------------------------------------------------------

string? BuildAndFindExe() {
    var projectPath = Path.Combine(repoRoot, "src", "Puck.World");

    if (!noBuild) {
        Console.WriteLine("[proof] building Puck.World (Release)...");

        var build = Process.Start(new ProcessStartInfo {
            Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
            FileName = "dotnet",
            UseShellExecute = false,
        })!;

        build.WaitForExit();

        if (build.ExitCode != 0) {
            Console.Error.WriteLine($"[proof] build failed ({build.ExitCode})");

            return null;
        }
    }

    var binRelease = Path.Combine(projectPath, "bin", "Release");
    var found = (Directory.Exists(binRelease)
        ? Directory.EnumerateFiles(binRelease, "Puck.World.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null);

    if (found is null) {
        Console.Error.WriteLine("[proof] Puck.World.exe not found under bin/Release — build first");
    }

    return found;
}

Process Launch(string exePath) {
    var psi = new ProcessStartInfo {
        FileName = exePath,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = repoRoot,
    };

    foreach (var arg in new[] { "--width", width.ToString(), "--height", height.ToString(), "--exit-after-seconds", "240" }) {
        psi.ArgumentList.Add(arg);
    }

    Console.WriteLine($"[proof] launching: {exePath} --width {width} --height {height}");

    var launched = new Process { StartInfo = psi };

    _ = launched.Start();
    launched.StandardInput.AutoFlush = true;

    return launched;
}

void Collect(StreamReader reader) {
    _ = Task.Run(() => {
        string? line;

        while ((line = reader.ReadLine()) is not null) {
            lines.Enqueue(line);
        }
    });
}

string? SendAwait(string line, string needle, double deadlineSeconds) {
    var mark = lines.Count;

    try {
        process.StandardInput.Write(line);
        process.StandardInput.Write('\n');
    } catch (IOException) {
        return null;
    }

    var deadline = DateTime.UtcNow.AddSeconds(deadlineSeconds);

    while (DateTime.UtcNow < deadline) {
        var snapshot = lines.ToArray();

        for (var i = mark; i < snapshot.Length; i++) {
            if (snapshot[i].Contains(needle)) {
                return snapshot[i];
            }
        }

        Thread.Sleep(100);
    }

    return null;
}

DeviceState? ReadState() {
    var echo = SendAwait("audio.state", "[audio.state:", 15.0);

    return ((echo is null) ? null : Parse(echo));
}

DeviceState? AwaitState(Func<DeviceState, bool> predicate, double deadlineSeconds) {
    var deadline = DateTime.UtcNow.AddSeconds(deadlineSeconds);

    while (DateTime.UtcNow < deadline) {
        if ((ReadState() is { } state) && predicate(state)) {
            return state;
        }

        Thread.Sleep(250);
    }

    return null;
}

DeviceState? Parse(string echo) {
    var match = stateRegex.Match(echo);

    if (!match.Success) {
        return null;
    }

    return new DeviceState(
        Device: match.Groups[1].Value,
        Frames: long.Parse(match.Groups[2].Value),
        Rebinds: int.Parse(match.Groups[3].Value),
        FillFaults: long.Parse(match.Groups[4].Value),
        Sources: int.Parse(match.Groups[5].Value),
        Voices: int.Parse(match.Groups[6].Value),
        Peak: int.Parse(match.Groups[7].Value),
        DroppedTriggers: int.Parse(match.Groups[8].Value),
        Emitters: int.Parse(match.Groups[9].Value),
        Fault: match.Groups[10].Value
    );
}

record struct DeviceState(string Device, long Frames, int Rebinds, long FillFaults, int Sources, int Voices, int Peak, int DroppedTriggers, int Emitters, string Fault);

// ---- the mock platform seam ---------------------------------------------------------------------------------------

// Always declines with a stable reason — the no-endpoint posture, made deterministic.
sealed class DecliningFactory : IAudioRenderDeviceFactory {
    public IAudioRenderDevice? TryOpen(int sampleRate, int maxQuantumFrames, AudioRenderFill fill, out string reason) {
        reason = "no endpoint (mock)";

        return null;
    }
}

// Opens MockDevices that pump the fill on their own thread and fault themselves after a quantum quota — the
// mid-stream device-loss shape without any hardware.
sealed class MockDeviceFactory(int faultAfterQuanta) : IAudioRenderDeviceFactory {
    private readonly List<MockDevice> m_devices = new();

    public int OpenCount { get; private set; }
    public bool AllDisposed {
        get {
            lock (m_devices) {
                return m_devices.All(device => device.Disposed);
            }
        }
    }

    public IAudioRenderDevice? TryOpen(int sampleRate, int maxQuantumFrames, AudioRenderFill fill, out string reason) {
        reason = "";

        var device = new MockDevice(sampleRate, maxQuantumFrames, fill, faultAfterQuanta);

        lock (m_devices) {
            OpenCount++;
            m_devices.Add(device);
        }

        return device;
    }
}

sealed class MockDevice : IAudioRenderDevice {
    private readonly Thread m_thread;
    private readonly int m_faultAfterQuanta;
    private long m_framesDelivered;
    private string? m_fault;
    private volatile bool m_stop;

    public MockDevice(int sampleRate, int maxQuantumFrames, AudioRenderFill fill, int faultAfterQuanta) {
        SampleRate = sampleRate;
        BufferFrames = (maxQuantumFrames * 4);
        m_faultAfterQuanta = faultAfterQuanta;
        m_thread = new Thread(() => {
            var block = new short[maxQuantumFrames * 2];
            var quanta = 0;

            while (!m_stop) {
                fill(block);
                Interlocked.Add(ref m_framesDelivered, maxQuantumFrames);

                if (++quanta >= m_faultAfterQuanta) {
                    Volatile.Write(ref m_fault, "mock device invalidated");

                    return;
                }

                Thread.Sleep(5);
            }
        }) { IsBackground = true, Name = "mock-render" };
        m_thread.Start();
    }

    public int SampleRate { get; }
    public int BufferFrames { get; }
    public long FramesDelivered => Interlocked.Read(ref m_framesDelivered);
    public long FillFaults => 0;
    public string? Fault => Volatile.Read(ref m_fault);
    public bool Disposed { get; private set; }

    public void Dispose() {
        m_stop = true;
        m_thread.Join(2000);
        Disposed = true;
    }
}
