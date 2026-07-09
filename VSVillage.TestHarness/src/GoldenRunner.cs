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
        suites["golden"] = new List<IGoldenScenario> { new ContainerFetchScenario() };
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
                sb.AppendLine("CHECK " + (c.pass ? "PASS" : "FAIL") + " " + suiteName + " " + r.ScenarioName + " " + c.desc);
            sb.AppendLine("SCENARIO " + (r.Passed ? "PASS" : "FAIL") + " " + suiteName + " " + r.ScenarioName);
        }
        sb.AppendLine("SUMMARY " + suiteName + " scenarios=" + total + " passed=" + passed + " failed=" + failed);

        string path = Path.Combine(GamePaths.DataPath, "golden-results.txt");
        File.WriteAllText(path, sb.ToString());
        sapi.Logger.Notification("GOLDEN SUITE COMPLETE: {0}/{1}", passed, total);
    }
}
