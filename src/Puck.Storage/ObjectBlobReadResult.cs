namespace Puck.Storage;

public readonly record struct ObjectBlobReadResult<T>(bool Found, T? Value);
