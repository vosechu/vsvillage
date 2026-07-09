using Vintagestory.API.Server;

namespace VsVillageTest;

public interface IGoldenScenario
{
    string Name { get; }
    // Required. One line: why a unit test can't cover this, what 3am-page-worthy behavior it
    // protects, and why it's durable. A behavioral scenario without this does not belong here.
    string Justification { get; }
    int SettleSeconds { get; }
    void Setup(ICoreServerAPI api);
    void Assert(ScenarioReport report);
    void Teardown();
}
