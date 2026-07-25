using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Brigadier.NET;
using Brigadier.NET.Builder;
using MinecraftClient.CommandHandler;
using MinecraftClient.Inventory;

namespace MinecraftClient.Commands;

/// <summary>
/// Produces a bounded player-inventory snapshot and performs safe main-inventory
/// slot moves for the RakitBot web console.
/// </summary>
class RbInventory : Command
{
    public override string CmdName => "rbinventory";
    public override string CmdUsage => "/rbinventory <snapshot|move <source:9-44> <target:9-44>>";
    public override string CmdDesc => Translations.cmd_inventory_desc;

    private const int InventoryId = 0;
    private const int FirstMovableSlot = 9;
    private const int LastMovableSlot = 44;
    private const string OutputDirectory = "RakitBot_Inventory";
    private const string OutputFile = "snapshot.json";

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
                .Then(l => l.Argument("source", Arguments.Integer(FirstMovableSlot, LastMovableSlot))
                    .Then(l => l.Argument("target", Arguments.Integer(FirstMovableSlot, LastMovableSlot))
                        .Executes(r => MoveItem(
                            r.Source,
                            Arguments.GetInteger(r, "source"),
                            Arguments.GetInteger(r, "target"))))))
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

    private static int MoveItem(CmdResult result, int source, int target)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);
        if (source == target)
            return result.SetAndReturn(CmdResult.Status.Fail);

        try
        {
            Container? inventory = client.GetInventory(InventoryId);
            if (inventory is null || HasCursorItem(inventory) || !inventory.Items.ContainsKey(source))
                return result.SetAndReturn(CmdResult.Status.Fail);

            Dictionary<ItemType, int> before = CountMovableItems(inventory);
            if (!client.DoWindowAction(InventoryId, source, WindowActionType.LeftClick))
                return result.SetAndReturn(CmdResult.Status.Fail);
            if (!client.DoWindowAction(InventoryId, target, WindowActionType.LeftClick))
            {
                client.DoWindowAction(InventoryId, source, WindowActionType.LeftClick);
                return result.SetAndReturn(CmdResult.Status.Fail);
            }

            inventory = client.GetInventory(InventoryId);
            if (inventory is null)
                return result.SetAndReturn(CmdResult.Status.Fail);

            // Dolu hedefte Minecraft hedef yığınını imlece alır. Onu kaynak
            // slota bırakarak gerçek sürükle-bırak takasını tamamla.
            if (HasCursorItem(inventory))
            {
                if (!client.DoWindowAction(InventoryId, source, WindowActionType.LeftClick))
                    return result.SetAndReturn(CmdResult.Status.Fail);
                inventory = client.GetInventory(InventoryId);
            }

            if (inventory is null || HasCursorItem(inventory))
                return result.SetAndReturn(CmdResult.Status.Fail);

            Dictionary<ItemType, int> after = CountMovableItems(inventory);
            if (!SameCounts(before, after))
                return result.SetAndReturn(CmdResult.Status.Fail);

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

    private static bool HasCursorItem(Container inventory)
    {
        return inventory.Items.TryGetValue(-1, out Item? item) && item.Count > 0;
    }

    private static Dictionary<ItemType, int> CountMovableItems(Container inventory)
    {
        return inventory.Items
            .Where(pair => pair.Key >= FirstMovableSlot && pair.Key <= LastMovableSlot && pair.Value.Count > 0)
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

    private static void WriteSnapshot(McClient client)
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
