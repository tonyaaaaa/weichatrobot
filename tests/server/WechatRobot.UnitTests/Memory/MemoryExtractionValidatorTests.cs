using System.Text.Json;
using WechatRobot.Application.Memory;
using WechatRobot.Domain.Memory;

namespace WechatRobot.UnitTests.Memory;

public sealed class MemoryExtractionValidatorTests
{
    private readonly MemoryExtractionValidator validator = new();
    private readonly Guid messageId = Guid.NewGuid();

    [Fact]
    public void Accepts_bounded_supported_candidate()
    {
        var result = validator.Validate(JsonSerializer.Serialize(new
        {
            memories = new[]
            {
                new
                {
                    type = "UserPreference",
                    content = "偏好结论优先",
                    confidence = .9,
                    @explicit = true,
                    sourceMessageIds = new[] { messageId }
                }
            }
        }), Context());

        var memory = Assert.Single(result.Memories);
        Assert.Equal(MemoryType.UserPreference, memory.Type);
        Assert.Equal(messageId, Assert.Single(memory.SourceMessageIds));
    }

    [Theory]
    [InlineData("""{"memories":[{"type":"Unknown","content":"x","confidence":0.9,"explicit":true,"sourceMessageIds":[]}]}""", "memory_content_invalid")]
    [InlineData("""{"memories":[{"type":"UserPreference","content":"password=secret","confidence":0.9,"explicit":true,"sourceMessageIds":["00000000-0000-0000-0000-000000000000"]}]}""", "memory_secret_detected")]
    [InlineData("""{"memories":[{"type":"UserPreference","content":"偏好简短","confidence":1.1,"explicit":true,"sourceMessageIds":["00000000-0000-0000-0000-000000000000"]}]}""", "memory_content_invalid")]
    public void Rejects_invalid_or_secret_candidates(string json, string failureCode)
    {
        var exception = Assert.Throws<MemoryExtractionException>(() => validator.Validate(json, Context()));
        Assert.Equal(failureCode, exception.FailureCode);
    }

    [Fact]
    public void Rejects_source_outside_current_window()
    {
        var json = JsonSerializer.Serialize(new
        {
            memories = new[]
            {
                new
                {
                    type = "UserPreference",
                    content = "偏好结论优先",
                    confidence = .9,
                    @explicit = true,
                    sourceMessageIds = new[] { Guid.NewGuid() }
                }
            }
        });

        var exception = Assert.Throws<MemoryExtractionException>(() => validator.Validate(json, Context()));
        Assert.Equal("memory_invalid_source", exception.FailureCode);
    }

    private MemoryExtractionContext Context() => new(
        MemoryScope.Create(
            MemoryScopeType.User,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Alice",
            "Alice"),
        [new MemoryExtractionMessage(messageId, "user", "请记住我偏好结论优先", DateTime.UtcNow)]);
}
