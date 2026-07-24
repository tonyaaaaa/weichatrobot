using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Models;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class RobotCallbackConfigurationTests : IClassFixture<ModelConfigurationApiFactory>
{
    private readonly ModelConfigurationApiFactory _factory;

    public RobotCallbackConfigurationTests(ModelConfigurationApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Message_and_result_callback_configuration_share_one_encrypted_secret_without_exposing_it()
    {
        var robot = await SeedRobotAsync("legacy-fake-secret");
        var recorder = _factory.Services.GetRequiredService<RecordingWorkToolClient>();
        recorder.Reset();
        using var client = _factory.CreateClient();

        var messageResponse = await client.PostAsJsonAsync(
            $"/api/admin/worktool/robots/{robot.Id:D}/message-callback/configure",
            new { publicBaseUrl = "https://callbacks.example.test", replyAll = true },
            TestContext.Current.CancellationToken);
        var messageBody = await messageResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        messageResponse.EnsureSuccessStatusCode();

        var storedAfterMessage = await LoadRobotAsync(robot.Id);
        var protector = _factory.Services.GetRequiredService<ISecretProtector>();
        var callbackSecret = protector.Unprotect(storedAfterMessage.EncryptedCallbackSecret!);
        Assert.Equal(Hash(callbackSecret), storedAfterMessage.CallbackSecretHash);
        Assert.Equal(Hash("legacy-fake-secret"), storedAfterMessage.PreviousCallbackSecretHash);
        Assert.True(storedAfterMessage.PreviousCallbackSecretExpiresAtUtc > DateTime.UtcNow);
        Assert.DoesNotContain(callbackSecret, messageBody, StringComparison.Ordinal);
        Assert.DoesNotContain(robot.WorkToolRobotId, messageBody, StringComparison.Ordinal);
        Assert.Contains($"token={callbackSecret}", recorder.LastMessageCallbackRequest!.CallbackUrl.Query, StringComparison.Ordinal);

        var resultResponse = await client.PostAsJsonAsync(
            $"/api/admin/worktool/robots/{robot.Id:D}/command-result-callback/configure",
            new { publicBaseUrl = "https://callbacks.example.test" },
            TestContext.Current.CancellationToken);
        var resultBody = await resultResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        resultResponse.EnsureSuccessStatusCode();

        var storedAfterResult = await LoadRobotAsync(robot.Id);
        Assert.Equal(storedAfterMessage.EncryptedCallbackSecret, storedAfterResult.EncryptedCallbackSecret);
        Assert.Equal(1, recorder.LastEventCallbackType);
        Assert.Contains($"token={callbackSecret}", recorder.LastEventCallbackUrl!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(callbackSecret, resultBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkTool_rejection_restores_the_legacy_callback_hash()
    {
        var legacySecret = $"legacy-{Guid.NewGuid():N}";
        var robot = await SeedRobotAsync(legacySecret);
        var recorder = _factory.Services.GetRequiredService<RecordingWorkToolClient>();
        recorder.Reset();
        recorder.NextMessageCallbackResult = new(false, true, true, "worktool_code_1001");
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/admin/worktool/robots/{robot.Id:D}/message-callback/configure",
            new { publicBaseUrl = "https://callbacks.example.test", replyAll = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var stored = await LoadRobotAsync(robot.Id);
        Assert.Equal(Hash(legacySecret), stored.CallbackSecretHash);
        Assert.Null(stored.EncryptedCallbackSecret);
        Assert.Null(stored.PreviousCallbackSecretHash);
        Assert.Null(stored.PreviousCallbackSecretExpiresAtUtc);
    }

    [Fact]
    public async Task New_robot_receives_an_encrypted_callback_secret_at_creation()
    {
        var id = Guid.NewGuid();
        var submittedRobotId = $"fake-robot-{Guid.NewGuid():N}";
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/worktool/robots/{id:D}",
            new
            {
                name = "new callback robot",
                workToolRobotId = submittedRobotId,
                isEnabled = true
            },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var stored = await LoadRobotAsync(id);
        Assert.NotNull(stored.EncryptedCallbackSecret);
        var plaintext = _factory.Services.GetRequiredService<ISecretProtector>()
            .Unprotect(stored.EncryptedCallbackSecret);
        Assert.Equal(Hash(plaintext), stored.CallbackSecretHash);
        Assert.DoesNotContain(plaintext, body, StringComparison.Ordinal);
        Assert.DoesNotContain(submittedRobotId, body, StringComparison.Ordinal);
    }

    [Fact]
    public void Callback_secret_verifier_accepts_previous_hash_only_during_grace()
    {
        var now = new DateTime(2026, 7, 24, 8, 0, 0, DateTimeKind.Utc);

        Assert.True(WorkToolCallbackSecretVerifier.Matches(
            "old-fake-secret",
            Hash("new-fake-secret"),
            Hash("old-fake-secret"),
            now.AddMinutes(1),
            now));
        Assert.False(WorkToolCallbackSecretVerifier.Matches(
            "old-fake-secret",
            Hash("new-fake-secret"),
            Hash("old-fake-secret"),
            now.AddTicks(-1),
            now));
    }

    private async Task<RobotConfigEntity> SeedRobotAsync(string legacySecret)
    {
        var robot = new RobotConfigEntity
        {
            Name = $"callback-{Guid.NewGuid():N}",
            WorkToolRobotId = $"legacy-placeholder-{Guid.NewGuid():N}",
            EncryptedWorkToolRobotId = _factory.Services.GetRequiredService<ISecretProtector>()
                .Protect($"fake-robot-{Guid.NewGuid():N}"),
            CallbackRouteCode = Guid.NewGuid().ToString("N"),
            CallbackSecretHash = Hash(legacySecret)
        };
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        database.RobotConfigs.Add(robot);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return robot;
    }

    private async Task<RobotConfigEntity> LoadRobotAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .RobotConfigs.AsNoTracking()
            .SingleAsync(item => item.Id == id, TestContext.Current.CancellationToken);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
