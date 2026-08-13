using System.Collections.Generic;
using MinecraftClient.Protocol.Message;

namespace MinecraftClient.Inventory;

public static class TooltipRegistryMapping
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<int, string>> Registries = new();
    private static readonly Dictionary<string, Dictionary<int, string>> Descriptions = new();

    public static bool IsSupported(string registryId) => registryId is
        "minecraft:trim_material" or
        "minecraft:trim_pattern" or
        "minecraft:mob_effect" or
        "minecraft:potion";

    public static void SetRegistry(
        string registryId,
        Dictionary<int, string> entries,
        Dictionary<int, string> descriptions)
    {
        if (!IsSupported(registryId)) return;
        lock (Sync)
        {
            Registries[registryId] = new Dictionary<int, string>(entries);
            Descriptions[registryId] = new Dictionary<int, string>(descriptions);
        }
    }

    public static string ReadDescription(Dictionary<string, object>? nbt)
    {
        if (nbt is null || !nbt.TryGetValue("description", out object? raw)) return string.Empty;
        try
        {
            return raw switch
            {
                Dictionary<string, object> component => ChatParser.ParseText(component),
                string json => ChatParser.ParseText(json),
                _ => string.Empty,
            };
        }
        catch
        {
            return raw as string ?? string.Empty;
        }
    }

    public static string? GetHolderName(string registryId, int holderValue)
    {
        if (holderValue <= 0) return null;
        lock (Sync)
        {
            if (!Registries.TryGetValue(registryId, out var entries)
                || !entries.TryGetValue(holderValue - 1, out string? value))
                return null;
            int separator = value.IndexOf(':');
            return separator >= 0 ? value[(separator + 1)..] : value;
        }
    }

    public static string? GetHolderDescription(string registryId, int holderValue)
    {
        if (holderValue <= 0) return null;
        lock (Sync)
        {
            return Descriptions.TryGetValue(registryId, out var entries)
                && entries.TryGetValue(holderValue - 1, out string? value)
                && value.Length > 0
                ? value
                : null;
        }
    }
}
