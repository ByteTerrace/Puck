using System.Security.Cryptography;

namespace Puck.AdvancedGamingBrick;

/// <summary>How a loaded BIOS image classifies against the retail image needed for cycle-accurate work.</summary>
public enum AgbBiosKind {
    /// <summary>The retail Advanced GamingBrick BIOS, verified by content hash — the only image trustworthy for
    /// cycle-parity / co-simulation work.</summary>
    RealVerified,

    /// <summary>The zeroed replacement stub (all bytes zero) — valid under direct boot, useless for cycle parity.</summary>
    ReplacementStub,

    /// <summary>A non-zero image that is not the verified retail BIOS (an open-source replacement, a wrong dump, or a
    /// corrupted file) — usable for direct boot but NOT for cycle parity.</summary>
    Unknown,
}

/// <summary>The pre-flight identity of a loaded BIOS image: its content hash, classification, and a human-readable
/// description. This is a host-side identification aid (computed at machine assembly, never on the emulated path), so
/// a SHA-1 content hash is used for a stable, recognisable fingerprint.</summary>
public readonly struct AgbBiosIdentity {
    /// <summary>The BIOS classification.</summary>
    public AgbBiosKind Kind { get; init; }

    /// <summary>The lowercase hexadecimal SHA-1 of the image (40 chars), or empty for a wrong-sized image.</summary>
    public string Sha1 { get; init; }

    /// <summary>A short human-readable description of the image (kind + hash prefix).</summary>
    public string Description { get; init; }

    /// <summary>Whether this image is trustworthy for cycle-parity / co-simulation work (only the verified retail
    /// BIOS is). Parity/co-sim diagnostics warn — the documented "phantom cycle drift" trap — when this is false.</summary>
    public bool IsCycleParityTrustworthy => (Kind == AgbBiosKind.RealVerified);

    /// <inheritdoc/>
    public override string ToString() => Description;
}

/// <summary>Identifies a loaded BIOS image by content hash so callers can refuse or warn on cycle-parity work run
/// against a non-retail image. A wrong BIOS once burned a whole session chasing phantom "cycle drift"; this makes
/// that failure mode loud and cheap to detect at machine assembly.</summary>
public static class AgbBiosProfile {
    // The retail Advanced GamingBrick BIOS (16 KiB) SHA-1. Public, checkable, and the only image the cycle-parity /
    // co-simulation tooling should trust. (Citations for external reference dumps live in docs/ACKNOWLEDGMENTS.md.)
    private const string RetailBiosSha1 = "300c20df6731a33952ded8c436f7f186d25d3492";

    /// <summary>Classifies a BIOS image by its content hash.</summary>
    /// <param name="image">The BIOS bytes (expected to be <see cref="ReplacementBios.ImageSize"/> long).</param>
    /// <returns>The image's identity.</returns>
    public static AgbBiosIdentity Identify(ReadOnlySpan<byte> image) {
        if (image.Length != ReplacementBios.ImageSize) {
            return new AgbBiosIdentity {
                Kind = AgbBiosKind.Unknown,
                Sha1 = string.Empty,
                Description = $"unknown BIOS (wrong size: {image.Length} bytes, expected {ReplacementBios.ImageSize})",
            };
        }

        var sha1 = Convert.ToHexStringLower(inArray: SHA1.HashData(source: image));

        if (string.Equals(a: sha1, b: RetailBiosSha1, comparisonType: StringComparison.Ordinal)) {
            return new AgbBiosIdentity {
                Kind = AgbBiosKind.RealVerified,
                Sha1 = sha1,
                Description = "real retail BIOS (verified)",
            };
        }

        if (image.IndexOfAnyExcept(value: (byte)0) < 0) {
            return new AgbBiosIdentity {
                Kind = AgbBiosKind.ReplacementStub,
                Sha1 = sha1,
                Description = "replacement (zeroed stub)",
            };
        }

        return new AgbBiosIdentity {
            Kind = AgbBiosKind.Unknown,
            Sha1 = sha1,
            Description = $"replacement/unknown BIOS (sha1 {sha1[..12]}…)",
        };
    }
}
