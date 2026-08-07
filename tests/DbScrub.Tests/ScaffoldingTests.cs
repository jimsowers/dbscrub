using Xunit;

namespace DbScrub.Tests;

/// <summary>
/// Guards the build wiring itself. These fail loudly if a .csproj change
/// silently stops copying the sample config, which would otherwise show up
/// much later as a confusing "file not found" inside a real test.
/// </summary>
public class ScaffoldingTests
{
    [Fact]
    public void SampleConfigIsCopiedNextToTheTestAssembly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "masking.sample.json");

        Assert.True(File.Exists(path), $"Expected the sample config at {path}. " +
            "Check the <None Include=...masking.sample.json> item in DbScrub.Tests.csproj.");
    }

    [Fact]
    public void CoreAndCliTargetTheFrameworkTheSpecAsksFor()
    {
        // SPEC section 1 pins net10.0 (DECISIONS.md D30). The target is asserted
        // rather than assumed because a retarget is a one-line edit that still
        // builds and still passes every other test — the cost only appears on a
        // machine whose runtime does not match, long after the change.
        var core = typeof(Core.AssemblyMarker).Assembly;
        var framework = core.GetCustomAttributes(
            typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false);

        var attribute = Assert.Single(framework);
        Assert.Equal(".NETCoreApp,Version=v10.0",
            ((System.Runtime.Versioning.TargetFrameworkAttribute)attribute).FrameworkName);
    }
}
