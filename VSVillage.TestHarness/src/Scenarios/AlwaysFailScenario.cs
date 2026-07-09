using Vintagestory.API.Server;

namespace VsVillageTest.Scenarios;

// Falsifiability control: proves the suite CAN go red and the runner exits non-zero.
public class AlwaysFailScenario : IGoldenScenario
{
    public string Name => "always-fail";
    public string Justification => "Self-test only: proves the suite can fail + runner exits 1. Not a mod behavior.";
    public int SettleSeconds => 0;
    public void Setup(ICoreServerAPI api) { }
    public void Assert(ScenarioReport report) => report.Check("deliberately false", false);
    public void Teardown() { }
}
