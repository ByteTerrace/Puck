using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Puck.Abstractions.Machines;

/// <summary>An immutable, file-free machine content admission policy.</summary>
/// <remarks>Format identifiers compare with ordinal equality. Executable exceptions are SHA-256 digests of the exact
/// executable bytes supplied in the request. Constructor inputs are cloned, and the request's borrowed bytes are not
/// retained. <see cref="MachineAssetAdmission"/> is deliberately independent so an auxiliary firmware asset is not
/// accidentally classified as an authored cartridge. The policy trusts a non-null source format only as a
/// provider stamp made after successful parse and compile; it never parses bytes itself.</remarks>
public sealed class MachineContentAdmissionPolicy : IMachineContentAdmissionPolicy {
    /// <summary>The policy result code for a successful open admission.</summary>
    public const string AllowedOpenCode = "allowed-open";

    /// <summary>The policy result code for a successful authored-format admission.</summary>
    public const string AllowedFormatCode = "allowed-authored-format";

    /// <summary>The policy result code for a successful executable-hash admission.</summary>
    public const string AllowedHashCode = "allowed-executable-hash";

    /// <summary>The policy result code for a successful explicit auxiliary-asset admission.</summary>
    public const string AllowedAssetCode = "allowed-asset-path";

    /// <summary>The policy result code for a malformed request.</summary>
    public const string InvalidRequestCode = "invalid-request";

    /// <summary>The policy result code for native or unverified content refusal.</summary>
    public const string NativeRefusedCode = "native-content-refused";

    /// <summary>The policy result code for an untrusted authored format.</summary>
    public const string UntrustedFormatCode = "untrusted-source-format";

    /// <summary>The policy result code for an executable hash refusal.</summary>
    public const string HashRefusedCode = "executable-hash-refused";

    /// <summary>The policy result code for a refused auxiliary asset path.</summary>
    public const string AssetRefusedCode = "asset-path-refused";

    private readonly FrozenSet<string> m_trustedSourceFormats;
    private readonly FrozenSet<string> m_executableSha256Exceptions;

    /// <summary>Creates an immutable policy from trusted format IDs and executable SHA-256 exceptions.</summary>
    /// <param name="mode">The primary admission mode.</param>
    /// <param name="trustedSourceFormats">Exact ordinal authored format IDs for <see cref="MachineContentAdmissionMode.AuthoredFormatsOnly"/>.</param>
    /// <param name="executableSha256Exceptions">Hex SHA-256 digests of exact executable bytes admitted in addition to the format policy.</param>
    /// <param name="assetAdmission">The independent disposition of auxiliary <see cref="MachineFieldRole.AssetPath"/> fields.</param>
    /// <exception cref="ArgumentException">A configured format ID or digest is empty or malformed, or a required allowlist is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The mode or asset disposition is not defined.</exception>
    public MachineContentAdmissionPolicy(
        MachineContentAdmissionMode mode,
        IEnumerable<string>? trustedSourceFormats = null,
        IEnumerable<string>? executableSha256Exceptions = null,
        MachineAssetAdmission assetAdmission = MachineAssetAdmission.Deny) {
        if (!Enum.IsDefined(mode)) {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown content admission mode.");
        }
        if (!Enum.IsDefined(assetAdmission)) {
            throw new ArgumentOutOfRangeException(nameof(assetAdmission), assetAdmission, "Unknown asset admission.");
        }

        m_trustedSourceFormats = CloneFormats(trustedSourceFormats);
        m_executableSha256Exceptions = CloneHashes(executableSha256Exceptions);
        if (mode == MachineContentAdmissionMode.AuthoredFormatsOnly && m_trustedSourceFormats.Count == 0) {
            throw new ArgumentException("Authored-format admission requires at least one trusted source format.", nameof(trustedSourceFormats));
        }
        if (mode == MachineContentAdmissionMode.HashOnly && m_executableSha256Exceptions.Count == 0) {
            throw new ArgumentException("Hash-only admission requires at least one executable SHA-256 exception.", nameof(executableSha256Exceptions));
        }
        Mode = mode;
        AssetAdmission = assetAdmission;
        TrustedSourceFormats = m_trustedSourceFormats;
        ExecutableSha256Exceptions = m_executableSha256Exceptions;
    }

    /// <summary>Creates an open policy with explicit auxiliary-asset disposition.</summary>
    public static MachineContentAdmissionPolicy Open(MachineAssetAdmission assetAdmission = MachineAssetAdmission.Deny) =>
        new(MachineContentAdmissionMode.Open, assetAdmission: assetAdmission);

    /// <summary>Gets the primary admission mode.</summary>
    public MachineContentAdmissionMode Mode { get; }

