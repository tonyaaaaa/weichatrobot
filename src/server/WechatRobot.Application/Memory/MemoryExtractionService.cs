using WechatRobot.Application.Models;

namespace WechatRobot.Application.Memory;

public sealed class MemoryExtractionService(IMemoryExtractor extractor)
{
    public async Task<MemoryExtractionResult> ExtractAsync(
        ModelProviderConfiguration configuration,
        MemoryExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Messages.Count is 0 or > 100)
        {
            throw new MemoryExtractionException("memory_content_invalid");
        }

        try
        {
            return await extractor.ExtractAsync(configuration, context, cancellationToken);
        }
        catch (MemoryExtractionException)
        {
            throw;
        }
        catch (ModelUnavailableException)
        {
            throw new MemoryExtractionException("memory_model_unavailable");
        }
    }
}
