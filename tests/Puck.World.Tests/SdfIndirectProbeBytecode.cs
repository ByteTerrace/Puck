namespace Puck.World.Tests;

internal static class SdfIndirectProbeBytecode {
    public static long CeilingOf(string kernel) => kernel switch {
        "sdf-indirect-trace-proof.comp" => ((8 * 1024) * 1024),
        "sdf-indirect-gather.comp" => ((3 * 1024) * 1024),
        "sdf-indirect-debug-proof.comp" => (64 * 1024),

        "sdf-indirect-sky-proof.comp" => (64 * 1024),

        "sdf-indirect-receiver-proof.comp" => (64 * 1024),
        "sdf-indirect-bricks-proof.comp" => (64 * 1024),
        "sdf-indirect-proof-ownership.comp" => (64 * 1024),
        "sdf-indirect-proof-trace-owner.comp" => (64 * 1024),
        "sdf-indirect-admission-proof.comp" => (64 * 1024),

        "sdf-indirect-light-proof.comp" => ((3 * 1024) * 1024),
        "sdf-indirect-participation.comp" => ((3 * 1024) * 1024),
        "sdf-indirect-near-proof.comp" => ((8 * 1024) * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(kernel), kernel, "Not an indirect probe kernel."),
    };
    public static string PathOf(string kernel, string extension) => Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets/Shaders", path3: (kernel + extension));
    public static byte[] Read(string kernel, string extension) {
        var path = PathOf(extension: extension, kernel: kernel);
        var ceiling = CeilingOf(kernel: kernel);
        var length = new FileInfo(fileName: path).Length;

        if (length > ceiling) {
            throw new InvalidDataException(message: $"{kernel}{extension} has {length} bytes, past its {ceiling}-byte interpreter expansion ceiling.");
        }
        return File.ReadAllBytes(path: path);
    }
}
