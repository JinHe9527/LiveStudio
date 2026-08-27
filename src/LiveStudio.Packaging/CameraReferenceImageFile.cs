using System.Buffers.Binary;
using System.Security.Cryptography;
using LiveStudio.Contracts;

namespace LiveStudio.Packaging;

public sealed record ValidatedCameraReferenceImage(
    ReadOnlyMemory<byte> Content,
    string MediaType,
    string Extension,
    string Sha256,
    int PixelWidth,
    int PixelHeight)
{
    public CameraReferenceImageSnapshot CreateSnapshot(int slot) => new(
        CameraReferenceImageFile.GetPackagePath(slot, Extension),
        MediaType,
        Content.Length,
        Sha256,
        PixelWidth,
        PixelHeight);
}

public static class CameraReferenceImageFile
{
    public const long MaximumLength = 8L * 1024 * 1024;
    private const int MaximumDimension = 8192;
    private const long MaximumPixels = 40_000_000;

    public static string GetPackagePath(int slot, string extension)
    {
        if (slot is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return $"camera-images/station-{slot + 1}{extension}";
    }

    public static async Task<ValidatedCameraReferenceImage> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("找不到选择的相机截图", fullPath);
        }

        if (info.Length is <= 0 or > MaximumLength)
        {
            throw new InvalidDataException("相机截图必须小于 8 MB");
        }

        var content = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        return Validate(content);
    }

    public static ValidatedCameraReferenceImage Validate(ReadOnlyMemory<byte> content)
    {
        if (content.Length <= 0 || content.Length > MaximumLength)
        {
            throw new InvalidDataException("相机截图必须小于 8 MB");
        }

        var span = content.Span;
        string mediaType;
        string extension;
        int width;
        int height;
        if (TryReadPng(span, out width, out height))
        {
            mediaType = "image/png";
            extension = ".png";
        }
        else if (TryReadJpeg(span, out width, out height))
        {
            mediaType = "image/jpeg";
            extension = ".jpg";
        }
        else
        {
            throw new InvalidDataException("只支持 PNG、JPG 或 JPEG 相机截图");
        }

        if (width is <= 0 or > MaximumDimension
            || height is <= 0 or > MaximumDimension
            || (long)width * height > MaximumPixels)
        {
            throw new InvalidDataException("相机截图尺寸过大，最长边不能超过 8192 像素");
        }

        return new ValidatedCameraReferenceImage(
            content,
            mediaType,
            extension,
            Convert.ToHexStringLower(SHA256.HashData(span)),
            width,
            height);
    }

    private static bool TryReadPng(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = 0;
        height = 0;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (content.Length < 33
            || !content[..8].SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32BigEndian(content[8..12]) != 13
            || !content[12..16].SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(content[16..20]));
        height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(content[20..24]));
        return true;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (content.Length < 11 || content[0] != 0xff || content[1] != 0xd8)
        {
            return false;
        }

        var offset = 2;
        while (offset + 4 <= content.Length)
        {
            while (offset < content.Length && content[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= content.Length)
            {
                break;
            }

            var marker = content[offset++];
            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            if (offset + 2 > content.Length)
            {
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(content[offset..(offset + 2)]);
            if (segmentLength < 2 || offset + segmentLength > content.Length)
            {
                break;
            }

            if (IsStartOfFrame(marker) && segmentLength >= 7)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(content[(offset + 3)..(offset + 5)]);
                width = BinaryPrimitives.ReadUInt16BigEndian(content[(offset + 5)..(offset + 7)]);
                return width > 0 && height > 0;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
        0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;
}
