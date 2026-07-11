using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using VsVillageTest.Scenarios;

namespace VsVillageTest;

public class GoldenRunner : ModSystem
{
    private ICoreServerAPI sapi;
    private readonly Dictionary<string, List<IGoldenScenario>> suites = new Dictionary<string, List<IGoldenScenario>>();

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        RegisterSuites();
        var parsers = api.ChatCommands.Parsers;
        api.ChatCommands.Create("vsvillage:test")
            .WithDescription("VS Village behavioral test suites (fork-only)")
            .RequiresPrivilege(Privilege.gamemode)
            .BeginSubCommand("list")
                .WithDescription("List suites and scenario counts")
                .HandleWith(OnList)
            .EndSubCommand()
            .BeginSubCommand("run")
                .WithDescription("Run all scenarios in a suite")
                .WithArgs(parsers.Word("suite"))
                .HandleWith(OnRun)
            .EndSubCommand();
    }

    private void RegisterSuites()
    {
        suites["golden"] = new List<IGoldenScenario> { new ContainerFetchScenario(), new ShepherdFeedHaulScenario() };
        // Aspirational navigation exploration — one obstacle-course arena per obstacle. Separate suite,
        // NOT the push gate. Headless locomotion is TELEPORT-ONLY (no entity physics without a player —
        // see ShepherdObstacleNavScenario's header), so these validate A* routes + decisions, not real
        // walking. Door is excluded from the default suite: a teleport can't land in a door cell, so the
        // door course can never be crossed headless — kept re-runnable as the nav-door probe below.
        suites["nav"] = new List<IGoldenScenario>
        {
            new ShepherdObstacleNavScenario(NavObstacle.FenceGate),
            new ShepherdObstacleNavScenario(NavObstacle.Moat),
        };
        // Direct-FindPath diagnostic matrix for the door/gate pathing question (no AI, runs in seconds).
        suites["nav-probe"] = new List<IGoldenScenario> { new PathfinderProbeScenario() };
        // Single-obstacle probes (nav-door = known-fail; see ShepherdObstacleNavScenario header).
        suites["nav-door"] = new List<IGoldenScenario> { new ShepherdObstacleNavScenario(NavObstacle.Door) };
        suites["nav-gate"] = new List<IGoldenScenario> { new ShepherdObstacleNavScenario(NavObstacle.FenceGate) };
        suites["nav-moat"] = new List<IGoldenScenario> { new ShepherdObstacleNavScenario(NavObstacle.Moat) };
        suites["selftest"] = new List<IGoldenScenario>
        {
            new AlwaysPassScenario(),
            new AlwaysFailScenario()
        };
    }

    private TextCommandResult OnList(TextCommandCallingArgs args)
    {
        if (suites.Count == 0) return TextCommandResult.Success("(no suites)");
        return TextCommandResult.Success(string.Join(", ",
            suites.Select(kv => kv.Key + " (" + kv.Value.Count + ")")));
    }

    private TextCommandResult OnRun(TextCommandCallingArgs args)
    {
        // Safety gate: scenarios PERMANENTLY edit terrain (they flatten an arena at spawn) and
        // teardown does not restore it. Refuse unless a sanctioned test entry point opted in via
        // the environment. golden-suite.sh sets this; a real game launch never does,
        // so an accidentally-loaded harness mod can't blow away a real save even with gamemode.
        if (Environment.GetEnvironmentVariable("VSVILLAGE_GOLDEN_ALLOW") != "1")
            return TextCommandResult.Error(
                "Refusing to run: golden scenarios permanently edit terrain (they flatten an arena "
                + "at spawn) and are for throwaway test worlds only. Set VSVILLAGE_GOLDEN_ALLOW=1 in "
                + "the server environment to allow — scripts/golden-suite.sh does this. "
                + "This guard protects real saves from an accidentally-loaded harness mod.");

        string suiteName = (string)args[0];
        if (!suites.TryGetValue(suiteName, out var scenarios))
            return TextCommandResult.Error("unknown suite '" + suiteName + "'");
        RunNext(suiteName, scenarios, 0, new List<ScenarioReport>());
        return TextCommandResult.Success("running suite '" + suiteName + "' (" + scenarios.Count + " scenarios)...");
    }

    // Chained on the server tick: scenario i+1 starts only after i tears down.
    private void RunNext(string suiteName, List<IGoldenScenario> scenarios, int i, List<ScenarioReport> results)
    {
        if (i >= scenarios.Count) { WriteResults(suiteName, results); return; }

        IGoldenScenario s = scenarios[i];
        ScenarioReport report = new ScenarioReport(s.Name);
        results.Add(report);

        bool setupOk = true;
        try { s.Setup(sapi); }
        catch (Exception e) { report.Fail("setup threw", e); setupOk = false; }

        if (!setupOk)
        {
            SafeTeardown(s, report);
            RunNext(suiteName, scenarios, i + 1, results);
            return;
        }

        sapi.World.RegisterCallback(_ =>
        {
            try { s.Assert(report); } catch (Exception e) { report.Fail("assert threw", e); }
            SafeTeardown(s, report);
            RunNext(suiteName, scenarios, i + 1, results);
        }, s.SettleSeconds * 1000);
    }

    private void SafeTeardown(IGoldenScenario s, ScenarioReport report)
    {
        try { s.Teardown(); } catch (Exception e) { report.Fail("teardown threw", e); }
    }

    private void WriteResults(string suiteName, List<ScenarioReport> results)
    {
        int total = results.Count;
        int passed = results.Count(r => r.Passed);
        int failed = total - passed;

        var sb = new StringBuilder();
        foreach (ScenarioReport r in results)
        {
            foreach ((string desc, bool pass) c in r.Checks)
                // Flatten any newline (e.g. from an exception .Message) so one CHECK stays one line —
                // the results file is a one-record-per-line contract the runner greps.
                sb.AppendLine("CHECK " + (c.pass ? "PASS" : "FAIL") + " " + suiteName + " " + r.ScenarioName + " "
                    + c.desc.Replace('\r', ' ').Replace('\n', ' '));
            sb.AppendLine("SCENARIO " + (r.Passed ? "PASS" : "FAIL") + " " + suiteName + " " + r.ScenarioName);
        }
        sb.AppendLine("SUMMARY " + suiteName + " scenarios=" + total + " passed=" + passed + " failed=" + failed);

        string path = Path.Combine(GamePaths.DataPath, "golden-results.txt");
        File.WriteAllText(path, sb.ToString());
        sapi.Logger.Notification("GOLDEN SUITE COMPLETE: {0}/{1}", passed, total);
    }
}
