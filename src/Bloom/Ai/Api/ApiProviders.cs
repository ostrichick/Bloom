using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Bloom.Ai;

/// The API providers Bloom can use with a key of the user's (설정 > AI 계정), in the order they are listed. Everything
/// but Anthropic speaks Chat Completions; OpenCode Go serves each model on its own endpoint.
internal static class ApiProviders
{
    public const string AnthropicId = "anthropic";

    public static readonly IReadOnlyList<ApiProvider> All = new ApiProvider[]
    {
        new("openai", "OpenAI", "https://api.openai.com/v1", ApiWire.ChatCompletions, "https://platform.openai.com/api-keys")
        {
            // OpenAI's model pages: the Codex and Pro models answer only on /responses; deep research and computer use
            // need tools Bloom doesn't give yet
            Routes = new ApiRoute[] { new("codex", ApiWire.Responses), new("-pro", ApiWire.Responses), new("deep-research", null), new("computer-use", null) },
            Preferred = new[] { "gpt-5-mini", "gpt-4.1-mini" },
            // most specific first: GPT-5.1 dropped "minimal" for "none", GPT-5.2 added "xhigh", the chat aliases and the
            // models before the o-series take no level
            EffortRules = new ApiEffortRule[]
            {
                new("gpt-5.2-pro", "medium", "high", "xhigh"),
                new("gpt-5-pro", "high"),
                new("gpt-5.1-codex-max", "low", "medium", "high", "xhigh"),
                new("gpt-5.1-codex", "low", "medium", "high"),
                new("gpt-5-codex", "low", "medium", "high"),
                new("gpt-5.2-chat"),
                new("gpt-5.1-chat"),
                new("gpt-5-chat"),
                new("gpt-5.2", "none", "low", "medium", "high", "xhigh"),
                new("gpt-5.1", "none", "low", "medium", "high"),
                new("gpt-5", "minimal", "low", "medium", "high"),
                new("o1-mini"),
                new("o1-preview"),
                new("o1", "low", "medium", "high"),
                new("o3", "low", "medium", "high"),
                new("o4", "low", "medium", "high"),
            },
        },
        new(AnthropicId, "Anthropic", "https://api.anthropic.com/v1", ApiWire.Messages, "https://console.anthropic.com/settings/keys"),
        new("gemini", "Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", ApiWire.ChatCompletions, "https://aistudio.google.com/apikey")
        {
            // its Gemma models answer a request with tools "Function calling is not enabled"
            NoToolModels = new[] { "gemma" },
            // the aliases follow Google's current models; 2.5 Flash "is no longer available to new users"
            Preferred = new[] { "gemini-flash-lite-latest", "gemini-flash-latest" },
            // only the thinking models take reasoning_effort: 2.5 Pro can't turn thinking off, 3 knows low and high
            EffortRules = new ApiEffortRule[]
            {
                new("gemini-2.5-pro", "low", "medium", "high"),
                new("gemini-2.5-flash", "none", "low", "medium", "high"),
                new("gemini-3", "low", "high"),
            },
        },
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", ApiWire.ChatCompletions, "https://openrouter.ai/settings/keys")
        {
            // OpenRouter turns the level into each model's own setting and drops it for a model without reasoning
            EffortRules = new ApiEffortRule[] { new("", "low", "medium", "high") },
            NestedEffort = true,
            Balance = ApiBalanceWay.OpenRouter,
        },
        new("river", "River", "https://api.river.ai/v1", ApiWire.ChatCompletions, "https://console.river.ai")
        {
            // River's OpenAI-compatible inference endpoint serves DeepSeek under the model's full ID.
            Preferred = new[] { "deepseek-ai/DeepSeek-V4.1-Flash-1017", "deepseek-ai/DeepSeek-V4.1-Flash" },
            EffortRules = new ApiEffortRule[] { new("deepseek-ai/deepseek-v4.1-flash", "none", "high", "max") },
            ReplaysReasoning = true,
        },
        new("inception", "Inception", "https://api.inceptionlabs.ai/v1", ApiWire.ChatCompletions, "https://platform.inceptionlabs.ai/dashboard/api-keys")
        {
            Preferred = new[] { "mercury-3" },
        },
        new("deepseek", "DeepSeek", "https://api.deepseek.com", ApiWire.ChatCompletions, "https://platform.deepseek.com/api_keys")
        {
            // its thinking mode (api-docs.deepseek.com/guides/thinking_mode): with tools, every earlier turn's reasoning goes back
            ReplaysReasoning = true,
            Balance = ApiBalanceWay.DeepSeek,
            Preferred = new[] { "deepseek-chat" },
        },
        new("xai", "xAI", "https://api.x.ai/v1", ApiWire.ChatCompletions, "https://console.x.ai")
        {
            // only Grok 3 mini takes reasoning_effort; Grok 4 refuses a request that names one
            EffortRules = new ApiEffortRule[] { new("grok-3-mini", "low", "high") },
        },
        new("opencode-go", "OpenCode Go", "https://opencode.ai/inference/go/v1", ApiWire.ChatCompletions, "https://opencode.ai/console")
        {
            // its /models gives ids only (checked 2026-10); its docs' endpoint table puts these families on /messages
            // and these on /responses, every other model on /chat/completions
            Routes = new ApiRoute[]
            {
                new("claude-", ApiWire.Messages), new("minimax-", ApiWire.Messages), new("qwen", ApiWire.Messages),
                new("gpt-", ApiWire.Responses), new("grok-", ApiWire.Responses), new("muse-", ApiWire.Responses),
            },
            MixedEndpoints = true,
            SessionHeader = "x-opencode-session",
        },
    };

