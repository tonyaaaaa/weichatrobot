using System.IO.Compression;
using System.Text;
using WechatRobot.Application.Knowledge;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class UploadValidationTests
{
    private static readonly DocumentUploadOptions Options = new()
    {
        MaximumBytes = 1024,
        MaximumArchiveEntries = 8,
        MaximumExpandedArchiveBytes = 4096,
        MaximumArchiveExpansionRatio = 20
    };

    [Fact]
    public async Task Unsupported_doc_is_rejected()
    {
        var error = await ValidateFailureAsync("legacy.doc", "application/msword", Encoding.UTF8.GetBytes("legacy"));
        Assert.Equal(DocumentUploadError.UnsupportedExtension, error);
    }

    [Theory]
    [InlineData("fake.pdf", "text/plain", "not a pdf")]
    [InlineData("fake.pdf", "application/pdf", "not a pdf")]
    [InlineData("fake.txt", "application/pdf", "%PDF-1.7")]
    [InlineData("fake.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "not a zip")]
    public async Task Extension_mime_and_header_spoofing_is_rejected(string fileName, string contentType, string content)
    {
        var error = await ValidateFailureAsync(fileName, contentType, Encoding.UTF8.GetBytes(content));
        Assert.True(error is DocumentUploadError.ContentTypeMismatch or DocumentUploadError.InvalidFileHeader or DocumentUploadError.MalformedArchive);
    }

    [Fact]
    public async Task Oversize_input_is_rejected_while_streaming()
    {
        var error = await ValidateFailureAsync("large.txt", "text/plain", new byte[Options.MaximumBytes + 1]);
        Assert.Equal(DocumentUploadError.FileTooLarge, error);
    }

    [Fact]
    public async Task Malformed_docx_archive_is_rejected()
    {
        var error = await ValidateFailureAsync("broken.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [0x50, 0x4b, 0x03, 0x04, 0x00]);
        Assert.Equal(DocumentUploadError.MalformedArchive, error);
    }

    [Fact]
    public async Task Configured_docx_expansion_limit_is_enforced()
    {
        await using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types />");
            WriteEntry(archive, "word/document.xml", new string('a', 5000));
        }

        var error = await ValidateFailureAsync("bomb.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", stream.ToArray());
        Assert.Equal(DocumentUploadError.ArchiveExpansionLimitExceeded, error);
    }

    [Fact]
    public async Task Corrupted_required_docx_entry_is_rejected_before_staging()
    {
        var bytes = CreateDocx("required-entry-corruption-" + new string('x', 256), CompressionLevel.Fastest);
        CorruptEntryPayload(bytes, "word/document.xml");

        var error = await ValidateFailureAsync("corrupt.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", bytes);
        Assert.Equal(DocumentUploadError.MalformedArchive, error);
    }

    [Fact]
    public async Task Validated_content_has_stable_hash_and_server_generated_safe_name()
    {
        var bytes = Encoding.UTF8.GetBytes("# safe markdown");
        var first = await DocumentUploadValidator.ValidateAndBufferAsync("../../unsafe name.md", "text/markdown", new MemoryStream(bytes), Options, TestContext.Current.CancellationToken);
        var second = await DocumentUploadValidator.ValidateAndBufferAsync("anything.md", "text/markdown", new MemoryStream(bytes), Options, TestContext.Current.CancellationToken);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal("source.md", first.SafeFileName);
        Assert.Equal(bytes, first.Content);
    }

    private static async Task<DocumentUploadError> ValidateFailureAsync(string fileName, string contentType, byte[] bytes)
    {
        var exception = await Assert.ThrowsAsync<DocumentUploadValidationException>(() =>
            DocumentUploadValidator.ValidateAndBufferAsync(fileName, contentType, new MemoryStream(bytes), Options, TestContext.Current.CancellationToken));
        return exception.Error;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.SmallestSize).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static byte[] CreateDocx(string documentXml, CompressionLevel level)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types />");
            using var writer = new StreamWriter(archive.CreateEntry("word/document.xml", level).Open(), Encoding.UTF8);
            writer.Write(documentXml);
        }
        return stream.ToArray();
    }

    private static void CorruptEntryPayload(byte[] archive, string entryName)
    {
        var name = Encoding.UTF8.GetBytes(entryName);
        for (var offset = 0; offset <= archive.Length - 30 - name.Length; offset++)
        {
            if (BitConverter.ToUInt32(archive, offset) != 0x04034b50) continue;
            var nameLength = BitConverter.ToUInt16(archive, offset + 26);
            var extraLength = BitConverter.ToUInt16(archive, offset + 28);
            if (nameLength != name.Length || !archive.AsSpan(offset + 30, nameLength).SequenceEqual(name)) continue;
            var compressedLength = BitConverter.ToInt32(archive, offset + 18);
            archive[offset + 30 + nameLength + extraLength + Math.Max(1, compressedLength / 2)] ^= 0xff;
            return;
        }
        throw new InvalidOperationException("Test ZIP entry was not found.");
    }
}
