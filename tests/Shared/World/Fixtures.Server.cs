using Puck.Commands;

using Xunit;

using Puck.Abstractions.Machines;
using Puck.Physics.Fields;
using Puck.Testing;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.World.Machines;

namespace Puck.World.Testing;

internal static partial class Fixtures {
    /// <summary>Builds a FRESH, isolated, in-process <see cref="WorldServer"/> over <paramref name="definition"/>
    /// (<see cref="BuildDocument"/>'s own output when omitted) — the same construction shape
    /// <see cref="Puck.World.WorldReplaySnapshot.Drive"/> uses to rehydrate an authoritative world for offline
    /// replay verification (no GPU, no window, no client): a fresh <see cref="WorldPopulation"/>, an unconfigured
    /// <see cref="WorldRenderEnvelope"/> (reads as "fits" — no render-growing edit is exercised here), a
    /// <see cref="WorldMachineHost"/> with the caller's catalog or engines (empty by default), and a
    /// scratch-directory <see cref="WorldOwnedWorlds"/> catalog seeded from the same document. Every caller —
    /// including one passing its own document — crosses the SAME serialize/deserialize round-trip
    /// <see cref="DefaultWorldBytes"/>'s own doc names as the fixture's trustworthiness proof. Callers own disposal
    /// via <see cref="WorldFixture.Dispose"/>.</summary>
    /// <param name="definition">The document to boot the server from, or <see langword="null"/> for <see cref="BuildDocument"/>.</param>
    /// <param name="engines">The screen-machine engines a declared <c>screens</c> row resolves against, or
    /// <see langword="null"/> for none when no <paramref name="machineCatalog"/> is supplied.</param>
    /// <param name="machineCatalog">An explicit catalog including content providers, instead of engine-only registration.</param>
    /// <param name="documentPath">The source document whose directory the definition's relative paths (machine content
    /// among them) resolve beside, or null for the definition's own directory.</param>
    /// <param name="landingRefusal">Optional transfer admission policy supplied by the law.</param>
    /// <param name="catalogNarration">The hub the fixture's owned-world catalog narrates through, or <see langword="null"/> for none.</param>
    /// <param name="consoleNarration">Whether the server narrates to the console, or attaches no narration sink at all.</param>
    public static WorldFixture FreshServer(WorldDefinition? definition = null, IEnumerable<Puck.Abstractions.Machines.IMachineEngine>? engines = null, WorldMachineCatalog? machineCatalog = null, string? documentPath = null, Func<int, string?>? landingRefusal = null, WorldOutputHub? catalogNarration = null, bool consoleNarration = true) {
        // The default document's BYTES are serialized once for the whole run. Each fixture still deserializes its
        // own graph — that is what keeps one test's mutation off the next test's document — but the serialize half
        // of the round trip is the same work every time and is not worth repeating seven hundred times.
        var bytes = ((definition is null)
            ? DefaultBytes.Value
            : WorldDefinitionSerialization.Serialize(definition: definition)
        );

        // The round trip keeps the directory the document's relative paths resolve beside: the named document's, else
        // the definition's own.
        definition = WorldDefinitionSerialization.Deserialize(
            documentDirectory: ((documentPath is { } named)
                ? WorldDocumentPaths.DirectoryOf(documentPath: named)
                : definition?.DocumentDirectory),
            utf8Json: bytes
        );

        var population = new WorldPopulation(definition: definition);
        var machines = new WorldMachineHost(
            screens: definition.Screens,
            catalog: (machineCatalog ?? ((engines is not null) ? new WorldMachineCatalog(engines) : TestMachines.Catalog()))
        );
        // The state directory is a PATH under the fixture's own scratch directory: WorldOwnedWorlds creates and
        // enumerates it itself, and WorldFixture.Dispose resolves the scratch directory. A host that may still hold a
        // file at disposal, or a slow disk, must never fail a law that already ran, so the delete is best-effort.
        var scratch = new TemporaryDirectory(
            bestEffortDelete: true,
            prefix: "puck-world-tests-"
        );
        var stateDirectory = scratch.PathOf(name: "state");
        var profiles = new WorldOwnedWorlds(
            template: definition,
            directory: stateDirectory,
            machineId: Guid.NewGuid(),
            narrationHub: catalogNarration
        );
        var server = new WorldServer(
            definition: definition,
            population: population,
            profiles: profiles,
            envelope: new WorldRenderEnvelope(),
            machines: machines,
            narrationSink: (consoleNarration ? new WorldConsoleNarrationSink() : null),
            landingRefusal: landingRefusal
        );

        return new WorldFixture(
            machines: machines,
            scratch: scratch,
            server: server,
            stepTicks: StepTicksAt(rateHz: definition.SimulationRateHz)
        );
    }
    /// <summary>Boots a fresh server over <paramref name="document"/>, joins one body via <paramref name="join"/>,
    /// then steps <paramref name="ticks"/> times collecting the per-tick
    /// <see cref="WorldReplaySnapshot.HashState"/> trace — the raw material for every "an identical replay
    /// reproduces identical hashes, a control diverges" law. <paramref name="perTick"/>, when supplied, runs before
    /// each step (submitting that tick's intent); when omitted, nothing is ever submitted, so the joined body sits
    /// on its source's own resolution.</summary>
    /// <param name="document">The document to boot the server from.</param>
    /// <param name="ticks">How many steps to drive and hash.</param>
    /// <param name="join">Joins (and optionally configures) the one body the trace drives.</param>
    /// <param name="perTick">The per-tick intent submission, or <see langword="null"/> to submit nothing.</param>
    /// <param name="stepTicks">The per-step tick width, or <see langword="null"/> for <see cref="StepTicks"/>.</param>
    public static ulong[] DriveHashTrace(WorldDefinition document, int ticks, Func<WorldFixture, WorldBody> join, Action<WorldBody, int>? perTick = null, ulong? stepTicks = null) {
        using var fixture = FreshServer(definition: document);
        var body = join(fixture);
        var hashes = new ulong[ticks];

        for (var tick = 0; (tick < ticks); tick++) {
            perTick?.Invoke(
                body,
                tick
            );
            fixture.Step(stepTicks: stepTicks);
            hashes[tick] = WorldReplaySnapshot.HashState(population: fixture.Server.Population);
        }

        return hashes;
    }
    /// <summary>Steps until <paramref name="settled"/> holds, or until <paramref name="ceiling"/> ticks have run.</summary>
    /// <param name="fixture">The fixture to step.</param>
    /// <param name="ceiling">The most ticks to run — the same worst-case bound a fixed loop would have spelled.</param>
    /// <param name="settled">The property the law is waiting for.</param>
    /// <returns>The tick the property first held on, or <paramref name="ceiling"/> when it never did.</returns>
    /// <remarks>A law that waits for a body to come to rest or a value to converge wants the SETTLED state, not a
    /// particular number of ticks. Spelling the wait as a fixed count means picking a number comfortably past the
    /// worst case and then simulating all of it every run, forever, including the thousands of ticks after the
    /// answer stopped changing. The ceiling still bounds a law that never settles, and the assertion that follows is
    /// unchanged — this only stops paying for ticks nobody reads.</remarks>
    public static int StepUntil(this WorldFixture fixture, int ceiling, Func<bool> settled) {
        ArgumentNullException.ThrowIfNull(argument: fixture);
        ArgumentNullException.ThrowIfNull(argument: settled);

        for (var tick = 0; (tick < ceiling); tick++) {
            if (settled()) {
                return tick;
            }

            fixture.Step();
        }

        return ceiling;
    }
    /// <summary>Steps until <paramref name="observe"/> reports the same value <paramref name="quiet"/> ticks running,
    /// or until <paramref name="ceiling"/> ticks have run.</summary>
    /// <param name="fixture">The fixture to step.</param>
    /// <param name="ceiling">The most ticks to run.</param>
    /// <param name="observe">The value whose convergence the law is waiting for.</param>
    /// <param name="quiet">How many consecutive unchanged reads count as converged.</param>
    /// <returns>The tick convergence was reached on, or <paramref name="ceiling"/> when it never was.</returns>
    public static int StepUntilStable(this WorldFixture fixture, int ceiling, Func<long> observe, int quiet = 64) {
        ArgumentNullException.ThrowIfNull(argument: fixture);
        ArgumentNullException.ThrowIfNull(argument: observe);

        var last = observe();
        var held = 0;

        for (var tick = 0; (tick < ceiling); tick++) {
            fixture.Step();

            var current = observe();

            held = ((current == last)
                ? (held + 1)
                : 0
            );
            last = current;

            if (held >= quiet) {
                return tick;
            }
        }

        return ceiling;
    }
    /// <summary>Formats one body's raw fixed-point motion state as a recorded-trace line: position, planar velocity,
    /// vertical velocity, and yaw, each as sixteen lowercase hex digits, space-separated.</summary>
    /// <param name="body">The body to read.</param>
    /// <returns>The trace line.</returns>
    public static string TraceLine(WorldBody body) {
        static string Hex(FixedQ4816 value) => value.Value.ToString(
            format: "x16",
            provider: System.Globalization.CultureInfo.InvariantCulture
        );

        var state = body.CaptureTransferState();
        var position = body.FixedPosition;

        return string.Join(
            separator: ' ',
            value: [
                Hex(value: position.X), Hex(value: position.Y), Hex(value: position.Z),
                Hex(value: state.PlanarVelocity.X), Hex(value: state.PlanarVelocity.Y), Hex(value: state.PlanarVelocity.Z),
                Hex(value: state.VerticalVelocity), Hex(value: body.FixedYaw),
            ]
        );
    }
    /// <summary>Compiles <paramref name="document"/> (plus any extra world <paramref name="state"/> rows) into a
    /// <see cref="FieldLattice"/> the way a booted world would.</summary>
    public static FieldLattice BuildLattice(
        WorldFieldsSection document,
        ulong worldSeed = 0UL,
        IReadOnlyList<WorldStateRow>? state = null
    ) {
        var section = WorldFieldsSection.ToStateSection(composite: document);

        if (state is { Count: > 0 }) {
            section = section with { World = [.. (section.World ?? []), .. state] };
        }

        var catalog = StateCatalog.Compile(section: section);

        return new FieldLattice(
            input: WorldPopulation.CompileFieldLatticeInput(
                document: document,
                program: WorldFieldProgram.Compile(
                    document: document,
                    state: catalog
                )
            ),
            worldSeed: worldSeed
        );
    }

