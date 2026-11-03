using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bloom.Ai;
using Bloom.Core;

namespace Bloom.Tests;

/// The API providers' side: the stream reader, each wire's request body and events, the model lists, the history a
/// session starts from, and the key store.
internal static class ApiTests
{
    private static ApiProvider P(string id) => ApiProviders.Find(id) ?? throw new InvalidOperationException("no provider " + id);

    private static ApiRequest Request(string provider, ApiWire wire, string? effort = null, params ApiMessage[] messages) =>
        new(P(provider), "sk-test", "model-x", wire, "Be brief.", messages.Length > 0 ? messages : new[] { new ApiMessage(ApiMessage.User, "hi", Array.Empty<string>()) }, effort, null);

    public static void RiverModelsAndEndpoint()
    {
        var river = P("river");
        Check.Eq(river.Url("chat/completions"), "https://api.river.ai/v1/chat/completions", "River uses its own inference API, not DeepSeek's endpoint");
        Check.Eq(river.Url("models"), "https://api.river.ai/v1/models", "River model discovery");
        Check.Eq(river.Preferred[0], "deepseek-ai/DeepSeek-V4.1-Flash-1017", "the current DeepSeek release is preferred");
        Check.True(river.ReplaysReasoning, "DeepSeek reasoning is kept for subsequent tool calls");
        Check.Eq(string.Join(",", ApiProviders.EffortsFor(river, river.Preferred[0])), "auto,none,high,max", "River DeepSeek reasoning levels");
        var models = ApiCatalog.Parse(river, "{\"data\":[{\"id\":\"deepseek-ai/DeepSeek-V4.1-Flash\"},{\"id\":\"deepseek-ai/DeepSeek-V4.1-Flash-1017\"}]}");
        Check.True(models.Count == 2 && models.All(m => m.Wire == ApiWire.ChatCompletions), "River models can be selected in Bloom");
    }

    public static void SseEvents()
    {
        var sse = new SseParser();
        var events = new List<SseEvent>();
        foreach (string line in new[] { ": keep-alive", "", "event: message_start", "data: {\"a\":1}", "", "data:{\"b\":2}", "data: line2", "", "data: tail" })
            if (sse.Line(line) is { } e) events.Add(e);
        if (sse.End() is { } last) events.Add(last);
        Check.Eq(events.Count, 3, "a comment alone makes no event; the unfinished last one comes at the end");
        Check.Eq(events[0].Name, "message_start", "the event name");
        Check.Eq(events[0].Data, "{\"a\":1}", "one space after the colon is dropped");
        Check.Eq(events[1].Name, null, "a name lasts one event");
        Check.Eq(events[1].Data, "{\"b\":2}\nline2", "data lines are joined by a line break");
        Check.Eq(events[2].Data, "tail", "the stream's end flushes the last event");
    }

    public static void ChatCompletionsStream()
    {
        var w = ChatCompletionsWire.Instance;
        Check.Eq(w.Read(new SseEvent(null, "{\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}")).Text, "Hel", "a delta");
        Check.Eq(w.Read(new SseEvent(null, "{\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}")).Text, "", "a delta without text");
        Check.True(w.Read(new SseEvent(null, "[DONE]")) is { Done: true, End: ApiEnd.Complete }, "[DONE] ends it");
        Check.Eq(w.Read(new SseEvent(null, "{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}")).End, ApiEnd.Complete, "a finish reason: whole");
        Check.Eq(w.Read(new SseEvent(null, "{\"choices\":[{\"delta\":{\"content\":\"x\"},\"finish_reason\":\"length\"}]}")).End, ApiEnd.Cut, "the length ceiling: cut");
        Check.Eq(w.Read(new SseEvent(null, "{\"error\":{\"message\":\"overloaded\"}}")).Error, "overloaded", "an error in the stream");
        Check.True(w.Read(new SseEvent(null, "not json")) == ApiChunk.None, "anything else is skipped");
    }

