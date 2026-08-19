using OpenWSR.Render.Radar;

namespace OpenWSR.App.Tests;

/// <summary>
/// Shaders are compiled at run time on the render thread, where a syntax error surfaces as
/// a black window and an exception nobody sees. Compiling them here turns that into a
/// failing test, which is where a broken shader belongs.
/// </summary>
public sealed class ShaderCompileTests
{
    [Fact]
    public void TheVolumeShadersCompile() => VolumeRenderer.ValidateShaders();
}
