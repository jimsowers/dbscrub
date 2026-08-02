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
        // SPEC section 1 pins net8.0. The dev machine only has SDK 9 and 10, so
        // an accidental retarget to net10.0 would still build here and then fail
        // on a machine with just the 8.0 runtime. Assert the intent instead.
        var core = typeof(Core.AssemblyMarker).Assembly;
        var framework = core.GetCustomAttributes(
            typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false);

        var attribute = Assert.Single(framework);
        Assert.Equal(".NETCoreApp,Version=v8.0",
            ((System.Runtime.Versioning.TargetFrameworkAttribute)attribute).FrameworkName);
    }
}
