namespace Puck.Azure.Functions.Services;

/// <summary>
/// Configuration surface for the service's warm issuing key and the two ceremony artifacts it
/// consumes. The ROOT key itself never appears here or anywhere in the service — only its
/// public key (whose SHA-256 fingerprint IS the domain) and the root-signed binding that
/// vouches for the issuing key. Both are emitted by the offline ceremony
/// (<c>ceremony/BindingCeremony</c>) and consumed here as configuration; neither is ever
/// generated in-process.
///
/// PROTOTYPE SHORTCUT, recorded as a TODO: <see cref="IssuingPrivateKeyPassword"/> is a plain
/// configuration value standing in for real key custody. <c>DefaultKeyPairService</c>'s subject
/// keys are sealed under a per-key random password whose WRAPPED password (never the password
/// itself) travels beside the ciphertext, protected by ASP.NET Core Data Protection — the
/// issuing key deserves the same treatment (or Key Vault-backed sealing) before this leaves
/// prototype stage.
/// </summary>
public sealed class IssuingKeyOptions
{
    /// <summary>SPKI PEM of the cold root's public key. Its SHA-256 fingerprint is the domain every envelope in this trust tree names.</summary>
    public string? RootPublicKeyPem { get; set; }
    /// <summary>The root-signed key binding for the issuing key: base64 of the CBOR wire bytes, as emitted by the ceremony.</summary>
    public string? IssuingBindingBase64 { get; set; }
    /// <summary>PKCS8-encrypted PEM of the issuing key's private half, as emitted by the ceremony.</summary>
    public string? IssuingPrivateKeyPem { get; set; }
    /// <summary>Passphrase for <see cref="IssuingPrivateKeyPem"/>. See the prototype-shortcut note on this class.</summary>
    public string? IssuingPrivateKeyPassword { get; set; }
    /// <summary>The validity window authored onto every subject key binding this service mints or re-attests.</summary>
    public TimeSpan SubjectBindingValidity { get; set; } = TimeSpan.FromDays(value: 30);
}

