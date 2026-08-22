using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MinecraftClient.Commands;
using MinecraftClient.Inventory;

namespace MinecraftClient;

/// <summary>
/// Açık Minecraft container ekranını RakitBot paneli için sınırlı, atomik bir
/// snapshot'a dönüştürür. Ekran anahtarı ve revision alanları eski tarayıcı
/// durumundan gelen tıklamaların yeni/reuse edilmiş pencereye uygulanmasını önler.
/// </summary>
internal sealed class RbScreenEmitter
{
    private const string OutputDirectory = "RakitBot_Screen";
    private const string OutputFile = "snapshot.json";
    private const long FlushDelayMs = 50;
    private readonly Lock sync = new();
    private int activeInventoryId;
    private int screenToken;
    private int revision;
    private bool dirty;
    private long flushAfter;
    private int actionId;
    private bool actionOk = true;
    private string actionError = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public void Open(int inventoryId)
    {
        if (inventoryId <= 0)
            return;
        using (sync.EnterScope())
        {
            activeInventoryId = inventoryId;
            screenToken = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            revision = 0;
            actionId = 0;
            actionOk = true;
            actionError = string.Empty;
            dirty = true;
            flushAfter = Environment.TickCount64 + FlushDelayMs;
        }
    }

    public void Update(int inventoryId)
    {
        if (inventoryId <= 0)
            return;
        using (sync.EnterScope())
        {
            if (inventoryId != activeInventoryId)
                return;
            dirty = true;
            flushAfter = Math.Min(flushAfter, Environment.TickCount64 + FlushDelayMs);
        }
    }

    public void Close(int inventoryId)
    {
        int token;
        int closedRevision;
        using (sync.EnterScope())
        {
            if (activeInventoryId <= 0 || (inventoryId > 0 && inventoryId != activeInventoryId))
                return;
            token = screenToken;
            closedRevision = ++revision;
            activeInventoryId = 0;
            dirty = false;
            actionId = 0;
            actionOk = true;
            actionError = string.Empty;
        }
        TryWriteClosed(token, closedRevision);
        TryEmit("close", token, closedRevision, 0);
    }

    public void Snapshot(McClient client)
    {
        client.InvokeOnMainThread(() =>
        {
            int foregroundId = client.GetInventories().Keys.Where(static id => id > 0).DefaultIfEmpty(0).Max();
            bool closeActive = false;
            using (sync.EnterScope())
            {
                if (foregroundId <= 0)
                {
                    closeActive = activeInventoryId > 0;
                }
                else if (activeInventoryId != foregroundId)
                {
                    activeInventoryId = foregroundId;
                    screenToken = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                    revision = 0;
                }
                dirty = true;
                flushAfter = 0;
            }
            if (foregroundId <= 0)
            {
                if (closeActive)
                    Close(0);
                else
                    TryWriteClosed(0, 0);
                return;
            }
            Flush(client, force: true);
        });
    }

    public (bool Ok, string Error) Click(
        McClient client,
        int expectedToken,
        int expectedRevision,
        int expectedInventoryId,
        int slot,
        WindowActionType action,
        int requestedActionId)
    {
        return client.InvokeOnMainThread(() =>
        {
            Container? inventory = client.GetInventory(expectedInventoryId);
            string error = string.Empty;
            using (sync.EnterScope())
            {
                if (activeInventoryId <= 0 || inventory is null)
                    error = "screen_closed";
                else if (expectedToken != screenToken || expectedInventoryId != activeInventoryId)
                    error = "screen_replaced";
                else if (expectedRevision != revision)
                    error = "screen_stale";
                else if (slot < 0 || slot >= inventory.Type.SlotCount())
                    error = "slot_invalid";
            }
            if (error.Length > 0)
            {
                RecordAction(requestedActionId, false, error);
                Flush(client, force: true);
                return (false, error);
            }

            bool sent;
            try { sent = client.DoWindowAction(expectedInventoryId, slot, action); }
            catch { sent = false; }
            RecordAction(requestedActionId, sent, sent ? string.Empty : "click_rejected");
            Update(expectedInventoryId);
            Flush(client, force: true);
            return sent ? (true, string.Empty) : (false, "click_rejected");
        });
    }

