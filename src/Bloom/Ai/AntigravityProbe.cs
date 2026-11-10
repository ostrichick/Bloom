using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Bloom.Core;

namespace Bloom.Ai;

/// Antigravity runs elevated (or otherwise hides its command lines): the discovery in AntigravityUsage
/// finds the language server's ports but not its --csrf_token, so the quota stays 401. One UAC approval
/// installs a SYSTEM scheduled task that collects the usage every few minutes on its own; Bloom only
/// reads the result file and never touches the task again, so the standard user needs no task rights.
internal static class AntigravityProbe
{
    public const string Arg = "--antigravity-probe";
    public const string InstallArg = "--install-antigravity-probe";

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

    /// The collection rhythm: the task rewrites the file this often; Bloom trusts files this young.
    internal const int TriggerMinutes = 10, FreshMinutes = 15;

    /// schtasks lines, split out so the tests can pin the quoting (paths with spaces).
    internal static string DeleteArgs(string task) => $"/Delete /TN \"{task}\" /F";
    internal static string CreateArgs(string task, string exe, string result, string startHhMm) =>
        $"/Create /TN \"{task}\" /TR \"\\\"{exe}\\\" {Arg} \\\"{result}\\\"\" /SC MINUTE /MO {TriggerMinutes} /ST {startHhMm} /RU SYSTEM /RL HIGHEST /F";

    /// Today two minutes from now, for the first trigger (a past start only warns, but spare it).
    internal static string TriggerStart(DateTime now) => now.AddMinutes(2).ToString("HH:mm");

    /// The task's file while it is fresh enough to show (anything older counts as no answer).
    internal static bool TryReadFresh(out string json, out DateTime writeUtc)
    {
        json = "";
        writeUtc = DateTime.MinValue;
        try
        {
            if (!File.Exists(Result)) return false;
            writeUtc = File.GetLastWriteTimeUtc(Result);
            if (!IsFresh(writeUtc)) return false;
            json = File.ReadAllText(Result);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsFresh(DateTime writeUtc) => DateTime.UtcNow - writeUtc <= TimeSpan.FromMinutes(FreshMinutes);

    /// The --install-antigravity-probe run (elevated, so schtasks needs no UAC of its own): creates the
    /// task and leaves schtasks' own answer plus a same-context query in the log file. Everything comes
    /// in as arguments because Paths is not initialized in this mode.
    public static int InstallMain(string task, string result, string outlog)
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var (code, output) = RunSchtasks(CreateArgs(task, exe, result, TriggerStart(DateTime.Now)));
            string me;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                bool admin = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                me = identity.Name + " admin=" + admin;
            }
            catch (Exception e) { me = "unknown: " + e.Message; }
            var (qcode, qout) = RunSchtasks($"/Query /TN \"{task}\" /XML");
            File.WriteAllText(outlog, $"who={me}\nexit {code}\n{output}\nquery-exit {qcode}\n{qout}");
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// The task (re)installed with one UAC prompt (for Bloom itself, so schtasks' answer is captured
    /// and verified in the elevated run's own log). `why` says what schtasks answered when it didn't
    /// work out (shown on the result card, not just the log). Bloom itself never queries the task.
    public static Outcome Install() => Install(out _);

    public static Outcome Install(out string why)
    {
        why = "";
        try
        {
            Directory.CreateDirectory(Paths.Local);
            string task = Name, result = Result, outlog = Path.Combine(Paths.Local, "antigravity-probe-install.log");
            Log.Write("antigravity probe install: " + CreateArgs(task, Exe(), result, TriggerStart(DateTime.Now)));
            int code = RunElevated(Exe(), $"{InstallArg} \"{task}\" \"{result}\" \"{outlog}\"");
            if (code == 1223) return Outcome.Declined; // ERROR_CANCELLED: the UAC prompt declined
            if (code != 0)
            {
                why = $"elevated helper exit {code}";
                Log.Write("antigravity probe install: " + why);
                return Outcome.Failed;
            }
            string answer = "";
            for (int i = 0; i < 40 && !File.Exists(outlog); i++) Task.Delay(250).GetAwaiter().GetResult();
            if (File.Exists(outlog)) answer = File.ReadAllText(outlog);
            bool ok = TaskVerified(answer, result);
            why = ok ? "" : $"helper exit {code}, schtasks: {FirstLine(answer)}";
            Log.Write("antigravity probe install: " + (ok ? "done" : why));
            return ok ? Outcome.Done : Outcome.Failed;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            why = e.Message;
            Log.Write("antigravity probe install: " + why);
            return Outcome.Failed;
        }
    }

    private static string Exe() => Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(Paths.AppDir, "Bloom.exe");

    /// The elevated run's own answer names the task it verified itself (language-independent XML
    /// elements, so no display language is assumed).
    internal static bool TaskVerified(string outlog, string result)
    {
        string args = CommandTag(outlog, "Arguments");
        if (args.Length == 0) return false;
        string decoded = System.Net.WebUtility.HtmlDecode(args);
        return decoded.Contains(Arg, StringComparison.Ordinal) && decoded.Contains(result, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CommandTag(string xml, string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", System.Text.RegularExpressions.RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : "";
    }

    public static void Remove()
    {
        try
        {
            if (RunElevated(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), DeleteArgs(Name)) == 0) Log.Write("antigravity probe removed");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Write("antigravity probe remove: " + e.Message);
        }
    }

    private static string FirstLine(string output)
    {
        int at = output.IndexOf('\n');
        return (at >= 0 ? output[..at] : output).Trim();
    }

    /// The --antigravity-probe run (SYSTEM, before anything else in Main): the discovery as tokens
    /// and ports only (never command lines), straight into the file. 0 on the way out.
    public static int ProbeMain(string outfile)
    {
        try
        {
            File.WriteAllText(outfile, AntigravityUsage.ProbeJson(AntigravityUsage.DiscoverJson().GetAwaiter().GetResult()));
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

    /// Bloom itself elevated (the one UAC prompt), waited for to the end. 1223 when declined.
    /// `file` should be this exe for the install (its publisher shows in the prompt); the elevated run
    /// answers through its own arguments, so schtasks' output is captured instead of lost.
    private static int RunElevated(string file, string args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(file, args)
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
