using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Bloom.Core;

namespace Bloom.Ai;

/// Antigravity runs elevated (or otherwise hides its command lines): the discovery in AntigravityUsage
/// finds the language server's ports but not its --csrf_token, so the quota stays 401. One UAC approval
/// installs a SYSTEM scheduled task with no triggers that runs this Bloom with --antigravity-probe
/// (the same discovery, which can read every command line then) and writes the servers to a file Bloom
/// reads. The task runs only on demand; after an uninstall it stays but never runs.
internal static class AntigravityProbe
{
    public const string Arg = "--antigravity-probe";

    public enum Outcome { Done, Declined, Failed }

    /// The task's name; a dev copy gets its own (its exe path moves with every build folder).
    internal static string TaskName(bool isDev, string instance) => isDev ? $"BloomAntigravityProbe.Dev.{instance}" : "BloomAntigravityProbe";

    private static string Name => TaskName(Paths.IsDev, ShortInstance());

    private static string ShortInstance()
    {
        string id = Paths.InstanceId;
        return id.Length <= 8 ? id : id[^8..];
    }

    /// Where the elevated run leaves the discovery JSON (the same shape as the script prints).
    internal static string ResultPath(string localDir) => Path.Combine(localDir, "antigravity-servers.json");

    private static string Result => ResultPath(Paths.Local);

    /// schtasks lines, split out so the tests can pin the quoting (paths with spaces).
    internal static string QueryArgs(string task) => $"/Query /TN \"{task}\"";
    internal static string RunArgs(string task) => $"/Run /TN \"{task}\"";
    internal static string DeleteArgs(string task) => $"/Delete /TN \"{task}\" /F";
    internal static string CreateArgs(string task, string exe, string result) =>
        $"/Create /TN \"{task}\" /TR \"\\\"{exe}\\\" {Arg} \\\"{result}\\\"\" /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /F";

    internal static bool IsFresh(DateTime writeUtc, DateTime startUtc) => writeUtc >= startUtc.AddSeconds(-2);

    public static bool Installed()
    {
        try
        {
            return RunSchtasks(QueryArgs(Name)).code == 0;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Write("antigravity probe query: " + e.Message);
            return false;
        }
    }

    /// The task (re)installed with one UAC prompt; already-correct tasks are left alone.
    public static Outcome Install()
    {
        try
        {
            Directory.CreateDirectory(Paths.Local);
            if (Installed() && TaskRuns(ResultOf(TaskName(Paths.IsDev, ShortInstance())))) return Outcome.Done;
            int code = RunElevated(CreateArgs(Name, Exe(), Result));
            if (code == 1223) return Outcome.Declined; // ERROR_CANCELLED: the UAC prompt declined
            bool ok = code == 0 && Installed();
            Log.Write("antigravity probe install: " + (ok ? "done" : $"exit {code}"));
            return ok ? Outcome.Done : Outcome.Failed;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Write("antigravity probe install: " + e.Message);
            return Outcome.Failed;
        }
    }

    private static string Exe() => Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(Paths.AppDir, "Bloom.exe");

    private static string ResultOf(string task)
    {
        try
        {
            var (code, output) = RunSchtasks($"/Query /TN \"{task}\" /V /FO LIST");
            if (code != 0) return "";
            foreach (var line in output.Split('\n'))
            {
                const string key = "Task To Run:";
                int at = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (at >= 0) return line[(at + key.Length)..].Trim();
            }
            return "";
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }

    private static bool TaskRuns(string taskToRun) =>
        taskToRun.Contains(Arg, StringComparison.Ordinal) && taskToRun.Contains(Result, StringComparison.OrdinalIgnoreCase);

    public static void Remove()
    {
        try
        {
            if (RunElevated(DeleteArgs(Name)) == 0) Log.Write("antigravity probe removed");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Write("antigravity probe remove: " + e.Message);
        }
    }

    /// The task run on demand, then its file while fresh (the server ports and token of this moment).
    /// Null when there is no task, it didn't answer in time, or the file isn't its answer.
    public static async Task<string?> QueryAsync()
    {
        if (!Installed()) return null;
        try
        {
            DateTime start = DateTime.UtcNow;
            var (code, output) = RunSchtasks(RunArgs(Name));
            if (code != 0)
            {
                Log.Write("antigravity probe run: exit " + code + " " + FirstLine(output));
                return null;
            }
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (File.Exists(Result) && IsFresh(File.GetLastWriteTimeUtc(Result), start))
                    return await File.ReadAllTextAsync(Result).ConfigureAwait(false);
            }
            Log.Write("antigravity probe run: no fresh file");
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Write("antigravity probe run: " + e.Message);
            return null;
        }
    }

    private static string FirstLine(string output)
    {
        int at = output.IndexOf('\n');
        return (at >= 0 ? output[..at] : output).Trim();
    }

    /// The --antigravity-probe run (SYSTEM, before anything else in Main): the discovery, whose
    /// command lines are all readable from up there, straight into the file. 0 on the way out.
    public static int ProbeMain(string outfile)
    {
        try
        {
            string json = AntigravityUsage.DiscoverJson().GetAwaiter().GetResult() ?? "[]";
            File.WriteAllText(outfile, json);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static (int code, string output) RunSchtasks(string args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        proc.Start();
        string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (proc.HasExited ? proc.ExitCode : 1, output);
    }

    /// schtasks itself elevated (the one UAC prompt), waited for to the end. 1223 when declined.
    private static int RunElevated(string args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            },
        };
        try
        {
            proc.Start();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return 1223;
        }
        if (!proc.WaitForExit(120_000)) return 1;
        return proc.ExitCode;
    }
}
