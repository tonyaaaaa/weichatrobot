using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WechatRobot.Application.Storage;
using WechatRobot.Application.Jobs;
using System.Text.Json;

namespace WechatRobot.Application.Knowledge;

public sealed class DocumentUploadOptions
{
    public const string SectionName = "DocumentUpload";
    public long MaximumBytes { get; set; } = 20 * 1024 * 1024;
    public int MaximumArchiveEntries { get; set; } = 2_000;
    public long MaximumExpandedArchiveBytes { get; set; } = 200 * 1024 * 1024;
    public int MaximumArchiveExpansionRatio { get; set; } = 100;
}

public enum DocumentUploadError
{
    UnsupportedExtension,
    ContentTypeMismatch,
    InvalidFileHeader,
    FileTooLarge,
    MalformedArchive,
    ArchiveExpansionLimitExceeded,
    DuplicateContent
}

public sealed class DocumentUploadValidationException(DocumentUploadError error, string message) : Exception(message)
{
    public DocumentUploadError Error { get; } = error;
}

public sealed record ValidatedDocument(byte[] Content, string Sha256, string SafeFileName, string ContentType);

public static class DocumentUploadValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static async Task<ValidatedDocument> ValidateAndBufferAsync(
        string clientFileName,
        string contentType,
        Stream input,
        DocumentUploadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateOptions(options);
        var extension = Path.GetExtension(clientFileName).ToLowerInvariant();
        if (extension is not (".md" or ".txt" or ".pdf" or ".docx"))
        {
            throw Failure(DocumentUploadError.UnsupportedExtension, "Only .md, .txt, .pdf, and .docx files are supported.");
        }

        ValidateContentType(extension, contentType);
        await using var buffered = new MemoryStream(capacity: (int)Math.Min(options.MaximumBytes, 1024 * 1024));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(block, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > options.MaximumBytes)
            {
                throw Failure(DocumentUploadError.FileTooLarge, "The document exceeds the configured upload limit.");
            }
            hash.AppendData(block, 0, read);
            await buffered.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }

        var content = buffered.ToArray();
        ValidateHeader(extension, content, options);
        return new ValidatedDocument(content, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), $"source{extension}", CanonicalContentType(extension));
    }

    private static void ValidateContentType(string extension, string supplied)
    {
        var mediaType = supplied.Split(';', 2)[0].Trim().ToLowerInvariant();
        var valid = extension switch
        {
            ".md" => mediaType is "text/markdown" or "text/plain" or "application/octet-stream",
            ".txt" => mediaType is "text/plain" or "application/octet-stream",
            ".pdf" => mediaType is "application/pdf",
            ".docx" => mediaType is DocxMime or "application/octet-stream",
            _ => false
        };
        if (!valid) throw Failure(DocumentUploadError.ContentTypeMismatch, "The supplied media type does not match the file extension.");
    }

    private static void ValidateHeader(string extension, byte[] content, DocumentUploadOptions options)
    {
        if (content.Length == 0) throw Failure(DocumentUploadError.InvalidFileHeader, "The document is empty.");
        if (extension == ".pdf")
        {
            if (content.Length < 5 || !content.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                throw Failure(DocumentUploadError.InvalidFileHeader, "The PDF signature is invalid.");
            return;
        }
        if (extension == ".docx")
        {
            ValidateDocx(content, options);
            return;
        }

        if (content.AsSpan().IndexOf((byte)0) >= 0 || content.AsSpan().StartsWith("%PDF-"u8) || content.AsSpan().StartsWith(new byte[] { 0x50, 0x4b }))
            throw Failure(DocumentUploadError.InvalidFileHeader, "The text document contains a binary signature.");
        try { _ = StrictUtf8.GetString(content); }
        catch (DecoderFallbackException) { throw Failure(DocumentUploadError.InvalidFileHeader, "The text document must be valid UTF-8."); }
    }

    private static void ValidateDocx(byte[] content, DocumentUploadOptions options)
    {
        if (content.Length < 4 || !content.AsSpan(0, 2).SequenceEqual(new byte[] { 0x50, 0x4b }))
            throw Failure(DocumentUploadError.InvalidFileHeader, "The DOCX ZIP signature is invalid.");
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count > options.MaximumArchiveEntries)
                throw Failure(DocumentUploadError.ArchiveExpansionLimitExceeded, "The DOCX contains too many archive entries.");
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                checked { expanded += entry.Length; }
                if (expanded > options.MaximumExpandedArchiveBytes || expanded > Math.Max(content.LongLength, 1) * options.MaximumArchiveExpansionRatio)
                    throw Failure(DocumentUploadError.ArchiveExpansionLimitExceeded, "The DOCX exceeds configured archive expansion limits.");
            }
            var contentTypes = archive.GetEntry("[Content_Types].xml");
            var document = archive.GetEntry("word/document.xml");
            if (contentTypes is null || document is null)
                throw Failure(DocumentUploadError.MalformedArchive, "The archive is not a valid DOCX document.");
            ReadEntryFully(contentTypes);
            ReadEntryFully(document);
        }
        catch (DocumentUploadValidationException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            throw Failure(DocumentUploadError.MalformedArchive, "The DOCX archive is malformed.");
        }
    }

    private static void ReadEntryFully(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        Span<byte> buffer = stackalloc byte[16 * 1024];
        long readTotal = 0;
        var crc = uint.MaxValue;
        while (true)
        {
            var read = source.Read(buffer);
            if (read == 0) break;
            readTotal += read;
            if (readTotal > entry.Length) throw new InvalidDataException("DOCX entry expanded beyond its declared length.");
            foreach (var value in buffer[..read])
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }
        if (readTotal != entry.Length) throw new InvalidDataException("DOCX entry ended before its declared length.");
        if (~crc != entry.Crc32) throw new InvalidDataException("DOCX entry checksum is invalid.");
    }

    private static string CanonicalContentType(string extension) => extension switch
    {
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".pdf" => "application/pdf",
        ".docx" => DocxMime,
        _ => throw new UnreachableException()
    };

    private static void ValidateOptions(DocumentUploadOptions options)
    {
        if (options.MaximumBytes is < 1 or > int.MaxValue || options.MaximumArchiveEntries < 1 || options.MaximumExpandedArchiveBytes < 1 || options.MaximumArchiveExpansionRatio < 1)
            throw new InvalidOperationException("Document upload limits are invalid.");
    }

    private static DocumentUploadValidationException Failure(DocumentUploadError error, string message) => new(error, message);
}