    public static void ChatCompletionsBody()
    {
        var body = JsonNode.Parse(ChatCompletionsWire.Instance.Body(Request("openai", ApiWire.ChatCompletions, "high",
            new ApiMessage(ApiMessage.User, "q1", Array.Empty<string>()), new ApiMessage(ApiMessage.Assistant, "a1", Array.Empty<string>()), new ApiMessage(ApiMessage.User, "q2", Array.Empty<string>()))))!;
        var messages = body["messages"]!.AsArray();
        Check.Eq(messages.Count, 4, "the instructions, then the whole conversation");
        Check.Eq((string?)messages[0]!["role"], "system", "instructions first, as a system message");
        Check.Eq((string?)messages[2]!["content"], "a1", "the earlier answer goes again");
        Check.Eq((bool?)body["stream"], true, "streamed");
        Check.Eq((string?)body["reasoning_effort"], "high", "OpenAI's level");
        var nested = JsonNode.Parse(ChatCompletionsWire.Instance.Body(Request("openrouter", ApiWire.ChatCompletions, "low")))!;
        Check.Eq((string?)nested["reasoning"]?["effort"], "low", "OpenRouter's level is nested");
        Check.True(nested["reasoning_effort"] is null, "… and not sent the other way");
        Check.True(JsonNode.Parse(ChatCompletionsWire.Instance.Body(Request("deepseek", ApiWire.ChatCompletions)))!["reasoning_effort"] is null, "no level: none sent");
    }

    public static void ImagesInBodies()
    {
        string png = Path.Combine(Path.GetTempPath(), "bloom-test-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            var asked = new ApiMessage(ApiMessage.User, "what is this?", new[] { png, png + ".gone" });
            var chat = JsonNode.Parse(ChatCompletionsWire.Instance.Body(Request("openai", ApiWire.ChatCompletions, null, asked)))!["messages"]![1]!["content"]!.AsArray();
            Check.Eq(chat.Count, 2, "a picture that is gone is left out");
            Check.Eq((string?)chat[0]!["image_url"]!["url"], "data:image/png;base64,iVBORw==", "a picture as a data URL");
            var messages = JsonNode.Parse(MessagesWire.Instance.Body(Request("anthropic", ApiWire.Messages, null, asked)))!["messages"]![0]!["content"]!.AsArray();
            Check.Eq((string?)messages[0]!["source"]!["media_type"], "image/png", "a picture as a base64 block");
            Check.Eq((string?)messages[1]!["text"], "what is this?", "the text after the picture");
            var responses = JsonNode.Parse(ResponsesWire.Instance.Body(Request("opencode-go", ApiWire.Responses, null, asked)))!["input"]![0]!["content"]!.AsArray();
            Check.Eq((string?)responses[1]!["type"], "input_image", "Responses takes it as an input image");
        }
        finally
        {
            File.Delete(png);
        }
    }

    public static void MessagesWireBodyAndStream()
    {
        var body = JsonNode.Parse(MessagesWire.Instance.Body(Request("anthropic", ApiWire.Messages)))!;
        Check.Eq((string?)body["system"], "Be brief.", "the instructions in system");
        Check.Eq((int?)body["max_tokens"], MessagesWire.MaxTokens, "the length ceiling Messages requires");
        Check.Eq((string?)body["messages"]![0]!["content"]![0]!["type"], "text", "content as a list of blocks");
        var w = MessagesWire.Instance;
        Check.Eq(w.Read(new SseEvent("content_block_delta", "{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"안녕\"}}")).Text, "안녕", "a text delta");
        Check.True(w.Read(new SseEvent("content_block_delta", "{\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"…\"}}")) == ApiChunk.None, "thinking is not shown");
        Check.True(w.Read(new SseEvent("message_stop", "{\"type\":\"message_stop\"}")).Done, "message_stop ends it");
        Check.Eq(w.Read(new SseEvent("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}")).End, ApiEnd.Cut, "max_tokens: cut");
        Check.Eq(w.Read(new SseEvent("error", "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}")).Error, "Overloaded", "an error event");
        Check.True(w.Read(new SseEvent(null, "{\"type\":\"message_stop\"}")).Done, "the type in the data when the event has no name");
    }

