using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinecraftClient.Protocol.Message;

/// <summary>
/// Aktif sunucu kaynak paketlerindeki bitmap fontları katmanlı ve sınırlı bir
/// artifact olarak dışa aktarır. Kaynak bazlı önbellek yeniden indirmeyi önler;
/// yayımlanan manifest ise yalnız mevcut bağlantının paketlerini içerir.
/// </summary>
internal static class RbResourcePackFont
{
    private const int MaxProviderFiles = 128;
    private const int MaxReferenceDepth = 32;
    private const int MaxGlyphs = 20000;
    private const int MaxSheetBytes = 512 * 1024;
    private const int MaxLayerSheetBytes = 8 * 1024 * 1024;
    private const int MaxPublishedSheetBytes = 16 * 1024 * 1024;
    private const int MaxManifestBytes = 512 * 1024;
    private const string OutputDirectory = "RakitBot_Inventory/font";
    private const string ManifestFile = "manifest.json";
    private static readonly object Sync = new();
    private static readonly List<FontLayer> ActiveLayers = [];

    public static void BeginConnection()
    {
        lock (Sync)
        {
            ActiveLayers.Clear();
            ClearPublishedArtifact();
        }
    }

    public static bool TryActivateCached(string packIdentifier, Uri resourcePackUri, string hash)
    {
        string source = SourceFingerprint(resourcePackUri, hash);
        try
        {
            FontLayer layer = ReadCachedLayer(packIdentifier, source);
            lock (Sync)
            {
                ActivateLayer(layer);
                PublishActiveLayers();
            }
            return true;
        }
        catch (Exception exception) when (IsArtifactException(exception))
        {
            return false;
        }
    }

    public static void Export(string packIdentifier, ZipArchive archive, Uri resourcePackUri, string hash)
    {
        string source = SourceFingerprint(resourcePackUri, hash);
        try
        {
            FontLayer layer = BuildLayer(packIdentifier, source, archive);
            WriteLayerCache(layer);
            lock (Sync)
            {
                ActivateLayer(layer);
                PublishActiveLayers();
            }
        }
        catch (Exception exception) when (IsArtifactException(exception))
        {
        }
    }

    public static void Remove(string packIdentifier)
    {
        lock (Sync)
        {
            ActiveLayers.RemoveAll(layer => layer.Identifier.Equals(packIdentifier, StringComparison.Ordinal));
            TryPublishOrClear();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            ActiveLayers.Clear();
            ClearPublishedArtifact();
        }
    }

    private static FontLayer BuildLayer(string packIdentifier, string source, ZipArchive archive)
    {
        var definitions = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries.Where(static item => IsFontDefinition(item.FullName)))
        {
            string? id = GetFontId(entry.FullName);
            if (id is not null && definitions.Count < MaxProviderFiles)
                definitions.TryAdd(id, entry);
        }

        var glyphs = new Dictionary<string, GlyphExport>(StringComparer.Ordinal);
        var sheets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalSheetBytes = 0;

