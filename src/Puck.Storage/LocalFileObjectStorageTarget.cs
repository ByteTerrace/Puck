namespace Puck.Storage;

public sealed record LocalFileObjectStorageTarget(string BasePath) : ObjectStorageTarget;