    public static ApiProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// The wire a model of the provider speaks: the endpoint its /models entry names (`meta`, for a provider that names
    /// them), else its family's Route, else the provider's own; null = none Bloom speaks (left out of the pickers).
    public static ApiWire? WireFor(ApiProvider provider, string model, JsonElement? meta = null)
    {
        if (provider.MixedEndpoints && meta is { ValueKind: JsonValueKind.Object } entry && Hint(entry) is { } named) return named;
        string id = model.ToLowerInvariant();
        return provider.Routes.FirstOrDefault(r => id.Contains(r.Pattern, StringComparison.Ordinal)) is { } route ? route.Wire : provider.Wire;
    }

    /// Whether a model takes tools: not when its listing (`info`) or its provider's NoToolModels say otherwise.
    public static bool TakesTools(ApiProvider provider, string model, ApiModelInfo? info)
    {
        string id = model.ToLowerInvariant();
        return info is not { NoTools: true } && !provider.NoToolModels.Any(n => id.Contains(n, StringComparison.Ordinal));
    }

    /// The levels a model takes, AiModels.Auto first (= none sent): its family's EffortRule, or Auto alone.
    public static string[] EffortsFor(ApiProvider provider, string model)
    {
        string id = model.ToLowerInvariant();
        var rule = provider.EffortRules.FirstOrDefault(r => id.StartsWith(r.Prefix, StringComparison.Ordinal));
        return [AiModels.Auto, .. rule?.Levels ?? []];
    }

    /// An endpoint or AI SDK package named in a model's metadata ("…/v1/messages", "@ai-sdk/anthropic"), as the docs'
    /// table names them.
    private static ApiWire? Hint(JsonElement entry)
    {
        foreach (string field in new[] { "endpoint", "api", "npm" })
            if (entry.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && WireNamed(v.GetString()!) is { } w) return w;
        if (entry.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.Object) return Hint(p);
        return null;
    }

    private static ApiWire? WireNamed(string s)
    {
        if (s.Contains("chat/completions", StringComparison.Ordinal) || s.Contains("openai-compatible", StringComparison.Ordinal)) return ApiWire.ChatCompletions;
        if (s.Contains("messages", StringComparison.Ordinal) || s.Contains("anthropic", StringComparison.Ordinal)) return ApiWire.Messages;
        if (s.Contains("responses", StringComparison.Ordinal) || s == "@ai-sdk/openai") return ApiWire.Responses;
        return null;
    }
}
