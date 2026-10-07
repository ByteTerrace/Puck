using System.Text.Json.Serialization;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.World;

/// <summary>A counted-cost ledger. Its compact document records measurement layouts and only nonzero budgets, grouped
/// by node, pass and detail. Kind classes come from <see cref="GpuWork.SubmissionKinds"/> unless the layout overrides
/// them. Device documents hold only differences from their backend's defaults. The expanded rows retain measurement
/// order and scope so missing rows, class changes and zero violations keep their exact diagnostics.</summary>
/// <param name="Workload">The world document, repository-relative with forward slashes.</param>
/// <param name="Script">The console script, repository-relative with forward slashes.</param>
/// <param name="Width">The offscreen presentation width in pixels.</param>
/// <param name="Height">The offscreen presentation height in pixels.</param>
/// <param name="Backends">The backends in recording order.</param>
[JsonConverter(typeof(WorldCountersCeilingsJsonConverter))]
public sealed record WorldCountersCeilings(string Workload, string Script, int Width, int Height,
    IReadOnlyList<WorldCountersBackendCeilings> Backends) {
    /// <summary>The document schema tag.</summary>
    public const string SchemaVersion = "puck.counters.ceilings.v1";

    /// <summary>Gets the document schema tag.</summary>
    public string Schema { get; init; } = SchemaVersion;
}
/// <summary>The expanded measurement scopes of a backend. Shared rows also judge an unrecorded device; device rows
/// judge only their recorded device. Storage factors common values and layouts without changing either scope.</summary>
/// <param name="Backend">The backend name.</param>
/// <param name="Devices">Device records in first-recorded order. The first device supplies the runtime floor budget.</param>
/// <param name="Ceilings">The shared measurement rows in diagnostic order, including implicit zero budgets.</param>
public sealed record WorldCountersBackendCeilings(string Backend, IReadOnlyList<WorldCountersDeviceCeilings> Devices,
    IReadOnlyList<WorldCountCeiling> Ceilings);
/// <summary>The expanded rows specific to one recorded device.</summary>
/// <param name="Device">The device identity, including its recorded driver version.</param>
/// <param name="Ceilings">The device's measurement rows in diagnostic order, including implicit zero budgets.</param>
public sealed record WorldCountersDeviceCeilings(GpuDeviceIdentity Device, IReadOnlyList<WorldCountCeiling> Ceilings);
/// <summary>An expanded measurement expectation. The document stores its presence in a layout and omits a zero budget.</summary>
/// <param name="Node">The render node.</param>
/// <param name="Pass">The pass, or null for work outside all passes.</param>
/// <param name="Kind">The GPU submission kind.</param>
/// <param name="Class">The kind's default class or its layout's explicit override.</param>
/// <param name="Ceiling">The nonzero budget, or zero when no budget is stored.</param>
/// <param name="Detail">The detail label, or null for the pass total or outside work.</param>
public sealed record WorldCountCeiling(string Node, string? Pass, string Kind, WorkClass Class, long Ceiling, string? Detail = null);