        void ProcessDefinition(string fontId, int depth)
        {
            if (depth > MaxReferenceDepth || glyphs.Count >= MaxGlyphs || !processed.Add(fontId)
                || !definitions.TryGetValue(fontId, out ZipArchiveEntry? fontEntry))
            {
                return;
            }

            string fontNamespace = fontId.Split(':', 2)[0];
            using JsonDocument document = JsonDocument.Parse(fontEntry.Open());
            if (!document.RootElement.TryGetProperty("providers", out JsonElement providers)
                || providers.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement provider in providers.EnumerateArray())
            {
                if (glyphs.Count >= MaxGlyphs)
                    break;
                if (TryReadReference(provider, fontNamespace, out string? reference))
                {
                    ProcessDefinition(reference, depth + 1);
                    continue;
                }
                if (!TryReadBitmap(provider, fontNamespace, source, out ProviderExport? parsed))
                    continue;

                var candidates = new List<(string CodePoint, int X, int Y)>();
                for (int row = 0; row < parsed.Rows.Count && glyphs.Count + candidates.Count < MaxGlyphs; row++)
                {
                    int column = 0;
                    foreach (Rune rune in parsed.Rows[row].EnumerateRunes())
                    {
                        if (rune.Value >= 0xe000 && rune.Value <= 0x10ffff
                            && rune.Value != Rune.ReplacementChar.Value)
                        {
                            string codePoint = rune.Value.ToString(CultureInfo.InvariantCulture);
                            if (!glyphs.ContainsKey(codePoint)
                                && !candidates.Any(item => item.CodePoint == codePoint))
                            {
                                candidates.Add((codePoint, column, row));
                            }
                        }
                        column++;
                    }
                }
                if (candidates.Count == 0)
                    continue;

                ZipArchiveEntry? textureEntry = archive.Entries.FirstOrDefault(entry =>
                    entry.FullName.Equals(parsed.TexturePath, StringComparison.OrdinalIgnoreCase));
                if (textureEntry is null || textureEntry.Length <= 0 || textureEntry.Length > MaxSheetBytes)
                    continue;
                if (!sheets.ContainsKey(parsed.OutputName))
                {
                    if (totalSheetBytes + textureEntry.Length > MaxLayerSheetBytes)
                        continue;
                    using Stream textureStream = textureEntry.Open();
                    using MemoryStream textureBuffer = new((int)textureEntry.Length);
                    textureStream.CopyTo(textureBuffer);
                    byte[] bytes = textureBuffer.ToArray();
                    if (!IsPng(bytes))
                        continue;
                    sheets[parsed.OutputName] = bytes;
                    totalSheetBytes += bytes.Length;
                }

                foreach ((string codePoint, int x, int y) in candidates)
                {
                    glyphs.TryAdd(codePoint, new GlyphExport
                    {
                        File = parsed.OutputName,
                        X = x,
                        Y = y,
                        Columns = parsed.Columns,
                        Rows = parsed.Rows.Count,
                        Ascent = parsed.Ascent,
                        Height = parsed.Height,
                    });
                }
            }
        }

        ProcessDefinition("minecraft:default", 0);
        foreach (string fontId in definitions.Keys.OrderBy(static value => value, StringComparer.Ordinal))
            ProcessDefinition(fontId, 0);

