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
        // The push gate: the two inventory behaviours merged into ONE settle window (fetch + haul run
        // concurrently in disjoint villages), so the gate costs ~max(fetch, haul) instead of fetch + haul serial.
        suites["golden"] = new List<IGoldenScenario> { new HaulGateScenario() };
        // Standalone single-behaviour suites — run these to LOCALISE a red gate (which behaviour broke).
        suites["container"] = new List<IGoldenScenario> { new ContainerFetchScenario() };
        suites["feedhaul"] = new List<IGoldenScenario> { new ShepherdFeedHaulScenario() };
        // Navigation exploration — one obstacle-course arena per obstacle, with real locomotion via
        // HeadlessPhysicsDriver. Separate suite, NOT the push gate: it exists to characterise villager
        // navigation (and catch regressions in it) without coupling the haul gate to pathfinding depth.
        suites["nav"] = new List<IGoldenScenario>
        {
            new ShepherdObstacleNavScenario(NavObstacle.Door),
            new ShepherdObstacleNavScenario(NavObstacle.FenceGate),
            new ShepherdObstacleNavScenario(NavObstacle.Moat),
        };
        // Direct-FindPath diagnostic matrix (no AI, runs in seconds): "does A* accept this block?"
        suites["nav-probe"] = new List<IGoldenScenario> { new PathfinderProbeScenario() };
        // Single-obstacle suites for fast iteration on one obstacle.
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

        // Poll each second: end the scenario as soon as it reports settled (early exit), with
        // SettleSeconds as the hard upper bound. Fixed windows made suite time ~2x the actual
        // behavior time; IsSettled trims exactly that padding.
        long elapsedMs = 0;
        long pollId = 0;
        pollId = sapi.Event.RegisterGameTickListener(_ =>
        {
            elapsedMs += 1000;
            bool settled = false;
            try { settled = s.IsSettled; } catch (Exception e) { report.Fail("IsSettled threw", e); settled = true; }
            if (!settled && elapsedMs < s.SettleSeconds * 1000L) return;
            sapi.Event.UnregisterGameTickListener(pollId);
            try { s.Assert(report); } catch (Exception e) { report.Fail("assert threw", e); }
            SafeTeardown(s, report);
            RunNext(suiteName, scenarios, i + 1, results);
        }, 1000);
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
