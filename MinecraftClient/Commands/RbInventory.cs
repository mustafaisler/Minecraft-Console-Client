using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Brigadier.NET;
using Brigadier.NET.Builder;
using MinecraftClient.CommandHandler;
using MinecraftClient.Inventory;
using MinecraftClient.Protocol.Handlers.StructuredComponents.Components._1_20_6;
using MinecraftClient.Protocol.Handlers.StructuredComponents.Components._1_21_2;
using MinecraftClient.Protocol.Handlers.StructuredComponents.Components._1_21_5;
using MinecraftClient.Protocol.Handlers.StructuredComponents.Components.Subcomponents._1_20_6;
using MinecraftClient.Protocol.Message;

namespace MinecraftClient.Commands;

/// <summary>
/// Produces a bounded player-inventory snapshot and performs safe main-inventory
/// slot moves, bounded stack splitting and explicit ground drops for the
/// RakitBot web console.
/// </summary>
class RbInventory : Command
{
    public override string CmdName => "rbinventory";
    public override string CmdUsage => "/rbinventory <snapshot|move <source:5-45> <target:5-45> [actionId]|transfer <source:5-45> <target:5-45> <one|half> [actionId]|drop <source:5-45> <one|stack> [actionId]>";
    public override string CmdDesc => Translations.cmd_inventory_desc;

