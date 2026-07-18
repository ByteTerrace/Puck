namespace Puck.Storage;

/// <summary>A typed blob read: whether it was found, the deserialized value, and the opaque version token the clobber
/// guard matches on (§2.5.2).</summary>
/// <typeparam name="T">The deserialized document type.</typeparam>
/// <param name="Found">Whether the blob existed.</param>
/// <param name="Value">The deserialized value, or <see langword="default"/> when absent.</param>
/// <param name="VersionToken">The blob's opaque version token, or <see langword="null"/> when absent/unsupplied.</param>
public readonly record struct ObjectBlobReadResult<T>(bool Found, T? Value, string? VersionToken);
