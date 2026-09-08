using System.Net;
using System.Text.Json;

using Puck.Assets.Documents;
using Puck.Launcher.Release;

namespace Puck.Cli.Official;

// The `puck official serve` verb: a minimal static HTTP server over a puck.official.v1 tree, using
// System.Net.HttpListener (already in the BCL — no new package) rather than a hosting framework, since this is a
// throwaway local dev loop, not a production endpoint. Content-Type comes from the manifest's own per-object
// contentType where a manifest names the object (an object under objects/sha256/... carries no extension of its
// own to sniff); Cache-Control is immutable for an object, max-age=60 for a manifest.json. CORS is permissive
// (Access-Control-Allow-Origin: *) so the portal's Vite dev server (http://localhost:61101) can fetch across
// origins.
internal static class OfficialServeCommand {
    private const int DefaultPort = 61102;
    private const string HelpText =
        """
        puck official serve — a minimal static HTTP server over a puck.official.v1 tree

        Usage: puck official serve --tree <dir> [--port 61102]

        Required:
          --tree <dir>   the official tree's root

        Options:
          --port <n>     the port to listen on (default 61102)
          -h, --help     this text

        Serves every file under <tree> by its path (a channel or builds manifest.json, and every object under
        objects/sha256/...). Content-Type comes from the enclosing manifest's own per-object contentType when one
        names the object; Cache-Control is immutable for an object, max-age=60 for a manifest.json. CORS is
        permissive (Access-Control-Allow-Origin: *). Prints the base URL and blocks until Ctrl+C.
        """;

    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "h").Flag(name: "help").Value(name: "tree").Value(name: "port");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"official serve: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        var tree = scanner.Get(name: "tree");

        if (tree is null) {
            Console.Error.WriteLine(value: "official serve: missing required argument(s): --tree.");

            return 2;
        }

        var root = Path.GetFullPath(path: tree);

        if (!Directory.Exists(path: root)) {
            Console.Error.WriteLine(value: $"official serve: tree directory '{root}' does not exist.");

            return 2;
        }

        var port = DefaultPort;

        if (scanner.Has(name: "port") && !scanner.TryGetInt(name: "port", value: out port)) {
            Console.Error.WriteLine(value: $"official serve: --port '{scanner.Get(name: "port")}' is not an integer.");

            return 2;
        }

        var contentTypesByPath = LoadContentTypes(root: root);
        var prefix = $"http://localhost:{port}/";

        using var listener = new HttpListener();

        listener.Prefixes.Add(uriPrefix: prefix);

        try {
            listener.Start();
        } catch (HttpListenerException exception) {
            Console.Error.WriteLine(value: $"official serve: cannot listen on {prefix}: {exception.Message}");

            return 2;
        }

        Console.Out.WriteLine(value: $"official serve: serving {root} at {prefix} (Ctrl+C to stop).");

        while (listener.IsListening) {
            HttpListenerContext context;

            try {
                context = listener.GetContext();
            } catch (HttpListenerException) {
                break;
            } catch (ObjectDisposedException) {
                break;
            }

            HandleRequest(context: context, contentTypesByPath: contentTypesByPath, root: root);
        }

        return 0;
    }
    private static void HandleRequest(HttpListenerContext context, IReadOnlyDictionary<string, string> contentTypesByPath, string root) {
        var response = context.Response;

        response.AddHeader(name: "Access-Control-Allow-Origin", value: "*");
        response.AddHeader(name: "Access-Control-Allow-Methods", value: "GET, HEAD, OPTIONS");

        try {
            if (string.Equals(a: context.Request.HttpMethod, b: "OPTIONS", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                response.StatusCode = ((int)HttpStatusCode.NoContent);

                return;
            }

            var relative = Uri.UnescapeDataString(stringToUnescape: context.Request.Url!.AbsolutePath.TrimStart(trimChar: '/'));

            if (relative.Length == 0 || relative.Contains(value: "..", comparisonType: StringComparison.Ordinal)) {
                response.StatusCode = ((int)HttpStatusCode.BadRequest);

                return;
            }

            var fullPath = Path.GetFullPath(path: Path.Combine(path1: root, path2: relative));

            if (!fullPath.StartsWith(value: root, comparisonType: StringComparison.Ordinal) || !File.Exists(path: fullPath)) {
                response.StatusCode = ((int)HttpStatusCode.NotFound);

                return;
            }

            var bytes = File.ReadAllBytes(path: fullPath);
            var isManifest = string.Equals(a: Path.GetFileName(path: fullPath), b: "manifest.json", comparisonType: StringComparison.Ordinal);

            response.ContentType = (contentTypesByPath.TryGetValue(key: relative.Replace(oldChar: '\\', newChar: '/'), value: out var contentType)
                ? contentType
                : (isManifest ? "application/json" : "application/octet-stream"));
            response.AddHeader(name: "Cache-Control", value: (isManifest ? "max-age=60" : "public, max-age=31536000, immutable"));
            response.ContentLength64 = bytes.LongLength;

            if (!string.Equals(a: context.Request.HttpMethod, b: "HEAD", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                response.OutputStream.Write(buffer: bytes, offset: 0, count: bytes.Length);
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            response.StatusCode = ((int)HttpStatusCode.InternalServerError);
        } finally {
            response.OutputStream.Close();
        }
    }
    // Every object's Content-Type is decided by the manifest that names it, never sniffed from its extensionless
    // objects/sha256/... path — collected once at startup from every channel manifest and every builds/*/manifest
    // present under the tree.
    private static Dictionary<string, string> LoadContentTypes(string root) {
        var map = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var manifestPath in Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "manifest.json")) {
            OfficialManifest? manifest;

            try {
                manifest = JsonSerializer.Deserialize<OfficialManifest>(utf8Json: File.ReadAllBytes(path: manifestPath), options: DocumentJsonOptions.Shared);
            } catch (JsonException) {
                continue;
            }

            if (manifest is null) {
                continue;
            }

            map[manifest.WorldSchemaBundle.Path.Replace(oldChar: '\\', newChar: '/')] = manifest.WorldSchemaBundle.ContentType;

            foreach (var file in manifest.Engine.Files) {
                map[file.Path.Replace(oldChar: '\\', newChar: '/')] = file.ContentType;
            }

            foreach (var entry in manifest.Documents) {
                map[entry.Path.Replace(oldChar: '\\', newChar: '/')] = entry.ContentType;
            }

            foreach (var entry in manifest.Composed) {
                map[entry.Path.Replace(oldChar: '\\', newChar: '/')] = entry.ContentType;
            }

            foreach (var entry in manifest.Assets) {
                map[entry.Path.Replace(oldChar: '\\', newChar: '/')] = entry.ContentType;
            }
        }

        return map;
    }
}
