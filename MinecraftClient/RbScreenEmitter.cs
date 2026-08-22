using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MinecraftClient.Commands;
using MinecraftClient.Dialogs;
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
    private const string ContainerKind = "container";
    private const string DialogKind = "dialog";
    private const long FlushDelayMs = 50;
    private readonly Lock sync = new();
    private DialogManager? attachedDialogManager;
    private string activeKind = string.Empty;
    private int activeInventoryId;
    private int activeDialogRevision;
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
            activeKind = ContainerKind;
            activeInventoryId = inventoryId;
            activeDialogRevision = 0;
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
            if (activeKind != ContainerKind || inventoryId != activeInventoryId)
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
            if (activeKind != ContainerKind
                || activeInventoryId <= 0
                || (inventoryId > 0 && inventoryId != activeInventoryId))
                return;
            token = screenToken;
            closedRevision = ++revision;
            activeInventoryId = 0;
            activeKind = string.Empty;
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
        EnsureDialogHooks(client);
        client.InvokeOnMainThread(() =>
        {
            DialogInstance? dialog = client.Dialogs.Current;
            if (dialog is not null)
            {
                bool sameDialog;
                using (sync.EnterScope())
                    sameDialog = activeKind == DialogKind && activeDialogRevision == dialog.Revision;
                if (sameDialog)
                    RefreshDialog(dialog, 0, true, string.Empty);
                else
                    OpenDialog(dialog);
                return;
            }
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
                    activeKind = ContainerKind;
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
                if (activeKind != ContainerKind || activeInventoryId <= 0 || inventory is null)
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
        EnsureDialogHooks(client);
        int inventoryId;
        int token;
        int nextRevision;
        int currentActionId;
        bool currentActionOk;
        string currentActionError;
        using (sync.EnterScope())
        {
            if (activeKind != ContainerKind
                || !dirty
                || activeInventoryId <= 0
                || (!force && Environment.TickCount64 < flushAfter))
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
            if (activeKind != ContainerKind || activeInventoryId != inventoryId || screenToken != token)
                return;
            revision = nextRevision;
            dirty = false;
        }
        TryEmit(nextRevision == 1 ? "open" : "update", token, nextRevision, inventoryId);
    }

    private void EnsureDialogHooks(McClient client)
    {
        DialogManager manager = client.Dialogs;
        using (sync.EnterScope())
        {
            if (ReferenceEquals(attachedDialogManager, manager))
                return;
            attachedDialogManager = manager;
            manager.DialogShown += OpenDialog;
            manager.DialogCleared += CloseDialog;
        }
    }

    public void OpenDialog(DialogInstance dialog)
    {
        int token;
        using (sync.EnterScope())
        {
            activeKind = DialogKind;
            activeInventoryId = 0;
            activeDialogRevision = dialog.Revision;
            screenToken = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            revision = 1;
            actionId = 0;
            actionOk = true;
            actionError = string.Empty;
            dirty = false;
            token = screenToken;
        }
        TryWriteDialog(dialog, token, 1, 0, true, string.Empty);
        TryEmit("open", token, 1, 0, DialogKind);
    }

    public void CloseDialog(int dialogRevision)
    {
        int token;
        int closedRevision;
        using (sync.EnterScope())
        {
            if (activeKind != DialogKind
                || (dialogRevision > 0 && activeDialogRevision != dialogRevision))
                return;
            token = screenToken;
            closedRevision = ++revision;
            activeKind = string.Empty;
            activeDialogRevision = 0;
            actionId = 0;
            actionOk = true;
            actionError = string.Empty;
        }
        TryWriteClosed(token, closedRevision);
        TryEmit("close", token, closedRevision, 0, DialogKind);
    }

    public (bool Ok, string Error) DialogAction(
        McClient client,
        int expectedToken,
        int expectedRevision,
        string operation,
        int index,
        string encodedValue,
        int requestedActionId)
    {
        return client.InvokeOnMainThread(() =>
        {
            DialogInstance? before = client.Dialogs.Current;
            string validationError = string.Empty;
            using (sync.EnterScope())
            {
                if (activeKind != DialogKind || before is null)
                    validationError = "screen_closed";
                else if (expectedToken != screenToken || before.Revision != activeDialogRevision)
                    validationError = "screen_replaced";
                else if (expectedRevision != revision)
                    validationError = "screen_stale";
            }
            if (validationError.Length > 0)
            {
                if (before is not null)
                    RefreshDialog(before, requestedActionId, false, validationError);
                return (false, validationError);
            }

            DialogActionResult outcome;
            try
            {
                outcome = operation switch
                {
                    "set" => SetDialogInput(client.Dialogs, before!, index, encodedValue),
                    "click" => client.Dialogs.Click(index),
                    "cancel" => client.Dialogs.Cancel(),
                    "dismiss" => client.Dialogs.Dismiss(),
                    _ => new DialogActionResult(false, string.Empty),
                };
            }
            catch (Exception exception) when (
                exception is FormatException
                or ArgumentException
                or InvalidOperationException)
            {
                outcome = new DialogActionResult(false, string.Empty);
            }

            DialogInstance? after = client.Dialogs.Current;
            string error = outcome.Success ? string.Empty : "dialog_rejected";
            if (after is not null)
            {
                bool currentEmitterDialog;
                using (sync.EnterScope())
                    currentEmitterDialog = activeKind == DialogKind && activeDialogRevision == after.Revision;
                if (!currentEmitterDialog)
                    OpenDialog(after);
                RefreshDialog(after, requestedActionId, outcome.Success, error);
            }
            else
            {
                int token;
                int closedRevision;
                using (sync.EnterScope())
                {
                    token = screenToken == 0 ? expectedToken : screenToken;
                    closedRevision = Math.Max(revision + 1, expectedRevision + 1);
                    activeKind = string.Empty;
                    activeDialogRevision = 0;
                    revision = closedRevision;
                }
                TryWriteClosed(token, closedRevision);
                TryEmit("close", token, closedRevision, 0, DialogKind);
            }
            return outcome.Success ? (true, string.Empty) : (false, error);
        });
    }

    private static DialogActionResult SetDialogInput(
        DialogManager manager,
        DialogInstance dialog,
        int index,
        string encodedValue)
    {
        if (index < 0 || index >= dialog.Definition.Inputs.Count)
            return new DialogActionResult(false, string.Empty);
        string value = DecodeBase64Url(encodedValue);
        if (value.Length > 2048)
            return new DialogActionResult(false, string.Empty);
        return manager.SetInput(dialog.Definition.Inputs[index].Key, value);
    }

    private static string DecodeBase64Url(string value)
    {
        if (value == "_")
            return string.Empty;
        if (value.Length > 4096 || value.Any(static character =>
            !(character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_')))
            throw new FormatException();
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(padded));
    }

    private void RefreshDialog(
        DialogInstance dialog,
        int requestedActionId,
        bool ok,
        string error)
    {
        int token;
        int nextRevision;
        using (sync.EnterScope())
        {
            if (activeKind != DialogKind || activeDialogRevision != dialog.Revision)
                return;
            token = screenToken;
            nextRevision = revision + 1;
        }
        if (!TryWriteDialog(dialog, token, nextRevision, requestedActionId, ok, error))
            return;
        using (sync.EnterScope())
        {
            if (activeKind != DialogKind
                || activeDialogRevision != dialog.Revision
                || screenToken != token)
                return;
            revision = nextRevision;
            actionId = requestedActionId;
            actionOk = ok;
            actionError = error;
        }
        TryEmit("update", token, nextRevision, 0, DialogKind);
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

    private static bool TryWriteDialog(
        DialogInstance dialog,
        int token,
        int revision,
        int actionId,
        bool actionOk,
        string actionError)
    {
        try
        {
            WriteDialog(dialog, token, revision, actionId, actionOk, actionError);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private static void WriteDialog(
        DialogInstance dialog,
        int token,
        int revision,
        int actionId,
        bool actionOk,
        string actionError)
    {
        DialogDefinition definition = dialog.Definition;
        var body = definition.Body.Take(32).Select(entry => new
        {
            kind = entry.Kind.ToString(),
            text = CleanDialogValue(entry.Text, 2048),
            type = entry.Type is null ? null : RbInventory.CleanText(entry.Type, 160),
        }).ToArray();
        var inputs = definition.Inputs.Take(32).Select((entry, index) => new
        {
            index,
            key = RbInventory.CleanText(entry.Key, 160),
            kind = entry.Kind.ToString(),
            label = RbInventory.CleanText(entry.Label, 512),
            value = CleanDialogValue(dialog.Values.TryGetValue(entry.Key, out string? value)
                ? value : entry.InitialValue, 2048),
            maxLength = Math.Clamp(entry.MaxLength, 0, 2048),
            labelVisible = entry.LabelVisible,
            multiline = entry.Multiline,
            options = (entry.Options ?? []).Take(64).Select(option => new
            {
                id = CleanDialogValue(option.Id, 512),
                display = RbInventory.CleanText(option.Display, 512),
                initial = option.Initial,
            }).ToArray(),
            onTrue = CleanDialogValue(entry.OnTrue, 512),
            onFalse = CleanDialogValue(entry.OnFalse, 512),
            start = float.IsFinite(entry.Start) ? entry.Start : 0,
            end = float.IsFinite(entry.End) ? entry.End : 1,
            step = entry.Step.HasValue && float.IsFinite(entry.Step.Value) ? entry.Step : null,
        }).ToArray();
        var actions = definition.Actions.Take(64).Select(entry => new
        {
            index = entry.Index,
            label = RbInventory.CleanText(entry.Label, 512),
            isCancel = entry.IsCancel,
            kind = (entry.Action?.Kind ?? DialogActionKind.None).ToString(),
            value = entry.Action?.Value is null ? null : CleanDialogValue(entry.Action.Value, 1024),
        }).ToArray();
        Write(new
        {
            version = 1,
            generatedAt = DateTimeOffset.UtcNow.ToString("O"),
            open = true,
            kind = DialogKind,
            token,
            revision,
            dialogType = RbInventory.CleanText(definition.Type, 160),
            title = RbInventory.CleanText(definition.Title, 512),
            externalTitle = definition.ExternalTitle is null
                ? null : RbInventory.CleanText(definition.ExternalTitle, 512),
            canCloseWithEscape = definition.CanCloseWithEscape,
            pause = definition.Pause,
            afterAction = definition.AfterAction.ToString(),
            columns = Math.Clamp(definition.Columns, 1, 16),
            buttonWidth = Math.Clamp(definition.ButtonWidth, 1, 1024),
            resolved = definition.IsResolved,
            body,
            inputs,
            actions,
            actionId,
            actionOk,
            actionError = RbInventory.CleanText(actionError, 48),
        });
    }

    private static string CleanDialogValue(string? value, int maxLength)
    {
        string clean = new((value ?? string.Empty)
            .Select(static character => char.IsControl(character) && character is not '\n' and not '\t'
                ? ' ' : character)
            .ToArray());
        return clean.Length <= maxLength ? clean : clean[..maxLength];
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

    private static void TryEmit(
        string type,
        int token,
        int revision,
        int inventoryId,
        string kind = ContainerKind)
    {
        try { ConsoleIO.WriteLine("[RBSCREEN]" + JsonSerializer.Serialize(new
        {
            v = 1,
            type,
            token,
            revision,
            inventoryId,
            kind,
            at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        })); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
    }
}
