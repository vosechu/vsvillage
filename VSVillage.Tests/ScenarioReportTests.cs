using VsVillageTest;
using Xunit;

namespace VsVillage.Tests;

public class ScenarioReport_When_evaluating_pass_state
{
    [Fact]
    public void It_is_not_passed_with_zero_checks()
    {
        var report = new ScenarioReport("empty");
        Assert.False(report.Passed);
    }

    [Fact]
    public void It_is_passed_when_all_checks_pass()
    {
        var report = new ScenarioReport("s");
        report.Check("a", true);
        report.Check("b", true);
        Assert.True(report.Passed);
    }

    [Fact]
    public void It_is_not_passed_when_any_check_fails()
    {
        var report = new ScenarioReport("s");
        report.Check("a", true);
        report.Check("b", false);
        Assert.False(report.Passed);
    }

    [Fact]
    public void It_records_a_failing_check_from_Fail()
    {
        var report = new ScenarioReport("s");
        report.Fail("boom");
        Assert.False(report.Passed);
        Assert.Single(report.Checks);
        Assert.False(report.Checks[0].pass);
    }
}
