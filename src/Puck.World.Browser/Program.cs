// The browser-wasm host never runs Main as a program: dotnet.js loads this assembly, resolves
// Exports.BrowserExports' [JSExport] members, and the studio (or the Node harness) calls them directly. Main exists
// only because Microsoft.NET.Sdk.WebAssembly requires OutputType=Exe to carry an entry point; it does nothing.
return 0;