    public void Flush(McClient client, bool force = false)
    {
        int inventoryId;
        int token;
        int nextRevision;
        int currentActionId;
        bool currentActionOk;
        string currentActionError;
        using (sync.EnterScope())
        {
            if (!dirty || activeInventoryId <= 0 || (!force && Environment.TickCount64 < flushAfter))
                return;
            inventoryId = activeInventoryId;
            token = screenToken;
            nextRevision = revision + 1;
            currentActionId = actionId;
            currentActionOk = actionOk;
            currentActionError = actionError;
        }

        Container? inventory = client.GetInventory(inventoryId);
        if (inventory is null)
        {
            Close(inventoryId);
            return;
        }

        try
        {
            WriteOpen(client, inventory, token, nextRevision,
                currentActionId, currentActionOk, currentActionError);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            return;
        }

        using (sync.EnterScope())
        {
            if (activeInventoryId != inventoryId || screenToken != token)
                return;
            revision = nextRevision;
            dirty = false;
        }
        TryEmit(nextRevision == 1 ? "open" : "update", token, nextRevision, inventoryId);
    }

    private void RecordAction(int requestedActionId, bool ok, string error)
    {
        using (sync.EnterScope())
        {
            actionId = requestedActionId;
            actionOk = ok;
            actionError = error;
            dirty = true;
            flushAfter = 0;
        }
    }

    private static object SnapshotItem(Item item, int? slot = null)
    {
        object[] enchantments = RbInventory.SnapshotEnchantments(item);
        var value = new Dictionary<string, object?>
        {
            ["type"] = item.Type.ToString(),
            ["count"] = item.Count,
            ["enchantments"] = enchantments,
            ["glint"] = RbInventory.SnapshotHasGlint(item),
            ["tooltip"] = RbInventory.SnapshotTooltip(item),
        };
        if (slot.HasValue)
            value["slot"] = slot.Value;
        return value;
    }

    private static void WriteOpen(
        McClient client,
        Container inventory,
        int token,
        int revision,
        int actionId,
        bool actionOk,
        string actionError)
    {
        int slotCount = inventory.Type.SlotCount();
        var slots = inventory.Items
            .Where(pair => pair.Key >= 0 && pair.Key < slotCount && pair.Value.Count > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => SnapshotItem(pair.Value, pair.Key))
            .ToArray();
        Item? cursor = null;
        Container? playerInventory = client.GetInventory(0);
        if (playerInventory is not null
            && playerInventory.Items.TryGetValue(-1, out Item? cursorItem)
            && cursorItem.Count > 0)
            cursor = cursorItem;
        var properties = inventory.Properties
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
        var snapshot = new
        {
            version = 1,
            generatedAt = DateTimeOffset.UtcNow.ToString("O"),
            open = true,
            token,
            revision,
            inventoryId = inventory.ID,
            inventoryType = inventory.Type.ToString(),
            title = RbInventory.CleanText(inventory.Title, 160),
            slotCount,
            containerSlotCount = Math.Max(0, slotCount - 36),
            stateId = inventory.StateID,
            properties,
            actionId,
            actionOk,
            actionError = RbInventory.CleanText(actionError, 48),
            cursor = cursor is null ? null : SnapshotItem(cursor),
            slots,
        };
        Write(snapshot);
    }

    private static void WriteClosed(int token, int revision) => Write(new
    {
        version = 1,
        generatedAt = DateTimeOffset.UtcNow.ToString("O"),
        open = false,
        token,
        revision,
    });

    private static void TryWriteClosed(int token, int revision)
    {
        try { WriteClosed(token, revision); }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        { }
    }

    private static void Write(object snapshot)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, OutputDirectory);
        string outputPath = Path.Combine(directory, OutputFile);
        string temporaryPath = Path.Combine(directory, "snapshot.tmp");
        Directory.CreateDirectory(directory);
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, outputPath, overwrite: true);
    }

    private static void TryEmit(string type, int token, int revision, int inventoryId)
    {
        try { ConsoleIO.WriteLine("[RBSCREEN]" + JsonSerializer.Serialize(new
        {
            v = 1,
            type,
            token,
            revision,
            inventoryId,
            at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        })); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
    }
}