    private const int InventoryId = 0;
    private const int FirstActionSlot = 5;
    private const int LastActionSlot = 45;
    private const string OutputDirectory = "RakitBot_Inventory";
    private const string OutputFile = "snapshot.json";
    private const int WindowCloseDelayMs = 120;
    private const int ClickDelayMs = 90;
    private const int ServerCorrectionDelayMs = 240;
    private const int MaxEnchantmentsPerItem = 32;
    private const int MaxLoreLinesPerItem = 32;
    private const int MaxTooltipLinesPerItem = 48;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
    };

    public override void RegisterCommand(CommandDispatcher<CmdResult> dispatcher)
    {
        dispatcher.Register(l => l.Literal(CmdName)
            .Then(l => l.Literal("snapshot")
                .Executes(r => TakeSnapshot(r.Source)))
            .Then(l => l.Literal("move")
                .Then(l => l.Argument("source", Arguments.Integer(FirstActionSlot, LastActionSlot))
                    .Then(l => l.Argument("target", Arguments.Integer(FirstActionSlot, LastActionSlot))
                        .Executes(r => MoveItem(
                            r.Source,
                            Arguments.GetInteger(r, "source"),
                            Arguments.GetInteger(r, "target"),
                            actionId: 0))
                        .Then(l => l.Argument("actionId", Arguments.Integer(1, int.MaxValue))
                            .Executes(r => MoveItem(
                                r.Source,
                                Arguments.GetInteger(r, "source"),
                                Arguments.GetInteger(r, "target"),
                                Arguments.GetInteger(r, "actionId")))))))
            .Then(l => l.Literal("transfer")
                .Then(l => l.Argument("source", Arguments.Integer(FirstActionSlot, LastActionSlot))
                    .Then(l => l.Argument("target", Arguments.Integer(FirstActionSlot, LastActionSlot))
                        .Then(l => l.Literal("one")
                            .Executes(r => TransferItem(
                                r.Source,
                                Arguments.GetInteger(r, "source"),
                                Arguments.GetInteger(r, "target"),
                                half: false,
                                actionId: 0))
                            .Then(l => l.Argument("actionId", Arguments.Integer(1, int.MaxValue))
                                .Executes(r => TransferItem(
                                    r.Source,
                                    Arguments.GetInteger(r, "source"),
                                    Arguments.GetInteger(r, "target"),
                                    half: false,
                                    actionId: Arguments.GetInteger(r, "actionId")))))
                        .Then(l => l.Literal("half")
                            .Executes(r => TransferItem(
                                r.Source,
                                Arguments.GetInteger(r, "source"),
                                Arguments.GetInteger(r, "target"),
                                half: true,
                                actionId: 0))
                            .Then(l => l.Argument("actionId", Arguments.Integer(1, int.MaxValue))
                                .Executes(r => TransferItem(
                                    r.Source,
                                    Arguments.GetInteger(r, "source"),
                                    Arguments.GetInteger(r, "target"),
                                    half: true,
                                    actionId: Arguments.GetInteger(r, "actionId"))))))))
            .Then(l => l.Literal("drop")
                .Then(l => l.Argument("source", Arguments.Integer(FirstActionSlot, LastActionSlot))
                    .Then(l => l.Literal("one")
                        .Executes(r => DropItemFromSlot(
                            r.Source,
                            Arguments.GetInteger(r, "source"),
                            entireStack: false,
                            actionId: 0))
                        .Then(l => l.Argument("actionId", Arguments.Integer(1, int.MaxValue))
                            .Executes(r => DropItemFromSlot(
                                r.Source,
                                Arguments.GetInteger(r, "source"),
                                entireStack: false,
                                actionId: Arguments.GetInteger(r, "actionId")))))
                    .Then(l => l.Literal("stack")
                        .Executes(r => DropItemFromSlot(
                            r.Source,
                            Arguments.GetInteger(r, "source"),
                            entireStack: true,
                            actionId: 0))
                        .Then(l => l.Argument("actionId", Arguments.Integer(1, int.MaxValue))
                            .Executes(r => DropItemFromSlot(
                                r.Source,
                                Arguments.GetInteger(r, "source"),
                                entireStack: true,
                                actionId: Arguments.GetInteger(r, "actionId")))))))
        );
    }

    private static int TakeSnapshot(CmdResult result)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);

        try
        {
            WriteSnapshot(client);
            return result.SetAndReturn(CmdResult.Status.Done);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            return result.SetAndReturn(CmdResult.Status.Fail);
        }
    }

    private static int MoveItem(CmdResult result, int source, int target, int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);
        if (source == target)
            return FinishFailure(result, client, actionId, "same_slot");

        try
        {
            if (!CloseForegroundInventories(client))
                return FinishFailure(result, client, actionId, "inventory_close_failed");

            Container? inventory = client.GetInventory(InventoryId);
            if (
                inventory is null
                || HasCursorItem(inventory)
                || !inventory.Items.TryGetValue(source, out Item? beforeSource)
            )
                return FinishFailure(result, client, actionId, "source_empty");

            ItemType sourceType = beforeSource.Type;
            int sourceCount = beforeSource.Count;
            Dictionary<ItemType, int> before = CountActionItems(inventory);
            if (!ClickAndWait(client, source))
                return FinishFailure(result, client, actionId, "source_click_failed");
            if (!ClickAndWait(client, target))
            {
                ClickAndWait(client, source);
                return FinishFailure(result, client, actionId, "target_click_failed");
            }

            inventory = client.GetInventory(InventoryId);
            if (inventory is null)
                return FinishFailure(result, client, actionId, "inventory_missing");

            // Dolu hedefte Minecraft hedef yığınını imlece alır. Onu kaynak
            // slota bırakarak gerçek sürükle-bırak takasını tamamla.
            if (HasCursorItem(inventory))
            {
                if (!ClickAndWait(client, source))
                    return FinishFailure(result, client, actionId, "source_restore_failed");
                inventory = client.GetInventory(InventoryId);
            }

            Thread.Sleep(ServerCorrectionDelayMs);
            inventory = client.GetInventory(InventoryId);
            if (inventory is null || HasCursorItem(inventory))
                return FinishFailure(result, client, actionId, "cursor_not_empty");

            Dictionary<ItemType, int> after = CountActionItems(inventory);
            if (!SameCounts(before, after))
                return FinishFailure(result, client, actionId, "item_counts_changed");

            int sourceAfter = inventory.Items.TryGetValue(source, out Item? afterSource)
                && afterSource.Type == sourceType
                ? afterSource.Count
                : 0;
            int moved = sourceCount - sourceAfter;
            if (moved <= 0)
                return FinishFailure(result, client, actionId, "target_rejected");

            WriteSnapshot(client, actionId, actionOk: true, actionMoved: moved);
            return result.SetAndReturn(CmdResult.Status.Done);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            return FinishFailure(result, client, actionId, "exception");
        }
    }

    private static int TransferItem(
        CmdResult result,
        int source,
        int target,
        bool half,
        int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);
        if (source == target)
            return FinishFailure(result, client, actionId, "same_slot");

        try
        {
            if (!CloseForegroundInventories(client))
                return FinishFailure(result, client, actionId, "inventory_close_failed");

            Container? inventory = client.GetInventory(InventoryId);
            if (
                inventory is null
                || HasCursorItem(inventory)
                || !inventory.Items.TryGetValue(source, out Item? beforeSource)
                || beforeSource.Count <= 0
            )
                return FinishFailure(result, client, actionId, "source_empty");

            ItemType sourceType = beforeSource.Type;
            int sourceCount = beforeSource.Count;
            int requested = half ? Math.Max(1, (sourceCount + 1) / 2) : 1;
            int targetBefore = 0;
            if (inventory.Items.TryGetValue(target, out Item? beforeTarget))
            {
                if (beforeTarget.Type != sourceType)
                    return FinishFailure(result, client, actionId, "target_different_item");
                targetBefore = beforeTarget.Count;
            }

            // Tek esya tasimak tum yiginin tasinmasina denk geliyorsa normal
            // move akisini kullan; zırh ve ikinci el slotlari da buna dahildir.
            if (requested >= sourceCount)
                return MoveItem(result, source, target, actionId);

            Dictionary<ItemType, int> before = CountActionItems(inventory);
            if (!ClickAndWait(client, source))
                return FinishFailure(result, client, actionId, "source_click_failed");
            for (int i = 0; i < requested; i++)
            {
                if (!ClickAndWait(client, target, WindowActionType.RightClick))
                {
                    ClickAndWait(client, source);
                    return FinishFailure(result, client, actionId, "target_click_failed");
                }
            }
            if (!ClickAndWait(client, source))
                return FinishFailure(result, client, actionId, "source_restore_failed");

            Thread.Sleep(ServerCorrectionDelayMs);
            inventory = client.GetInventory(InventoryId);
            if (inventory is null || HasCursorItem(inventory))
                return FinishFailure(result, client, actionId, "cursor_not_empty");
            if (!SameCounts(before, CountActionItems(inventory)))
                return FinishFailure(result, client, actionId, "item_counts_changed");

            int targetAfter = inventory.Items.TryGetValue(target, out Item? afterTarget)
                && afterTarget.Type == sourceType
                ? afterTarget.Count
                : 0;
            int moved = targetAfter - targetBefore;
            if (moved <= 0)
                return FinishFailure(result, client, actionId, "target_rejected");

            WriteSnapshot(client, actionId, actionOk: true, actionMoved: moved);
            return result.SetAndReturn(CmdResult.Status.Done);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            return FinishFailure(result, client, actionId, "exception");
        }
    }

    private static bool CloseForegroundInventories(McClient client)
    {
        int[] openInventoryIds = client.InvokeOnMainThread(() => client.GetInventories()
            .Keys
            .Where(static inventoryId => inventoryId != InventoryId)
            .OrderByDescending(static inventoryId => inventoryId)
            .ToArray());

        foreach (int inventoryId in openInventoryIds)
        {
            if (!client.CloseInventory(inventoryId))
                return false;
        }

        if (openInventoryIds.Length > 0)
            Thread.Sleep(WindowCloseDelayMs);

        return client.InvokeOnMainThread(() => client.GetInventories()
            .Keys
            .All(static inventoryId => inventoryId == InventoryId));
    }

    private static int DropItemFromSlot(
        CmdResult result,
        int source,
        bool entireStack,
        int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);

        try
        {
            if (!CloseForegroundInventories(client))
                return FinishFailure(result, client, actionId, "inventory_close_failed");

            Container? inventory = client.GetInventory(InventoryId);
            if (
                inventory is null
                || HasCursorItem(inventory)
                || !inventory.Items.TryGetValue(source, out Item? beforeItem)
                || beforeItem.Count <= 0
            )
                return FinishFailure(result, client, actionId, "source_empty");

            ItemType itemType = beforeItem.Type;
            int beforeCount = beforeItem.Count;
            WindowActionType action = entireStack
                ? WindowActionType.DropItemStack
                : WindowActionType.DropItem;
            if (!client.DoWindowAction(InventoryId, source, action))
                return FinishFailure(result, client, actionId, "drop_rejected");

            Thread.Sleep(ClickDelayMs + ServerCorrectionDelayMs);
            inventory = client.GetInventory(InventoryId);
            if (inventory is null || HasCursorItem(inventory))
                return FinishFailure(result, client, actionId, "cursor_not_empty");

            int afterCount = inventory.Items.TryGetValue(source, out Item? afterItem)
                && afterItem.Type == itemType
                ? afterItem.Count
                : 0;
            int expectedCount = entireStack ? 0 : beforeCount - 1;
            if (afterCount != expectedCount)
                return FinishFailure(result, client, actionId, "drop_count_mismatch");

            WriteSnapshot(
                client,
                actionId,
                actionOk: true,
                actionMoved: beforeCount - afterCount);
            return result.SetAndReturn(CmdResult.Status.Done);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            return FinishFailure(result, client, actionId, "exception");
        }
    }

    private static bool ClickAndWait(
        McClient client,
        int slot,
        WindowActionType action = WindowActionType.LeftClick)
    {
        if (!client.DoWindowAction(InventoryId, slot, action))
            return false;
        Thread.Sleep(ClickDelayMs);
        return true;
    }

    private static bool HasCursorItem(Container inventory)
    {
        return inventory.Items.TryGetValue(-1, out Item? item) && item.Count > 0;
    }

    private static Dictionary<ItemType, int> CountActionItems(Container inventory)
    {
        return inventory.Items
            .Where(pair => pair.Key >= FirstActionSlot && pair.Key <= LastActionSlot && pair.Value.Count > 0)
            .GroupBy(pair => pair.Value.Type)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value.Count));
    }

    private static bool SameCounts(
        Dictionary<ItemType, int> before,
        Dictionary<ItemType, int> after)
    {
        return before.Count == after.Count
            && before.All(pair => after.TryGetValue(pair.Key, out int count) && count == pair.Value);
    }

    private static int FinishFailure(
        CmdResult result,
        McClient client,
        int actionId,
        string error)
    {
        try
        {
            WriteSnapshot(
                client,
                actionId,
                actionOk: false,
                actionMoved: 0,
                actionError: error);
        }
        catch
        {
            // Asil islem hatasini golgeleme.
        }
        return result.SetAndReturn(CmdResult.Status.Fail);
    }

    private static void WriteSnapshot(
        McClient client,
        int actionId = 0,
        bool actionOk = true,
        int actionMoved = 0,
        string actionError = "")
    {
        Container? inventory = client.GetInventory(InventoryId);
        if (inventory is null)
            throw new InvalidOperationException("player_inventory_missing");

        var slots = inventory.Items
            .Where(pair => pair.Key >= 0 && pair.Key < inventory.Type.SlotCount() && pair.Value.Count > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair =>
            {
                object[] enchantments = SnapshotEnchantments(pair.Value);
                return new
                {
                    slot = pair.Key,
                    type = pair.Value.Type.ToString(),
                    count = pair.Value.Count,
                    enchantments,
                    glint = SnapshotHasGlint(pair.Value),
                    tooltip = SnapshotTooltip(pair.Value),
                };
            })
            .ToArray();

        var snapshot = new
        {
            version = 1,
            generatedAt = DateTimeOffset.UtcNow.ToString("O"),
            inventoryId = InventoryId,
            inventoryType = inventory.Type.ToString(),
            title = CleanText(inventory.Title, 80),
            slotCount = inventory.Type.SlotCount(),
            selectedHotbar = client.GetCurrentSlot() + 1,
            actionId,
            actionOk,
            actionMoved,
            actionError = CleanText(actionError, 48),
            slots,
        };

        string outputDirectory = Path.Combine(AppContext.BaseDirectory, OutputDirectory);
        string outputPath = Path.Combine(outputDirectory, OutputFile);
        string temporaryPath = Path.Combine(outputDirectory, "snapshot.tmp");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(snapshot, s_jsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, outputPath, overwrite: true);
    }

    private static object[] SnapshotEnchantments(Item item)
    {
        List<object> output = new();
        var componentEnchantments = item.EnchantmentList;
        if (componentEnchantments is not null)
        {
            foreach (var enchantment in componentEnchantments.Take(MaxEnchantmentsPerItem))
            {
                string type = CleanEnchantmentType(enchantment.Type.ToString());
                if (type.Length == 0 || enchantment.Level <= 0)
                    continue;
                output.Add(new
                {
                    type,
                    name = CleanText(EnchantmentMapping.GetEnchantmentName(enchantment.Type), 80),
                    level = Math.Min(255, enchantment.Level),
                });
            }
            return output.ToArray();
        }

        var nbt = SnapshotNbt(item);
        if (nbt is null
            || (!nbt.TryGetValue("Enchantments", out object? raw)
                && !nbt.TryGetValue("StoredEnchantments", out raw))
            || raw is not object[] entries)
            return output.ToArray();

        foreach (var entry in entries.OfType<Dictionary<string, object>>().Take(MaxEnchantmentsPerItem))
        {
            if (!entry.TryGetValue("id", out object? idValue)
                || !entry.TryGetValue("lvl", out object? levelValue))
                continue;
            string type = CleanEnchantmentType(Convert.ToString(idValue) ?? string.Empty);
            int level;
            try { level = Convert.ToInt32(levelValue); }
            catch { continue; }
            if (type.Length == 0 || level <= 0)
                continue;
            output.Add(new { type, level = Math.Min(255, level) });
        }
        return output.ToArray();
    }

    private static object SnapshotTooltip(Item item)
    {
        string displayName = string.Empty;
        string[] lore = [];
        try
        {
            displayName = CleanText(item.DisplayName, 512);
            lore = (item.Lores ?? [])
                .Take(MaxLoreLinesPerItem)
                .Select(line => CleanText(line, 512))
                .Where(static line => line.Length > 0)
                .ToArray();
        }
        catch
        {
            // Bozuk tek bir eşya tüm snapshotı engellemez.
        }

        List<string> details = SnapshotDetailLines(item);
        List<string> advanced = SnapshotAdvancedLines(item);
        return new
        {
            displayName,
            rarity = SnapshotRarity(item),
            lore,
            details = details.Take(MaxTooltipLinesPerItem).ToArray(),
            advanced = advanced.Take(MaxTooltipLinesPerItem).ToArray(),
            hidden = SnapshotTooltipHidden(item),
        };
    }

    private static bool SnapshotHasGlint(Item item)
    {
        var overrideComponent = item.Components?
            .OfType<EnchantmentGlintOverrideComponent>()
            .FirstOrDefault();
        return overrideComponent?.HasGlint
            ?? (SnapshotHasAnyEnchantments(item)
                || item.Type is ItemType.EnchantedBook or ItemType.EnchantedGoldenApple);
    }

    private static bool SnapshotHasAnyEnchantments(Item item)
    {
        if (item.EnchantmentList?.Count > 0)
            return true;
        if (item.Components?.Any(component =>
                component.ComponentName is "minecraft:enchantments" or "minecraft:stored_enchantments") == true)
            return true;
        var nbt = SnapshotNbt(item);
        if (nbt is null)
            return false;
        return (nbt.TryGetValue("Enchantments", out object? raw)
                || nbt.TryGetValue("StoredEnchantments", out raw))
            && raw is object[] entries
            && entries.Length > 0;
    }

    private static string SnapshotRarity(Item item)
    {
        var rarity = item.Components?.OfType<RarityComponent>().FirstOrDefault();
        if (rarity is not null)
            return rarity.Rarity.ToString().ToLowerInvariant();
        return item.Type switch
        {
            ItemType.EnchantedGoldenApple or ItemType.DragonEgg => "epic",
            ItemType.EnchantedBook or ItemType.NetherStar or ItemType.Elytra => "uncommon",
            _ => "common",
        };
    }

    private static bool SnapshotTooltipHidden(Item item)
    {
        if (item.Components?.Any(component => component is HideTooltipComponent) == true)
            return true;
        return item.Components?.OfType<TooltipDisplayComponent>().Any(component => component.HideTooltip) == true;
    }

    private static List<string> SnapshotDetailLines(Item item)
    {
        List<string> lines = [];
        var nbt = SnapshotNbt(item);
        int hideFlags = NbtInt(nbt, "HideFlags") ?? 0;
        bool hideAdditional = item.Components?.Any(component => component is HideAdditionalTooltipComponent) == true;
        if ((hideFlags & 4) == 0 && SnapshotUnbreakable(item))
            lines.Add("§9Kırılmaz");

        bool hasCustomAttributes = nbt?.ContainsKey("AttributeModifiers") == true
            || item.Components?.Any(component => component.ComponentName == "minecraft:attribute_modifiers") == true;
        if ((hideFlags & 2) == 0 && !hasCustomAttributes)
            AppendDefaultAttributes(lines, item.Type.ToString());

        if ((hideFlags & 64) == 0
            && !hideAdditional
            && ComponentTooltipVisible(item, "minecraft:dyed_color")
            && TryReadColor(item, out string? color))
            lines.Add("§7Renk: §f" + color);

        if ((hideFlags & 8) == 0)
            AppendStringList(lines, nbt, "CanDestroy", "§7Şunları kırabilir:");
        if ((hideFlags & 16) == 0)
            AppendStringList(lines, nbt, "CanPlaceOn", "§7Şunların üzerine yerleştirilebilir:");

        if ((hideFlags & 32) == 0
            && !hideAdditional
            && ComponentTooltipVisible(item, "minecraft:potion_contents"))
            AppendPotionEffects(lines, item);
        if ((hideFlags & 128) == 0
            && !hideAdditional
            && ComponentTooltipVisible(item, "minecraft:trim"))
            AppendTrim(lines, item);
        return lines;
    }

    private static bool ComponentTooltipVisible(Item item, string componentName)
    {
        if (item.Components is null)
            return true;
        var component = item.Components.FirstOrDefault(candidate => candidate.ComponentName == componentName);
        if (component is null)
            return true;
        var display = item.Components.OfType<TooltipDisplayComponent>().FirstOrDefault();
        return display is null || !display.HiddenComponentIds.Contains(component.TypeId);
    }

    private static void AppendDefaultAttributes(List<string> lines, string type)
    {
        Dictionary<string, (double sword, double pickaxe, double axe, double shovel, double speed)> tools = new()
        {
            ["Wooden"] = (4, 2, 7, 2.5, 0),
            ["Stone"] = (5, 3, 9, 3.5, 0),
            ["Iron"] = (6, 4, 9, 4.5, 0),
            ["Golden"] = (4, 2, 7, 2.5, 0),
            ["Diamond"] = (7, 5, 9, 5.5, 0),
            ["Netherite"] = (8, 6, 10, 6.5, 0),
        };
        foreach (var pair in tools)
        {
            double damage;
            double speed;
            if (type == pair.Key + "Sword") { damage = pair.Value.sword; speed = 1.6; }
            else if (type == pair.Key + "Pickaxe") { damage = pair.Value.pickaxe; speed = 1.2; }
            else if (type == pair.Key + "Axe") { damage = pair.Value.axe; speed = pair.Key switch { "Wooden" or "Stone" => 0.8, "Iron" => 0.9, _ => 1.0 }; }
            else if (type == pair.Key + "Shovel") { damage = pair.Value.shovel; speed = 1.0; }
            else if (type == pair.Key + "Hoe") { damage = 1; speed = pair.Key switch { "Stone" => 2, "Iron" => 3, "Diamond" or "Netherite" => 4, _ => 1 }; }
            else continue;
            lines.Add("§7Ana eldeyken:");
            lines.Add("§9 " + damage.ToString("0.#") + " Saldırı Hasarı");
            lines.Add("§9 " + speed.ToString("0.#") + " Saldırı Hızı");
            return;
        }

        Dictionary<string, int[]> armor = new()
        {
            ["Leather"] = [1, 3, 2, 1],
            ["Chainmail"] = [2, 5, 4, 1],
            ["Iron"] = [2, 6, 5, 2],
            ["Golden"] = [2, 5, 3, 1],
            ["Diamond"] = [3, 8, 6, 3],
            ["Netherite"] = [3, 8, 6, 3],
        };
        string[] suffixes = ["Helmet", "Chestplate", "Leggings", "Boots"];
        string[] headings = ["Baştayken:", "Gövde üzerindeyken:", "Bacaklardayken:", "Ayaklardayken:"];
        foreach (var pair in armor)
        {
            int index = Array.FindIndex(suffixes, suffix => type == pair.Key + suffix);
            if (index < 0) continue;
            lines.Add("§7" + headings[index]);
            lines.Add("§9 +" + pair.Value[index] + " Zırh");
            if (pair.Key == "Diamond") lines.Add("§9 +2 Zırh Sertliği");
            if (pair.Key == "Netherite")
            {
                lines.Add("§9 +3 Zırh Sertliği");
                lines.Add("§9 +1 Savrulma Direnci");
            }
            return;
        }
        if (type == "TurtleHelmet")
        {
            lines.Add("§7Baştayken:");
            lines.Add("§9 +2 Zırh");
        }
    }

    private static List<string> SnapshotAdvancedLines(Item item)
    {
        List<string> lines = [];
        int damage = Math.Max(0, item.Damage);
        int maxDamage = SnapshotMaxDamage(item);
        if (maxDamage > 0)
            lines.Add($"§fDayanıklılık: {Math.Max(0, maxDamage - damage)} / {maxDamage}");

        lines.Add("§8minecraft:" + item.Type.ToString().ToUnderscoreCase());
        var nbt = SnapshotNbt(item);
        if (nbt?.Count > 0)
            lines.Add($"§8NBT: {nbt.Count} etiket");
        if (item.Components?.Count > 0)
            lines.Add($"§8{item.Components.Count} bileşen");

        int? customModelData = NbtInt(nbt, "CustomModelData")
            ?? item.Components?.OfType<CustomModelDataComponent1206>().FirstOrDefault()?.Value;
        if (customModelData.HasValue)
            lines.Add("§8Özel Model Verisi: " + customModelData.Value);
        return lines;
    }

    private static bool SnapshotUnbreakable(Item item)
    {
        if (item.Components?.OfType<UnbreakableComponent1206>().Any(component => component.Unbreakable) == true)
            return true;
        if (item.Components?.Any(component => component.ComponentName == "minecraft:unbreakable") == true)
            return true;
        return NbtInt(SnapshotNbt(item), "Unbreakable") is int value && value != 0;
    }

    private static int SnapshotMaxDamage(Item item)
    {
        int? componentValue = item.Components?.OfType<MaxDamageComponent>().FirstOrDefault()?.MaxDamage;
        if (componentValue > 0) return componentValue.Value;
        string type = item.Type.ToString();
        if (type == "Elytra") return 432;
        if (type == "Shield") return 336;
        if (type == "Bow") return 384;
        if (type == "Crossbow") return 465;
        if (type == "Trident") return 250;
        if (type == "FishingRod") return 64;
        if (type == "FlintAndSteel") return 64;
        if (type == "Shears") return 238;
        if (type == "Brush") return 64;

        Dictionary<string, int[]> armor = new()
        {
            ["Leather"] = [55, 80, 75, 65],
            ["Chainmail"] = [165, 240, 225, 195],
            ["Iron"] = [165, 240, 225, 195],
            ["Golden"] = [77, 112, 105, 91],
            ["Diamond"] = [363, 528, 495, 429],
            ["Netherite"] = [407, 592, 555, 481],
        };
        string[] armorSuffixes = ["Helmet", "Chestplate", "Leggings", "Boots"];
        foreach (var pair in armor)
            for (int index = 0; index < armorSuffixes.Length; index++)
                if (type == pair.Key + armorSuffixes[index]) return pair.Value[index];
        if (type == "TurtleHelmet") return 275;

        Dictionary<string, int> tools = new()
        {
            ["Wooden"] = 59,
            ["Stone"] = 131,
            ["Iron"] = 250,
            ["Golden"] = 32,
            ["Diamond"] = 1561,
            ["Netherite"] = 2031,
        };
        string[] toolSuffixes = ["Sword", "Pickaxe", "Axe", "Shovel", "Hoe"];
        foreach (var pair in tools)
            if (toolSuffixes.Any(suffix => type == pair.Key + suffix)) return pair.Value;
        return 0;
    }

    private static bool TryReadColor(Item item, out string? color)
    {
        color = null;
        var component1206 = item.Components?.OfType<DyeColorComponent>().FirstOrDefault();
        if (component1206 is not null && component1206.ShowInTooltip)
        {
            color = "#" + (component1206.Color & 0xFFFFFF).ToString("X6");
            return true;
        }
        var component1215 = item.Components?.OfType<DyeColorComponent1215>().FirstOrDefault();
        if (component1215 is not null)
        {
            color = "#" + (component1215.Color & 0xFFFFFF).ToString("X6");
            return true;
        }
        var nbt = SnapshotNbt(item);
        if (nbt is null || !nbt.TryGetValue("display", out object? rawDisplay)
            || rawDisplay is not Dictionary<string, object> display)
            return false;
        int? value = NbtInt(display, "color");
        if (!value.HasValue) return false;
        color = "#" + (value.Value & 0xFFFFFF).ToString("X6");
        return true;
    }

    private static void AppendStringList(
        List<string> lines,
        Dictionary<string, object>? nbt,
        string key,
        string heading)
    {
        if (nbt is null || !nbt.TryGetValue(key, out object? raw) || raw is not object[] values || values.Length == 0)
            return;
        lines.Add(heading);
        foreach (string value in values.OfType<string>().Take(32))
            lines.Add("§8" + CleanText(value, 160));
    }

    private static void AppendPotionEffects(List<string> lines, Item item)
    {
        IEnumerable<PotionEffectSubComponent>? componentEffects = item.Components?
            .OfType<PotionContentsComponent>()
            .FirstOrDefault()?.Effects;
        componentEffects ??= item.Components?
            .OfType<PotionContentsComponent1212>()
            .FirstOrDefault()?.Effects;
        if (componentEffects?.Any() == true)
        {
            foreach (var effect in componentEffects.Take(16))
                AppendPotionEffectLine(lines, effect.TypeId, effect.Details.Amplifier, effect.Details.Duration);
            return;
        }

        var nbt = SnapshotNbt(item);
        if (nbt is null
            || !nbt.TryGetValue("CustomPotionEffects", out object? raw)
            || raw is not object[] effects)
            return;
        foreach (var effect in effects.OfType<Dictionary<string, object>>().Take(16))
            AppendPotionEffectLine(
                lines,
                NbtInt(effect, "Id") ?? 0,
                NbtInt(effect, "Amplifier") ?? 0,
                NbtInt(effect, "Duration") ?? 0);
    }

    private static void AppendPotionEffectLine(List<string> lines, int id, int amplifier, int duration)
    {
        string name;
        try { name = new EffectData((Effects)id, amplifier, duration, 0).GetDisplayName(); }
        catch { name = "Etki " + id; }
        string time = duration > 0 ? $" ({duration / 20 / 60}:{duration / 20 % 60:00})" : string.Empty;
        bool harmful = id is 2 or 4 or 7 or 9 or 15 or 17 or 18 or 19 or 20 or 27 or 31;
        lines.Add((harmful ? "§c" : "§9") + CleanText(name, 120) + time);
    }

    private static void AppendTrim(List<string> lines, Item item)
    {
        var component1206 = item.Components?.OfType<TrimComponent>().FirstOrDefault();
        if (component1206 is not null)
        {
            if (!component1206.ShowInTooltip)
                return;
            string componentMaterial = component1206.TrimMaterialType == 0
                ? component1206.Description
                : TooltipRegistryMapping.GetHolderDescription(
                    "minecraft:trim_material", component1206.TrimMaterialType)
                    ?? TranslateTrim("trim_material", TooltipRegistryMapping.GetHolderName(
                    "minecraft:trim_material", component1206.TrimMaterialType)
                    ?? TrimMaterialName(component1206.TrimMaterialType));
            string componentPattern = component1206.TrimPatternType == 0
                ? component1206.TrimPatternTypeDescription
                : TooltipRegistryMapping.GetHolderDescription(
                    "minecraft:trim_pattern", component1206.TrimPatternType)
                    ?? TranslateTrim("trim_pattern", TooltipRegistryMapping.GetHolderName(
                    "minecraft:trim_pattern", component1206.TrimPatternType)
                    ?? TrimPatternName(component1206.TrimPatternType));
            AppendTrimLines(lines, componentPattern, componentMaterial);
            return;
        }

        var component1215 = item.Components?.OfType<TrimComponent1215>().FirstOrDefault();
        if (component1215 is not null)
        {
            string componentMaterial = component1215.MaterialHolderValue == 0
                ? component1215.DirectMaterial?.Description ?? string.Empty
                : TooltipRegistryMapping.GetHolderDescription(
                    "minecraft:trim_material", component1215.MaterialHolderValue)
                    ?? TranslateTrim("trim_material", TooltipRegistryMapping.GetHolderName(
                    "minecraft:trim_material", component1215.MaterialHolderValue)
                    ?? TrimMaterialName(component1215.MaterialHolderValue));
            string componentPattern = component1215.PatternHolderValue == 0
                ? component1215.DirectPattern?.Description ?? string.Empty
                : TooltipRegistryMapping.GetHolderDescription(
                    "minecraft:trim_pattern", component1215.PatternHolderValue)
                    ?? TranslateTrim("trim_pattern", TooltipRegistryMapping.GetHolderName(
                    "minecraft:trim_pattern", component1215.PatternHolderValue)
                    ?? TrimPatternName(component1215.PatternHolderValue));
            AppendTrimLines(lines, componentPattern, componentMaterial);
            return;
        }

        var nbt = SnapshotNbt(item);
        if (nbt is not null
            && nbt.TryGetValue("Trim", out object? raw)
            && raw is Dictionary<string, object> trim)
        {
            string material = trim.TryGetValue("material", out object? materialValue)
                ? CleanText(Convert.ToString(materialValue), 80) : string.Empty;
            string pattern = trim.TryGetValue("pattern", out object? patternValue)
                ? CleanText(Convert.ToString(patternValue), 80) : string.Empty;
            pattern = TranslateTrim("trim_pattern", pattern.Split(':').Last());
            material = TranslateTrim("trim_material", material.Split(':').Last());
            AppendTrimLines(lines, pattern, material);
        }
    }

    private static void AppendTrimLines(List<string> lines, string pattern, string material)
    {
        if (pattern.Length == 0 && material.Length == 0) return;
        string heading = ChatParser.TranslateString("item.minecraft.smithing_template.upgrade") ?? "Zırh Süslemesi:";
        lines.Add("§7" + CleanText(heading, 120));
        if (pattern.Length > 0) lines.Add(HasFormatting(pattern) ? pattern : "§9 " + pattern);
        if (material.Length > 0) lines.Add(HasFormatting(material) ? material : "§9 " + material);
    }

    private static string TranslateTrim(string prefix, string resourceName)
    {
        if (resourceName.Length == 0) return string.Empty;
        return CleanText(ChatParser.TranslateString(prefix + ".minecraft." + resourceName) ?? resourceName, 120);
    }

    private static string TrimMaterialName(int holderValue)
    {
        string[] names = ["quartz", "iron", "netherite", "redstone", "copper", "gold", "emerald", "diamond", "lapis", "amethyst", "resin"];
        int index = holderValue - 1;
        return index >= 0 && index < names.Length ? names[index] : "malzeme_" + holderValue;
    }

    private static string TrimPatternName(int holderValue)
    {
        string[] names = ["sentry", "dune", "coast", "wild", "ward", "eye", "vex", "tide", "snout", "rib", "spire", "wayfinder", "shaper", "silence", "raiser", "host", "flow", "bolt"];
        int index = holderValue - 1;
        return index >= 0 && index < names.Length ? names[index] : "desen_" + holderValue;
    }

    private static bool HasFormatting(string value)
    {
        return value.IndexOf('§') >= 0;
    }

    private static Dictionary<string, object>? SnapshotNbt(Item item)
    {
        var customData = item.Components?.OfType<CustomDataComponent>().FirstOrDefault()?.Nbt;
        if (item.NBT is null || item.NBT.Count == 0)
            return customData;
        if (customData is null || customData.Count == 0)
            return item.NBT;
        Dictionary<string, object> merged = new(item.NBT);
        foreach (var pair in customData)
            merged.TryAdd(pair.Key, pair.Value);
        return merged;
    }

    private static int? NbtInt(Dictionary<string, object>? nbt, string key)
    {
        if (nbt is null || !nbt.TryGetValue(key, out object? value) || value is null) return null;
        try { return Convert.ToInt32(value); }
        catch { return null; }
    }

    private static string CleanEnchantmentType(string value)
    {
        string resourceName = value.Split(':').LastOrDefault() ?? string.Empty;
        return new string(resourceName
            .Where(character => character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_')
            .Take(80)
            .ToArray());
    }

    private static string CleanText(string? value, int maxLength)
    {
        string clean = new((value ?? string.Empty)
            .Select(static character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        clean = clean.Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }
}
