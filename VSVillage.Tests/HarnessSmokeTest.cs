using Xunit;

namespace VsVillage.Tests;

// Proves the xUnit harness compiles, discovers, and runs. Loads no mod or game
// types — its only job is to fail if the test project itself is misconfigured.
public class Harness_When_running
{
    [Fact]
    public void It_executes_a_test()
    {
        Assert.True(true);
    }
}