public interface IKnowledgeDocumentStore
{
    Task<PendingDocumentUpload?> StageAsync(DocumentStageRequest request, CancellationToken cancellationToken);
    Task<PendingDocumentUpload?> GetRetryableAsync(Guid documentId, CancellationToken cancellationToken);
    Task<PendingDocumentUpload?> GetRecoverableAsync(Guid versionId, CancellationToken cancellationToken);
    Task<bool> MarkUploadedAsync(PendingDocumentUpload upload, StoredObject stored, CancellationToken cancellationToken);
    Task MarkFailedAsync(PendingDocumentUpload upload, CancellationToken cancellationToken);
    Task<bool> RequestPhysicalDeleteAsync(Guid documentId, CancellationToken cancellationToken);
}

public sealed record DocumentStageRequest(Guid? DocumentId, string DisplayName, ValidatedDocument Document);
public sealed record PendingDocumentUpload(Guid DocumentId, Guid VersionId, int Version, string ObjectKey, string SafeFileName,
    string ContentType, string Sha256, byte[] Content, string State = "uploading", string? PublicUrl = null);
public sealed record DocumentUploadResult(Guid DocumentId, Guid VersionId, int Version, string State, string? PublicUrl,
    string ObjectKey, string SafeFileName, string Sha256, long SizeBytes, bool PublicReadRiskAccepted, bool ProviderSucceeded);

public sealed class DuplicateDocumentContentException : Exception { }
public sealed class DocumentNotFoundException : Exception { }
public sealed class DocumentNotRetryableException : Exception { }
public sealed class DocumentDeletedException : Exception { }

public sealed class DocumentUploadService(
    DocumentUploadOptions options,
    bool publicReadRiskAccepted,
    IObjectStorage storage,
    IKnowledgeDocumentStore store)
{
    public async Task<DocumentUploadResult> UploadAsync(Guid? documentId, string clientFileName, string contentType, Stream content, CancellationToken cancellationToken)
    {
        var validated = await DocumentUploadValidator.ValidateAndBufferAsync(clientFileName, contentType, content, options, cancellationToken);
        var pending = await store.StageAsync(new DocumentStageRequest(documentId, SafeDisplayName(clientFileName), validated), cancellationToken)
            ?? throw new DuplicateDocumentContentException();
        return await UploadPendingAsync(pending, cancellationToken);
    }

    public async Task<DocumentUploadResult> RetryAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var pending = await store.GetRetryableAsync(documentId, cancellationToken) ?? throw new DocumentNotRetryableException();
        return await UploadPendingAsync(pending, cancellationToken);
    }

    public async Task<bool> RecoverAsync(LeasedDurableJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<UploadJobPayload>(job.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Knowledge upload job payload is invalid.");
        if (payload.VersionId == Guid.Empty) throw new InvalidOperationException("Knowledge upload job version is missing.");
        var pending = await store.GetRecoverableAsync(payload.VersionId, cancellationToken);
        if (pending is null) return true;
        try { return (await UploadPendingAsync(pending, cancellationToken)).ProviderSucceeded; }
        catch (DocumentDeletedException) { return true; }
    }

    public async Task RequestPhysicalDeleteAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await store.RequestPhysicalDeleteAsync(documentId, cancellationToken)) throw new DocumentNotFoundException();
    }

    private async Task<DocumentUploadResult> UploadPendingAsync(PendingDocumentUpload pending, CancellationToken cancellationToken)
    {
        try
        {
            await using var content = new MemoryStream(pending.Content, writable: false);
            var stored = await storage.PutAsync(pending.ObjectKey, content, pending.ContentType, cancellationToken);
            if (!string.Equals(stored.ObjectKey, pending.ObjectKey, StringComparison.Ordinal))
                throw new IOException("The object storage response did not preserve the requested key.");
            if (!await store.MarkUploadedAsync(pending, stored, cancellationToken)) throw new DocumentDeletedException();
            return ToResult(pending with { State = "uploaded", PublicUrl = stored.PublicUrl.AbsoluteUri }, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (DocumentDeletedException) { throw; }
        catch
        {
            await store.MarkFailedAsync(pending, cancellationToken);
            return ToResult(pending with { State = "failed", PublicUrl = null }, false);
        }
    }

    private sealed class UploadJobPayload
    {
        public Guid VersionId { get; init; }
    }

    private DocumentUploadResult ToResult(PendingDocumentUpload upload, bool succeeded) => new(upload.DocumentId, upload.VersionId,
        upload.Version, upload.State, upload.PublicUrl, upload.ObjectKey, upload.SafeFileName, upload.Sha256, upload.Content.LongLength,
        publicReadRiskAccepted, succeeded);

    private static string SafeDisplayName(string clientName)
    {
        var value = Path.GetFileName(clientName).Trim();
        if (string.IsNullOrWhiteSpace(value)) value = "document";
        return value.Length <= 256 ? value : value[..256];
    }
}
