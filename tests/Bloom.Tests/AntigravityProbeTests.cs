using System;
using Bloom.Ai;

namespace Bloom.Tests;

/// The elevated Antigravity probe's task names, schtasks lines and result freshness.
internal static class AntigravityProbeTests
{
    public static void TaskNames()
    {
        Check.Eq(AntigravityProbe.TaskName(false, "Bloom"), "BloomAntigravityProbe", "the installed copy's task");
        string dev = AntigravityProbe.TaskName(true, "89abcdef");
        Check.True(dev.StartsWith("BloomAntigravityProbe.Dev.", StringComparison.Ordinal)
            && dev.Length == "BloomAntigravityProbe.Dev.".Length + 8, "a dev copy's own task: " + dev);
        Check.True(AntigravityProbe.TaskName(false, "Bloom") != dev, "never the installed task");
    }

    public static void SchtasksLines()
    {
        const string task = "BloomAntigravityProbe";
        Check.Eq(AntigravityProbe.QueryArgs(task), "/Query /TN \"BloomAntigravityProbe\"", "exists check");
        Check.Eq(AntigravityProbe.RunArgs(task), "/Run /TN \"BloomAntigravityProbe\"", "on-demand run");
        Check.Eq(AntigravityProbe.DeleteArgs(task), "/Delete /TN \"BloomAntigravityProbe\" /F", "removal");
        string create = AntigravityProbe.CreateArgs(task,
            @"C:\Users\me\AppData\Local\BloomLauncher\current\Bloom.exe",
            @"C:\Users\me\AppData\Local\Bloom\antigravity-servers.json");
        Check.Eq(create, "/Create /TN \"BloomAntigravityProbe\" /TR \"\\\"C:\\Users\\me\\AppData\\Local\\BloomLauncher\\current\\Bloom.exe\\\" --antigravity-probe \\\"C:\\Users\\me\\AppData\\Local\\Bloom\\antigravity-servers.json\\\"\" /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /F",
            "install: quoted paths, no triggers, SYSTEM with the full token");
    }

    public static void ResultFreshness()
    {
        var start = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        Check.True(AntigravityProbe.IsFresh(start, start), "written for this run");
        Check.True(AntigravityProbe.IsFresh(start.AddSeconds(5), start), "written after the run started");
        Check.True(!AntigravityProbe.IsFresh(start.AddMinutes(-10), start), "an older answer is not this run's");
    }

    public static void ResultPath()
    {
        Check.Eq(AntigravityProbe.ResultPath(@"C:\x\local"), @"C:\x\local\antigravity-servers.json", "next to the other local data");
    }
}