    /// <summary>Gets the independent auxiliary-asset disposition.</summary>
    public MachineAssetAdmission AssetAdmission { get; }

    /// <summary>Gets the cloned trusted authored format IDs.</summary>
    public IReadOnlySet<string> TrustedSourceFormats { get; }

    /// <summary>Gets the cloned normalized executable SHA-256 exceptions.</summary>
    public IReadOnlySet<string> ExecutableSha256Exceptions { get; }

    /// <inheritdoc />
    public MachineContentAdmissionDecision Evaluate(MachineContentAdmissionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.EngineId) || string.IsNullOrWhiteSpace(request.FieldPath)
            || request.SourceBytes.IsEmpty || request.ExecutableBytes.IsEmpty
            || !Enum.IsDefined(request.Role)
            || (request.VerifiedSourceFormat is not null && string.IsNullOrWhiteSpace(request.VerifiedSourceFormat))) {
            return Refuse(InvalidRequestCode, "Engine, field, role, format and exact non-empty byte inputs are required.");
        }

        if (request.Role == MachineFieldRole.AssetPath) {
            return AssetAdmission == MachineAssetAdmission.Allow
                ? Allow(AllowedAssetCode, $"Auxiliary asset '{request.FieldPath}' is explicitly admitted.")
                : Refuse(AssetRefusedCode, $"Auxiliary asset '{request.FieldPath}' is not admitted by this policy.");
        }
        if (request.Role != MachineFieldRole.ContentPath) {
            return Refuse(InvalidRequestCode, $"Field '{request.FieldPath}' is not a content or auxiliary asset path.");
        }

        if (m_executableSha256Exceptions.Count > 0) {
            var executableHash = Convert.ToHexString(SHA256.HashData(request.ExecutableBytes.Span));
            if (m_executableSha256Exceptions.Contains(executableHash)) {
                return Allow(AllowedHashCode, $"Executable SHA-256 {executableHash} is explicitly admitted.");
            }
            if (Mode == MachineContentAdmissionMode.HashOnly) {
                return Refuse(HashRefusedCode, $"Executable SHA-256 {executableHash} is not admitted.");
            }
        }

        return Mode switch {
            MachineContentAdmissionMode.Open => request.VerifiedSourceFormat is null
                ? Allow(AllowedOpenCode, $"Content '{request.FieldPath}' is admitted by the open policy.")
                : Allow(AllowedOpenCode, $"Content '{request.FieldPath}' is admitted by the open policy with format '{request.VerifiedSourceFormat}'."),
            MachineContentAdmissionMode.AuthoredFormatsOnly => EvaluateAuthoredFormat(request),
            MachineContentAdmissionMode.HashOnly => Refuse(HashRefusedCode, "Executable SHA-256 is not admitted."),
            _ => Refuse(InvalidRequestCode, "The policy contains an invalid admission mode."),
        };
    }

    private MachineContentAdmissionDecision EvaluateAuthoredFormat(MachineContentAdmissionRequest request) {
        if (request.VerifiedSourceFormat is null) {
            return Refuse(NativeRefusedCode, $"Content '{request.FieldPath}' has no verified authored source format.");
        }
        return m_trustedSourceFormats.Contains(request.VerifiedSourceFormat)
            ? Allow(AllowedFormatCode, $"Source format '{request.VerifiedSourceFormat}' is trusted for '{request.FieldPath}'.")
            : Refuse(UntrustedFormatCode, $"Source format '{request.VerifiedSourceFormat}' is not trusted for '{request.FieldPath}'.");
    }

    private static FrozenSet<string> CloneFormats(IEnumerable<string>? formats) {
        if (formats is null) {
            return Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
        }
        var values = new List<string>();
        foreach (var format in formats) {
            if (string.IsNullOrWhiteSpace(format)) {
                throw new ArgumentException("Trusted source format IDs must be non-empty.", nameof(formats));
            }
            values.Add(format);
        }
        return values.ToFrozenSet(StringComparer.Ordinal);
    }

    private static FrozenSet<string> CloneHashes(IEnumerable<string>? hashes) {
        if (hashes is null) {
            return Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
        }
        var values = new List<string>();
        foreach (var hash in hashes) {
            if (hash is null || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character))) {
                throw new ArgumentException("Executable SHA-256 exceptions must be 64 hexadecimal characters.", nameof(hashes));
            }
            values.Add(hash.ToUpperInvariant());
        }
        return values.ToFrozenSet(StringComparer.Ordinal);
    }

    private static MachineContentAdmissionDecision Allow(string code, string detail) => new(true, code, detail);

    private static MachineContentAdmissionDecision Refuse(string code, string detail) => new(false, code, detail);
}
