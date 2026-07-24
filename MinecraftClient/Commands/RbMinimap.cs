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
using MinecraftClient.Mapping;
using MinecraftClient.Tui;

namespace MinecraftClient.Commands;

/// <summary>
/// Produces a bounded, read-only world snapshot for the RakitBot web minimap.
/// </summary>
class RbMinimap : Command
{
    public override string CmdName => "rbminimap";
    public override string CmdUsage => "/rbminimap snapshot [zoom:1-8] [auto|overview]";
    public override string CmdDesc => Translations.cmd_rbminimap_desc;

    private const int Width = 48;
    private const int Height = 48;
    private const int DefaultZoom = 2;
    private const int MaxEntities = 128;
    private const string OutputDirectory = "RakitBot_Minimap";
    private const string OutputFile = "snapshot.json";
    private static int s_busy;

    private enum ViewMode
    {
        Auto,
        Overview,
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
    };

    public override void RegisterCommand(CommandDispatcher<CmdResult> dispatcher)
    {
        dispatcher.Register(l => l.Literal(CmdName)
            .Then(l => l.Literal("snapshot")
                .Executes(r => Start(r.Source, DefaultZoom, ViewMode.Auto))
                .Then(l => l.Argument("zoom", Arguments.Integer(1, 8))
                    .Executes(r => Start(r.Source, Arguments.GetInteger(r, "zoom"), ViewMode.Auto))
                    .Then(l => l.Literal("auto")
                        .Executes(r => Start(r.Source, Arguments.GetInteger(r, "zoom"), ViewMode.Auto)))
                    .Then(l => l.Literal("overview")
                        .Executes(r => Start(r.Source, Arguments.GetInteger(r, "zoom"), ViewMode.Overview)))))
        );
    }

    private static int Start(CmdResult result, int zoom, ViewMode viewMode)
    {
        McClient client = CmdResult.currentHandler!;
        if (!client.GetTerrainEnabled())
            return result.SetAndReturn(CmdResult.Status.FailNeedTerrain);

        if (Interlocked.CompareExchange(ref s_busy, 1, 0) != 0)
        {
            client.Log.Info(Translations.cmd_rbminimap_busy);
            return result.SetAndReturn(CmdResult.Status.Fail);
        }

        new Thread(() => WriteSnapshot(client, zoom, viewMode))
        {
            IsBackground = true,
            Name = "RakitBot Minimap Snapshot",
        }.Start();
        return result.SetAndReturn(CmdResult.Status.Done);
    }

    private static void WriteSnapshot(McClient client, int zoom, ViewMode viewMode)
    {
        try
        {
            var sample = MinimapControl.SampleTerrain(
                client,
                zoom,
                Width,
                Height,
                showPlayers: false,
                showHostile: false,
                showNeutral: false,
                showPassive: false,
                caveOpt: viewMode == ViewMode.Overview ? CaveModeOption.off : CaveModeOption.auto,
                ct: CancellationToken.None,
                fullHeight: viewMode == ViewMode.Overview);

            byte[] rgb = new byte[Width * Height * 3];
            int offset = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    var color = sample.Pixels[x, y];
                    rgb[offset++] = color.R;
                    rgb[offset++] = color.G;
                    rgb[offset++] = color.B;
                }
            }

            List<object> entities = [];
            if (sample.EntityMap is not null)
            {
                entities = sample.EntityMap
                    .Cast<List<MinimapControl.PixelEntityInfo>?>()
                    .Where(static list => list is not null)
                    .SelectMany(static list => list!)
                    .OrderByDescending(static entity => entity.Priority)
                    .Take(MaxEntities)
                    .Select(static entity => (object)new
                    {
                        name = CleanName(entity.Name),
                        category = entity.Category.ToString().ToLowerInvariant(),
                        x = entity.X,
                        y = entity.Y,
                        z = entity.Z,
                        health = entity.Health,
                        maxHealth = entity.MaxHealth,
                    })
                    .ToList();
            }

            Location center = client.GetCurrentLocation();
            Dimension dimension = World.GetDimension();
            var snapshot = new
            {
                version = 1,
                generatedAt = DateTimeOffset.UtcNow.ToString("O"),
                dimension = dimension.Name,
                width = Width,
                height = Height,
                blocksPerPixel = zoom,
                viewMode = viewMode == ViewMode.Overview ? "overview" : "auto",
                caveMode = sample.CaveModeActive,
                center = new { x = center.X, y = center.Y, z = center.Z },
                rgb = Convert.ToBase64String(rgb),
                entities,
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

            client.Log.Info(string.Format(
                Translations.cmd_rbminimap_success,
                Width,
                Height,
                zoom,
                entities.Count));
        }
        catch (Exception ex)
        {
            client.Log.Info(string.Format(
                Translations.cmd_rbminimap_failure,
                CleanReason(ex.Message)));
        }
        finally
        {
            Interlocked.Exchange(ref s_busy, 0);
        }
    }

    private static string CleanName(string? value)
    {
        string clean = new((value ?? string.Empty)
            .Select(static character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        clean = clean.Trim();
        return clean.Length <= 64 ? clean : clean[..64];
    }

    private static string CleanReason(string? value)
    {
        string clean = CleanName(value).Replace(' ', '_');
        return clean.Length > 0 ? clean : Translations.cmd_rbminimap_unknown_reason;
    }
}