    public static void ResponsesWireBodyAndStream()
    {
        var body = JsonNode.Parse(ResponsesWire.Instance.Body(Request("opencode-go", ApiWire.Responses, "medium",
            new ApiMessage(ApiMessage.User, "q", Array.Empty<string>()), new ApiMessage(ApiMessage.Assistant, "a", Array.Empty<string>()))))!;
        Check.Eq((string?)body["instructions"], "Be brief.", "the instructions");
        Check.Eq((bool?)body["store"], false, "nothing kept on the provider");
        Check.Eq((string?)body["input"]![1]!["content"]![0]!["type"], "output_text", "an earlier answer as output text");
        Check.Eq((string?)body["reasoning"]?["effort"], "medium", "the level");
        var w = ResponsesWire.Instance;
        Check.Eq(w.Read(new SseEvent("response.output_text.delta", "{\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}")).Text, "Hi", "a delta");
        Check.True(w.Read(new SseEvent(null, "{\"type\":\"response.completed\",\"response\":{}}")) is { Done: true, End: ApiEnd.Complete }, "completed ends it");
        Check.True(w.Read(new SseEvent(null, "{\"type\":\"response.incomplete\",\"response\":{}}")) is { Done: true, End: ApiEnd.Cut }, "incomplete ends it cut off");
        Check.Eq(w.Read(new SseEvent(null, "{\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"bad\"}}}")).Error, "bad", "a failed response");
    }

    public static void OpenCodeEndpoints()
    {
        var go = P("opencode-go");
        Check.Eq(ApiProviders.WireFor(go, "minimax-m3"), ApiWire.Messages, "MiniMax on /messages");
        Check.Eq(ApiProviders.WireFor(go, "qwen3.8-max"), ApiWire.Messages, "Qwen on /messages");
        Check.Eq(ApiProviders.WireFor(go, "claude-haiku-5-5"), ApiWire.Messages, "Claude on /messages");
        Check.Eq(ApiProviders.WireFor(go, "gpt-6-luna"), ApiWire.Responses, "GPT on /responses");
        Check.Eq(ApiProviders.WireFor(go, "kimi-k3"), ApiWire.ChatCompletions, "the rest on /chat/completions");
        using var meta = JsonDocument.Parse("{\"id\":\"gpt-new\",\"provider\":{\"npm\":\"@ai-sdk/openai-compatible\"}}");
        Check.Eq(ApiProviders.WireFor(go, "gpt-new", meta.RootElement), ApiWire.ChatCompletions, "an endpoint named in the metadata wins");
        Check.Eq(ApiProviders.WireFor(P("openai"), "gpt-6-luna"), ApiWire.ChatCompletions, "other providers keep their own wire");
        Check.Eq(ApiProviders.WireFor(P("openai"), "gpt-5-codex"), ApiWire.Responses, "OpenAI's Codex models on /responses");
        Check.Eq(ApiProviders.WireFor(P("openai"), "o3-pro"), ApiWire.Responses, "… and its Pro ones");
        Check.Eq(ApiProviders.WireFor(P("openai"), "o3-deep-research"), null, "deep research: none Bloom speaks");
    }

    public static void ModelLists()
    {
        var openai = ApiCatalog.Parse(P("openai"), "{\"data\":[{\"id\":\"gpt-5\"},{\"id\":\"text-embedding-3-small\"},{\"id\":\"whisper-1\"},{\"id\":\"gpt-4o\"},{\"id\":\"gpt-5-codex\"},{\"id\":\"o4-mini-deep-research\"}]}");
        Check.Eq(string.Join(",", openai.Select(m => m.Id + "/" + m.Wire)), "gpt-4o/ChatCompletions,gpt-5/ChatCompletions,gpt-5-codex/Responses", "chat models only, by name, each on its wire");
        // as the compat endpoint lists them (live, 2026-10): "models/" ids, speech and embedding models among them
        var gemini = ApiCatalog.Parse(P("gemini"), "{\"object\":\"list\",\"data\":[{\"id\":\"models/gemini-flash-lite-latest\",\"object\":\"model\",\"owned_by\":\"google\"},{\"id\":\"models/gemini-2.5-flash-preview-tts\"},{\"id\":\"models/gemini-embedding-001\"},{\"id\":\"models/gemini-2.5-flash-image\"},{\"id\":\"models/gemma-3-27b-it\"},{\"id\":\"models/antigravity-preview\"},{\"id\":\"models/deep-research-pro-preview\"}]}");
        Check.Eq(string.Join(",", gemini.Select(m => m.Id)), "gemini-flash-lite-latest,gemma-3-27b-it", "Gemini's models/ prefix goes; speech, embedding, picture, agent and research models too");
        Check.True(!ApiProviders.TakesTools(P("gemini"), "gemma-3-27b-it", gemini[1]) && ApiProviders.TakesTools(P("gemini"), "gemini-flash-lite-latest", gemini[0]), "Gemma takes no tools");
        var tooled = ApiCatalog.Parse(P("openrouter"), "{\"data\":[{\"id\":\"a/tools\",\"supported_parameters\":[\"tools\",\"reasoning\"]},{\"id\":\"b/plain\",\"supported_parameters\":[\"temperature\"]}]}");
        Check.True(!tooled[0].NoTools && tooled[1].NoTools, "OpenRouter's supported_parameters say which take tools");
        var router = ApiCatalog.Parse(P("openrouter"), "{\"data\":[{\"id\":\"a/text\",\"name\":\"A: Text\",\"architecture\":{\"output_modalities\":[\"text\"]}},{\"id\":\"b/pic\",\"name\":\"B\",\"architecture\":{\"output_modalities\":[\"picture\"]}}]}");
        Check.True(router.Count == 1 && router[0].Name == "A: Text", "a model writing no text is left out; its name is kept");
        var anthropic = ApiCatalog.Parse(P("anthropic"), "{\"data\":[{\"id\":\"claude-x\",\"display_name\":\"Claude X\"}],\"has_more\":false}");
        Check.True(anthropic[0] is { Name: "Claude X", Wire: ApiWire.Messages }, "Anthropic's display name and wire");
        Check.Eq(ApiCatalog.Parse(P("opencode-go"), "{\"data\":[{\"id\":\"minimax-m3\"}]}")[0].Wire, ApiWire.Messages, "OpenCode Go's endpoint per model");
    }

