using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A named deterministic device whose lifetime is independent of its output consumers.</summary>
/// <param name="Name">The instance identity used by displays, controls, and hardware operations.</param>
/// <param name="Engine">The registered provider identifier.</param>
/// <param name="Configuration">The provider's versioned configuration, validated through its descriptor.</param>
/// <param name="Running">Whether the host advances this instance. A stopped instance retains its hardware state.</param>
/// <param name="Memory">Ordered hardware bindings, independent of any display.</param>
/// <param name="Cable">The optional standing cable endpoint owned by this machine instance.</param>
/// <param name="ContentFolders">The folders holding the content this device can swap to, in the order they are
/// listed and searched, each relative to the declaring document (<see cref="WorldDocumentPaths"/>), or
/// <see langword="null"/> for a device with no swappable content. A folder that does not exist on this machine is
/// listed as absent, never refused, so a world can name a local folder that only some machines hold.
/// <c>machine.content</c> lists the files in them the device's engine can mount (<see cref="WorldMachineContentFolder"/>)
/// and <c>machine.swap</c> mounts one by name; nothing outside the folders is listed. Omitted from the wire when
/// null.</param>
public sealed record WorldMachine(string Name, string Engine, JsonElement Configuration, bool Running = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMachineMemory>? Memory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMachineCable? Cable = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ContentFolders = null);
