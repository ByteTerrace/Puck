using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A named deterministic device whose lifetime is independent of its output consumers.</summary>
/// <param name="Name">The instance identity used by displays, controls, and hardware operations.</param>
/// <param name="Engine">The registered provider identifier.</param>
/// <param name="Configuration">The provider's versioned configuration, validated through its descriptor.</param>
/// <param name="Running">Whether the host advances this instance. A stopped instance retains its hardware state.</param>
/// <param name="Memory">Ordered hardware bindings, independent of any display.</param>
public sealed record WorldMachine(string Name, string Engine, JsonElement Configuration, bool Running = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMachineMemory>? Memory = null);
