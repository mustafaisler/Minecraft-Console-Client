using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinecraftClient.Protocol.Message;

/// <summary>
/// Exports bounded bitmap font providers from the active server resource pack.
/// The panel only receives sheets referenced by characters in a live tooltip.
/// </summary>
internal static class RbResourcePackFont
{
    private const int MaxProviderFiles = 128;
    private const int MaxGlyphs = 20000;
    private const int MaxSheetBytes = 512 * 1024;
    private const int MaxTotalSheetBytes = 8 * 1024 * 1024;
    private const int MaxManifestBytes = 512 * 1024;
    private const string OutputDirectory = "RakitBot_Inventory/font";
    private const string ManifestFile = "manifest.json";

    public static bool IsCurrent(Uri resourcePackUri, string hash)
    {
        string path = Path.Combine(AppContext.BaseDirectory, OutputDirectory, ManifestFile);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxManifestBytes)
                return false;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            return root.TryGetProperty("version", out JsonElement version) && version.TryGetInt32(out int versionNumber) && versionNumber == 1
                && root.TryGetProperty("source", out JsonElement source)
                && source.ValueKind == JsonValueKind.String
                && source.GetString() == SourceFingerprint(resourcePackUri, hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    public static void Export(ZipArchive archive, Uri resourcePackUri, string hash)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, OutputDirectory);
        string manifestPath = Path.Combine(directory, ManifestFile);
        string temporaryManifestPath = Path.Combine(directory, "manifest.tmp");
        var glyphs = new Dictionary<string, GlyphExport>(StringComparer.Ordinal);
        var sheets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int totalSheetBytes = 0;

