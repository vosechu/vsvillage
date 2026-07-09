using System;
using System.Collections.Generic;
using System.Linq;

namespace VsVillageTest;

public class ScenarioReport
{
    public string ScenarioName { get; }
    private readonly List<(string desc, bool pass)> checks = new List<(string, bool)>();

    public ScenarioReport(string scenarioName) { ScenarioName = scenarioName; }

    public void Check(string desc, bool pass) => checks.Add((desc, pass));

    public void Fail(string desc, Exception e = null)
        => checks.Add((e == null ? desc : desc + ": " + e.Message, false));

    public bool Passed => checks.Count > 0 && checks.All(c => c.pass);

    public IReadOnlyList<(string desc, bool pass)> Checks => checks;
}
