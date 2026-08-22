using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using MinecraftClient.Protocol.Message;

namespace MinecraftClient
{
    /// <summary>
    /// Publishes safe, bounded click actions embedded in server chat components.
    /// The regular chat line remains untouched; RakitBot renders this companion
    /// event as browser controls immediately below it.
    /// </summary>
    internal sealed class RbChatMenuEmitter
    {
        private const int MaxActions = 64;
        private const int MaxLabelLength = 256;
        private const int MaxValueLength = 2048;
        private const int MaxHoverLength = 512;
        private int eventId;

        private sealed class ClickAction
        {
            public string Kind { get; }
            public string Value { get; }
            public string Hover { get; }

            public ClickAction(string kind, string value, string hover)
            {
                Kind = kind;
                Value = value;
                Hover = hover;
            }
        }

        private sealed class MenuAction
        {
            public string Kind { get; }
            public string Value { get; }
            public string Hover { get; }
            public string Label { get; set; }

            public MenuAction(ClickAction action, string label)
            {
                Kind = action.Kind;
                Value = action.Value;
                Hover = action.Hover;
                Label = label;
            }
        }

        internal void Emit(ChatMessage message, string displayedText)
        {
            try
            {
                List<MenuAction> actions = new();
                HashSet<string> parsed = new(StringComparer.Ordinal);
                AddDocument(message.content, actions, parsed);
                if (!string.IsNullOrWhiteSpace(message.unsignedContent))
                    AddDocument(message.unsignedContent!, actions, parsed);
                if (actions.Count == 0)
                    return;

                int id = unchecked(++eventId);
                if (id <= 0)
                {
                    eventId = 1;
                    id = 1;
                }
                var payload = new
                {
                    v = 1,
                    type = "menu",
                    id,
                    text = Limit(displayedText, 2000),
                    actions = actions.Take(MaxActions).Select(static action => new
                    {
                        kind = action.Kind,
                        label = action.Label,
                        value = action.Value,
                        hover = action.Hover,
                    }).ToArray(),
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                ConsoleIO.WriteLine("[RBCHATMENU]" + JsonSerializer.Serialize(payload));
            }
            catch (Exception)
            {
                // A malformed server component must never interrupt normal chat.
            }
        }

        private static void AddDocument(string? raw, List<MenuAction> actions, HashSet<string> parsed)
        {
            string source = raw?.Trim() ?? string.Empty;
            if (source.Length < 2 || (source[0] != '{' && source[0] != '[') || !parsed.Add(source))
                return;
            JsonNode? root = JsonNode.Parse(source, documentOptions: new JsonDocumentOptions
            {
                MaxDepth = 64,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            Walk(root, null, actions, 0);
        }

        private static void Walk(JsonNode? node, ClickAction? inherited, List<MenuAction> actions, int depth)
        {
            if (node is null || depth > 64 || actions.Count >= MaxActions)
                return;
            if (node is JsonArray array)
            {
                foreach (JsonNode? child in array)
                    Walk(child, inherited, actions, depth + 1);
                return;
            }
            if (node is JsonValue value)
            {
                if (inherited is not null && value.TryGetValue<string>(out string? text))
                    AddAction(actions, inherited, text ?? string.Empty);
                return;
            }
            if (node is not JsonObject component)
                return;

            ClickAction? current = ReadClick(component) ?? inherited;
            string ownText = OwnText(component);
            if (current is not null && ownText.Length > 0)
                AddAction(actions, current, ownText);

            if (component["extra"] is JsonArray extra)
                Walk(extra, current, actions, depth + 1);

            // Translation arguments may contain their own click actions. The translated
            // parent label is already collected above, so only inspect arguments when
            // the parent itself is not clickable to avoid duplicate buttons.
            if (current is null && component["with"] is JsonArray with)
                Walk(with, null, actions, depth + 1);
        }

        private static void AddAction(List<MenuAction> actions, ClickAction action, string label)
        {
            label = Limit(label, MaxLabelLength);
            if (string.IsNullOrWhiteSpace(label))
                label = DefaultLabel(action.Kind);
            MenuAction? previous = actions.Count > 0 ? actions[^1] : null;
            if (previous is not null && previous.Kind == action.Kind
                && previous.Value == action.Value && previous.Hover == action.Hover)
            {
                previous.Label = Limit(previous.Label + label, MaxLabelLength);
                return;
            }
            actions.Add(new MenuAction(action, label));
        }

        private static ClickAction? ReadClick(JsonObject component)
        {
            JsonObject? click = component["clickEvent"] as JsonObject
                ?? component["click_event"] as JsonObject;
            if (click is null)
                return null;
            string kind = ReadString(click, "action").ToLowerInvariant();
            if (kind.StartsWith("minecraft:", StringComparison.Ordinal))
                kind = kind[10..];
            string value = kind switch
            {
                "run_command" or "suggest_command" => FirstString(click, "command", "value"),
                "open_url" => FirstString(click, "url", "value"),
                "copy_to_clipboard" => FirstString(click, "value", "text"),
                _ => string.Empty,
            };
            if (kind is not ("run_command" or "suggest_command" or "open_url" or "copy_to_clipboard"))
                return null;
            value = Limit(value, MaxValueLength);
            if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                return null;
            return new ClickAction(kind, value, ReadHover(component));
        }

        private static string ReadHover(JsonObject component)
        {
            JsonObject? hover = component["hoverEvent"] as JsonObject
                ?? component["hover_event"] as JsonObject;
            if (hover is null)
                return string.Empty;
            string action = ReadString(hover, "action");
            if (action.StartsWith("minecraft:", StringComparison.Ordinal))
                action = action[10..];
            if (!action.Equals("show_text", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            JsonNode? contents = hover["contents"] ?? hover["value"];
            return Limit(ComponentText(contents), MaxHoverLength);
        }

        private static string OwnText(JsonObject component)
        {
            if (component["text"] is JsonValue text && text.TryGetValue<string>(out string? value))
                return value ?? string.Empty;
            if (!component.ContainsKey("translate") && !component.ContainsKey("keybind")
                && !component.ContainsKey("selector") && !component.ContainsKey("score")
                && !component.ContainsKey("nbt"))
                return string.Empty;
            try
            {
                JsonObject shallow = new();
                foreach ((string key, JsonNode? valueNode) in component)
                {
                    if (key is "extra" or "clickEvent" or "click_event" or "hoverEvent" or "hover_event")
                        continue;
                    shallow[key] = valueNode?.DeepClone();
                }
                return ChatParser.ParseText(shallow.ToJsonString());
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                return string.Empty;
            }
        }

        private static string ComponentText(JsonNode? node)
        {
            if (node is null)
                return string.Empty;
            if (node is JsonValue value && value.TryGetValue<string>(out string? text))
                return text ?? string.Empty;
            try { return ChatParser.ParseText(node.ToJsonString()); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                return string.Empty;
            }
        }

        private static string ReadString(JsonObject value, string key)
        {
            return value[key] is JsonValue node && node.TryGetValue<string>(out string? text)
                ? text ?? string.Empty
                : string.Empty;
        }

        private static string FirstString(JsonObject value, params string[] keys)
        {
            foreach (string key in keys)
            {
                string text = ReadString(value, key);
                if (text.Length > 0)
                    return text;
            }
            return string.Empty;
        }

        private static string DefaultLabel(string kind) => kind switch
        {
            "run_command" => "Komutu çalıştır",
            "suggest_command" => "Komutu yaz",
            "open_url" => "Bağlantıyı aç",
            "copy_to_clipboard" => "Kopyala",
            _ => "Etkileşim",
        };

        private static string Limit(string value, int maxLength)
        {
            value ??= string.Empty;
            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
