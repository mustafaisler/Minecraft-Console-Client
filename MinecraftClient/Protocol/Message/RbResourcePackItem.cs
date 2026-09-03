using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinecraftClient.Protocol.Message;

/// <summary>
/// Aktif sunucu kaynak paketlerindeki eşya tanımlarını, modelleri ve dokuları
/// katman sırasını koruyan, sınırlı bir RakitBot artifacti olarak dışa aktarır.
/// Tarayıcı bu artifactten yalnız ekranda bulunan eşyaların bağımlılıklarını ister.
/// </summary>
internal static class RbResourcePackItem
{
    private const int MaxAssets = 8192;
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxTextureBytes = 4 * 1024 * 1024;
    private const long MaxLayerBytes = 48L * 1024 * 1024;
    private const long MaxPublishedBytes = 64L * 1024 * 1024;
    private const int MaxManifestBytes = 2 * 1024 * 1024;
    private const string CacheFingerprintVersion = "1";
    private const string OutputDirectory = "RakitBot_Inventory/items";
    private const string ManifestFile = "manifest.json";
    private static readonly object Sync = new();
    private static readonly List<ItemLayer> ActiveLayers = [];
    private static readonly JsonDocumentOptions PackJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 96,
    };

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
            ItemLayer layer = ReadCachedLayer(packIdentifier, source);
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

    public static bool Export(string packIdentifier, ZipArchive archive, Uri resourcePackUri, string hash)
    {
        string source = SourceFingerprint(resourcePackUri, hash);
        try
        {
            ItemLayer layer = BuildLayer(packIdentifier, source, archive);
            WriteLayerCache(layer);
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

    private static ItemLayer BuildLayer(string packIdentifier, string source, ZipArchive archive)
    {
        var candidates = archive.Entries
            .Select(entry => (Entry: entry, Path: NormalizeAssetPath(entry.FullName)))
            .Where(item => item.Path is not null)
            .OrderBy(item => AssetPriority(item.Path!))
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        var assets = new Dictionary<string, AssetExport>(StringComparer.Ordinal);
        var pending = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long totalBytes = 0;

        foreach ((ZipArchiveEntry entry, string? candidatePath) in candidates)
        {
            if (candidatePath is null || assets.Count >= MaxAssets)
                break;
            string path = candidatePath;
            int maxBytes = path.EndsWith(".json", StringComparison.Ordinal) ? MaxJsonBytes : MaxTextureBytes;
            if (entry.Length <= 0 || entry.Length > maxBytes || totalBytes + entry.Length > MaxLayerBytes)
                continue;

            byte[] bytes;
            using (Stream input = entry.Open())
                bytes = ReadBounded(input, maxBytes);
            if (path.EndsWith(".json", StringComparison.Ordinal))
            {
                using JsonDocument document = JsonDocument.Parse(bytes, PackJsonOptions);
                bytes = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
                if (bytes.Length <= 0 || bytes.Length > MaxJsonBytes)
                    continue;
            }
            else if (!TryNormalizePng(bytes))
            {
                continue;
            }
            if (totalBytes + bytes.Length > MaxLayerBytes)
                continue;

            string file = ArtifactName(path);
            assets[path] = new AssetExport { File = file, Size = bytes.Length };
            pending[file] = bytes;
            totalBytes += bytes.Length;
        }
        return new ItemLayer(packIdentifier, source, LayerDirectory(source), assets, pending);
    }

    private static ItemLayer ReadCachedLayer(string packIdentifier, string source)
    {
        string directory = LayerDirectory(source);
        var manifestInfo = new FileInfo(Path.Combine(directory, ManifestFile));
        if (!manifestInfo.Exists || manifestInfo.Length <= 0 || manifestInfo.Length > MaxManifestBytes)
            throw new InvalidDataException("Resource-pack item cache manifest is missing.");
        ItemManifest? manifest = JsonSerializer.Deserialize<ItemManifest>(File.ReadAllBytes(manifestInfo.FullName),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || manifest.Version != 1 || manifest.Source != source
            || manifest.Assets.Count > MaxAssets)
            throw new InvalidDataException("Resource-pack item cache manifest is invalid.");

        long totalBytes = 0;
        foreach ((string path, AssetExport asset) in manifest.Assets)
        {
            if (NormalizeAssetPath(path) != path || !IsValidAsset(asset, path))
                throw new InvalidDataException("Resource-pack item cache asset is invalid.");
            var info = new FileInfo(Path.Combine(directory, asset.File));
            if (!info.Exists || info.Length != asset.Size || (totalBytes += info.Length) > MaxLayerBytes)
                throw new InvalidDataException("Resource-pack item cache file is invalid.");
        }
        return new ItemLayer(packIdentifier, source, directory, manifest.Assets, null);
    }

    private static void WriteLayerCache(ItemLayer layer)
    {
        Directory.CreateDirectory(layer.Directory);
        foreach ((string file, byte[] bytes) in layer.PendingAssets!)
            WriteAtomic(Path.Combine(layer.Directory, file), bytes);
        WriteAtomic(Path.Combine(layer.Directory, ManifestFile), SerializeManifest(layer.Source, layer.Assets));
        var current = new HashSet<string>(layer.PendingAssets.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string old in Directory.EnumerateFiles(layer.Directory, "asset_*.*"))
        {
            if (!current.Contains(Path.GetFileName(old)))
                TryDelete(old);
        }
    }

    private static void ActivateLayer(ItemLayer layer)
    {
        ActiveLayers.RemoveAll(item => item.Identifier.Equals(layer.Identifier, StringComparison.Ordinal));
        ActiveLayers.Add(new ItemLayer(layer.Identifier, layer.Source, layer.Directory, layer.Assets, null));
    }

    private static void PublishActiveLayers()
    {
        if (ActiveLayers.Count == 0)
        {
            ClearPublishedArtifact();
            return;
        }

        var selected = new Dictionary<string, (AssetExport Asset, string SourcePath)>(StringComparer.Ordinal);
        foreach (ItemLayer layer in ActiveLayers)
        {
            foreach ((string path, AssetExport asset) in layer.Assets)
                selected[path] = (asset, Path.Combine(layer.Directory, asset.File));
        }

        string directory = PublishedDirectory();
        Directory.CreateDirectory(directory);
        long totalBytes = 0;
        var published = new Dictionary<string, AssetExport>(StringComparer.Ordinal);
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, (AssetExport asset, string sourcePath)) in selected
            .OrderBy(pair => AssetPriority(pair.Key)).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (published.Count >= MaxAssets || totalBytes + asset.Size > MaxPublishedBytes)
                break;
            var info = new FileInfo(sourcePath);
            if (!info.Exists || info.Length != asset.Size)
                throw new InvalidDataException("Published resource-pack item source is missing.");
            CopyAtomic(sourcePath, Path.Combine(directory, asset.File));
            published[path] = asset;
            current.Add(asset.File);
            totalBytes += asset.Size;
        }
        WriteAtomic(Path.Combine(directory, ManifestFile),
            SerializeManifest(MergedFingerprint(ActiveLayers), published));
        foreach (string old in Directory.EnumerateFiles(directory, "asset_*.*"))
        {
            if (!current.Contains(Path.GetFileName(old)))
                TryDelete(old);
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
            foreach (string asset in Directory.EnumerateFiles(directory, "asset_*.*"))
                TryDelete(asset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static byte[] SerializeManifest(string source, Dictionary<string, AssetExport> assets)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ItemManifest
        {
            Version = 1,
            Source = source,
            Assets = assets,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (bytes.Length > MaxManifestBytes)
            throw new InvalidDataException("Resource-pack item manifest is too large.");
        return bytes;
    }

    private static string? NormalizeAssetPath(string raw)
    {
        string path = raw.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || parts[0] != "assets" || !IsSafePart(parts[1]) || parts.Any(part => !IsSafePart(part)))
            return null;
        bool json = path.EndsWith(".json", StringComparison.Ordinal)
            && (parts[2] == "items" || parts[2] == "models");
        bool png = path.EndsWith(".png", StringComparison.Ordinal) && parts[2] == "textures";
        return json || png ? string.Join('/', parts) : null;
    }

    private static int AssetPriority(string path)
    {
        if (path.Contains("/items/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)) return 0;
        if (path.Contains("/models/item/", StringComparison.Ordinal)) return 1;
        if (path.Contains("/textures/item/", StringComparison.Ordinal)) return 2;
        if (path.EndsWith(".json", StringComparison.Ordinal)) return 3;
        return 4;
    }

    private static bool IsValidAsset(AssetExport asset, string path)
    {
        string expected = ArtifactName(path);
        int maxBytes = path.EndsWith(".json", StringComparison.Ordinal) ? MaxJsonBytes : MaxTextureBytes;
        return asset.File == expected && asset.Size > 0 && asset.Size <= maxBytes;
    }

    private static string ArtifactName(string path)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        string extension = path.EndsWith(".json", StringComparison.Ordinal) ? ".json" : ".png";
        return "asset_" + hash[..20] + extension;
    }

    private static bool IsSafePart(string value) => value.Length is > 0 and <= 160
        && value != "." && value != ".."
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static byte[] ReadBounded(Stream input, int maxBytes)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new InvalidDataException("Resource-pack item asset exceeds limits.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
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

    private static string SourceFingerprint(Uri resourcePackUri, string hash)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(CacheFingerprintVersion + "\n"
            + resourcePackUri.AbsoluteUri + "\n" + (hash ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string MergedFingerprint(IEnumerable<ItemLayer> layers)
    {
        string value = string.Join("\n", layers.Select(static layer => layer.Identifier + "\n" + layer.Source));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string PublishedDirectory() => Path.Combine(AppContext.BaseDirectory, OutputDirectory);
    private static string LayerDirectory(string source) => Path.Combine(PublishedDirectory(), "cache_" + source);

    private static bool TryNormalizePng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < signature.Length || !bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
            return false;
        int offset = 8;
        bool foundEnd = false;
        while (offset <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            long crcOffsetValue = (long)offset + 8 + length;
            if (crcOffsetValue + 4 > bytes.Length)
                return false;
            int crcOffset = (int)crcOffsetValue;
            uint crc = ComputePngCrc32(bytes.AsSpan(offset + 4, checked((int)length + 4)));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(crcOffset, 4), crc);
            bool isEnd = length == 0 && bytes[offset + 4] == (byte)'I' && bytes[offset + 5] == (byte)'E'
                && bytes[offset + 6] == (byte)'N' && bytes[offset + 7] == (byte)'D';
            offset = crcOffset + 4;
            if (isEnd) { foundEnd = true; break; }
        }
        return foundEnd && offset == bytes.Length;
    }

    private static uint ComputePngCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static bool IsArtifactException(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidDataException or JsonException
        or CryptographicException or InvalidOperationException or ArgumentException;

    private sealed record ItemLayer(string Identifier, string Source, string Directory,
        Dictionary<string, AssetExport> Assets, Dictionary<string, byte[]>? PendingAssets);

    private sealed class ItemManifest
    {
        public int Version { get; init; }
        public string Source { get; init; } = string.Empty;
        public Dictionary<string, AssetExport> Assets { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class AssetExport
    {
        public string File { get; init; } = string.Empty;
        public int Size { get; init; }
    }
}
