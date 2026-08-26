using System.Net;
using LiveStudio.Cloud.Infrastructure;
using Microsoft.Extensions.Options;

namespace LiveStudio.Core.Tests;

public sealed class ObjectStorageTests
{
    [Fact]
    public void UploadUriScopesSignatureToBucketAndObject()
    {
        var storage = CreateStorage(new RecordingHandler());

        var uri = storage.CreateUploadUri("organization/uploads/package.lscfg", TimeSpan.FromMinutes(5));

        Assert.Equal("/livestudio/organization/uploads/package.lscfg", uri.AbsolutePath);
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", uri.Query, StringComparison.Ordinal);
        Assert.Contains("X-Amz-SignedHeaders=host", uri.Query, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientUrisUsePublicEndpointWhileServerOperationsStayInternal()
    {
        var handler = new RecordingHandler();
        var storage = CreateStorage(handler, new Uri("https://111.229.162.72:8443"));

        var uploadUri = storage.CreateUploadUri(
            "organization/uploads/package.lscfg",
            TimeSpan.FromMinutes(5));
        var downloadUri = storage.CreateDownloadUri(
            "organization/snapshots/package.lscfg",
            TimeSpan.FromMinutes(5));
        await storage.UploadAsync(
            "organization/internal/preview.jpg",
            new byte[] { 1, 2, 3 },
            "image/jpeg",
            CancellationToken.None);

        Assert.Equal("https://111.229.162.72:8443", uploadUri.GetLeftPart(UriPartial.Authority));
        Assert.Equal("https://111.229.162.72:8443", downloadUri.GetLeftPart(UriPartial.Authority));
        Assert.Equal("minio", handler.RequestUri?.Host);
        Assert.Equal(9000, handler.RequestUri?.Port);
    }

    [Fact]
    public async Task CopySignsAndSendsCopySourceHeader()
    {
        var handler = new RecordingHandler();
        var storage = CreateStorage(handler);

        await storage.CopyAsync(
            "organization/uploads/package.lscfg",
            "organization/snapshots/package.lscfg",
            CancellationToken.None);

        Assert.Equal("/livestudio/organization/uploads/package.lscfg", handler.CopySource);
        Assert.Contains("X-Amz-SignedHeaders=host%3Bx-amz-copy-source", handler.RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectKeyRejectsTraversal()
    {
        var storage = CreateStorage(new RecordingHandler());

        Assert.Throws<ArgumentException>(() => storage.CreateDownloadUri(
            "organization/../package.lscfg",
            TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task MultipartUploadSignsPartNumbersAndCompletesWithReturnedEtags()
    {
        var handler = new RecordingHandler();
        var storage = CreateStorage(handler);
        var objectKey = "organization/uploads/package.lscfg";

        var uploadId = await storage.CreateMultipartUploadAsync(
            objectKey,
            "application/vnd.livestudio.snapshot",
            CancellationToken.None);
        var partUri = storage.CreateUploadPartUri(
            objectKey,
            uploadId,
            2,
            TimeSpan.FromMinutes(5));
        await storage.CompleteMultipartUploadAsync(
            objectKey,
            uploadId,
            [new CompletedMultipartPart(2, "\"etag-2\""), new CompletedMultipartPart(1, "\"etag-1\"")],
            CancellationToken.None);

        Assert.Equal("upload-session", uploadId);
        Assert.Contains("partNumber=2", partUri.Query, StringComparison.Ordinal);
        Assert.Contains("uploadId=upload-session", partUri.Query, StringComparison.Ordinal);
        var completionContent = Assert.IsType<string>(handler.RequestContent);
        Assert.Contains("<PartNumber>1</PartNumber><ETag>\"etag-1\"</ETag>", completionContent, StringComparison.Ordinal);
        Assert.Contains("<PartNumber>2</PartNumber><ETag>\"etag-2\"</ETag>", completionContent, StringComparison.Ordinal);
        Assert.True(completionContent.IndexOf("etag-1", StringComparison.Ordinal)
            < completionContent.IndexOf("etag-2", StringComparison.Ordinal));
    }

    private static S3ObjectStorage CreateStorage(
        HttpMessageHandler handler,
        Uri? publicServiceUrl = null) => new(
        new HttpClient(handler),
        Options.Create(new ObjectStorageOptions
        {
            ServiceUrl = new Uri("http://minio:9000"),
            PublicServiceUrl = publicServiceUrl,
            Region = "us-east-1",
            Bucket = "livestudio",
            AccessKey = "access-key",
            SecretKey = "secret-key",
            UsePathStyle = true
        }));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? CopySource { get; private set; }

        public string? RequestContent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            CopySource = request.Headers.TryGetValues("x-amz-copy-source", out var values)
                ? values.Single()
                : null;
            RequestContent = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var query = request.RequestUri?.Query ?? string.Empty;
            var content = request.Method == HttpMethod.Post && query.Contains("uploads=", StringComparison.Ordinal)
                ? "<InitiateMultipartUploadResult><UploadId>upload-session</UploadId></InitiateMultipartUploadResult>"
                : request.Method == HttpMethod.Post && query.Contains("uploadId=", StringComparison.Ordinal)
                    ? "<CompleteMultipartUploadResult><ETag>completed</ETag></CompleteMultipartUploadResult>"
                    : string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            };
        }
    }
}
