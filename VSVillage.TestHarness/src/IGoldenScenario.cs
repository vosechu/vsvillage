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
    // Whether this scenario joins the auto-discovered `all` suite (the one-boot comprehensive run). Default
    // TRUE, so a new scenario file is picked up automatically — no central list to remember to edit. Override
    // to false for framework stubs (the selftest AlwaysPass/AlwaysFail) and the aspirational nav scenarios
    // (a pathfinding limit is a finding, not a gate failure); those stay runnable via their own named suites.
    bool InAllSuite => true;
    void Setup(ICoreServerAPI api);
    void Assert(ScenarioReport report);
    void Teardown();
}