    public static void ApiModelIds()
    {
        string id = AiModels.Api("openrouter", "deepseek/deepseek-r1:free");
        Check.True(AiModels.IsApi(id) && !AiModels.IsChatGpt(id), "an API id is neither side's");
        Check.Eq(AiModels.ApiParts(id), ("openrouter", "deepseek/deepseek-r1:free"), "a ':' inside the model id stays");
        Check.Eq(AiModels.Provider(id), "api", "its side");
        Check.True(AiModels.Inline(id), "files come inline");
        Check.True(AiModels.HasEffort(id) && AiModels.Efforts(id)[0] == AiModels.Auto, "OpenRouter takes levels, the model's own first");
        string deepseek = AiModels.Api("deepseek", "deepseek-chat");
        Check.True(!AiModels.HasEffort(deepseek) && AiModels.Fit(deepseek, "high") == AiModels.Auto, "no levels: always the model's own");
        string mini = AiModels.Api("xai", "grok-3-mini");
        Check.Eq(AiModels.Fit(mini, "medium"), "low", "the nearest level below");
        Check.True(!AiModels.HasEffort(AiModels.Api("xai", "grok-4")), "Grok 4 takes no level");
        Check.Eq(string.Join(",", AiModels.Efforts(AiModels.Api("openai", "gpt-5.1"))), "auto,none,low,medium,high", "GPT-5.1: none, no minimal");
        Check.Eq(string.Join(",", AiModels.Efforts(AiModels.Api("openai", "gpt-5-mini"))), "auto,minimal,low,medium,high", "GPT-5: minimal");
        Check.True(!AiModels.HasEffort(AiModels.Api("openai", "gpt-4o")), "a model before reasoning takes none");
        Check.Eq(AiModels.Fit(AiModels.Api("openai", "gpt-5.1"), "minimal"), "none", "a level the model lacks fits to its nearest");
    }

    public static void PreparedLevelFollowsTheModel()
    {
        using var data = new SettingsTransferTests.DataFolder();
        ApiKeys.Set("openai", "sk-test");
        var (request, _) = ApiClient.PrepareAsync(AiModels.Api("openai", "gpt-5.1"), "minimal", "", Array.Empty<ApiMessage>(), null).GetAwaiter().GetResult();
        Check.True(request is { Effort: null, Wire: ApiWire.ChatCompletions }, "a level the model doesn't take is not sent");
        (request, _) = ApiClient.PrepareAsync(AiModels.Api("openai", "gpt-5.1"), "none", "", Array.Empty<ApiMessage>(), null).GetAwaiter().GetResult();
        Check.Eq(request?.Effort, "none", "one it takes is");
        (request, _) = ApiClient.PrepareAsync(AiModels.Api("openai", "gpt-5-codex"), "high", "", Array.Empty<ApiMessage>(), null).GetAwaiter().GetResult();
        Check.True(request is { Wire: ApiWire.Responses, Effort: "high" }, "a Codex model on /responses with its level");
        var (none, error) = ApiClient.PrepareAsync(AiModels.ApiUnset, null, "", Array.Empty<ApiMessage>(), null).GetAwaiter().GetResult();
        Check.True(none is null && error == AiErrors.ApiModelGone, "no API model: said, not sent elsewhere");
        ApiKeys.Remove("openai");
        ApiCatalog.Saving().GetAwaiter().GetResult();
    }