        try
        {
            foreach (ZipArchiveEntry fontEntry in archive.Entries
                .Where(static entry => IsFontDefinition(entry.FullName))
                .Take(MaxProviderFiles))
            {
                string fontNamespace = GetNamespace(fontEntry.FullName);
                using JsonDocument document = JsonDocument.Parse(fontEntry.Open());
                if (!document.RootElement.TryGetProperty("providers", out JsonElement providers)
                    || providers.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement provider in providers.EnumerateArray())
                {
                    if (glyphs.Count >= MaxGlyphs)
                        break;
                    if (!TryReadProvider(provider, fontNamespace, out ProviderExport? parsed))
                        continue;

                    ZipArchiveEntry? textureEntry = archive.Entries.FirstOrDefault(entry =>
                        entry.FullName.Equals(parsed.TexturePath, StringComparison.OrdinalIgnoreCase));
                    if (textureEntry is null || textureEntry.Length <= 0 || textureEntry.Length > MaxSheetBytes)
                        continue;

                    string outputName = SheetName(parsed.TexturePath);
                    if (!sheets.ContainsKey(outputName))
                    {
                        if (totalSheetBytes + textureEntry.Length > MaxTotalSheetBytes)
                            continue;
                        using Stream textureStream = textureEntry.Open();
                        using MemoryStream textureBuffer = new((int)textureEntry.Length);
                        textureStream.CopyTo(textureBuffer);
                        byte[] textureBytes = textureBuffer.ToArray();
                        if (!IsPng(textureBytes))
                            continue;
                        sheets[outputName] = textureBytes;
                        totalSheetBytes += textureBytes.Length;
                    }

                    for (int row = 0; row < parsed.Rows.Count && glyphs.Count < MaxGlyphs; row++)
                    {
                        int column = 0;
                        foreach (Rune rune in parsed.Rows[row].EnumerateRunes())
                        {
                            if (rune.Value != 0 && rune.Value != Rune.ReplacementChar.Value)
                            {
                                string codePoint = rune.Value.ToString(CultureInfo.InvariantCulture);
                                glyphs.TryAdd(codePoint, new GlyphExport
                                {
                                    File = outputName,
                                    X = column,
                                    Y = row,
                                    Columns = parsed.Columns,
                                    Rows = parsed.Rows.Count,
                                    Ascent = parsed.Ascent,
                                    Height = parsed.Height,
                                });
                            }
                            column++;
                        }
                    }
                }
            }

            Directory.CreateDirectory(directory);
            foreach ((string name, byte[] bytes) in sheets)
            {
                string temporarySheetPath = Path.Combine(directory, name + ".tmp");
                File.WriteAllBytes(temporarySheetPath, bytes);
                File.Move(temporarySheetPath, Path.Combine(directory, name), overwrite: true);
            }

            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new FontManifest
            {
                Version = 1,
                Source = SourceFingerprint(resourcePackUri, hash),
                Glyphs = glyphs,
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (manifestBytes.Length > MaxManifestBytes)
                throw new InvalidDataException("Resource-pack font manifest is too large.");

            File.WriteAllBytes(temporaryManifestPath, manifestBytes);
            File.Move(temporaryManifestPath, manifestPath, overwrite: true);

            var currentSheets = new HashSet<string>(sheets.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (string oldSheet in Directory.EnumerateFiles(directory, "sheet_*.png"))
            {
                if (!currentSheets.Contains(Path.GetFileName(oldSheet)))
                    File.Delete(oldSheet);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or CryptographicException
            or InvalidOperationException)
        {
            try { File.Delete(temporaryManifestPath); } catch { }
        }
    }

    private static bool TryReadProvider(JsonElement provider, string fontNamespace, out ProviderExport? parsed)
    {
        parsed = null;
        if (!provider.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != "bitmap"
            || !provider.TryGetProperty("file", out JsonElement fileElement)
            || fileElement.ValueKind != JsonValueKind.String
            || !provider.TryGetProperty("chars", out JsonElement charsElement)
            || charsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string? resource = fileElement.GetString();
        if (!TryTexturePath(resource, fontNamespace, out string? texturePath))
            return false;

        double height = ReadNumber(provider, "height", 8);
        double ascent = ReadNumber(provider, "ascent", 7);
        if (!double.IsFinite(height) || !double.IsFinite(ascent)
            || height <= 0 || height > 32 || ascent < -32 || ascent > 32)
        {
            return false;
        }

        var rows = new List<string>();
        int columns = 0;
        foreach (JsonElement row in charsElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.String || rows.Count >= 512)
                return false;
            string text = row.GetString() ?? string.Empty;
            int width = text.EnumerateRunes().Count();
            if (width <= 0 || width > 512)
                return false;
            rows.Add(text);
            columns = Math.Max(columns, width);
        }
        if (rows.Count == 0)
            return false;

        parsed = new ProviderExport(texturePath, rows, columns, ascent, height);
        return true;
    }

    private static bool TryTexturePath(string? resource, string defaultNamespace, out string? texturePath)
    {
        texturePath = null;
        if (string.IsNullOrWhiteSpace(resource))
            return false;
        string normalized = resource.Replace('\\', '/').TrimStart('/');
        string resourceNamespace = defaultNamespace;
        int separator = normalized.IndexOf(':');
        if (separator >= 0)
        {
            resourceNamespace = normalized[..separator];
            normalized = normalized[(separator + 1)..];
        }
        if (!IsSafePart(resourceNamespace) || !IsSafePath(normalized) || !normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return false;
        texturePath = $"assets/{resourceNamespace}/textures/{normalized}";
        return true;
    }

    private static bool IsFontDefinition(string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4
            && parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("font", StringComparison.OrdinalIgnoreCase)
            && parts[^1].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetNamespace(string path) => path.Replace('\\', '/').Split('/')[1];

    private static bool IsSafePart(string value) => value.Length is > 0 and <= 64
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static bool IsSafePath(string value) => value.Length is > 0 and <= 240
        && !value.Contains("..", StringComparison.Ordinal)
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/');

    private static double ReadNumber(JsonElement element, string property, double fallback) =>
        element.TryGetProperty(property, out JsonElement number) && number.TryGetDouble(out double value) ? value : fallback;

    private static string SheetName(string texturePath)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texturePath))).ToLowerInvariant();
        return $"sheet_{hash[..16]}.png";
    }

    private static string SourceFingerprint(Uri resourcePackUri, string hash)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(resourcePackUri.AbsoluteUri + "\n" + (hash ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool IsPng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return bytes.Length >= signature.Length && bytes.AsSpan(0, signature.Length).SequenceEqual(signature);
    }

    private sealed record ProviderExport(string TexturePath, List<string> Rows, int Columns, double Ascent, double Height);

    private sealed class FontManifest
    {
        public int Version { get; init; }
        public string Source { get; init; } = string.Empty;
        public Dictionary<string, GlyphExport> Glyphs { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class GlyphExport
    {
        public string File { get; init; } = string.Empty;
        public int X { get; init; }
        public int Y { get; init; }
        public int Columns { get; init; }
        public int Rows { get; init; }
        public double Ascent { get; init; }
        public double Height { get; init; }
    }
}
