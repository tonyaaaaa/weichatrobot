using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Identity;

namespace WechatRobot.IntegrationTests.Memory;

public sealed class MemoryEndpointTests : IClassFixture<UserAdministrationApiFactory>
{
    private readonly UserAdministrationApiFactory factory;

    public MemoryEndpointTests(UserAdministrationApiFactory factory) =>
        this.factory = factory;

    [Fact]
    public async Task Candidate_list_requires_authorization_and_manual_promotion_is_versioned()
    {
        await factory.ResetAsync();
        using var anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(
                "/api/admin/memory/candidates?page=1&pageSize=20",
                TestContext.Current.CancellationToken)).StatusCode);

        var admin = await factory.CreateUserAsync(
            "memory-admin@example.test",
            "Memory Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        var candidateId = await SeedCandidateAsync("UserPreference");
        using var client = factory.CreateAdminClient(admin);

        var page = await client.GetAsync(
            "/api/admin/memory/candidates?status=accumulating&page=1&pageSize=20",
            TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        Assert.Contains(candidateId.ToString("D"), await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var stale = await client.PostAsJsonAsync(
            $"/api/admin/memory/candidates/{candidateId:D}/promote",
            new { expectedVersion = 99 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var promoted = await client.PostAsJsonAsync(
            $"/api/admin/memory/candidates/{candidateId:D}/promote",
            new { expectedVersion = 1 },
            TestContext.Current.CancellationToken);
        promoted.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var candidate = await database.MemoryCandidates.AsNoTracking()
            .SingleAsync(x => x.Id == candidateId, TestContext.Current.CancellationToken);
        Assert.Equal("promoted", candidate.Status);
        Assert.NotNull(candidate.PromotedMemoryEntryId);
        Assert.True(await database.MemoryEntries.AnyAsync(
            x => x.Id == candidate.PromotedMemoryEntryId,
            TestContext.Current.CancellationToken));
        Assert.True(await database.AdministrationAudits.AnyAsync(
            x => x.TargetId == candidateId.ToString("D"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Business_fact_cannot_be_promoted_as_behavior_memory()
    {
        await factory.ResetAsync();
        var admin = await factory.CreateUserAsync(
            "fact-admin@example.test",
            "Fact Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        var candidateId = await SeedCandidateAsync("BusinessFact");
        using var client = factory.CreateAdminClient(admin);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/memory/candidates/{candidateId:D}/promote",
            new { expectedVersion = 1 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("knowledge learning review",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> SeedCandidateAsync(string memoryType)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var candidate = new MemoryCandidateEntity
        {
            ScopeType = "Global",
            ScopeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])),
            MemoryType = memoryType,
            Content = memoryType == "BusinessFact" ? "营业时间是九点" : "回答结论优先",
            NormalizedKey = memoryType,
            Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(memoryType))),
            Confidence = .9,
            IsExplicit = true,
            ObservationCount = 1,
            DistinctSessionCount = 1,
            DistinctDayCount = 1,
            Status = "accumulating",
            Version = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        database.MemoryCandidates.Add(candidate);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return candidate.Id;
    }
}
