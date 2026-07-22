using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge.Ocr;

public sealed class OcrProcessingOptions
{
    public int MinimumExtractedTextCharacters { get; set; } = 20;
    public int MaximumPages { get; set; } = 100;
    public long MaximumImagePixels { get; set; } = 16_000_000;
    public long MaximumRenderedBytes { get; set; } = 6 * 1024 * 1024;
    public int RenderTimeoutSeconds { get; set; } = 15;
    public int PageLeaseSeconds { get; set; } = 60;
}

public sealed class OcrIncompleteException() : DocumentParsingException(DocumentParsingError.OcrIncomplete, "OCR did not complete every PDF page.");

public sealed class ScannedPdfOcrService(
    WechatRobotDbContext database,
    IPdfPageRenderer renderer,
    IOcrClient client,
    OcrProcessingOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool ShouldFallback(ParsedDocument document) =>
        document.Blocks.Sum(block => block.Text.Length) < options.MinimumExtractedTextCharacters;

    public async Task<ParsedDocument> RecognizeAsync(Guid versionId, Stream pdf, DocumentProcessingContext context)
    {
        context.Checkpoint("ocr-page-count-before");
        Reset(pdf);
        var pageCount = await WithRenderDeadline(token => renderer.GetPageCountAsync(pdf, context), context);
        context.Checkpoint("ocr-page-count-after");
        if (pageCount < 1 || pageCount > Math.Min(options.MaximumPages, context.Limits.MaximumPages))
            throw new DocumentParsingException(DocumentParsingError.PageLimitExceeded, "The PDF exceeds the OCR page limit.");

        var owner = $"ocr-{Guid.NewGuid():N}";
        var claimed = new List<int>();
        for (var page = 1; page <= pageCount; page++)
            if (await TryClaimAsync(versionId, page, owner, context.Token)) claimed.Add(page);

        if (claimed.Count > 0)
        {
            try
            {
                Reset(pdf);
                var rendered = await WithRenderDeadline(token => renderer.RenderAsync(pdf, claimed, context), context);
                ValidateRendered(claimed, rendered, context);
                ReserveHttpRequest(rendered, context);
                var results = await client.RecognizeAsync(rendered, context.Token);
                await PersistResultsAsync(versionId, owner, claimed, results, context);
            }
            catch (OcrClientException exception) when (exception.Error == OcrClientError.Timeout)
            {
                await MarkClaimsFailedAsync(versionId, owner, claimed, "OCR page timed out.", context.Token);
                throw new DocumentParsingException(DocumentParsingError.Timeout, "OCR service timed out.", exception);
            }
            catch (Exception) when (!context.Token.IsCancellationRequested)
            {
                await MarkClaimsFailedAsync(versionId, owner, claimed, "OCR page processing failed.", context.Token);
                throw;
            }
        }

        database.ChangeTracker.Clear();
        var pages = await database.KnowledgeOcrPages.AsNoTracking()
            .Where(item => item.KnowledgeDocumentVersionId == versionId)
            .OrderBy(item => item.PageNumber).ToArrayAsync(context.Token);
        if (pages.Length != pageCount || pages.Any(item => item.Status != "completed")) throw new OcrIncompleteException();
        var blocks = new List<ParsedBlock>();
        foreach (var page in pages)
        {
            var pageBlocks = JsonSerializer.Deserialize<OcrTextBlock[]>(page.BlocksJson) ?? [];
            var text = string.Join('\n', pageBlocks.OrderBy(item => item.Order).Select(item => item.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
            context.Reserve(checked((long)text.Length * sizeof(char) + 128), $"ocr-result:{page.PageNumber}");
            context.AddResultCharacters(text.Length, $"ocr-page:{page.PageNumber}");
            if (text.Length > 0) blocks.Add(new ParsedBlock(text, page.PageNumber, [], false, null, null));
        }
        if (blocks.Count == 0) throw new OcrIncompleteException();
        return new ParsedDocument(blocks);
    }

    private async Task<bool> TryClaimAsync(Guid versionId, int pageNumber, string owner, CancellationToken token)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var row = await database.KnowledgeOcrPages.SingleOrDefaultAsync(item => item.KnowledgeDocumentVersionId == versionId && item.PageNumber == pageNumber, token);
        if (row?.Status == "completed") return false;
        if (row?.Status == "processing" && row.LeaseExpiresAtUtc > now) return false;
        if (row is null)
        {
            row = new KnowledgeOcrPageEntity { KnowledgeDocumentVersionId = versionId, PageNumber = pageNumber };
            database.KnowledgeOcrPages.Add(row);
        }
        row.Status = "processing";
        row.Error = null;
        row.AttemptCount++;
        row.LeaseOwner = owner;
        row.LeaseExpiresAtUtc = now.AddSeconds(options.PageLeaseSeconds);
        row.UpdatedAtUtc = now;
        try { await database.SaveChangesAsync(token); return true; }
        catch (DbUpdateConcurrencyException) { database.ChangeTracker.Clear(); return false; }
        catch (DbUpdateException) { database.ChangeTracker.Clear(); return false; }
    }

    private void ValidateRendered(IReadOnlyList<int> claimed, IReadOnlyList<OcrRenderedPage> pages, DocumentProcessingContext context)
    {
        if (pages.Count != claimed.Count || !pages.Select(item => item.PageNumber).Order().SequenceEqual(claimed.Order()))
            throw new DocumentParsingException(DocumentParsingError.OcrIncomplete, "The PDF renderer returned an incomplete page set.");
        long total = 0;
        foreach (var page in pages)
        {
            long pixels;
            try { pixels = checked((long)page.Width * page.Height); total = checked(total + page.ImageBytes.LongLength); }
            catch (OverflowException exception) { throw new DocumentParsingException(DocumentParsingError.OcrLimitExceeded, "OCR render limits overflowed.", exception); }
            if (pixels > options.MaximumImagePixels || total > options.MaximumRenderedBytes)
                throw new DocumentParsingException(DocumentParsingError.OcrLimitExceeded, "OCR rendered page limits were exceeded.");
            context.Reserve(page.ImageBytes.LongLength, $"ocr-render:{page.PageNumber}");
        }
    }

    private static void ReserveHttpRequest(IReadOnlyList<OcrRenderedPage> pages, DocumentProcessingContext context)
    {
        long bytes = 0;
        try
        {
            foreach (var page in pages)
            {
                var base64Characters = checked(((page.ImageBytes.LongLength + 2) / 3) * 4);
                // Account for the temporary UTF-16 Base64 string, UTF-8 JSON payload and object overhead.
                bytes = checked(bytes + base64Characters * 3 + 256);
            }
        }
        catch (OverflowException exception)
        { throw new DocumentParsingException(DocumentParsingError.MemoryLimitExceeded, "OCR HTTP request memory accounting overflowed.", exception); }
        context.Reserve(bytes, "ocr-http-request");
    }

    private async Task PersistResultsAsync(Guid versionId, string owner, IReadOnlyList<int> claimed, IReadOnlyList<OcrPageResult> results, DocumentProcessingContext context)
    {
        var byPage = results.GroupBy(item => item.PageNumber).ToDictionary(group => group.Key, group => group.Single());
        foreach (var pageNumber in claimed)
        {
            var row = await database.KnowledgeOcrPages.SingleAsync(item => item.KnowledgeDocumentVersionId == versionId && item.PageNumber == pageNumber, context.Token);
            if (row.LeaseOwner != owner) continue;
            if (!byPage.TryGetValue(pageNumber, out var result))
            { row.Status = "failed"; row.Error = "OCR page result was missing."; }
            else if (result.Status == OcrPageStatus.Completed)
            { row.Status = "completed"; row.BlocksJson = JsonSerializer.Serialize(result.Blocks.OrderBy(item => item.Order)); row.Error = null; }
            else
            { row.Status = result.Status == OcrPageStatus.Timeout ? "timeout" : "failed"; row.Error = result.Error is null ? "OCR page failed." : result.Error[..Math.Min(512, result.Error.Length)]; }
            row.LeaseOwner = null;
            row.LeaseExpiresAtUtc = null;
            row.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }
        await database.SaveChangesAsync(context.Token);
    }

    private async Task MarkClaimsFailedAsync(Guid versionId, string owner, IReadOnlyList<int> claimed, string error, CancellationToken token)
    {
        var rows = await database.KnowledgeOcrPages.Where(item => item.KnowledgeDocumentVersionId == versionId && claimed.Contains(item.PageNumber) && item.LeaseOwner == owner).ToArrayAsync(token);
        foreach (var row in rows) { row.Status = "failed"; row.Error = error; row.LeaseOwner = null; row.LeaseExpiresAtUtc = null; }
        await database.SaveChangesAsync(token);
    }

    private async Task<T> WithRenderDeadline<T>(Func<CancellationToken, Task<T>> action, DocumentProcessingContext context)
    {
        var deadline = _timeProvider.GetUtcNow().AddSeconds(options.RenderTimeoutSeconds);
        context.Checkpoint("ocr-render-deadline-before");
        var result = await action(context.Token);
        context.Checkpoint("ocr-render-deadline-after");
        if (_timeProvider.GetUtcNow() >= deadline)
            throw new DocumentParsingException(DocumentParsingError.Timeout, "PDF rendering timed out.");
        return result;
    }

    private static void Reset(Stream stream)
    {
        if (!stream.CanSeek) throw new DocumentParsingException(DocumentParsingError.MalformedDocument, "OCR requires a seekable bounded PDF stream.");
        stream.Position = 0;
    }
}
