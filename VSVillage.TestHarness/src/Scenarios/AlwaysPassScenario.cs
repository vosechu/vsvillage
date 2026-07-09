using Vintagestory.API.Server;

namespace VsVillageTest.Scenarios;

// Falsifiability control: proves the pipeline reports PASS and the runner exits 0.
public class AlwaysPassScenario : IGoldenScenario
{
    public string Name => "always-pass";
    public string Justification => "Self-test only: proves the runner reports PASS + exits 0. Not a mod behavior.";
    public int SettleSeconds => 0;
    public void Setup(ICoreServerAPI api) { }
    public void Assert(ScenarioReport report) => report.Check("trivially true", true);
    public void Teardown() { }
}
