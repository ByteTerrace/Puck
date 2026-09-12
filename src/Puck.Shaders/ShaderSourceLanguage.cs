using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>The source language accepted by <see cref="ShaderCompiler"/>.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderSourceLanguage>))]
public enum ShaderSourceLanguage { Hlsl, Glsl, ShadertoyGlsl }