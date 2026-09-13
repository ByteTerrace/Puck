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
    /// <summary>The policy result code for a successful explicit auxiliary-asset admission.</summary>
    public const string AllowedAssetCode = "allowed-asset-path";
    /// <summary>The policy result code for a successful authored-format admission.</summary>
    public const string AllowedFormatCode = "allowed-authored-format";
    /// <summary>The policy result code for a successful executable-hash admission.</summary>
    public const string AllowedHashCode = "allowed-executable-hash";
    /// <summary>The policy result code for a successful open admission.</summary>
    public const string AllowedOpenCode = "allowed-open";
    /// <summary>The policy result code for a refused auxiliary asset path.</summary>
    public const string AssetRefusedCode = "asset-path-refused";
    /// <summary>The policy result code for an executable hash refusal.</summary>
    public const string HashRefusedCode = "executable-hash-refused";
    /// <summary>The policy result code for a malformed request.</summary>
    public const string InvalidRequestCode = "invalid-request";
    /// <summary>The policy result code for native or unverified content refusal.</summary>
    public const string NativeRefusedCode = "native-content-refused";
    /// <summary>The policy result code for an untrusted authored format.</summary>
    public const string UntrustedFormatCode = "untrusted-source-format";

    private readonly FrozenSet<string> m_executableSha256Exceptions;
    private readonly FrozenSet<string> m_trustedSourceFormats;

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
        if (!Enum.IsDefined(value: mode)) {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "Unknown content admission mode."
            );
        }
        if (!Enum.IsDefined(value: assetAdmission)) {
            throw new ArgumentOutOfRangeException(
                nameof(assetAdmission),
                assetAdmission,
                "Unknown asset admission."
            );
        }

        m_trustedSourceFormats = CloneFormats(formats: trustedSourceFormats);
        m_executableSha256Exceptions = CloneHashes(hashes: executableSha256Exceptions);
        if (
            (mode == MachineContentAdmissionMode.AuthoredFormatsOnly) &&
            (m_trustedSourceFormats.Count == 0)
        ) {
            throw new ArgumentException(
                message: "Authored-format admission requires at least one trusted source format.",
                paramName: nameof(trustedSourceFormats)
            );
        }
        if (
            (mode == MachineContentAdmissionMode.HashOnly) &&
            (m_executableSha256Exceptions.Count == 0)
        ) {
            throw new ArgumentException(
                message: "Hash-only admission requires at least one executable SHA-256 exception.",
                paramName: nameof(executableSha256Exceptions)
            );
        }
        Mode = mode;
        AssetAdmission = assetAdmission;
        TrustedSourceFormats = m_trustedSourceFormats;
        ExecutableSha256Exceptions = m_executableSha256Exceptions;
    }

    private static MachineContentAdmissionDecision Allow(string code, string detail) => new(
        Allowed: true,
        Code: code,
        Detail: detail
    );
    private static FrozenSet<string> CloneFormats(IEnumerable<string>? formats) {
        if (formats is null) {
            return Array.Empty<string>().ToFrozenSet(comparer: StringComparer.Ordinal);
        }
        var values = new List<string>();

        foreach (var format in formats) {
            if (string.IsNullOrWhiteSpace(value: format)) {
                throw new ArgumentException(
                    message: "Trusted source format IDs must be non-empty.",
                    paramName: nameof(formats)
                );
            }
            values.Add(item: format);
        }
        return values.ToFrozenSet(comparer: StringComparer.Ordinal);
    }
    private static FrozenSet<string> CloneHashes(IEnumerable<string>? hashes) {
        if (hashes is null) {
            return Array.Empty<string>().ToFrozenSet(comparer: StringComparer.Ordinal);
        }
        var values = new List<string>();

        foreach (var hash in hashes) {
            if (
                (hash is null) ||
                (hash.Length != 64) ||
                hash.Any(predicate: character => !Uri.IsHexDigit(character: character))
            ) {
                throw new ArgumentException(
                    message: "Executable SHA-256 exceptions must be 64 hexadecimal characters.",
                    paramName: nameof(hashes)
                );
            }
            values.Add(item: hash.ToUpperInvariant());
        }
        return values.ToFrozenSet(comparer: StringComparer.Ordinal);
    }
    private MachineContentAdmissionDecision EvaluateAuthoredFormat(MachineContentAdmissionRequest request) {
        if (request.VerifiedSourceFormat is null) {
            return Refuse(
                code: NativeRefusedCode,
                detail: $"Content '{request.FieldPath}' has no verified authored source format."
            );
        }
        return (m_trustedSourceFormats.Contains(item: request.VerifiedSourceFormat)
            ? Allow(
                code: AllowedFormatCode,
                detail: $"Source format '{request.VerifiedSourceFormat}' is trusted for '{request.FieldPath}'."
            )
            : Refuse(
                code: UntrustedFormatCode,
                detail: $"Source format '{request.VerifiedSourceFormat}' is not trusted for '{request.FieldPath}'."
            )
        );
    }
    private static MachineContentAdmissionDecision Refuse(string code, string detail) => new(
        Allowed: false,
        Code: code,
        Detail: detail
    );

    /// <inheritdoc />
    public MachineContentAdmissionDecision Evaluate(MachineContentAdmissionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        if (
            string.IsNullOrWhiteSpace(value: request.EngineId) ||
            string.IsNullOrWhiteSpace(value: request.FieldPath) ||
            request.SourceBytes.IsEmpty ||
            request.ExecutableBytes.IsEmpty ||
            !Enum.IsDefined(value: request.Role) ||
            ((request.VerifiedSourceFormat is not null) && string.IsNullOrWhiteSpace(value: request.VerifiedSourceFormat))
        ) {
            return Refuse(
                code: InvalidRequestCode,
                detail: "Engine, field, role, format and exact non-empty byte inputs are required."
            );
        }

        if (request.Role == MachineFieldRole.AssetPath) {
            return ((AssetAdmission == MachineAssetAdmission.Allow)
                ? Allow(
                    code: AllowedAssetCode,
                    detail: $"Auxiliary asset '{request.FieldPath}' is explicitly admitted."
                )
                : Refuse(
                    code: AssetRefusedCode,
                    detail: $"Auxiliary asset '{request.FieldPath}' is not admitted by this policy."
                )
            );
        }
        if (request.Role != MachineFieldRole.ContentPath) {
            return Refuse(
                code: InvalidRequestCode,
                detail: $"Field '{request.FieldPath}' is not a content or auxiliary asset path."
            );
        }

        if (m_executableSha256Exceptions.Count > 0) {
            var executableHash = Convert.ToHexString(inArray: SHA256.HashData(source: request.ExecutableBytes.Span));

            if (m_executableSha256Exceptions.Contains(item: executableHash)) {
                return Allow(
                    code: AllowedHashCode,
                    detail: $"Executable SHA-256 {executableHash} is explicitly admitted."
                );
            }
            if (Mode == MachineContentAdmissionMode.HashOnly) {
                return Refuse(
                    code: HashRefusedCode,
                    detail: $"Executable SHA-256 {executableHash} is not admitted."
                );
            }
        }

        return Mode switch {
            MachineContentAdmissionMode.Open => ((request.VerifiedSourceFormat is null)
            ? Allow(
                code: AllowedOpenCode,
                detail: $"Content '{request.FieldPath}' is admitted by the open policy."
            )
            : Allow(
                code: AllowedOpenCode,
                detail: $"Content '{request.FieldPath}' is admitted by the open policy with format '{request.VerifiedSourceFormat}'."
            )),
            MachineContentAdmissionMode.AuthoredFormatsOnly => EvaluateAuthoredFormat(request: request),
            MachineContentAdmissionMode.HashOnly => Refuse(
            code: HashRefusedCode,
            detail: "Executable SHA-256 is not admitted."
        ),
            _ => Refuse(
            code: InvalidRequestCode,
            detail: "The policy contains an invalid admission mode."
        ),
        };
    }
    /// <summary>Creates an open policy with explicit auxiliary-asset disposition.</summary>
    public static MachineContentAdmissionPolicy Open(MachineAssetAdmission assetAdmission = MachineAssetAdmission.Deny) =>
        new(
            MachineContentAdmissionMode.Open,
            assetAdmission: assetAdmission
        );

    /// <summary>Gets the independent auxiliary-asset disposition.</summary>
    public MachineAssetAdmission AssetAdmission { get; }
    /// <summary>Gets the cloned normalized executable SHA-256 exceptions.</summary>
    public IReadOnlySet<string> ExecutableSha256Exceptions { get; }
    /// <summary>Gets the primary admission mode.</summary>
    public MachineContentAdmissionMode Mode { get; }
    /// <summary>Gets the cloned trusted authored format IDs.</summary>
    public IReadOnlySet<string> TrustedSourceFormats { get; }
}
