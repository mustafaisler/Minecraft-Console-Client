using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace MinecraftClient;

/// <summary>
/// MCC ekran yazılarını panelin tükettiği sınırlı JSON olaylarına dönüştürür.
/// Actionbar ve bossbar tekrarları konsol geçmişini şişirmeden canlılığı korur.
/// </summary>
internal sealed class RbHudEmitter
{
    private const long ActionBarLifetimeMs = 3500;
    private const long HeartbeatMs = 30000;
    private const int BossBarEmissionLimit = 256;
    private readonly Lock sync = new();
    private readonly Dictionary<string, (string Fingerprint, long EmittedAt)> bossBarEmissions = new();
    private string actionBarText = string.Empty;
    private long actionBarLastSeenAt;
    private long actionBarLastEmittedAt;
    private bool actionBarPersistent;

    public void EmitTitle(int action, string titleText, string subtitleText, string actionBarText,
        int fadeIn, int stay, int fadeOut)
    {
        string? type = action switch
        {
            0 => "title",
            1 => "subtitle",
            2 => "actionbar",
            3 => "times",
            4 => "clear",
            5 => "reset",
            _ => null,
        };
        if (type is null)
            return;
        if (action == 2)
        {
            EmitActionBar(Normalize(actionBarText), fadeIn, stay, fadeOut);
            return;
        }
        Emit(new
        {
            v = 1,
            type,
            text = action switch
            {
                0 => Normalize(titleText),
                1 => Normalize(subtitleText),
                _ => string.Empty,
            },
            fadeIn,
            stay,
            fadeOut,
            at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    public void EmitBossBar(Guid id, int action, string title, float progress, int color, int division, byte flags)
    {
        string actionName = action switch
        {
            0 => "add",
            1 => "remove",
            2 => "progress",
            3 => "title",
            4 => "style",
            5 => "flags",
            _ => "unknown",
        };
        float stableProgress = action == 2 ? (float)(int)Math.Round(progress * 100F) : progress;
        string fingerprint = string.Join('\u001f', Normalize(title), stableProgress, color, division, flags);
        string idText = id.ToString("D");
        string key = idText + ":" + actionName;
        long now = Environment.TickCount64;
        using (sync.EnterScope())
        {
            if (bossBarEmissions.TryGetValue(key, out var previous)
                && previous.Fingerprint.Equals(fingerprint, StringComparison.Ordinal)
                && now - previous.EmittedAt < HeartbeatMs)
            {
                return;
            }
            bossBarEmissions[key] = (fingerprint, now);
            if (action == 1)
            {
                string prefix = idText + ":";
                foreach (string oldKey in bossBarEmissions.Keys
                    .Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    bossBarEmissions.Remove(oldKey);
                }
            }
            while (bossBarEmissions.Count > BossBarEmissionLimit)
            {
                string oldest = bossBarEmissions.MinBy(entry => entry.Value.EmittedAt).Key;
                bossBarEmissions.Remove(oldest);
            }
        }
        Emit(new
        {
            v = 1,
            type = "bossbar",
            id = idText,
            action = actionName,
            title = Normalize(title),
            progress,
            color,
            division,
            flags,
            at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    public void ExpireActionBar()
    {
        long now = Environment.TickCount64;
        bool shouldClear = false;
        using (sync.EnterScope())
        {
            if (actionBarPersistent && now - actionBarLastSeenAt > ActionBarLifetimeMs)
            {
                actionBarText = string.Empty;
                actionBarPersistent = false;
                actionBarLastSeenAt = now;
                actionBarLastEmittedAt = now;
                shouldClear = true;
            }
        }
        if (shouldClear)
            EmitActionBarPayload(string.Empty, -1, -1, -1, false);
    }

    private void EmitActionBar(string text, int fadeIn, int stay, int fadeOut)
    {
        long now = Environment.TickCount64;
        bool shouldEmit = false;
        bool persistent;
        using (sync.EnterScope())
        {
            long sinceLastSeen = now - actionBarLastSeenAt;
            if (!text.Equals(actionBarText, StringComparison.Ordinal) || sinceLastSeen > ActionBarLifetimeMs)
            {
                actionBarText = text;
                actionBarPersistent = false;
                shouldEmit = true;
            }
            else if (!string.IsNullOrEmpty(text) && !actionBarPersistent)
            {
                actionBarPersistent = true;
                shouldEmit = true;
            }
            else if (actionBarPersistent && now - actionBarLastEmittedAt >= HeartbeatMs)
            {
                shouldEmit = true;
            }
            actionBarLastSeenAt = now;
            persistent = actionBarPersistent;
            if (shouldEmit)
                actionBarLastEmittedAt = now;
        }
        if (shouldEmit)
            EmitActionBarPayload(text, fadeIn, stay, fadeOut, persistent);
    }

    private static void EmitActionBarPayload(string text, int fadeIn, int stay, int fadeOut, bool persistent) =>
        Emit(new
        {
            v = 1,
            type = "actionbar",
            text,
            fadeIn,
            stay,
            fadeOut,
            persistent,
            at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

    private static string Normalize(string? value) =>
        string.Equals(value?.Trim(), "null", StringComparison.OrdinalIgnoreCase) ? string.Empty : value ?? string.Empty;

    private static void Emit(object payload)
    {
        if (Settings.Config.Main.Advanced.ShowXPBarMessages)
            ConsoleIO.WriteLine("[RBHUD]" + JsonSerializer.Serialize(payload));
    }
}
