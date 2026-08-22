using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace LiveStudio.Cloud.Infrastructure;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    public required Uri ServiceUrl { get; set; }

    public required string Region { get; set; }

    public required string Bucket { get; set; }

    public required string AccessKey { get; set; }

    public required string SecretKey { get; set; }

    public bool UsePathStyle { get; set; } = true;
}

public sealed record ObjectMetadata(long Length, string? ContentType);

public sealed record CompletedMultipartPart(int PartNumber, string ETag);

public interface IObjectStorage
{
    Uri CreateUploadUri(string objectKey, TimeSpan lifetime);

    Task<string> CreateMultipartUploadAsync(
        string objectKey,
        string contentType,
        CancellationToken cancellationToken);

    Uri CreateUploadPartUri(
        string objectKey,
        string uploadId,
        int partNumber,
        TimeSpan lifetime);

    Task CompleteMultipartUploadAsync(
        string objectKey,
        string uploadId,
        IReadOnlyList<CompletedMultipartPart> parts,
        CancellationToken cancellationToken);

    Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken);

    Uri CreateDownloadUri(string objectKey, TimeSpan lifetime);

    Task<ObjectMetadata?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken);

    Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken);

    Task UploadAsync(
        string objectKey,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken);

    Task CopyAsync(string sourceObjectKey, string destinationObjectKey, CancellationToken cancellationToken);

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed class S3ObjectStorage(
    HttpClient httpClient,
    IOptions<ObjectStorageOptions> optionsAccessor) : IObjectStorage
{
    private readonly ObjectStorageOptions _options = optionsAccessor.Value;

    public Uri CreateUploadUri(string objectKey, TimeSpan lifetime) =>
        CreatePresignedUri(HttpMethod.Put, objectKey, lifetime);

    public async Task<string> CreateMultipartUploadAsync(
        string objectKey,
        string contentType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            CreatePresignedUri(
                HttpMethod.Post,
                objectKey,
                TimeSpan.FromMinutes(2),
                additionalQuery: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["uploads"] = string.Empty
                }));
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        return document.Descendants().FirstOrDefault(element => element.Name.LocalName == "UploadId")?.Value
            ?? throw new InvalidDataException("对象存储没有返回 Multipart UploadId");
    }

    public Uri CreateUploadPartUri(
        string objectKey,
        string uploadId,
        int partNumber,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        if (partNumber is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        return CreatePresignedUri(
            HttpMethod.Put,
            objectKey,
            lifetime,
            additionalQuery: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["partNumber"] = partNumber.ToString(CultureInfo.InvariantCulture),
                ["uploadId"] = uploadId
            });
    }

    public async Task CompleteMultipartUploadAsync(
        string objectKey,
        string uploadId,
        IReadOnlyList<CompletedMultipartPart> parts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        if (parts.Count is < 1 or > 10_000
            || parts.Select(part => part.PartNumber).Distinct().Count() != parts.Count
            || parts.Any(part => part.PartNumber is < 1 or > 10_000 || string.IsNullOrWhiteSpace(part.ETag)))
        {
            throw new ArgumentException("Multipart 分段列表无效", nameof(parts));
        }

        var document = new XDocument(
            new XElement(
                "CompleteMultipartUpload",
                parts.OrderBy(part => part.PartNumber).Select(part =>
                    new XElement(
                        "Part",
                        new XElement("PartNumber", part.PartNumber),
                        new XElement("ETag", part.ETag)))));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            CreatePresignedUri(
                HttpMethod.Post,
                objectKey,
                TimeSpan.FromMinutes(5),
                additionalQuery: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["uploadId"] = uploadId
                }));
        request.Content = new StringContent(
            document.ToString(SaveOptions.DisableFormatting),
            Encoding.UTF8,
            "application/xml");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseXml = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(responseXml))
        {
            var responseDocument = XDocument.Parse(responseXml);
            if (string.Equals(responseDocument.Root?.Name.LocalName, "Error", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    responseDocument.Descendants().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value
                    ?? "对象存储完成 Multipart Upload 失败");
            }
        }
    }

    public async Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            CreatePresignedUri(
                HttpMethod.Delete,
                objectKey,
                TimeSpan.FromMinutes(2),
                additionalQuery: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["uploadId"] = uploadId
                }));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public Uri CreateDownloadUri(string objectKey, TimeSpan lifetime) =>
        CreatePresignedUri(HttpMethod.Get, objectKey, lifetime);

    public async Task<ObjectMetadata?> GetMetadataAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Head,
            CreatePresignedUri(HttpMethod.Head, objectKey, TimeSpan.FromMinutes(2)));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return new ObjectMetadata(response.Content.Headers.ContentLength ?? 0, response.Content.Headers.ContentType?.MediaType);
    }

    public async Task DownloadToAsync(
        string objectKey,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            CreatePresignedUri(HttpMethod.Get, objectKey, TimeSpan.FromMinutes(5)));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(destination, cancellationToken);
    }

    public async Task CopyAsync(
        string sourceObjectKey,
        string destinationObjectKey,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(sourceObjectKey);
        var copySource = $"/{AwsEncode(_options.Bucket)}/{string.Join('/', sourceObjectKey.Split('/').Select(AwsEncode))}";
        var signedHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-amz-copy-source"] = copySource
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            CreatePresignedUri(HttpMethod.Put, destinationObjectKey, TimeSpan.FromMinutes(2), signedHeaders));
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", copySource);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UploadAsync(
        string objectKey,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            CreatePresignedUri(HttpMethod.Put, objectKey, TimeSpan.FromMinutes(2)));
        request.Content = new ReadOnlyMemoryContent(content);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            CreatePresignedUri(HttpMethod.Delete, objectKey, TimeSpan.FromMinutes(2)));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private Uri CreatePresignedUri(
        HttpMethod method,
        string objectKey,
        TimeSpan lifetime,
        IReadOnlyDictionary<string, string>? additionalHeaders = null,
        IReadOnlyDictionary<string, string>? additionalQuery = null)
    {
        ValidateObjectKey(objectKey);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var now = DateTimeOffset.UtcNow;
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var timestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var serviceUri = _options.ServiceUrl;
        var host = _options.UsePathStyle
            ? serviceUri.Authority
            : $"{_options.Bucket}.{serviceUri.Authority}";
        var basePath = serviceUri.AbsolutePath.TrimEnd('/');
        var objectPath = string.Join('/', objectKey.Split('/').Select(AwsEncode));
        var canonicalUri = _options.UsePathStyle
            ? $"{basePath}/{AwsEncode(_options.Bucket)}/{objectPath}"
            : $"{basePath}/{objectPath}";
        if (!canonicalUri.StartsWith('/'))
        {
            canonicalUri = $"/{canonicalUri}";
        }

        var scope = $"{date}/{_options.Region}/s3/aws4_request";
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host
        };
        if (additionalHeaders is not null)
        {
            foreach (var header in additionalHeaders)
            {
                headers.Add(header.Key.ToLowerInvariant(), header.Value.Trim());
            }
        }

        var signedHeaderNames = string.Join(';', headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(header => $"{header.Key}:{header.Value}\n"));
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{_options.AccessKey}/{scope}",
            ["X-Amz-Date"] = timestamp,
            ["X-Amz-Expires"] = ((long)lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = signedHeaderNames
        };
        if (additionalQuery is not null)
        {
            foreach (var pair in additionalQuery)
            {
                query.Add(pair.Key, pair.Value);
            }
        }
        var canonicalQuery = string.Join('&', query.Select(pair => $"{AwsEncode(pair.Key)}={AwsEncode(pair.Value)}"));
        var canonicalRequest = string.Join(
            '\n',
            method.Method,
            canonicalUri,
            canonicalQuery,
            canonicalHeaders,
            signedHeaderNames,
            "UNSIGNED-PAYLOAD");
        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            timestamp,
            scope,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signature = Convert.ToHexStringLower(SignString(stringToSign, date));
        var scheme = serviceUri.Scheme;
        return new Uri($"{scheme}://{host}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}");
    }

    private byte[] SignString(string stringToSign, string date)
    {
        var dateKey = Hmac(Encoding.UTF8.GetBytes($"AWS4{_options.SecretKey}"), date);
        var regionKey = Hmac(dateKey, _options.Region);
        var serviceKey = Hmac(regionKey, "s3");
        var signingKey = Hmac(serviceKey, "aws4_request");
        return Hmac(signingKey, stringToSign);
    }

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string AwsEncode(string value) => Uri.EscapeDataString(value)
        .Replace("%7E", "~", StringComparison.Ordinal);

    private static void ValidateObjectKey(string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        if (objectKey.StartsWith('/')
            || objectKey.Contains('\\')
            || objectKey.Split('/').Any(part => part is "." or ".." or ""))
        {
            throw new ArgumentException("对象存储 Key 非法", nameof(objectKey));
        }
    }
}