    /// <summary>The <c>machineHostFactory</c> every <see cref="WorldReplayTape"/>/<see cref="WorldReplaySnapshot"/>/
    /// <see cref="WorldInstanceHost"/> construction here wires — the real <see cref="WorldMachineHost"/>
    /// (<c>Puck.World.Addons.Machines</c>), the same type <see cref="FreshServer"/> constructs directly.</summary>
    public static readonly Func<IReadOnlyList<WorldScreen>, IEnumerable<IMachineEngine>, WorldOutputHub?, IWorldMachineHost> MachineHostFactory =
        static (screens, engines, narrationHub) => {
            var selected = engines.ToArray();
            var providers = TestMachines.Catalog().ContentProviders.Values
                .Where(predicate: provider => selected.Any(predicate: engine => (engine.Id == provider.EngineId)));

            return new WorldMachineHost(
                screens: screens,
                engines: selected,
                compilers: providers,
                narrationHub: narrationHub
            );
        };
}
/// <summary>A fresh, disposable <see cref="WorldServer"/> plus the resources its construction owns
/// (<see cref="WorldMachineHost"/>, the scratch profile-catalog directory) — bundled so a law body drives the
/// server without having to know what else a fresh boot required.</summary>
internal sealed class WorldFixture : IDisposable {
    private readonly WorldMachineHost m_machines;
    private readonly TemporaryDirectory? m_scratch;
    private readonly ulong m_stepTicks;