        return new FontLayer(packIdentifier, source, LayerDirectory(source), glyphs, sheets);
    }

    private static bool TryReadReference(JsonElement provider, string defaultNamespace,
        [NotNullWhen(true)] out string? fontId)
    {
        fontId = null;
        return provider.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
            && type.GetString() == "reference" && provider.TryGetProperty("id", out JsonElement id)
            && id.ValueKind == JsonValueKind.String && TryFontId(id.GetString(), defaultNamespace, out fontId);
    }

    private static bool TryReadBitmap(JsonElement provider, string defaultNamespace, string source,
        [NotNullWhen(true)] out ProviderExport? parsed)
    {
        parsed = null;
        if (!provider.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != "bitmap" || !provider.TryGetProperty("file", out JsonElement file)
            || file.ValueKind != JsonValueKind.String || !provider.TryGetProperty("chars", out JsonElement chars)
            || chars.ValueKind != JsonValueKind.Array
            || !TryTexturePath(file.GetString(), defaultNamespace, out string? texturePath))
        {
            return false;
        }

        double height = ReadNumber(provider, "height", 8);
        double ascent = ReadNumber(provider, "ascent", 7);
        if (!double.IsFinite(height) || !double.IsFinite(ascent)
            || height <= 0 || height > 32 || ascent < -32 || ascent > 32)
        {
            return false;
        }

        var rows = new List<string>();
        int columns = 0;
        foreach (JsonElement row in chars.EnumerateArray())
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

        parsed = new ProviderExport(texturePath, SheetName(source, texturePath), rows, columns, ascent, height);
        return true;
    }

    private static bool TryTexturePath(string? resource, string defaultNamespace,
        [NotNullWhen(true)] out string? texturePath)
    {
        texturePath = null;
        if (!TryResourceLocation(resource, defaultNamespace, out string? resourceNamespace, out string? path)
            || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        texturePath = $"assets/{resourceNamespace}/textures/{path}";
        return true;
    }

    private static bool TryFontId(string? resource, string defaultNamespace,
        [NotNullWhen(true)] out string? fontId)
    {
        fontId = null;
        if (!TryResourceLocation(resource, defaultNamespace, out string? resourceNamespace, out string? path))
            return false;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            path = path[..^5];
        fontId = resourceNamespace + ":" + path;
        return true;
    }

    private static bool TryResourceLocation(string? resource, string defaultNamespace,
        [NotNullWhen(true)] out string? resourceNamespace, [NotNullWhen(true)] out string? path)
    {
        resourceNamespace = null;
        path = null;
        if (string.IsNullOrWhiteSpace(resource))
            return false;
        string normalized = resource.Replace('\\', '/').TrimStart('/');
        resourceNamespace = defaultNamespace;
        int separator = normalized.IndexOf(':');
        if (separator >= 0)
        {
            resourceNamespace = normalized[..separator];
            normalized = normalized[(separator + 1)..];
        }
        if (!IsSafePart(resourceNamespace) || !IsSafePath(normalized))
            return false;
        path = normalized;
        return true;
    }

    private static bool IsFontDefinition(string path) => GetFontId(path) is not null;

    private static string? GetFontId(string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("font", StringComparison.OrdinalIgnoreCase)
            || !parts[^1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || !IsSafePart(parts[1]))
        {
            return null;
        }
        string fontPath = string.Join('/', parts.Skip(3));
        fontPath = fontPath[..^5];
        return IsSafePath(fontPath) ? parts[1] + ":" + fontPath : null;
    }

    private static FontLayer ReadCachedLayer(string packIdentifier, string source)
    {
        string directory = LayerDirectory(source);
        var info = new FileInfo(Path.Combine(directory, ManifestFile));
        if (!info.Exists || info.Length <= 0 || info.Length > MaxManifestBytes)
            throw new InvalidDataException("Resource-pack font cache manifest is missing.");
        FontManifest? manifest = JsonSerializer.Deserialize<FontManifest>(File.ReadAllBytes(info.FullName),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || manifest.Version != 2 || manifest.Source != source
            || manifest.Glyphs.Count > MaxGlyphs)
        {
            throw new InvalidDataException("Resource-pack font cache manifest is invalid.");
        }

        long totalBytes = 0;
        foreach ((string codePoint, GlyphExport glyph) in manifest.Glyphs)
        {
            if (!int.TryParse(codePoint, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value < 0xe000 || value > 0x10ffff || !IsValidGlyph(glyph))
            {
                throw new InvalidDataException("Resource-pack font cache glyph is invalid.");
            }
        }
        foreach (string file in manifest.Glyphs.Values.Select(static glyph => glyph.File).Distinct(StringComparer.Ordinal))
        {
            var sheet = new FileInfo(Path.Combine(directory, file));
            if (!sheet.Exists || sheet.Length <= 0 || sheet.Length > MaxSheetBytes
                || (totalBytes += sheet.Length) > MaxLayerSheetBytes)
            {
                throw new InvalidDataException("Resource-pack font cache sheet is invalid.");
            }
        }
        return new FontLayer(packIdentifier, source, directory, manifest.Glyphs, null);
    }

    private static void WriteLayerCache(FontLayer layer)
    {
        Directory.CreateDirectory(layer.Directory);
        foreach ((string name, byte[] bytes) in layer.PendingSheets!)
            WriteAtomic(Path.Combine(layer.Directory, name), bytes);
        WriteAtomic(Path.Combine(layer.Directory, ManifestFile), SerializeManifest(layer.Source, layer.Glyphs));
        var current = new HashSet<string>(layer.PendingSheets.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string oldSheet in Directory.EnumerateFiles(layer.Directory, "sheet_*.png"))
        {
            if (!current.Contains(Path.GetFileName(oldSheet)))
                File.Delete(oldSheet);
        }
    }

    private static void ActivateLayer(FontLayer layer)
    {
        ActiveLayers.RemoveAll(item => item.Identifier.Equals(layer.Identifier, StringComparison.Ordinal));
        ActiveLayers.Add(new FontLayer(layer.Identifier, layer.Source, layer.Directory, layer.Glyphs, null));
    }

    private static void PublishActiveLayers()
    {
        if (ActiveLayers.Count == 0)
        {
            ClearPublishedArtifact();
            return;
        }

        var glyphs = new Dictionary<string, GlyphExport>(StringComparer.Ordinal);
        for (int index = ActiveLayers.Count - 1; index >= 0 && glyphs.Count < MaxGlyphs; index--)
        {
            foreach ((string codePoint, GlyphExport glyph) in ActiveLayers[index].Glyphs)
            {
                if (glyphs.Count >= MaxGlyphs)
                    break;
                glyphs.TryAdd(codePoint, glyph);
            }
        }

        string directory = PublishedDirectory();
        Directory.CreateDirectory(directory);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FontLayer layer in ActiveLayers)
        {
            foreach (string file in layer.Glyphs.Values.Select(static glyph => glyph.File).Distinct(StringComparer.Ordinal))
                sources[file] = Path.Combine(layer.Directory, file);
        }

        long totalBytes = 0;
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in glyphs.Values.Select(static glyph => glyph.File).Distinct(StringComparer.Ordinal))
        {
            if (!sources.TryGetValue(file, out string? sourcePath))
                throw new InvalidDataException("Resource-pack font sheet source is missing.");
            var info = new FileInfo(sourcePath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxSheetBytes
                || (totalBytes += info.Length) > MaxPublishedSheetBytes)
            {
                throw new InvalidDataException("Published resource-pack font sheets exceed limits.");
            }
            CopyAtomic(sourcePath, Path.Combine(directory, file));
            current.Add(file);
        }

        WriteAtomic(Path.Combine(directory, ManifestFile), SerializeManifest(MergedFingerprint(ActiveLayers), glyphs));
        foreach (string oldSheet in Directory.EnumerateFiles(directory, "sheet_*.png"))
        {
            if (!current.Contains(Path.GetFileName(oldSheet)))
                File.Delete(oldSheet);
        }
    }

    private static void TryPublishOrClear()
    {
        try { PublishActiveLayers(); }
        catch (Exception exception) when (IsArtifactException(exception)) { ClearPublishedArtifact(); }
    }

    private static void ClearPublishedArtifact()
    {
        string directory = PublishedDirectory();
        TryDelete(Path.Combine(directory, ManifestFile));
        TryDelete(Path.Combine(directory, ManifestFile + ".tmp"));
        if (!Directory.Exists(directory))
            return;
        try
        {
            foreach (string sheet in Directory.EnumerateFiles(directory, "sheet_*.png"))
                TryDelete(sheet);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static byte[] SerializeManifest(string source, Dictionary<string, GlyphExport> glyphs)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new FontManifest
        {
            Version = 2,
            Source = source,
            Glyphs = glyphs,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (bytes.Length > MaxManifestBytes)
            throw new InvalidDataException("Resource-pack font manifest is too large.");
        return bytes;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void CopyAtomic(string source, string destination)
    {
        string temporaryPath = destination + ".tmp";
        File.Copy(source, temporaryPath, overwrite: true);
        File.Move(temporaryPath, destination, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsValidGlyph(GlyphExport glyph) => IsSheetFile(glyph.File)
        && glyph.Columns is >= 1 and <= 512 && glyph.Rows is >= 1 and <= 512
        && glyph.X >= 0 && glyph.X < glyph.Columns && glyph.Y >= 0 && glyph.Y < glyph.Rows
        && double.IsFinite(glyph.Ascent) && glyph.Ascent is >= -32 and <= 32
        && double.IsFinite(glyph.Height) && glyph.Height is > 0 and <= 32;

    private static bool IsSheetFile(string value) => value.Length == 26
        && value.StartsWith("sheet_", StringComparison.Ordinal)
        && value.EndsWith(".png", StringComparison.Ordinal)
        && value.Substring(6, 16).All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafePart(string value) => value.Length is > 0 and <= 64
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static bool IsSafePath(string value) => value.Length is > 0 and <= 240
        && !value.Contains("..", StringComparison.Ordinal)
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/');

    private static double ReadNumber(JsonElement element, string property, double fallback) =>
        element.TryGetProperty(property, out JsonElement number) && number.TryGetDouble(out double value) ? value : fallback;

    private static string SheetName(string source, string texturePath)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + texturePath))).ToLowerInvariant();
        return $"sheet_{hash[..16]}.png";
    }

    private static string SourceFingerprint(Uri resourcePackUri, string hash)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(resourcePackUri.AbsoluteUri + "\n" + (hash ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string MergedFingerprint(IEnumerable<FontLayer> layers)
    {
        string value = string.Join("\n", layers.Select(static layer => layer.Identifier + "\n" + layer.Source));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string PublishedDirectory() => Path.Combine(AppContext.BaseDirectory, OutputDirectory);
    private static string LayerDirectory(string source) => Path.Combine(PublishedDirectory(), "cache_" + source);

    private static bool IsPng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return bytes.Length >= signature.Length && bytes.AsSpan(0, signature.Length).SequenceEqual(signature);
    }

    private static bool IsArtifactException(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidDataException or JsonException
        or CryptographicException or InvalidOperationException or ArgumentException;

    private sealed record ProviderExport(string TexturePath, string OutputName, List<string> Rows,
        int Columns, double Ascent, double Height);

    private sealed record FontLayer(string Identifier, string Source, string Directory,
        Dictionary<string, GlyphExport> Glyphs, Dictionary<string, byte[]>? PendingSheets);

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
