using OnStepX.Core;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// Check that the phase 0 scaffolding is in place. Replaced by the real
/// protocol tests in phase 1.
/// </summary>
public class ScaffoldTests
{
    [Fact]
    public void CoreAssemblyIsReferenced()
    {
        Assert.Equal("OnStepX.Core", CoreInfo.Name);
    }
}
