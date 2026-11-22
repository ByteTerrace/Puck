using System.Diagnostics;
using Puck.Abstractions.Machines;
using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>
/// What a frame reads of one world's machines, captured whole by the machine host on the pump at a host step's collect:
/// the video outputs of its running instances, each instance's fault, the revision of its instance map, and the
/// presentation clock its machines' segments are stamped with. A screen holds this record, never the host, so a frame never reads a field the next tick
/// writes. The outputs are the runtimes' own presentation objects, which the machines hand their completed frames to.
/// </summary>
public sealed class WorldMachinePresentation {
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IMachineVideoOutput>> NoOutputs =
        new Dictionary<string, IReadOnlyDictionary<string, IMachineVideoOutput>>(comparer: StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> NoFaults = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IMachineVideoOutput>> m_outputs;

    /// <summary>Initializes a new instance of the <see cref="WorldMachinePresentation"/> class.</summary>
    /// <param name="instanceRevision">The host's instance revision when the outputs were read.</param>
    /// <param name="clock">The presentation clock: the sum of the steps the host advanced, in engine ticks.</param>
    /// <param name="step">The latest step's width, in engine ticks, or zero before the first step.</param>
    /// <param name="stepTimestamp">The <see cref="Stopwatch"/> timestamp the latest step began at.</param>
    /// <param name="synchronous">Whether the latest step completed every machine's work before a screen reads it
    /// (lockstep), so a frame presents the step's end.</param>
    /// <param name="outputs">Each running instance's video outputs by provider-owned name, keyed by instance name.</param>
    /// <param name="faults">Each faulted instance's fault (a missing content file, an unresolved engine, a queue fault),
    /// keyed by instance name; an instance running cleanly has none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outputs"/> or <paramref name="faults"/> is
    /// <see langword="null"/>.</exception>
    public WorldMachinePresentation(ulong instanceRevision, ulong clock, ulong step, long stepTimestamp, bool synchronous,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IMachineVideoOutput>> outputs, IReadOnlyDictionary<string, string> faults) {
        ArgumentNullException.ThrowIfNull(argument: outputs);
        ArgumentNullException.ThrowIfNull(argument: faults);
        Faults = faults;
        InstanceRevision = instanceRevision;
        Clock = clock;
        Step = step;
        StepTimestamp = stepTimestamp;
        Synchronous = synchronous;
        m_outputs = outputs;
    }

    /// <summary>Gets the presentation of a host with no machines.</summary>
    public static WorldMachinePresentation None { get; } = new(clock: 0UL, faults: NoFaults, instanceRevision: 0UL, outputs: NoOutputs, step: 0UL, stepTimestamp: 0L, synchronous: false);

    /// <summary>Gets the sum of the steps the host advanced, in engine ticks: the clock every machine's segments are
    /// stamped with.</summary>
    public ulong Clock { get; }
    /// <summary>Gets each faulted instance's fault, keyed by instance name.</summary>
    public IReadOnlyDictionary<string, string> Faults { get; }
    /// <summary>Gets the host's instance revision when the outputs were read: it moves whenever an instance boots, is
    /// replaced, removed or restored.</summary>
    public ulong InstanceRevision { get; }
    /// <summary>Gets the latest step's width, in engine ticks, or zero before the first step.</summary>
    public ulong Step { get; }
    /// <summary>Gets the <see cref="Stopwatch"/> timestamp the latest step began at.</summary>
    public long StepTimestamp { get; }
    /// <summary>Gets a value indicating whether the latest step completed every machine's work before a screen reads it
    /// (lockstep).</summary>
    public bool Synchronous { get; }

    /// <summary>Gets the position on <see cref="Clock"/> that a frame rendered at <paramref name="timestamp"/> presents:
    /// the frames a machine completed up to it are due (<see cref="IMachineVideoOutput.FrameDue"/>). It runs from the
    /// latest step's start to its end over the host time one step takes, so a machine's frames show at the rate it
    /// completes them however few steps a second the world runs; under lockstep it is the step's end.</summary>
    /// <param name="timestamp">A <see cref="Stopwatch"/> timestamp.</param>
    /// <returns>The presented position, in engine ticks.</returns>
    public ulong PresentedTicks(long timestamp) {
        if (Synchronous || (Step == 0UL)) {
            return Clock;
        }

        var fraction = Math.Clamp(
            max: 1d,
            min: 0d,
            value: (Stopwatch.GetElapsedTime(endingTimestamp: timestamp, startingTimestamp: StepTimestamp).TotalSeconds / FixedTickConversion.ToSeconds(ticks: Step))
        );

        return ((Clock - Step) + ((ulong)(fraction * Step)));
    }
    /// <summary>Resolves one running instance's video output.</summary>
    /// <param name="instance">The authored instance name.</param>
    /// <param name="output">The provider's output name.</param>
    /// <returns>The output, or <see langword="null"/> when no running instance of that name has it.</returns>
    public IMachineVideoOutput? VideoOutput(string instance, string output) => ((m_outputs.TryGetValue(key: instance, value: out var outputs) && outputs.TryGetValue(key: output, value: out var selected))
        ? selected
        : null);
}
