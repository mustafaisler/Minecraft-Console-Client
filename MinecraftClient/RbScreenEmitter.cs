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
using MinecraftClient.Mapping;

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
    private const string BookKind = "book";
    private const string SignKind = "sign";
    private const long FlushDelayMs = 50;
    private readonly Lock sync = new();
    private DialogManager? attachedDialogManager;
    private string activeKind = string.Empty;
    private int activeInventoryId;
    private int activeDialogRevision;
    private BookHand activeBookHand;
    private Location activeSignLocation;
    private bool activeSignFront;
    private string[] activeSignLines = [string.Empty, string.Empty, string.Empty, string.Empty];
    private string activeAnvilText = string.Empty;
    private List<VillagerTrade> activeTrades = [];
    private VillagerInfo? activeVillagerInfo;
    private int activeMerchantSelection;
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
            activeAnvilText = string.Empty;
            activeTrades = [];
            activeVillagerInfo = null;
            activeMerchantSelection = 0;
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

    public void UpdateTrades(int inventoryId, List<VillagerTrade> trades, VillagerInfo villagerInfo)
    {
        if (inventoryId <= 0)
            return;
        using (sync.EnterScope())
        {
            if (activeKind != ContainerKind || inventoryId != activeInventoryId)
                return;
            activeTrades = trades.Take(256).ToList();
            activeVillagerInfo = villagerInfo;
            activeMerchantSelection = Math.Clamp(activeMerchantSelection, 0, Math.Max(0, activeTrades.Count - 1));
            dirty = true;
            flushAfter = 0;
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
            bool bookActive;
            BookHand bookHand;
            using (sync.EnterScope())
            {
                bookActive = activeKind == BookKind;
                bookHand = activeBookHand;
            }
            if (bookActive)
            {
                RefreshBook(client, bookHand, 0, true, string.Empty);
                return;
            }
            bool signActive;
            using (sync.EnterScope())
                signActive = activeKind == SignKind;
            if (signActive)
            {
                RefreshSign(0, true, string.Empty);
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

    public (bool Ok, string Error) CloseScreen(
        McClient client,
        int expectedToken,
        int expectedRevision,
        int expectedInventoryId,
        int requestedActionId)
    {
        return client.InvokeOnMainThread(() =>
        {
            string error = string.Empty;
            using (sync.EnterScope())
            {
                if (activeKind != ContainerKind || activeInventoryId <= 0)
                    error = "screen_closed";
                else if (expectedToken != screenToken || expectedInventoryId != activeInventoryId)
                    error = "screen_replaced";
                else if (expectedRevision != revision)
                    error = "screen_stale";
            }
            if (error.Length > 0)
            {
                RecordAction(requestedActionId, false, error);
                Flush(client, force: true);
                return (false, error);
            }

            bool closed;
            try { closed = client.CloseInventory(expectedInventoryId); }
            catch { closed = false; }
            if (!closed)
            {
                RecordAction(requestedActionId, false, "close_rejected");
                Flush(client, force: true);
                return (false, "close_rejected");
            }
            Close(expectedInventoryId);
            return (true, string.Empty);
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
        string currentAnvilText;
        List<VillagerTrade> currentTrades;
        VillagerInfo? currentVillagerInfo;
        int currentMerchantSelection;
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
            currentAnvilText = activeAnvilText;
            currentTrades = activeTrades.ToList();
            currentVillagerInfo = activeVillagerInfo;
            currentMerchantSelection = activeMerchantSelection;
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
                currentActionId, currentActionOk, currentActionError, currentAnvilText,
                currentTrades, currentVillagerInfo, currentMerchantSelection);
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

    public void OpenBook(McClient client, BookHand hand)
    {
        client.InvokeOnMainThread(() =>
        {
            if (!client.TryGetHeldBookContent(out BookContent content, hand))
                return;
            int token;
            using (sync.EnterScope())
            {
                activeKind = BookKind;
                activeInventoryId = 0;
                activeDialogRevision = 0;
                activeBookHand = hand;
                screenToken = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                revision = 1;
                actionId = 0;
                actionOk = true;
                actionError = string.Empty;
                dirty = false;
                token = screenToken;
            }
            TryWriteBook(client, content, hand, token, 1, 0, true, string.Empty);
            TryEmit("open", token, 1, 0, BookKind);
        });
    }

    public (bool Ok, string Error) BookAction(
        McClient client,
        int expectedToken,
        int expectedRevision,
        string operation,
        int requestedActionId)
    {
        return client.InvokeOnMainThread(() =>
        {
            BookHand hand;
            string validationError = string.Empty;
            using (sync.EnterScope())
            {
                hand = activeBookHand;
                if (activeKind != BookKind)
                    validationError = "screen_closed";
                else if (expectedToken != screenToken)
                    validationError = "screen_replaced";
                else if (expectedRevision != revision)
                    validationError = "screen_stale";
            }
            if (validationError.Length > 0)
                return (false, validationError);

            if (operation == "close")
            {
                CloseBook(expectedToken, expectedRevision);
                return (true, string.Empty);
            }
            if (hand != BookHand.Main
                || !client.TryGetHeldBookContent(out BookContent current, hand)
                || current.IsSigned)
            {
                RefreshBook(client, hand, requestedActionId, false, "book_rejected");
                return (false, "book_rejected");
            }

            try
            {
                BookLimits limits = SafeBookLimits(client);
                (IReadOnlyList<string> pages, string? title) = ReadBookAction(
                    expectedToken, expectedRevision, requestedActionId, operation, limits);
                bool sent = client.SendBookEdit(pages, operation == "sign" ? title : null);
                if (!sent)
                {
                    RefreshBook(client, hand, requestedActionId, false, "book_rejected");
                    return (false, "book_rejected");
                }
                var updated = new BookContent(
                    pages,
                    operation == "sign" ? title : null,
                    operation == "sign" ? current.Author : null,
                    current.Generation,
                    IsSigned: operation == "sign");
                RefreshBookContent(client, updated, hand, requestedActionId, true, string.Empty);
                return (true, string.Empty);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException
                or FormatException
                or ArgumentException)
            {
                RefreshBook(client, hand, requestedActionId, false, "book_rejected");
                return (false, "book_rejected");
            }
            finally
            {
                TryDeleteBookAction();
            }
        });
    }

    private void CloseBook(int expectedToken, int expectedRevision)
    {
        int closedRevision;
        using (sync.EnterScope())
        {
            if (activeKind != BookKind || screenToken != expectedToken || revision != expectedRevision)
                return;
            closedRevision = ++revision;
            activeKind = string.Empty;
            actionId = 0;
            actionOk = true;
            actionError = string.Empty;
        }
        TryWriteClosed(expectedToken, closedRevision);
        TryEmit("close", expectedToken, closedRevision, 0, BookKind);
    }

    private void RefreshBook(
        McClient client,
        BookHand hand,
        int requestedActionId,
        bool ok,
        string error)
    {
        if (!client.TryGetHeldBookContent(out BookContent content, hand))
        {
            int token;
            int currentRevision;
            using (sync.EnterScope())
            {
                if (activeKind != BookKind)
                    return;
                token = screenToken;
                currentRevision = revision;
            }
            CloseBook(token, currentRevision);
            return;
        }
        RefreshBookContent(client, content, hand, requestedActionId, ok, error);
    }

    private void RefreshBookContent(
        McClient client,
        BookContent content,
        BookHand hand,
        int requestedActionId,
        bool ok,
        string error)
    {
        int token;
        int nextRevision;
        using (sync.EnterScope())
        {
            if (activeKind != BookKind || activeBookHand != hand)
                return;
            token = screenToken;
            nextRevision = revision + 1;
        }
        if (!TryWriteBook(client, content, hand, token, nextRevision, requestedActionId, ok, error))
            return;
        using (sync.EnterScope())
        {
            if (activeKind != BookKind || activeBookHand != hand || screenToken != token)
                return;
            revision = nextRevision;
            actionId = requestedActionId;
            actionOk = ok;
            actionError = error;
        }
        TryEmit("update", token, nextRevision, 0, BookKind);
    }

    private static BookLimits SafeBookLimits(McClient client)
    {
        BookLimits limits = BookLimits.ForProtocol(client.GetProtocolVersion());
        return new BookLimits(
            Math.Clamp(limits.MaxPages, 1, 100),
            Math.Clamp(limits.MaxPageLength, 1, 4096),
            Math.Clamp(limits.MaxTitleLength, 1, 128));
    }

    private static (IReadOnlyList<string> Pages, string? Title) ReadBookAction(
        int expectedToken,
        int expectedRevision,
        int expectedActionId,
        string expectedOperation,
        BookLimits limits)
    {
        string path = Path.Combine(AppContext.BaseDirectory, OutputDirectory, "book-action.json");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > 512 * 1024)
            throw new InvalidOperationException();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1
            || root.GetProperty("token").GetInt32() != expectedToken
            || root.GetProperty("revision").GetInt32() != expectedRevision
            || root.GetProperty("actionId").GetInt32() != expectedActionId
            || root.GetProperty("operation").GetString() != expectedOperation)
            throw new InvalidOperationException();
        DateTimeOffset generatedAt = DateTimeOffset.Parse(root.GetProperty("generatedAt").GetString() ?? string.Empty);
        if (generatedAt > DateTimeOffset.UtcNow.AddSeconds(5)
            || generatedAt < DateTimeOffset.UtcNow.AddSeconds(-30))
            throw new InvalidOperationException();
        var pages = root.GetProperty("pages").EnumerateArray()
            .Select(page => CleanDialogValue(page.GetString(), limits.MaxPageLength))
            .Take(limits.MaxPages + 1)
            .ToArray();
        if (pages.Length < 1 || pages.Length > limits.MaxPages)
            throw new InvalidOperationException();
        string? title = root.TryGetProperty("title", out JsonElement titleElement)
            && titleElement.ValueKind == JsonValueKind.String
                ? RbInventory.CleanText(titleElement.GetString(), limits.MaxTitleLength)
                : null;
        if (expectedOperation == "sign" && string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException();
        if (expectedOperation is not "save" and not "sign")
            throw new InvalidOperationException();
        return (pages, title);
    }

    private static void TryDeleteBookAction()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, OutputDirectory, "book-action.json");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void OpenSignEditor(Location location, bool front)
    {
        int token;
        string[] lines = [string.Empty, string.Empty, string.Empty, string.Empty];
        using (sync.EnterScope())
        {
            activeKind = SignKind;
            activeInventoryId = 0;
            activeDialogRevision = 0;
            activeSignLocation = location;
            activeSignFront = front;
            activeSignLines = lines;
            screenToken = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            revision = 1;
            actionId = 0;
            actionOk = true;
            actionError = string.Empty;
            dirty = false;
            token = screenToken;
        }
        TryWriteSign(location, front, lines, token, 1, 0, true, string.Empty);
        TryEmit("open", token, 1, 0, SignKind);
    }

    public (bool Ok, string Error) AnvilAction(
        McClient client,
        int expectedToken,
        int expectedRevision,
        int expectedInventoryId,
        string operation,
        int requestedActionId)
    {
        return client.InvokeOnMainThread(() =>
        {
            Container? inventory = client.GetInventory(expectedInventoryId);
            string validationError = string.Empty;
            using (sync.EnterScope())
            {
                if (activeKind != ContainerKind || inventory is null || inventory.Type != ContainerType.Anvil)
                    validationError = "screen_closed";
                else if (expectedToken != screenToken || expectedInventoryId != activeInventoryId)
                    validationError = "screen_replaced";
                else if (expectedRevision != revision)
                    validationError = "screen_stale";
            }
            if (validationError.Length > 0)
            {
                RecordAction(requestedActionId, false, validationError);
                Flush(client, force: true);
                return (false, validationError);
            }

            try
            {
                var (value, _) = ReadTextAction(
                    expectedToken, expectedRevision, requestedActionId, "anvil", operation);
                bool sent = operation == "rename" && client.SendRenameItem(value);
                if (sent)
                {
                    using (sync.EnterScope())
                        activeAnvilText = value;
                }
                RecordAction(requestedActionId, sent, sent ? string.Empty : "text_rejected");
                Update(expectedInventoryId);
                Flush(client, force: true);
                return sent ? (true, string.Empty) : (false, "text_rejected");
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException
                or FormatException
                or ArgumentException)
            {
                RecordAction(requestedActionId, false, "text_rejected");
                Flush(client, force: true);
                return (false, "text_rejected");
            }
            finally
            {
                TryDeleteTextAction();
            }
        });
    }

    public (bool Ok, string Error) SpecialAction(
        McClient client,
        int expectedToken,
        int expectedRevision,
        int expectedInventoryId,
        string kind,
        string operation,
        int firstValue,
        int secondValue,
        int requestedActionId)
    {
        return client.InvokeOnMainThread(() =>
        {
            Container? inventory = client.GetInventory(expectedInventoryId);
            string validationError = string.Empty;
            using (sync.EnterScope())
            {
                if (activeKind != ContainerKind || inventory is null)
                    validationError = "screen_closed";
                else if (expectedToken != screenToken || expectedInventoryId != activeInventoryId)
                    validationError = "screen_replaced";
                else if (expectedRevision != revision)
                    validationError = "screen_stale";
                else if ((kind == "merchant" && inventory.Type != ContainerType.Merchant)
                    || (kind == "beacon" && inventory.Type != ContainerType.Beacon)
                    || (kind == "loom" && inventory.Type != ContainerType.Loom))
                    validationError = "special_rejected";
            }
            if (validationError.Length > 0)
            {
                RecordAction(requestedActionId, false, validationError);
                Flush(client, force: true);
                return (false, validationError);
            }

            bool sent = false;
            if (kind == "merchant" && operation == "select")
            {
                bool validTrade;
                using (sync.EnterScope())
                    validTrade = firstValue >= 0 && firstValue < activeTrades.Count
                        && !activeTrades[firstValue].TradeDisabled;
                if (validTrade)
                {
                    sent = client.SelectTrade(firstValue);
                    if (sent)
                    {
                        using (sync.EnterScope())
                            activeMerchantSelection = firstValue;
                    }
                }
            }
            else if (kind == "beacon" && operation == "apply")
            {
                int level = inventory!.Properties.TryGetValue(0, out short levelValue)
                    ? Math.Clamp((int)levelValue, 0, 4) : 0;
                bool primaryAllowed = level >= 1 && (
                    firstValue is 1 or 3
                    || (level >= 2 && (firstValue is 8 or 11))
                    || (level >= 3 && firstValue == 5));
                bool secondaryAllowed = secondValue == -1
                    || (level >= 4 && (secondValue == 10 || secondValue == firstValue));
                if (primaryAllowed && secondaryAllowed)
                    sent = client.SetBeaconEffects(firstValue, secondValue);
            }
            else if (kind == "loom" && operation == "select"
                && firstValue >= 0 && firstValue <= 255)
            {
                sent = client.ClickContainerButton(expectedInventoryId, firstValue);
            }

            RecordAction(requestedActionId, sent, sent ? string.Empty : "special_rejected");
            Update(expectedInventoryId);
            Flush(client, force: true);
            return sent ? (true, string.Empty) : (false, "special_rejected");
        });
    }

    public (bool Ok, string Error) SignAction(
        McClient client,
        int expectedToken,
        int expectedRevision,
        string operation,
        int requestedActionId)
    {
        return client.InvokeOnMainThread(() =>
        {
            Location location;
            bool front;
            string validationError = string.Empty;
            using (sync.EnterScope())
            {
                location = activeSignLocation;
                front = activeSignFront;
                if (activeKind != SignKind)
                    validationError = "screen_closed";
                else if (expectedToken != screenToken)
                    validationError = "screen_replaced";
                else if (expectedRevision != revision)
                    validationError = "screen_stale";
            }
            if (validationError.Length > 0)
                return (false, validationError);
            if (operation == "close")
            {
                CloseSign(expectedToken, expectedRevision);
                return (true, string.Empty);
            }

            try
            {
                var (_, lines) = ReadTextAction(
                    expectedToken, expectedRevision, requestedActionId, "sign", operation);
                bool sent = operation == "submit" && client.UpdateSign(
                    location, lines[0], lines[1], lines[2], lines[3], front);
                if (!sent)
                {
                    using (sync.EnterScope())
                        activeSignLines = lines;
                    RefreshSign(requestedActionId, false, "text_rejected");
                    return (false, "text_rejected");
                }
                using (sync.EnterScope())
                    activeSignLines = lines;
                CloseSign(expectedToken, expectedRevision);
                return (true, string.Empty);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException
                or FormatException
                or ArgumentException)
            {
                RefreshSign(requestedActionId, false, "text_rejected");
                return (false, "text_rejected");
            }
            finally
            {
                TryDeleteTextAction();
            }
        });
    }

    private void CloseSign(int expectedToken, int expectedRevision)
    {
        int closedRevision;
        using (sync.EnterScope())
        {
            if (activeKind != SignKind || screenToken != expectedToken || revision != expectedRevision)
                return;
            closedRevision = ++revision;
            activeKind = string.Empty;
            actionId = 0;
            actionOk = true;
            actionError = string.Empty;
        }
        TryWriteClosed(expectedToken, closedRevision);
        TryEmit("close", expectedToken, closedRevision, 0, SignKind);
    }

    private void RefreshSign(int requestedActionId, bool ok, string error)
    {
        Location location;
        bool front;
        string[] lines;
        int token;
        int nextRevision;
        using (sync.EnterScope())
        {
            if (activeKind != SignKind)
                return;
            location = activeSignLocation;
            front = activeSignFront;
            lines = activeSignLines.ToArray();
            token = screenToken;
            nextRevision = revision + 1;
        }
        if (!TryWriteSign(location, front, lines, token, nextRevision, requestedActionId, ok, error))
            return;
        using (sync.EnterScope())
        {
            if (activeKind != SignKind || screenToken != token)
                return;
            revision = nextRevision;
            actionId = requestedActionId;
            actionOk = ok;
            actionError = error;
        }
        TryEmit("update", token, nextRevision, 0, SignKind);
    }

    private static (string Value, string[] Lines) ReadTextAction(
        int expectedToken,
        int expectedRevision,
        int expectedActionId,
        string expectedKind,
        string expectedOperation)
    {
        string path = Path.Combine(AppContext.BaseDirectory, OutputDirectory, "text-action.json");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > 16 * 1024)
            throw new InvalidOperationException();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1
            || root.GetProperty("token").GetInt32() != expectedToken
            || root.GetProperty("revision").GetInt32() != expectedRevision
            || root.GetProperty("actionId").GetInt32() != expectedActionId
            || root.GetProperty("kind").GetString() != expectedKind
            || root.GetProperty("operation").GetString() != expectedOperation)
            throw new InvalidOperationException();
        DateTimeOffset generatedAt = DateTimeOffset.Parse(root.GetProperty("generatedAt").GetString() ?? string.Empty);
        if (generatedAt > DateTimeOffset.UtcNow.AddSeconds(5)
            || generatedAt < DateTimeOffset.UtcNow.AddSeconds(-30))
            throw new InvalidOperationException();
        if (expectedKind == "anvil")
        {
            if (expectedOperation != "rename")
                throw new InvalidOperationException();
            string value = CleanTextActionValue(root.GetProperty("value").GetString(), 50);
            return (value, []);
        }
        if (expectedKind != "sign" || expectedOperation != "submit")
            throw new InvalidOperationException();
        string[] lines = root.GetProperty("lines").EnumerateArray()
            .Select(line => CleanTextActionValue(line.GetString(), 23))
            .Take(5)
            .ToArray();
        if (lines.Length != 4)
            throw new InvalidOperationException();
        return (string.Empty, lines);
    }

    private static string CleanTextActionValue(string? value, int maxLength)
    {
        string text = value ?? string.Empty;
        if (text.Length > maxLength || text.Any(character => char.IsControl(character)))
            throw new InvalidOperationException();
        return text;
    }

    private static void TryDeleteTextAction()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, OutputDirectory, "text-action.json");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
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

    private static object? SnapshotSpecial(
        Container inventory,
        IReadOnlyList<VillagerTrade> trades,
        VillagerInfo? villagerInfo,
        int merchantSelection)
    {
        if (inventory.Type == ContainerType.Merchant)
        {
            VillagerInfo info = villagerInfo ?? new VillagerInfo();
            return new
            {
                kind = "merchant",
                selectedTrade = Math.Clamp(merchantSelection, 0, 255),
                villager = new
                {
                    level = Math.Clamp(info.Level, 0, 255),
                    experience = Math.Max(0, info.Experience),
                    regular = info.IsRegularVillager,
                    canRestock = info.CanRestock,
                },
                trades = trades.Take(256).Select((trade, index) => new
                {
                    index,
                    input1 = SnapshotItem(trade.InputItem1),
                    input2 = trade.InputItem2 is null ? null : SnapshotItem(trade.InputItem2),
                    output = SnapshotItem(trade.OutputItem),
                    disabled = trade.TradeDisabled,
                    uses = Math.Max(0, trade.NumberOfTradeUses),
                    maxUses = Math.Max(Math.Max(0, trade.NumberOfTradeUses), trade.MaximumNumberOfTradeUses),
                    xp = Math.Max(0, trade.Xp),
                    specialPrice = trade.SpecialPrice,
                    priceMultiplier = float.IsFinite(trade.PriceMultiplier)
                        ? Math.Clamp(trade.PriceMultiplier, 0F, 100F) : 0F,
                    demand = trade.Demand,
                }).ToArray(),
            };
        }
        if (inventory.Type == ContainerType.Beacon)
        {
            inventory.Properties.TryGetValue(0, out short level);
            short primary = inventory.Properties.TryGetValue(1, out short primaryValue) ? primaryValue : (short)-1;
            short secondary = inventory.Properties.TryGetValue(2, out short secondaryValue) ? secondaryValue : (short)-1;
            return new
            {
                kind = "beacon",
                level = Math.Clamp((int)level, 0, 4),
                primary = Math.Clamp((int)primary, -1, 255),
                secondary = Math.Clamp((int)secondary, -1, 255),
            };
        }
        if (inventory.Type == ContainerType.Loom)
        {
            short pattern = inventory.Properties.TryGetValue(0, out short patternValue) ? patternValue : (short)-1;
            return new
            {
                kind = "loom",
                selectedPattern = Math.Clamp((int)pattern, -1, 255),
            };
        }
        return null;
    }

    private static void WriteOpen(
        McClient client,
        Container inventory,
        int token,
        int revision,
        int actionId,
        bool actionOk,
        string actionError,
        string anvilText,
        IReadOnlyList<VillagerTrade> trades,
        VillagerInfo? villagerInfo,
        int merchantSelection)
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
            textInput = inventory.Type == ContainerType.Anvil ? new
            {
                kind = "anvil",
                value = RbInventory.CleanText(anvilText, 50),
                maxLength = 50,
            } : null,
            special = SnapshotSpecial(inventory, trades, villagerInfo, merchantSelection),
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

    private static bool TryWriteBook(
        McClient client,
        BookContent content,
        BookHand hand,
        int token,
        int revision,
        int actionId,
        bool actionOk,
        string actionError)
    {
        try
        {
            BookLimits limits = SafeBookLimits(client);
            string[] pages = content.Pages
                .Take(limits.MaxPages)
                .Select(page => CleanDialogValue(page, limits.MaxPageLength))
                .DefaultIfEmpty(string.Empty)
                .ToArray();
            Write(new
            {
                version = 1,
                generatedAt = DateTimeOffset.UtcNow.ToString("O"),
                open = true,
                kind = BookKind,
                token,
                revision,
                hand = hand.ToString(),
                title = content.Title is null ? null : RbInventory.CleanText(content.Title, limits.MaxTitleLength),
                author = content.Author is null ? null : RbInventory.CleanText(content.Author, 128),
                generation = Math.Clamp(content.Generation, 0, 3),
                signed = content.IsSigned,
                editable = hand == BookHand.Main && !content.IsSigned,
                maxPages = limits.MaxPages,
                maxPageLength = limits.MaxPageLength,
                maxTitleLength = limits.MaxTitleLength,
                pages,
                actionId,
                actionOk,
                actionError = RbInventory.CleanText(actionError, 48),
            });
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

    private static bool TryWriteSign(
        Location location,
        bool front,
        IReadOnlyList<string> lines,
        int token,
        int revision,
        int actionId,
        bool actionOk,
        string actionError)
    {
        try
        {
            Write(new
            {
                version = 1,
                generatedAt = DateTimeOffset.UtcNow.ToString("O"),
                open = true,
                kind = SignKind,
                token,
                revision,
                location = new
                {
                    x = (int)location.X,
                    y = (int)location.Y,
                    z = (int)location.Z,
                },
                front,
                lines = lines.Take(4).Select(line => CleanTextActionValue(line, 23)).ToArray(),
                actionId,
                actionOk,
                actionError = RbInventory.CleanText(actionError, 48),
            });
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
