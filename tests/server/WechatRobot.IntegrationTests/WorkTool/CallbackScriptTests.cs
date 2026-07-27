using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class CallbackScriptTests
{
    [Fact]
    public async Task Preview_uses_production_defaults_without_credentials_or_network_access()
    {
        var script = ScriptPath("update-worktool-callback.ps1");
        var source = await File.ReadAllTextAsync(script, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("WorkToolRobotId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CallbackToken", source, StringComparison.Ordinal);

        var result = await RunPowerShellAsync(script, []);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("API origin: https://wxrobot.aavisa.com", result.Output, StringComparison.Ordinal);
        Assert.Contains("Public callback origin: https://wxrobot.aavisa.com", result.Output, StringComparison.Ordinal);
        Assert.Contains("Actions: configure message callback and command-result callback", result.Output, StringComparison.Ordinal);
        Assert.Contains("Preview only. Re-run with -Apply.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_logs_in_selects_the_only_enabled_robot_and_configures_both_callbacks()
    {
        var port = AvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        const string token = "fake-login-bearer-token";
        const string password = "fake-login-password";
        var robotConfigId = Guid.NewGuid();
        var captured = CaptureSetupAsync(listener, robotConfigId);

        var result = await RunPowerShellAsync(ScriptPath("update-worktool-callback.ps1"),
        [
            "-ApiBaseUrl", $"http://127.0.0.1:{port}/",
            "-Email", "admin@example.test",
            "-TestPassword", password,
            "-Apply",
            "-Confirmation", "UPDATE"
        ]);
        var requests = await captured.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
        [
            "/api/auth/login",
            "/api/admin/worktool/robots",
            $"/api/admin/worktool/robots/{robotConfigId:D}/message-callback/configure",
            $"/api/admin/worktool/robots/{robotConfigId:D}/command-result-callback/configure"
        ], requests.Select(request => request.Path).ToArray());
        Assert.Null(requests[0].Authorization);
        Assert.All(requests.Skip(1), request => Assert.Equal($"Bearer {token}", request.Authorization));
        using (var login = JsonDocument.Parse(requests[0].Body))
        {
            Assert.Equal("admin@example.test", login.RootElement.GetProperty("email").GetString());
            Assert.Equal(password, login.RootElement.GetProperty("password").GetString());
        }
        Assert.All(requests.Skip(1), request =>
        {
            Assert.DoesNotContain(token, request.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(password, request.Body, StringComparison.Ordinal);
        });
        Assert.Contains("Selected robot: Main robot", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(password, result.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(robotConfigId.ToString("D"), result.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", result.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fake_WorkTool_declares_all_supported_routes_and_correlated_result_callback()
    {
        var source = await File.ReadAllTextAsync(
            ScriptPath("Start-FakeWorkTool.ps1"),
            TestContext.Current.CancellationToken);

        foreach (var route in new[]
                 {
                     "/robot/robotInfo/get",
                     "/robot/robotInfo/online",
                     "/robot/robotInfo/update",
                     "/robot/robotInfo/callBack/get",
                     "/robot/robotInfo/callBack/bind",
                     "/robot/robotInfo/callBack/deleteByType",
                     "/wework/sendRawMessage"
                 })
            Assert.Contains(route, source, StringComparison.Ordinal);
        Assert.Contains("fake-command-", source, StringComparison.Ordinal);
        Assert.Contains("messageId", source, StringComparison.Ordinal);
        Assert.Contains("errorCode", source, StringComparison.Ordinal);
        Assert.Contains("successList", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_WorkTool_returns_a_deterministic_id_and_posts_the_correlated_result()
    {
        var fakePort = AvailablePort();
        var callbackPort = AvailablePort();
        var logPath = Path.Combine(Path.GetTempPath(), $"fake-worktool-{Guid.NewGuid():N}.log");
        using var callbackListener = new HttpListener();
        callbackListener.Prefixes.Add($"http://127.0.0.1:{callbackPort}/");
        callbackListener.Start();
        using var fake = StartPowerShell(
            ScriptPath("Start-FakeWorkTool.ps1"),
            ["-LogPath", logPath, "-Port", fakePort.ToString()]);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{fakePort}/") };
            await WaitUntilReadyAsync(client);
            using var bind = await client.PostAsJsonAsync(
                "robot/robotInfo/callBack/bind?robotId=fake-robot",
                new { type = 1, callBackUrl = $"http://127.0.0.1:{callbackPort}/result" },
                TestContext.Current.CancellationToken);
            bind.EnsureSuccessStatusCode();

            var callbackTask = callbackListener.GetContextAsync();
            using var send = await client.PostAsJsonAsync(
                "wework/sendRawMessage?robotId=fake-robot",
                new { socketType = 2, list = new[] { new { type = 203, titleList = new[] { "Fake Group" }, receivedContent = "test" } } },
                TestContext.Current.CancellationToken);
            send.EnsureSuccessStatusCode();
            using var sendJson = JsonDocument.Parse(await send.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var messageId = sendJson.RootElement.GetProperty("data").GetString();

            var callback = await callbackTask.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            using var reader = new StreamReader(callback.Request.InputStream, callback.Request.ContentEncoding);
            using var callbackJson = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
            callback.Response.StatusCode = 200;
            callback.Response.Close();

            Assert.Equal("fake-command-000001", messageId);
            Assert.Equal(messageId, callbackJson.RootElement.GetProperty("messageId").GetString());
            Assert.Equal(0, callbackJson.RootElement.GetProperty("errorCode").GetInt32());
            Assert.Equal(1, callbackJson.RootElement.GetProperty("type").GetInt32());
            Assert.Equal(JsonValueKind.Array, callbackJson.RootElement.GetProperty("successList").ValueKind);
        }
        finally
        {
            if (!fake.HasExited) fake.Kill(entireProcessTree: true);
            await fake.WaitForExitAsync(TestContext.Current.CancellationToken);
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    private static async Task<IReadOnlyList<CapturedRequest>> CaptureSetupAsync(
        HttpListener listener,
        Guid robotConfigId)
    {
        var requests = new List<CapturedRequest>();
        for (var index = 0; index < 4; index++)
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            requests.Add(new(
                context.Request.Url!.AbsolutePath,
                context.Request.Headers["Authorization"],
                body));
            string responseBody;
            if (index == 0)
            {
                responseBody = """{"accessToken":"fake-login-bearer-token","tokenType":"Bearer","expiresInSeconds":900,"user":{"id":"00000000-0000-0000-0000-000000000001","email":"admin@example.test","displayName":"Admin","roles":["Admin"]}}""";
            }
            else if (index == 1)
            {
                responseBody = $$"""[{"id":"{{robotConfigId:D}}","name":"Main robot","robotReference":"configured","isEnabled":true}]""";
            }
            else
            {
                responseBody = """{"succeeded":true}""";
            }
            var bytes = Encoding.UTF8.GetBytes(responseBody);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
            context.Response.Close();
        }
        return requests;
    }

    private static async Task<ScriptResult> RunPowerShellAsync(string script, IReadOnlyList<string> arguments)
    {
        using var process = StartPowerShell(script, arguments);
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new(process.ExitCode, await outputTask, await errorTask);
    }

    private static Process StartPowerShell(string script, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
    }

    private static async Task WaitUntilReadyAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(
                    "robot/robotInfo/get?robotId=fake-robot",
                    TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Fake WorkTool did not start.");
    }

    private static int AvailablePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private static string ScriptPath(string name, [CallerFilePath] string sourcePath = "")
    {
        var root = Directory.GetParent(sourcePath)!.Parent!.Parent!.Parent!.Parent!.FullName;
        var path = Path.Combine(root, "scripts", name);
        Assert.True(File.Exists(path), $"Script was not found: {path}");
        return path;
    }

    private sealed record CapturedRequest(string Path, string? Authorization, string Body);
    private sealed record ScriptResult(int ExitCode, string Output, string Error)
    {
        public string AllOutput => $"{Output}\n{Error}";
    }
}
