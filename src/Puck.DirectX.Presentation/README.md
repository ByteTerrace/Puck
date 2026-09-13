# Puck.DirectX.Presentation

Puck.DirectX.Presentation connects the Direct3D 12 backend to Puck's rendering
contracts. It registers the DirectX surface presenter and compute services,
records draw and command-list work, and composes rendered surfaces for a host.
The project depends on `Puck.DirectX` and `Puck.Hosting`; it owns presentation
integration, while device and COM bindings remain in the DirectX package.

## Usage

Register the services from the composition root after selecting a Windows
surface and Direct3D 12 device. The presenter consumes the neutral surface and
shader contracts; it does not compile shaders or choose a native window.

## Verification

Build the project with `dotnet build src/Puck.DirectX.Presentation -c Release`.
Runtime GPU checks belong to the rendering parity workflow in the development
guide and require a supported Windows device.

## Documentation

📚 [Rendering](../../docs/rendering/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
