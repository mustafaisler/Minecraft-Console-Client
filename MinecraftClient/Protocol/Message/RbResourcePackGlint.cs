using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace MinecraftClient.Protocol.Message;

/// <summary>
/// Exports the active server resource pack's enchanted-item glint to the fixed,
/// bounded RakitBot inventory artifact directory. Missing or invalid pack assets
/// remove the old export so the web panel safely falls back to its bundled glint.
/// </summary>
internal static class RbResourcePackGlint
{
    private const int MaxGlintBytes = 2 * 1024 * 1024;
    private const string OutputDirectory = "RakitBot_Inventory";
    private const string OutputFile = "enchanted_item_glint.png";

    private static readonly string[] s_candidates =
    [
        "assets/minecraft/textures/misc/enchanted_item_glint.png",
        "assets/minecraft/textures/misc/enchanted_glint_item.png",
    ];

    public static void Export(ZipArchive archive)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, OutputDirectory);
        string outputPath = Path.Combine(directory, OutputFile);
        string temporaryPath = Path.Combine(directory, "enchanted_item_glint.tmp");

        try
        {
            ZipArchiveEntry? entry = s_candidates
                .Select(candidate => archive.Entries.FirstOrDefault(item =>
                    item.FullName.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(static item => item is not null);
            if (entry is null || entry.Length <= 0 || entry.Length > MaxGlintBytes)
            {
                File.Delete(outputPath);
                return;
            }

            using Stream input = entry.Open();
            using MemoryStream buffer = new((int)entry.Length);
            input.CopyTo(buffer);
            byte[] bytes = buffer.ToArray();
            if (!IsPng(bytes))
            {
                File.Delete(outputPath);
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool IsPng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return bytes.Length >= signature.Length
            && bytes.AsSpan(0, signature.Length).SequenceEqual(signature);
    }
}
