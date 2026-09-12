namespace Puck.Abstractions.Machines;

/// <summary>The JSON value kind of an authored provider field.</summary>
public enum MachineFieldKind {
    /// <summary>A text value, optionally constrained to declared choices.</summary>
    String,
    /// <summary>An exact JSON integer, including unsigned hardware addresses.</summary>
    Integer,
    /// <summary>A JSON boolean.</summary>
    Boolean,
    /// <summary>An object whose admitted members are described by child fields.</summary>
    Object,
    /// <summary>An ordered array whose elements follow the item descriptor.</summary>
    Array,
}

/// <summary>How a field participates in host composition, relocation, and pinning.</summary>
public enum MachineFieldRole {
    /// <summary>Provider-owned data with no document reference semantics.</summary>
    Value,
    /// <summary>A content path prepared by this engine's selected content provider when recognized.</summary>
    ContentPath,
    /// <summary>An auxiliary asset path, such as firmware, pinned as exact bytes.</summary>
    AssetPath,
    /// <summary>A reference to a declared world state row.</summary>
    StateReference,
    /// <summary>A reference to a declared machine instance.</summary>
    MachineReference,
    /// <summary>A reference to a declared screen.</summary>
    ScreenReference,
    /// <summary>A provider-owned local declaration whose references are rewritten together under an import alias.</summary>
    Declaration,
    /// <summary>A reference to a provider-owned declaration.</summary>
    LocalReference,
}

/// <summary>One provider-authored field. Runtime admission, schema emission, completion, and import rewriting consume
/// this same description; the host does not recognize console-specific property names.</summary>
/// <param name="Name">The JSON member name, or the diagnostic label for an array item.</param>
/// <param name="Kind">The accepted JSON kind.</param>
/// <param name="Description">Author-facing meaning and units.</param>
/// <param name="Required">Whether an object must contain this field.</param>
/// <param name="Role">The field's composition and asset semantics.</param>
/// <param name="Choices">Exact accepted string values, or null for unrestricted text.</param>
/// <param name="Fields">The admitted children of an object.</param>
/// <param name="Item">The element shape of an array.</param>
/// <param name="Minimum">An inclusive integer lower bound, or null for no bound.</param>
/// <param name="Maximum">An inclusive integer upper bound, or null for no bound.</param>
public sealed record MachineFieldDescriptor(
    string Name,
    MachineFieldKind Kind,
    string Description,
    bool Required = false,
    MachineFieldRole Role = MachineFieldRole.Value,
    IReadOnlyList<string>? Choices = null,
    IReadOnlyList<MachineFieldDescriptor>? Fields = null,
    MachineFieldDescriptor? Item = null,
    decimal? Minimum = null,
    decimal? Maximum = null
);

/// <summary>A versioned object shape owned by a provider.</summary>
/// <param name="Id">The exact schema identifier carried by a configuration or operation envelope.</param>
/// <param name="Fields">The admitted object fields, in author-facing order. Undeclared fields refuse.</param>
public sealed record MachineObjectDescriptor(string Id, IReadOnlyList<MachineFieldDescriptor> Fields);

/// <summary>One named optional input or output.</summary>
/// <param name="Name">The stable provider-owned port name.</param>
/// <param name="Contract">The capability contract, such as puck.machine.video.v1 or puck.machine.pad.v1.</param>
/// <param name="Description">What the port presents or controls.</param>
public sealed record MachinePortDescriptor(string Name, string Contract, string Description);

/// <summary>A provider-owned ordered operation and the exact payload it accepts.</summary>
/// <param name="Id">The operation identifier used by commands and rule effects.</param>
/// <param name="Description">The hardware and lifecycle effects visible to authors.</param>
/// <param name="Payload">The versioned payload shape.</param>
public sealed record MachineOperationDescriptor(string Id, string Description, MachineObjectDescriptor Payload);

/// <summary>The provider's single authoring and admission description. Runtime capabilities must agree with it for
/// every admitted configuration; provider-specific semantic validation may impose additional constraints.</summary>
/// <param name="Id">The registered engine identifier.</param>
/// <param name="Description">The engine's author-facing purpose.</param>
/// <param name="Configuration">The structured configuration shape.</param>
/// <param name="VideoOutputs">Available video output names.</param>
/// <param name="AudioOutputs">Available audio stream names.</param>
/// <param name="InputPorts">Available normalized input ports.</param>
/// <param name="Operations">Supported ordered operations.</param>
/// <param name="MemorySpaces">Inspectable or writable hardware address spaces.</param>
public sealed record MachineEngineDescriptor(
    string Id,
    string Description,
    MachineObjectDescriptor Configuration,
    IReadOnlyList<MachinePortDescriptor> VideoOutputs,
    IReadOnlyList<MachinePortDescriptor> AudioOutputs,
    IReadOnlyList<MachinePortDescriptor> InputPorts,
    IReadOnlyList<MachineOperationDescriptor> Operations,
    IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces
);
