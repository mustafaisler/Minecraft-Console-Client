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
            .Select(pair => new
            {
                slot = pair.Key,
                type = pair.Value.Type.ToString(),
                count = pair.Value.Count,
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

    private static string CleanText(string? value, int maxLength)
    {
        string clean = new((value ?? string.Empty)
            .Select(static character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        clean = clean.Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }
}
