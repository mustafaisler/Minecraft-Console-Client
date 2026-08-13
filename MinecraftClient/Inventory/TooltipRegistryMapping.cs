using System.Collections.Generic;

namespace MinecraftClient.Inventory;

public static class TooltipRegistryMapping
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<int, string>> Registries = new();

    public static bool IsSupported(string registryId) => registryId is
        "minecraft:trim_material" or
        "minecraft:trim_pattern" or
        "minecraft:mob_effect" or
        "minecraft:potion";

    public static void SetRegistry(string registryId, Dictionary<int, string> entries)
    {
        if (!IsSupported(registryId)) return;
        lock (Sync)
            Registries[registryId] = new Dictionary<int, string>(entries);
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
}
