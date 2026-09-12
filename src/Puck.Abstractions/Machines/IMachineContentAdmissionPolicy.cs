namespace Puck.Abstractions.Machines;

/// <summary>How a machine content admission policy treats authored content.</summary>
public enum MachineContentAdmissionMode {
    /// <summary>Admit any non-empty content path after the host has pinned its bytes.</summary>
    Open,
    /// <summary>Admit only content carrying one of the policy's exact authored format identifiers.</summary>
    AuthoredFormatsOnly,
    /// <summary>Admit only executable bytes whose SHA-256 digest is in the policy's allowlist.</summary>
    HashOnly,
}

/// <summary>Whether an auxiliary asset path is admitted independently of authored cartridge policy.</summary>
/// <remarks>Allowing an asset path is an explicit host permission. Content admission alone is not a sandbox for
/// privileged native code; machine and hardware configuration permissions remain a separate host concern.</remarks>
public enum MachineAssetAdmission {
    /// <summary>Refuse auxiliary asset paths.</summary>
    Deny,
    /// <summary>Admit non-empty auxiliary asset bytes without treating them as authored cartridge content.</summary>
    Allow,
}

/// <summary>The host-stamped bytes and role presented for one machine field admission decision.</summary>
/// <param name="EngineId">The registered engine identifier stamped by the host.</param>
/// <param name="FieldPath">The dotted descriptor path being prepared.</param>
/// <param name="Role">The descriptor role of the field.</param>
/// <param name="VerifiedSourceFormat">The provider-verified authored format identifier, or null for native bytes.</param>
/// <param name="SourceBytes">The exact authored bytes borrowed for this decision.</param>
/// <param name="ExecutableBytes">The exact executable bytes that will be mounted.</param>
/// <remarks>The host owns both byte sequences for the complete preparation and mount operation. The policy does not
/// read files, inspect filenames or headers, or retain either borrowed memory value. A non-null format is a
/// provider stamp made only after that provider successfully parses and compiles the authored source.</remarks>
public sealed record MachineContentAdmissionRequest(
    string EngineId,
    string FieldPath,
    MachineFieldRole Role,
    string? VerifiedSourceFormat,
    ReadOnlyMemory<byte> SourceBytes,
    ReadOnlyMemory<byte> ExecutableBytes
);

/// <summary>The stable result of evaluating one machine content admission request.</summary>
/// <param name="Allowed">Whether the host may continue preparation and machine construction.</param>
/// <param name="Code">A stable machine-readable result code.</param>
/// <param name="Detail">A diagnostic explanation suitable for logs and user-facing host errors.</param>
public readonly record struct MachineContentAdmissionDecision(bool Allowed, string Code, string Detail);

/// <summary>Decides whether host-pinned machine content may enter a machine field.</summary>
/// <remarks>Implementations must be deterministic and must not perform file I/O, inspect a filename or executable
/// header, or retain the request's borrowed byte memory.</remarks>
public interface IMachineContentAdmissionPolicy {
    /// <summary>Evaluates one host-pinned content request.</summary>
    /// <param name="request">The engine, field, role, verified format and exact byte sequences.</param>
    /// <returns>A stable allow/refuse decision.</returns>
    MachineContentAdmissionDecision Evaluate(MachineContentAdmissionRequest request);
}
