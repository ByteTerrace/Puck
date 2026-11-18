using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Confinement;

namespace Puck.World;

/// <summary>Installs the executable form of an authored draw declaration. The authority's private seed is
/// an input to the simulation, held outside world packages and compiled products.</summary>
public sealed class WorldPrivateDrawKeys {
    private readonly Lazy<byte[]> m_seed;

    private WorldPrivateDrawKeys(Func<byte[]> seed) => m_seed = new(seed);

    /// <summary>The native authority's persisted credential. Reading it happens before simulation.</summary>
    public static WorldPrivateDrawKeys Authority { get; } = new(() =>
        LoadSeed(Path.Join(PuckUserDirectory.Resolve("authority", requireUserProfile: true), "private-draw.key")));

    /// <summary>Reproducible sample values for authoring and cost previews, isolated from every authority.</summary>
    public static WorldPrivateDrawKeys Preview { get; } = new(static () => new byte[32]);

    private static byte[] LoadSeed(string path) {
        if (!File.Exists(path)) {
            using var pending = AtomicFile.StageAllBytes(path, RandomNumberGenerator.GetBytes(32),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try { pending.Commit(overwrite: false); }
            catch (IOException) when (File.Exists(path)) { /* Another authority process installed its seed first. */ }
        }
        var seed = File.ReadAllBytes(path);
        if (seed.Length != 32 || seed.AsSpan().IndexOfAnyExcept((byte)0) < 0) {
            throw new InvalidDataException("The authority's private draw seed must be a nonzero 32-byte key.");
        }
        return seed;
    }

    /// <summary>Reads the credential or caches its failure before a simulation accepts row declarations.</summary>
    public void PrepareAuthority() => TryGetSeed(out _, out _);

    private bool TryGetSeed(out byte[] seed, out string reason) {
        try {
            seed = m_seed.Value;
            reason = string.Empty;
            return true;
        } catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException) {
            seed = [];
            reason = $"The authority's private draw credential is unavailable: {failure.Message}";
            return false;
        }
    }

    /// <summary>Refuses a submitted key and installs the private draw declared by a source or row edit.</summary>
    /// <param name="definition">The world that owns the site.</param>
    /// <param name="instance">The authority's instance identity.</param>
    /// <param name="submitted">The submitted declaration.</param>
    /// <param name="previous">The installed row when editing.</param>
    /// <param name="installed">The row the authority can execute.</param>
    /// <param name="reason">The refusal when the declaration supplied a key.</param>
    public bool TryInstall(WorldDefinition definition, string instance, WorldStateRow submitted, WorldStateRow? previous,
        out WorldStateRow installed, out string reason) {
        installed = submitted;
        reason = string.Empty;
        if (submitted.Draw is not { } draw) { return true; }
        if (draw.Secret.HasValue) {
            reason = $"state row '{submitted.Name}': draw.secret is authority-owned; declare privateStream instead.";
            return false;
        }
        if (!draw.PrivateStream) { return true; }
        // Canonical bytes compare the contents of an inline generator's lists after a JSON view round trip.
        var declaration = JsonSerializer.SerializeToUtf8Bytes(draw, WorldJsonContext.Authored.Draw);
        if (previous?.Draw is { Secret: not null } old &&
            declaration.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(old, WorldJsonContext.Authored.Draw))) {
            installed = submitted with { Draw = old };
            return true;
        }
        if (!TryGetSeed(out var seed, out reason)) { return false; }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartArray();
            writer.WriteStringValue(definition.Generation?.DrawOwner?.ToString("D"));
            writer.WriteStringValue(definition.Host.AuthorityNamespace);
            writer.WriteStringValue(definition.DocumentId);
            writer.WriteStringValue(instance);
            writer.WriteNumberValue(definition.Generation?.WorldSeed ?? 0UL);
            writer.WriteStringValue(WorldDrawSites.StateRow(submitted.Name));
            writer.WriteRawValue(declaration);
            writer.WriteEndArray();
        }
        var digest = HMACSHA256.HashData(seed, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        var key = new ClosedBitset256(BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(8)), BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(24)));
        installed = submitted with { Draw = draw with { Secret = key } };
        return true;
    }
}
