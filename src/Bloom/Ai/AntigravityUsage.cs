using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Bloom.Core;

namespace Bloom.Ai;

/// Gemini quota from the running Antigravity IDE's local language server. The quota summary has
/// the real 5-hour and weekly buckets; older servers may expose only a per-model quota.
/// No Google credentials are read or stored, and nothing is sent outside 127.0.0.1.
internal static class AntigravityUsage
{
    public const string Id = "antigravity";
    private const string Endpoint = "/exa.language_server_pb.LanguageServerService/";
    private const string Discovery = """
        $found = @()
        Get-CimInstance Win32_Process -Filter "Name LIKE 'language_server%'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match 'antigravity' } |
            ForEach-Object {
                $ports = @(Get-NetTCPConnection -State Listen -OwningProcess $_.ProcessId -ErrorAction SilentlyContinue |
                    Where-Object { $_.LocalAddress -eq '127.0.0.1' -or $_.LocalAddress -eq '0.0.0.0' -or $_.LocalAddress -eq '::1' -or $_.LocalAddress -eq '::' } |
                    Select-Object -ExpandProperty LocalPort -Unique)
                if ($ports.Count -gt 0) {
                    $found += [pscustomobject]@{ cmd = $_.CommandLine; ports = @($ports) }
                }
            }
        ConvertTo-Json -InputObject @($found) -Depth 3 -Compress
        """;
    private sealed record Server(string Token, List<int> Ports);

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        // Antigravity's local language server uses a self-signed certificate. This exception
        // is restricted to the loopback address; no remote HTTPS certificate is bypassed.
        ServerCertificateCustomValidationCallback = static (request, _, _, _) =>
            request.RequestUri?.Host == "127.0.0.1",
        UseProxy = false,
    }) { Timeout = TimeSpan.FromSeconds(2) };

    /// Avoid starting the relatively expensive Windows process discovery when the IDE is closed.
    public static bool Running
    {
        get
        {
            try
            {
                var processes = Process.GetProcessesByName("Antigravity");
                foreach (var p in processes) p.Dispose();
                return processes.Length > 0;
            }
            catch (Exception) { return false; }
        }
    }

    public static async Task<UsageAccount?> Ask()
    {
        if (!Running) return null;
        foreach (var server in await Discover())
            foreach (int port in server.Ports)
                foreach (string scheme in new[] { "https", "http" })
                {
                    var summary = await Post(scheme, port, server.Token, "RetrieveUserQuotaSummary");
                    if (summary is not null)
                    {
                        try
                        {
                            if (ParseSummary(summary) is { } card) return card;
                        }
                        catch (JsonException) { /* old or incomplete response; try the other methods */ }
                    }
                    // A legacy IDE may not have the group summary. Its per-model response
                    // does not contain a weekly bucket, so never label it as weekly.
                    var status = await Post(scheme, port, server.Token, "GetUserStatus");
                    if (status is not null)
                    {
                        try
                        {
                            if (ParseModels(status) is { } fallback) return fallback;
                        }
                        catch (JsonException) { /* try model configurations */ }
                    }
                    var models = await Post(scheme, port, server.Token, "GetCommandModelConfigs");
                    if (models is not null)
                    {
                        try
                        {
                            if (ParseModels(models) is { } fallback) return fallback;
                        }
                        catch (JsonException) { /* try another listening port */ }
                    }
                }
        return null;
    }

    private static async Task<List<Server>> Discover()
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            },
        };
        proc.StartInfo.ArgumentList.Add("-NoProfile");
        proc.StartInfo.ArgumentList.Add("-NonInteractive");
        proc.StartInfo.ArgumentList.Add("-Command");
        proc.StartInfo.ArgumentList.Add(Discovery);
        bool started = false;
        try
        {
            started = proc.Start();
            if (!started) return new();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string output = await proc.StandardOutput.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            if (proc.ExitCode != 0) return new();
            return ParseServers(output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or JsonException)
        {
            return new();
        }
        finally
        {
            if (started && !proc.HasExited)
                try { proc.Kill(true); }
                catch (InvalidOperationException) { }
        }
    }

    private static List<Server> ParseServers(string json)
    {
        var result = new List<Server>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("cmd", out var raw) || raw.ValueKind != JsonValueKind.String) continue;
            var match = Regex.Match(raw.GetString() ?? "", @"(?:^|\s)--csrf_token(?:=|\s+)(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string token = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim('"');
            if (token.Length == 0 || token.Contains('\r') || token.Contains('\n')) continue;
            var ports = new List<int>();
            if (item.TryGetProperty("ports", out var rawPorts) && rawPorts.ValueKind == JsonValueKind.Array)
                foreach (var entry in rawPorts.EnumerateArray())
                    if (entry.TryGetInt32(out int port) && port is > 0 and <= 65535 && !ports.Contains(port)) ports.Add(port);
            // The extension server port, when supplied, is a better first candidate.
            var explicitPort = Regex.Match(raw.GetString() ?? "", @"--extension_server_port(?:=|\s+)(\d+)");
            if (explicitPort.Success && int.TryParse(explicitPort.Groups[1].Value, out int preferred) && ports.Remove(preferred))
                ports.Insert(0, preferred);
            if (ports.Count > 0) result.Add(new Server(token, ports.GetRange(0, Math.Min(ports.Count, 6))));
        }
        return result;
    }

    private static async Task<string?> Post(string scheme, int port, string token, string method)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, scheme + "://127.0.0.1:" + port + Endpoint + method);
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", token);
            request.Content = new StringContent(method == "RetrieveUserQuotaSummary"
                ? "{}" : """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}}""",
                Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return null;
        }
    }

    /// The group summary distinguishes the 5h and weekly Gemini pools. Never infer a weekly
    /// percentage from the model quota or the 5h number.
    internal static UsageAccount? ParseSummary(string json, DateTime? at = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Property(root, "response") is { } response) root = response;
        if (Property(root, "groups") is not { ValueKind: JsonValueKind.Array } groups) return null;
        UsageLimit? session = null, weekly = null;
        foreach (var group in groups.EnumerateArray())
        {
            bool isGemini = String(group, "displayName")?.Contains("gemini", StringComparison.OrdinalIgnoreCase) == true;
            if (Property(group, "buckets") is not { ValueKind: JsonValueKind.Array } buckets) continue;
            foreach (var bucket in buckets.EnumerateArray())
            {
                string id = String(bucket, "bucketId") ?? "";
                if (!isGemini && !id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) continue;
                string window = (String(bucket, "window") ?? "") + " " + id;
                double? remaining = Number(bucket, "remainingFraction");
                if (remaining is null || !double.IsFinite(remaining.Value) || remaining is < 0 or > 1) continue;
                var reset = Reset(bucket, "resetTime");
                double used = (1 - remaining.Value) * 100;
                if (window.Contains("week", StringComparison.OrdinalIgnoreCase) || window.Contains("7d", StringComparison.OrdinalIgnoreCase))
                    weekly ??= new UsageLimit(Tr("주간", "Weekly"), used, reset, UsageLimit.WeekMins);
                else if (window.Contains("5h", StringComparison.OrdinalIgnoreCase) || window.Contains("five", StringComparison.OrdinalIgnoreCase))
                    session ??= new UsageLimit(Tr("5시간", "5h"), used, reset, UsageLimit.SessionMins);
            }
        }
        var limits = new List<UsageLimit>();
        if (session is not null) limits.Add(session);
        if (weekly is not null) limits.Add(weekly);
        return limits.Count == 0 ? null : new UsageAccount("Gemini", null, limits, at ?? DateTime.Now) { Id = Id };
    }

    /// Legacy GetUserStatus exposes only per-model fractions, without identified 5h/week
    /// windows. Show the lowest remaining Gemini quota without inventing a period.
    internal static UsageAccount? ParseModels(string json, DateTime? at = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Property(root, "response") is { } response) root = response;
        if (Property(root, "userStatus") is { } user) root = user;
        var config = Property(root, "cascadeModelConfigData") ?? root;
        if (Property(config, "clientModelConfigs") is not { ValueKind: JsonValueKind.Array } models) return null;
        double? lowest = null;
        DateTime? reset = null;
        foreach (var model in models.EnumerateArray())
        {
            if (String(model, "label")?.Contains("gemini", StringComparison.OrdinalIgnoreCase) != true) continue;
            if (Property(model, "quotaInfo") is not { } quota) continue;
            double? fraction = Number(quota, "remainingFraction");
            if (fraction is null || !double.IsFinite(fraction.Value) || fraction is < 0 or > 1) continue;
            if (lowest is null || fraction < lowest)
            {
                lowest = fraction;
                reset = Reset(quota, "resetTime");
            }
        }
        return lowest is null ? null : new UsageAccount("Gemini", null,
            new List<UsageLimit> { new(Tr("모델 한도", "Model quota"), (1 - lowest.Value) * 100, reset) },
            at ?? DateTime.Now) { Id = Id };
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
    private static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
    private static double? Number(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetDouble(out var n) ? n : null;
    private static DateTime? Reset(JsonElement element, string name) =>
        DateTimeOffset.TryParse(String(element, name), out var reset) ? reset.LocalDateTime : null;
}