    // stepTicks is the engine-tick width one Step advances by; null means one simulation tick at the default
    // fixture rate. scratch is a directory the fixture owns and resolves at Dispose; null means the caller owns
    // whatever directory the server was booted over.
    internal WorldFixture(WorldServer server, WorldMachineHost machines, ulong? stepTicks = null, TemporaryDirectory? scratch = null) {
        Server = server;
        m_machines = machines;
        m_scratch = scratch;
        m_stepTicks = (stepTicks ?? Fixtures.StepTicks);
    }

    /// <summary>The live server under test.</summary>
    public WorldServer Server { get; }

    /// <summary>The live document's current bytes — the byte-identity probe the all-or-nothing law compares
    /// before/after an apply attempt.</summary>
    public byte[] DefinitionBytes() => WorldDefinitionSerialization.Serialize(definition: Server.Definition);
    /// <inheritdoc/>
    public void Dispose() {
        m_machines.Dispose();
        m_scratch?.Dispose();
    }
    /// <summary>Steps until the first search reports done or <paramref name="maxTicks"/> steps have run, whichever is
    /// first, and returns that search's last status.</summary>
    /// <param name="maxTicks">The step bound.</param>
    /// <returns>The search's status after the last step.</returns>
    public ArenaSearchStatus SettleSearch(int maxTicks = 4000) {
        var status = Server.SearchStatus()[0];

        for (var tick = 0; ((tick < maxTicks) && !status.Done); tick++) {
            Step();
            status = Server.SearchStatus()[0];
        }

        return status;
    }
    /// <summary>Joins local seat <paramref name="slot"/> through the ordinary session door, asserting it is
    /// accepted, and returns the body it drives.</summary>
    /// <param name="slot">The 0-based seat slot, which is also the body index.</param>
    /// <returns>The joined body.</returns>
    public WorldBody JoinSeat(int slot = 0) {
        var actor = Principal.Seat(slot: slot);

        Assert.True(condition: Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        return Server.Body(index: actor.Index)!;
    }
    /// <summary>Drains one authority step through the normal buffered mutation pipeline. Uses the same
    /// authority-owned advancement as the production step shell, including checkpoint rewinds.</summary>
    /// <remarks>The default width is one SIMULATION tick of the document this fixture booted from, so a law that
    /// authors its own <c>simulation.rateHz</c> gets that rate's tick duration without restating it per call.</remarks>
    /// <param name="stepTicks">An explicit engine-tick width, or <see langword="null"/> for one simulation tick.</param>
    public void Step(ulong? stepTicks = null) {
        var width = (stepTicks ?? m_stepTicks);

        Server.Advance(stepTicks: width);
    }
}
/// <summary>An <see cref="IWorldAddonHost"/> that mounts and pumps nothing — the addon-less shadow host tests
/// unrelated to the addon seam wire a <see cref="WorldReplayTape"/>'s required <c>addonHostFactory</c> parameter
/// with, since this project cannot reference <c>Puck.World.Addons</c>.</summary>
internal sealed class NullAddonHost : IWorldAddonHost {
    /// <inheritdoc/>
    public bool AnyEverPumped => false;
    /// <inheritdoc/>
    public int MountedCount => 0;
    /// <summary>Gets how many times <see cref="TryPrepare"/> was actually called on THIS instance — the structural-
    /// attach discriminator <c>ReplayAddonHostAttachLawTests</c> reads: a shadow server that never reached
    /// this exact object leaves it at zero regardless of what the recorded stream submitted.</summary>
    public int PrepareCallCount { get; private set; }
    /// <inheritdoc/>
    public IReadOnlyList<WorldAddonReceipt> Receipts => [];
    public WorldExtensionReplayPolicy ReplayPolicy { get; set; } = WorldExtensionReplayPolicy.Recomputed;

    /// <inheritdoc/>
    public void ApplyContributions(ulong tick) { }
    /// <inheritdoc/>
    public void Commit(IWorldAddonPreparedPlan plan) { }
    /// <inheritdoc/>
    public void CompleteMutation(long addonInstanceId, ushort actOrdinal, bool applied) { }
    /// <inheritdoc/>
    public string? DescribeUndeclaredGrantedChannels(Principal principal, ChannelReachMask? reach, WorldChannelTable channels) => null;
    /// <inheritdoc/>
    public void Dispose() { }
    /// <inheritdoc/>
    public void Finish(IWorldAddonPreparedPlan plan) { }
    /// <inheritdoc/>
    public void ResolveReads(ulong tick) { }
    /// <inheritdoc/>
    public void TickAddons(ulong tick) { }
    /// <inheritdoc/>
    public bool TryPrepare(WorldDefinition? current, WorldDefinition candidate, out IWorldAddonPreparedPlan? plan, out string? reason) {
        ++PrepareCallCount;
        plan = null;
        reason = null;

        return true;
    }
}
