using System.Text;
using SkiaSharp;
using UglyToad.PdfPig;

namespace SecureNotes.Api.Attachments;

public sealed class AttachmentContentValidator
{
    private const long MaximumImagePixels = 20_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ValidatedAttachment> ValidateAsync(
        string originalFileName,
        string declaredContentType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var fileName = NormalizeFileName(originalFileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var mediaType = ResolveMediaType(extension, declaredContentType);
        if (content.Length == 0)
        {
            throw new AttachmentRejectedException("Tệp rỗng không được hỗ trợ.");
        }

        return extension switch
        {
            ".txt" or ".md" => ValidateText(fileName, mediaType, content),
            ".pdf" => ValidatePdf(fileName, mediaType, content),
            ".png" or ".jpg" or ".jpeg" =>
                await ValidateImageAsync(fileName, mediaType, extension, content, cancellationToken),
            _ => throw new AttachmentRejectedException("Chỉ chấp nhận PDF, TXT, MD, PNG và JPEG.")
        };
    }

    private static ValidatedAttachment ValidateText(string fileName, string mediaType, byte[] content)
    {
        try
        {
            var text = StrictUtf8.GetString(content);
            if (text.Any(character =>
                    character == '\0'
                    || char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            {
                throw new AttachmentRejectedException("Tệp văn bản chứa ký tự điều khiển không hợp lệ.");
            }
        }
        catch (DecoderFallbackException)
        {
            throw new AttachmentRejectedException("Tệp TXT/MD phải được mã hóa UTF-8 hợp lệ.");
        }

        return new ValidatedAttachment(fileName, mediaType, content);
    }

    private static ValidatedAttachment ValidatePdf(string fileName, string mediaType, byte[] content)
    {
        if (!content.AsSpan().StartsWith("%PDF-"u8))
        {
            throw new AttachmentRejectedException("Nội dung tệp không khớp định dạng PDF.");
        }

        var eofIndex = content.AsSpan().LastIndexOf("%%EOF"u8);
        if (eofIndex < 0 || HasNonPdfWhitespace(content.AsSpan(eofIndex + 5)))
        {
            throw new AttachmentRejectedException("Cấu trúc PDF không hợp lệ hoặc có dữ liệu nối thêm.");
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var document = PdfDocument.Open(stream);
            if (document.NumberOfPages is < 1 or > 10000)
            {
                throw new AttachmentRejectedException("PDF không có trang hợp lệ hoặc vượt giới hạn.");
            }

            _ = document.GetPage(1);
        }
        catch (AttachmentRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new AttachmentRejectedException("Không thể phân tích cấu trúc PDF.", exception);
        }

        return new ValidatedAttachment(fileName, mediaType, content);
    }

    private static async Task<ValidatedAttachment> ValidateImageAsync(
        string fileName,
        string mediaType,
        string extension,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var isPng = content.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var isJpeg = content.AsSpan().StartsWith(new byte[] { 255, 216, 255 });
        if (extension == ".png" && !isPng || (extension is ".jpg" or ".jpeg") && !isJpeg)
        {
            throw new AttachmentRejectedException("Nội dung tệp không khớp phần mở rộng PNG/JPEG.");
        }

        try
        {
            using var encoded = SKData.CreateCopy(content);
            using var codec = SKCodec.Create(encoded)
                ?? throw new AttachmentRejectedException("Ảnh không hợp lệ hoặc không thể giải mã.");
            var sourceInfo = codec.Info;
            if (sourceInfo.Width <= 0
                || sourceInfo.Height <= 0
                || (long)sourceInfo.Width * sourceInfo.Height > MaximumImagePixels
                || codec.FrameCount > 1)
            {
                throw new AttachmentRejectedException("Ảnh vượt giới hạn kích thước xử lý.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var image = SKBitmap.Decode(codec)
                ?? throw new AttachmentRejectedException("Ảnh không hợp lệ hoặc không thể giải mã.");
            using var normalized = NormalizeOrientation(image, codec.EncodedOrigin);
            using var outputImage = SKImage.FromBitmap(normalized);
            using var sanitized = outputImage.Encode(isPng ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 90)
                ?? throw new AttachmentRejectedException("Không thể chuẩn hóa ảnh.");
            return new ValidatedAttachment(fileName, mediaType, sanitized.ToArray());
        }
        catch (AttachmentRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new AttachmentRejectedException("Ảnh không hợp lệ hoặc không thể giải mã.", exception);
        }
    }

    private static SKBitmap NormalizeOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        var swapDimensions = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
        var width = swapDimensions ? source.Height : source.Width;
        var height = swapDimensions ? source.Width : source.Height;
        var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(GetOrientationMatrix(origin, source.Width, source.Height));
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return result;
    }

    private static SKMatrix GetOrientationMatrix(SKEncodedOrigin origin, int width, int height) =>
        origin switch
        {
            SKEncodedOrigin.TopRight => Matrix(-1, 0, width, 0, 1, 0),
            SKEncodedOrigin.BottomRight => Matrix(-1, 0, width, 0, -1, height),
            SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, height),
            SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => Matrix(0, -1, height, 1, 0, 0),
            SKEncodedOrigin.RightBottom => Matrix(0, -1, height, -1, 0, width),
            SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, width),
            _ => Matrix(1, 0, 0, 0, 1, 0)
        };

    private static SKMatrix Matrix(
        float scaleX,
        float skewX,
        float transX,
        float skewY,
        float scaleY,
        float transY) =>
        new()
        {
            ScaleX = scaleX,
            SkewX = skewX,
            TransX = transX,
            SkewY = skewY,
            ScaleY = scaleY,
            TransY = transY,
            Persp2 = 1
        };

    private static string ResolveMediaType(string extension, string declaredContentType)
    {
        var normalizedType = declaredContentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var mediaType = extension switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" when normalizedType is "text/plain" or "text/markdown" => normalizedType,
            ".md" => throw new AttachmentRejectedException("MIME không khớp phần mở rộng MD."),
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => throw new AttachmentRejectedException("Phần mở rộng tệp không được hỗ trợ.")
        };

        if (extension != ".md" && normalizedType != mediaType)
        {
            throw new AttachmentRejectedException("MIME khai báo không khớp định dạng tệp.");
        }

        return mediaType;
    }

    private static bool HasNonPdfWhitespace(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value is not 9 and not 10 and not 12 and not 13 and not 32)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeFileName(string originalFileName)
    {
        var name = Path.GetFileName((originalFileName ?? string.Empty).Replace('\\', '/'));
        var normalized = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (normalized.Length > 255)
        {
            normalized = normalized[..255];
            if (char.IsHighSurrogate(normalized[^1]))
            {
                normalized = normalized[..^1];
            }
        }

        if (string.IsNullOrWhiteSpace(normalized) || normalized is "." or "..")
        {
            throw new AttachmentRejectedException("Tên tệp không hợp lệ.");
        }

        return normalized;
    }
}

public sealed record ValidatedAttachment(string FileName, string ContentType, byte[] Content);

public sealed class AttachmentRejectedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
