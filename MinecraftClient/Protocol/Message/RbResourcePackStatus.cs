using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinecraftClient.Protocol.Message;

/// <summary>
/// Kaynak paketi font akışının son durumunu kullanıcıya çıktı üretmeden,
/// RakitBot tarafından okunabilecek küçük ve atomik bir artifact olarak tutar.
/// </summary>
internal static class RbResourcePackStatus
{
    private const string OutputDirectory = "RakitBot_Inventory";
    private const string StatusFile = "resource-pack-font-status.json";
    private static readonly object Sync = new();

    public static void Write(string state, string packIdentifier = "", Uri? source = null,
        Exception? exception = null, string contentType = "", long? contentLength = null,
        string signature = "", string trailer = "", string detail = "", int normalizedEntries = 0)
    {
        try
        {
            var status = new ResourcePackStatus
            {
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
                State = Limit(state, 64),
                Source = string.IsNullOrEmpty(packIdentifier) ? string.Empty : Fingerprint(packIdentifier),
                Scheme = source is null ? string.Empty : Limit(source.Scheme, 16),
                Host = source is null ? string.Empty : Limit(source.IdnHost, 253),
                ErrorType = exception is null ? string.Empty : Limit(exception.GetType().Name, 96),
                ContentType = Limit(contentType, 96),
                ContentLength = contentLength,
                Signature = Limit(signature, 32),
                Trailer = Limit(trailer, 64),
                Detail = Limit(detail.Replace('\r', ' ').Replace('\n', ' '), 512),
                NormalizedEntries = Math.Max(0, normalizedEntries),
                StatusCode = exception is System.Net.Http.HttpRequestException requestException
                    && requestException.StatusCode.HasValue
                        ? (int)requestException.StatusCode.Value
                        : null,
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(status,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            string directory = Path.Combine(AppContext.BaseDirectory, OutputDirectory);
            string path = Path.Combine(directory, StatusFile);
            string temporaryPath = path + ".tmp";
            lock (Sync)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(temporaryPath, bytes);
                File.Move(temporaryPath, path, overwrite: true);
            }
        }
        catch (Exception artifactException) when (artifactException is IOException
            or UnauthorizedAccessException or JsonException or CryptographicException)
        {
        }
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed class ResourcePackStatus
    {
        public int Version { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public string State { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Scheme { get; init; } = string.Empty;
        public string Host { get; init; } = string.Empty;
        public string ErrorType { get; init; } = string.Empty;
        public int? StatusCode { get; init; }
        public string ContentType { get; init; } = string.Empty;
        public long? ContentLength { get; init; }
        public string Signature { get; init; } = string.Empty;
        public string Trailer { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public int NormalizedEntries { get; init; }
    }
}
