using Vintagestory.API.Server;

namespace VsVillageTest;

public interface IGoldenScenario
{
    string Name { get; }
    // Required. One line: why a unit test can't cover this, what 3am-page-worthy behavior it
    // protects, and why it's durable. A behavioral scenario without this does not belong here.
    string Justification { get; }
    int SettleSeconds { get; }
    // Early exit: when true, the runner asserts immediately instead of waiting out SettleSeconds
    // (which then only bounds the worst case). Default false = always run the full window. NOTE for
    // scenarios with NEGATIVE checks ("X never happened"): returning true as soon as the positives
    // pass shortens the window in which a violation could be observed — gate on a minimum elapsed
    // time too, or leave the default.
    bool IsSettled => false;
    void Setup(ICoreServerAPI api);
    void Assert(ScenarioReport report);
    void Teardown();
}
