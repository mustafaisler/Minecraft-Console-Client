using Brigadier.NET;
using Brigadier.NET.Builder;
using MinecraftClient.CommandHandler;
using MinecraftClient.Inventory;

namespace MinecraftClient.Commands;

/// <summary>RakitBot web paneli için güvenli açık-ekran snapshot ve tıklama komutu.</summary>
class RbScreen : Command
{
    public override string CmdName => "rbscreen";
    public override string CmdUsage => "/rbscreen <snapshot|click ...|close ...|dialog ...|book ...|anvil ...|sign ...|special ...>";
    public override string CmdDesc => CmdName;

    public override void RegisterCommand(CommandDispatcher<CmdResult> dispatcher)
    {
        dispatcher.Register(l => l.Literal(CmdName)
            .Then(l => l.Literal("snapshot")
                .Executes(result => Snapshot(result.Source)))
            .Then(l => l.Literal("close")
                .Then(l => l.Argument("closeToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("closeRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Argument("closeInventoryId", Arguments.Integer(1, 255))
                            .Then(l => l.Argument("closeActionId", Arguments.Integer(1, int.MaxValue))
                                .Executes(result => Close(
                                    result.Source,
                                    Arguments.GetInteger(result, "closeToken"),
                                    Arguments.GetInteger(result, "closeRevision"),
                                    Arguments.GetInteger(result, "closeInventoryId"),
                                    Arguments.GetInteger(result, "closeActionId"))))))))
            .Then(l => l.Literal("click")
                .Then(l => l.Argument("token", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("revision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Argument("inventoryId", Arguments.Integer(1, 255))
                            .Then(l => l.Argument("slot", Arguments.Integer(0, 255))
                                .Then(l => l.Literal("left")
                                    .Then(l => l.Argument("actionId", Arguments.Integer(1, int.MaxValue))
                                        .Executes(result => Click(result.Source, WindowActionType.LeftClick,
                                            Arguments.GetInteger(result, "token"),
                                            Arguments.GetInteger(result, "revision"),
                                            Arguments.GetInteger(result, "inventoryId"),
                                            Arguments.GetInteger(result, "slot"),
                                            Arguments.GetInteger(result, "actionId")))))
                                .Then(l => l.Literal("right")
                                    .Then(l => l.Argument("actionId", Arguments.Integer(1, int.MaxValue))
                                        .Executes(result => Click(result.Source, WindowActionType.RightClick,
                                            Arguments.GetInteger(result, "token"),
                                            Arguments.GetInteger(result, "revision"),
                                            Arguments.GetInteger(result, "inventoryId"),
                                            Arguments.GetInteger(result, "slot"),
                                            Arguments.GetInteger(result, "actionId")))))
                                .Then(l => l.Literal("shift")
                                    .Then(l => l.Argument("actionId", Arguments.Integer(1, int.MaxValue))
                                        .Executes(result => Click(result.Source, WindowActionType.ShiftClick,
                                            Arguments.GetInteger(result, "token"),
                                            Arguments.GetInteger(result, "revision"),
                                            Arguments.GetInteger(result, "inventoryId"),
                                            Arguments.GetInteger(result, "slot"),
                                            Arguments.GetInteger(result, "actionId")))))
                                .Then(l => l.Literal("shiftright")
                                    .Then(l => l.Argument("actionId", Arguments.Integer(1, int.MaxValue))
                                        .Executes(result => Click(result.Source, WindowActionType.ShiftRightClick,
                                            Arguments.GetInteger(result, "token"),
                                            Arguments.GetInteger(result, "revision"),
                                            Arguments.GetInteger(result, "inventoryId"),
                                            Arguments.GetInteger(result, "slot"),
                                            Arguments.GetInteger(result, "actionId"))))))))))
        );
        dispatcher.Register(l => l.Literal(CmdName)
            .Then(l => l.Literal("dialog")
                .Then(l => l.Argument("dialogToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("dialogRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Literal("set")
                            .Then(l => l.Argument("inputIndex", Arguments.Integer(0, 31))
                                .Then(l => l.Argument("inputValue", Arguments.String())
                                    .Then(l => l.Argument("dialogActionId", Arguments.Integer(1, int.MaxValue))
                                        .Executes(result => DialogAction(result.Source, "set",
                                            Arguments.GetInteger(result, "dialogToken"),
                                            Arguments.GetInteger(result, "dialogRevision"),
                                            Arguments.GetInteger(result, "inputIndex"),
                                            Arguments.GetString(result, "inputValue"),
                                            Arguments.GetInteger(result, "dialogActionId")))))))))));
        dispatcher.Register(l => l.Literal(CmdName)
            .Then(l => l.Literal("dialog")
                .Then(l => l.Argument("dialogToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("dialogRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Literal("click")
                            .Then(l => l.Argument("buttonIndex", Arguments.Integer(0, 1024))
                                .Then(l => l.Argument("dialogActionId", Arguments.Integer(1, int.MaxValue))
                                    .Executes(result => DialogAction(result.Source, "click",
                                        Arguments.GetInteger(result, "dialogToken"),
                                        Arguments.GetInteger(result, "dialogRevision"),
                                        Arguments.GetInteger(result, "buttonIndex"),
                                        string.Empty,
                                        Arguments.GetInteger(result, "dialogActionId"))))))))));
        dispatcher.Register(l => l.Literal(CmdName)
            .Then(l => l.Literal("dialog")
                .Then(l => l.Argument("dialogToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("dialogRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Literal("cancel")
                            .Then(l => l.Argument("dialogActionId", Arguments.Integer(1, int.MaxValue))
                                .Executes(result => DialogAction(result.Source, "cancel",
                                    Arguments.GetInteger(result, "dialogToken"),
                                    Arguments.GetInteger(result, "dialogRevision"),
                                    0,
                                    string.Empty,
                                    Arguments.GetInteger(result, "dialogActionId")))))))));
        dispatcher.Register(l => l.Literal(CmdName)
            .Then(l => l.Literal("dialog")
                .Then(l => l.Argument("dialogToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("dialogRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Literal("dismiss")
                            .Then(l => l.Argument("dialogActionId", Arguments.Integer(1, int.MaxValue))
                                .Executes(result => DialogAction(result.Source, "dismiss",
                                    Arguments.GetInteger(result, "dialogToken"),
                                    Arguments.GetInteger(result, "dialogRevision"),
                                    0,
                                    string.Empty,
                                    Arguments.GetInteger(result, "dialogActionId")))))))));
        RegisterBookAction(dispatcher, "save");
        RegisterBookAction(dispatcher, "sign");
        RegisterBookAction(dispatcher, "close");
        dispatcher.Register(l => l.Literal(CmdName)
            .Then(l => l.Literal("anvil")
                .Then(l => l.Argument("anvilToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("anvilRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Argument("anvilInventoryId", Arguments.Integer(1, 255))
                            .Then(l => l.Literal("rename")
                                .Then(l => l.Argument("anvilActionId", Arguments.Integer(1, int.MaxValue))
                                    .Executes(result => AnvilAction(
                                        result.Source,
                                        Arguments.GetInteger(result, "anvilToken"),
                                        Arguments.GetInteger(result, "anvilRevision"),
                                        Arguments.GetInteger(result, "anvilInventoryId"),
                                        Arguments.GetInteger(result, "anvilActionId"))))))))));
        RegisterSignAction(dispatcher, "submit");
        RegisterSignAction(dispatcher, "close");
        RegisterSelectionSpecial(dispatcher, "merchant");
        RegisterSelectionSpecial(dispatcher, "loom");
        dispatcher.Register(l => l.Literal(CmdName)
            .Then(l => l.Literal("special")
                .Then(l => l.Argument("specialToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("specialRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Argument("specialInventoryId", Arguments.Integer(1, 255))
                            .Then(l => l.Literal("beacon")
                                .Then(l => l.Literal("apply")
                                    .Then(l => l.Argument("beaconPrimary", Arguments.Integer(0, 255))
                                        .Then(l => l.Argument("beaconSecondary", Arguments.Integer(-1, 255))
                                            .Then(l => l.Argument("specialActionId", Arguments.Integer(1, int.MaxValue))
                                                .Executes(result => SpecialAction(
                                                    result.Source,
                                                    "beacon",
                                                    "apply",
                                                    Arguments.GetInteger(result, "specialToken"),
                                                    Arguments.GetInteger(result, "specialRevision"),
                                                    Arguments.GetInteger(result, "specialInventoryId"),
                                                    Arguments.GetInteger(result, "beaconPrimary"),
                                                    Arguments.GetInteger(result, "beaconSecondary"),
                                                    Arguments.GetInteger(result, "specialActionId")))))))))))));
    }

    private static void RegisterBookAction(CommandDispatcher<CmdResult> dispatcher, string operation)
    {
        dispatcher.Register(l => l.Literal("rbscreen")
            .Then(l => l.Literal("book")
                .Then(l => l.Argument("bookToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("bookRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Literal(operation)
                            .Then(l => l.Argument("bookActionId", Arguments.Integer(1, int.MaxValue))
                                .Executes(result => BookAction(
                                    result.Source,
                                    operation,
                                    Arguments.GetInteger(result, "bookToken"),
                                    Arguments.GetInteger(result, "bookRevision"),
                                    Arguments.GetInteger(result, "bookActionId")))))))));
    }

    private static void RegisterSignAction(CommandDispatcher<CmdResult> dispatcher, string operation)
    {
        dispatcher.Register(l => l.Literal("rbscreen")
            .Then(l => l.Literal("sign")
                .Then(l => l.Argument("signToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("signRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Literal(operation)
                            .Then(l => l.Argument("signActionId", Arguments.Integer(1, int.MaxValue))
                                .Executes(result => SignAction(
                                    result.Source,
                                    operation,
                                    Arguments.GetInteger(result, "signToken"),
                                    Arguments.GetInteger(result, "signRevision"),
                                    Arguments.GetInteger(result, "signActionId")))))))));
    }

    private static void RegisterSelectionSpecial(CommandDispatcher<CmdResult> dispatcher, string kind)
    {
        dispatcher.Register(l => l.Literal("rbscreen")
            .Then(l => l.Literal("special")
                .Then(l => l.Argument("specialToken", Arguments.Integer(1, int.MaxValue))
                    .Then(l => l.Argument("specialRevision", Arguments.Integer(1, int.MaxValue))
                        .Then(l => l.Argument("specialInventoryId", Arguments.Integer(1, 255))
                            .Then(l => l.Literal(kind)
                                .Then(l => l.Literal("select")
                                    .Then(l => l.Argument("specialIndex", Arguments.Integer(0, 255))
                                        .Then(l => l.Argument("specialActionId", Arguments.Integer(1, int.MaxValue))
                                            .Executes(result => SpecialAction(
                                                result.Source,
                                                kind,
                                                "select",
                                                Arguments.GetInteger(result, "specialToken"),
                                                Arguments.GetInteger(result, "specialRevision"),
                                                Arguments.GetInteger(result, "specialInventoryId"),
                                                Arguments.GetInteger(result, "specialIndex"),
                                                -1,
                                                Arguments.GetInteger(result, "specialActionId"))))))))))));
    }

    private static int Snapshot(CmdResult result)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);
        client.GetRakitBotScreen().Snapshot(client);
        return result.SetAndReturn(CmdResult.Status.Done);
    }

    private static int Click(
        CmdResult result,
        WindowActionType action,
        int token,
        int revision,
        int inventoryId,
        int slot,
        int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);
        var outcome = client.GetRakitBotScreen().Click(
            client, token, revision, inventoryId, slot, action, actionId);
        return result.SetAndReturn(outcome.Ok ? CmdResult.Status.Done : CmdResult.Status.Fail);
    }

    private static int Close(
        CmdResult result,
        int token,
        int revision,
        int inventoryId,
        int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);
        var outcome = client.GetRakitBotScreen().CloseScreen(
            client, token, revision, inventoryId, actionId);
        return result.SetAndReturn(outcome.Ok ? CmdResult.Status.Done : CmdResult.Status.Fail);
    }

    private static int DialogAction(
        CmdResult result,
        string operation,
        int token,
        int revision,
        int index,
        string value,
        int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        var outcome = client.GetRakitBotScreen().DialogAction(
            client, token, revision, operation, index, value, actionId);
        return result.SetAndReturn(outcome.Ok ? CmdResult.Status.Done : CmdResult.Status.Fail);
    }

    private static int BookAction(
        CmdResult result,
        string operation,
        int token,
        int revision,
        int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        var outcome = client.GetRakitBotScreen().BookAction(
            client, token, revision, operation, actionId);
        return result.SetAndReturn(outcome.Ok ? CmdResult.Status.Done : CmdResult.Status.Fail);
    }

    private static int AnvilAction(
        CmdResult result,
        int token,
        int revision,
        int inventoryId,
        int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);
        var outcome = client.GetRakitBotScreen().AnvilAction(
            client, token, revision, inventoryId, "rename", actionId);
        return result.SetAndReturn(outcome.Ok ? CmdResult.Status.Done : CmdResult.Status.Fail);
    }

    private static int SignAction(
        CmdResult result,
        string operation,
        int token,
        int revision,
        int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        var outcome = client.GetRakitBotScreen().SignAction(
            client, token, revision, operation, actionId);
        return result.SetAndReturn(outcome.Ok ? CmdResult.Status.Done : CmdResult.Status.Fail);
    }

    private static int SpecialAction(
        CmdResult result,
        string kind,
        string operation,
        int token,
        int revision,
        int inventoryId,
        int firstValue,
        int secondValue,
        int actionId)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetInventoryEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedInventory);
        var outcome = client.GetRakitBotScreen().SpecialAction(
            client, token, revision, inventoryId, kind, operation, firstValue, secondValue, actionId);
        return result.SetAndReturn(outcome.Ok ? CmdResult.Status.Done : CmdResult.Status.Fail);
    }
}
