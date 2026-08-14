using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace MinecraftClient;

/// <summary>
/// Keeps a small, browser-independent chat history for the RakitBot panel.
/// HUD packets never pass through this class; only OnTextReceived calls it.
/// </summary>
internal static class RakitBotChatHistory
{
    internal const int MaxEntries = 300;
    internal const int MaxTextLength = 2000;
    internal const string RelativePath = "RakitBot_Data/chat-history.json";

    private static readonly object s_sync = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly List<Entry> s_entries = Load();
    private static Timer? s_flushTimer;
    private static bool s_flushScheduled;

    internal static void Append(string text)
    {
        if (String.IsNullOrEmpty(text))
            return;

        lock (s_sync)
        {
            s_entries.Add(new Entry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                text.Length > MaxTextLength ? text[..MaxTextLength] : text));
            if (s_entries.Count > MaxEntries)
                s_entries.RemoveRange(0, s_entries.Count - MaxEntries);

            // Sohbet akisi yogun olsa da diske en fazla 500 ms'de bir yaz.
            if (!s_flushScheduled)
            {
                s_flushScheduled = true;
                s_flushTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                s_flushTimer.Change(500, Timeout.Infinite);
            }
        }
    }

    private static List<Entry> Load()
    {
        try
        {
            if (!File.Exists(RelativePath))
                return new List<Entry>();
            List<Entry>? loaded = JsonSerializer.Deserialize<List<Entry>>(
                File.ReadAllText(RelativePath), s_jsonOptions);
            return (loaded ?? new List<Entry>())
                .Where(entry => entry is not null && !String.IsNullOrEmpty(entry.Text))
                .TakeLast(MaxEntries)
                .Select(entry => entry with
                {
                    Text = entry.Text.Length > MaxTextLength ? entry.Text[..MaxTextLength] : entry.Text,
                })
                .ToList();
        }
        catch
        {
            // Bozuk/eski dosya botun acilmasini engellememeli.
            return new List<Entry>();
        }
    }

    private static void Flush()
    {
        lock (s_sync)
        {
            try
            {
                string? directory = Path.GetDirectoryName(RelativePath);
                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                string temporaryPath = RelativePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(s_entries, s_jsonOptions), Encoding.UTF8);
                File.Move(temporaryPath, RelativePath, overwrite: true);
            }
            catch
            {
                // Gecmis yardimci bir ozelliktir; disk hatasi MCC'yi durdurmamali.
            }
            finally
            {
                s_flushScheduled = false;
            }
        }
    }

    internal sealed record Entry(long Timestamp, string Text);
}
