using System;
using Bloom.Ai;

namespace Bloom.Tests;

/// The elevated Antigravity probe's task names, schtasks lines, verification and result freshness.
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
        Check.Eq(AntigravityProbe.DeleteArgs(task), "/Delete /TN \"BloomAntigravityProbe\" /F", "removal");
        string create = AntigravityProbe.CreateArgs(task,
            @"C:\Users\me\AppData\Local\BloomLauncher\current\Bloom.exe",
            @"C:\Users\me\AppData\Local\Bloom\antigravity-servers.json", "14:00");
        Check.Eq(create, "/Create /TN \"BloomAntigravityProbe\" /TR \"\\\"C:\\Users\\me\\AppData\\Local\\BloomLauncher\\current\\Bloom.exe\\\" --antigravity-probe \\\"C:\\Users\\me\\AppData\\Local\\Bloom\\antigravity-servers.json\\\"\" /SC MINUTE /MO 10 /ST 14:00 /RU SYSTEM /RL HIGHEST /F",
            "install: quoted paths, every 10 minutes, SYSTEM with the full token");
        Check.Eq(AntigravityProbe.TriggerStart(new DateTime(2026, 11, 10, 13, 58, 0)), "14:00", "the first trigger stays ahead");
    }

    public static void TaskVerified()
    {
        string result = @"C:\y\antigravity-servers.json";
        string ok = "exit 0\n<?xml version=\"1.0\"?><Task><Actions><Exec><Command>C:\\x\\Bloom.exe</Command><Arguments>--antigravity-probe &quot;C:\\y\\antigravity-servers.json&quot;</Arguments></Exec></Actions></Task>";
        Check.True(AntigravityProbe.TaskVerified(ok, result), "the elevated run's own answer counts");
        Check.True(!AntigravityProbe.TaskVerified("exit 1\nERROR: ", result), "a failed create never verifies");
        Check.True(!AntigravityProbe.TaskVerified(ok, @"C:\other\result.json"), "another task's file does not count");
    }

    public static void ResultFreshness()
    {
        Check.True(AntigravityProbe.IsFresh(DateTime.UtcNow), "just written");
        Check.True(!AntigravityProbe.IsFresh(DateTime.UtcNow.AddMinutes(-(AntigravityProbe.FreshMinutes + 1))), "older than the window");
    }

    public static void ResultPath()
    {
        Check.Eq(AntigravityProbe.ResultPath(@"C:\x\local"), @"C:\x\local\antigravity-servers.json", "next to the other local data");
    }

    public static void TaskTargetParsing()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>C:\x\Bloom.exe</Command>
                  <Arguments>--antigravity-probe &quot;C:\y\antigravity-servers.json&quot;</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        Check.Eq(AntigravityProbe.CommandTag(xml, "Command"), @"C:\x\Bloom.exe", "element, not display language");
        Check.True(AntigravityProbe.CommandTag(xml, "Arguments").Contains("&quot;"), "quotes come escaped");
        Check.Eq(AntigravityProbe.CommandTag(xml, "Missing"), "", "unknown tag: empty");
    }

    public static void ProbeJsonRoundTrip()
    {
        string discovery = """[{"cmd":"C:\\a\\language_server.exe --csrf_token t-1 --extension_server_port 1","ports":[52091,52092]}]""";
        string minimal = AntigravityUsage.ProbeJson(discovery);
        Check.True(!minimal.Contains("cmd") && !minimal.Contains("csrf_token") && !minimal.Contains("language_server"), "no command lines on disk: " + minimal);
        var servers = AntigravityUsage.ParseProbeJson(minimal);
        Check.Eq(servers.Count, 1, "one server back");
        Check.Eq((servers[0].Token, string.Join(",", servers[0].Ports)), ("t-1", "52091,52092"), "token and ports survive");
        Check.Eq(AntigravityUsage.ParseProbeJson("[]").Count, 0, "empty stays empty");
    }
}
