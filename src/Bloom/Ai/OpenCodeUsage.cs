using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bloom.Core;

namespace Bloom.Ai;

/// OpenCode Go's plan usage from OpenCode's own Console sign-in: the account and session token OpenCode keeps in its
/// local database, then one HTTPS request to the endpoint its Console billing panel reads. Read-only: nothing is
/// refreshed or saved here, and the token never leaves this process except to opencode.ai.
/// Not an official API, so it may change (the card then keeps its last numbers until OpenCode's own sign-in is read
/// again).
internal static class OpenCodeUsage
{
    public const string Id = "opencode";
    private const string StatusUrl = "https://opencode.ai/console/api/go/status";
    private const string LoginQuery =
        "SELECT a.access_token, a.email, s.active_org_id FROM account_state s " +
        "JOIN account a ON a.id = s.active_account_id WHERE a.access_token IS NOT NULL LIMIT 1";

    private static readonly HttpClient Http = MakeClient();

    internal sealed record Login(string Token, string? Email, string Org);

    /// The installed OpenCode CLI, asked only for its database path and login row. Null when OpenCode is not installed.
    public static bool Available => FindCli() is not null;

    public static string? FindCli()
    {
        foreach (string candidate in new[]
        {
            Path.Combine(Paths.AppData, @"npm\node_modules\opencode-ai\bin\opencode.exe"),
            Path.Combine(Paths.AppData, @"npm\node_modules\opencode-ai\node_modules\opencode-windows-x64\bin\opencode.exe"),
            Path.Combine(Paths.AppData, @"npm\node_modules\opencode-ai\node_modules\opencode-windows-x64-baseline\bin\opencode.exe"),
            Path.Combine(Paths.Home, @".local\bin\opencode.exe"),
        })
            if (File.Exists(candidate)) return candidate;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                if (dir.Length == 0) continue;
                string p = Path.Combine(dir.Trim(), "opencode.exe");
                if (File.Exists(p)) return p;
            }
            catch
            {
                /* a bad PATH entry */
            }
        }
        return null;
    }

    /// One look; null when OpenCode is not signed in, its token ran out, or the server said no (the reason goes to the log).
    public static async Task<UsageAccount?> Ask(string cli)
    {
        try
        {
            var psi = OneShot.Psi(cli);
            foreach (string a in new[] { "db", LoginQuery, "--format", "json" })
                psi.ArgumentList.Add(a);
            var (code, output, _) = await OneShot.RunCli(psi, "", CancellationToken.None);
            if (code != 0 || ParseLogin(output) is not { } login)
            {
                Log.Write($"ai usage: opencode could not read its login (exit {code})");
                return null;
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, StatusUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.Token);
            req.Headers.TryAddWithoutValidation("x-org-id", login.Org);
            using var res = await Http.SendAsync(req);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                Log.Write("ai usage: opencode sign-in has expired (run OpenCode to refresh it)");
                return null;
            }
            if (!res.IsSuccessStatusCode)
            {
                Log.Write($"ai usage: opencode answered {(int)res.StatusCode}");
                return null;
            }
            return ParseStatus(await res.Content.ReadAsStringAsync(), login.Email);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("ai usage: opencode — " + e.GetType().Name + ": " + e.Message);
            return null;
        }
    }

    /// OpenCode's local database answer: the active account's session token, its e-mail and workspace id.
    internal static Login? ParseLogin(string json)
    {
        try
        {
            var rows = JsonNode.Parse(json) as JsonArray;
            if (rows is not { Count: > 0 } || rows[0] is not JsonObject row) return null;
            string? token = Str(row, "access_token"), org = Str(row, "active_org_id");
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(org)) return null;
            return new Login(token, Str(row, "email"), org);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// The Console answer → the card: product ("go" or "go-plus") and access.meters.fiveHour / week / month, each
    /// { limitMicroCents, usedMicroCents, resetsAt }. Money is US cents × 10,000 (1e8 per dollar).
    internal static UsageAccount? ParseStatus(string json, string? email = null, DateTime? at = null)
    {
        var now = at ?? DateTime.Now;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (root?["access"] is not JsonObject access || access["meters"] is not JsonObject meters) return null;
        var limits = new List<UsageLimit>(3);
        if (Meter(meters, "fiveHour") is { } five)
            limits.Add(new UsageLimit(Tr("5시간", "5h"), five.Percent, five.Resets, UsageLimit.SessionMins));
        if (Meter(meters, "week") is { } week)
            limits.Add(new UsageLimit(Tr("주간", "Weekly"), week.Percent, week.Resets, UsageLimit.WeekMins));
        if (Meter(meters, "month") is { } month)
            limits.Add(new UsageLimit(Tr("월간", "Monthly"), month.Percent, month.Resets, 43200));
        if (limits.Count == 0) return null;
        string plan = Str(root, "product") == "go-plus" ? "Go Plus" : "Go";
        return new UsageAccount("OpenCode", plan, limits, now) { Id = Id, Who = email };
    }

    private static (double Percent, DateTime? Resets)? Meter(JsonObject meters, string name)
    {
        if (meters[name] is not JsonObject meter) return null;
        if (Num(meter, "limitMicroCents") is not { } limit || limit <= 0) return null;
        double used = Num(meter, "usedMicroCents") ?? 0;
        DateTime? resets = DateTimeOffset.TryParse(Str(meter, "resetsAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var r)
            ? r.LocalDateTime : null;
        return (Math.Clamp(used / limit * 100, 0, 100), resets);
    }

    private static HttpClient MakeClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Bloom");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static double? Num(JsonNode? n, string key) => n?[key] switch
    {
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
        _ => null,
    };
}