    public static void HistoryFromChat()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bloom-history-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string notes = Path.Combine(dir, "notes.txt"), photo = Path.Combine(dir, "photo.png");
            File.WriteAllText(notes, "the notes' contents");
            File.WriteAllBytes(photo, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            var messages = new List<ChatMessage>
            {
                new() { Role = "user", Text = "q1", Sent = "10:00\n<page title=\"P\">\npage text\n</page>\n\nq1" },
                new() { Role = "assistant", Text = "a1" },
                new() { Role = "user", Text = "q2" },
                new() { Role = "assistant", Text = "failed", Error = true },
                new() { Role = "user", Text = "q3", Image = Path.Combine(dir, "gone.png"), Files = new() { notes, photo } },
                new() { Role = "assistant", Text = "a3" },
                new() { Role = "user", Text = "now" },
                new() { Role = "assistant", Text = "", Streaming = true },
            };
            var past = ApiSession.Past(messages);
            Check.Eq(past.Count, 2, "pairs that got an answer; not the turn being sent");
            var history = ApiSession.HistoryOf(past, Chats.UnavailableNote());
            Check.True(history[0].Text.Contains("page text", StringComparison.Ordinal), "the page's contents go again, as sent");
            Check.True(history[2].Text.StartsWith("q3", StringComparison.Ordinal) && history[2].Text.Contains("the notes' contents", StringComparison.Ordinal), "an older message's text, its text file inline again");
            Check.Eq(string.Join(",", history[2].Images), photo, "the attached picture; the Lens picture that is gone is not sent");
            Check.Eq(history[3].Text, "a3", "the answer");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public static void ErrorsAndRedaction()
    {
        Check.Eq(ApiJson.ErrorMessage("{\"error\":{\"message\":\"Invalid model\"}}"), "Invalid model", "OpenAI's shape");
        Check.Eq(ApiJson.ErrorMessage("[{\"error\":{\"code\":400,\"message\":\"Bad\"}}]"), "Bad", "Gemini's shape");
        Check.Eq(ApiJson.ErrorMessage("{\"error\":\"nope\"}"), "nope", "a plain error");
        Check.Eq(ApiJson.ErrorMessage("plain text"), "plain text", "not JSON");
        Check.Eq(ApiClient.Redact("bad key sk-secret-1", "sk-secret-1"), "bad key ***", "the key never shows");
        Check.True(AiErrors.ApiStatus("OpenAI", 401, "").Contains("API 키", StringComparison.Ordinal), "401: the key");
        Check.True(AiErrors.ApiStatus("OpenAI", 429, "").Contains("한도", StringComparison.Ordinal), "429: the limit");
        Check.True(AiErrors.ApiStatus("OpenAI", 400, "Invalid model").Contains("Invalid model", StringComparison.Ordinal), "another refusal shows what was said");
        Check.True(AiErrors.ApiStatus("Gemini", 404, "This model is no longer available to new users.").Contains("no longer available", StringComparison.Ordinal), "404 keeps why");
        Check.True(AiErrors.ApiStatus("Gemini", 503, "The model is overloaded.").Contains("overloaded", StringComparison.Ordinal), "503 keeps why");
        Check.True(AiErrors.ApiStatus("Gemini", 302, "").Contains("따라가지", StringComparison.Ordinal), "3xx: not followed");
    }

    public static void KeyStore()
    {
        const string key = "sk-proj-abc123-never-in-the-file";
        string blob = ApiKeys.Protect(key);
        Check.True(!blob.Contains(key, StringComparison.Ordinal), "the stored form is not the key");
        Check.Eq(ApiKeys.Unprotect(blob), key, "it opens again for this user");
        Check.Eq(ApiKeys.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3 })), null, "a broken blob opens to nothing");
        string file = Path.Combine(Path.GetTempPath(), "bloom-test-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            ApiKeys.Write(file, new Dictionary<string, string> { ["openai"] = blob });
            Check.True(!File.ReadAllText(file).Contains(key, StringComparison.Ordinal), "the file holds no key in clear");
            Check.Eq(ApiKeys.Unprotect(ApiKeys.Read(file)["openai"]), key, "read back from the file");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
