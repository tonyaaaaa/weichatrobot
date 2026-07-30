using WechatRobot.Infrastructure.Configuration;

namespace WechatRobot.UnitTests.Configuration;

[Collection("EnvironmentVariables")]
public sealed class DotEnvFileLoaderTests
{
    [Fact]
    public void Loads_values_without_overwriting_existing_environment()
    {
        using var scope = new EnvironmentScope();
        var first = scope.UniqueName("FIRST");
        var second = scope.UniqueName("SECOND");
        scope.Set(first, "machine-wins");
        var file = scope.WriteEnv($"{first}=file-value\n{second}=file-value\n");
        scope.Set(DotEnvFileLoader.EnvironmentFileVariable, file);

        var loaded = DotEnvFileLoader.Load(scope.ApplicationDirectory);

        Assert.Equal(Path.GetFullPath(file), loaded);
        Assert.Equal("machine-wins", Environment.GetEnvironmentVariable(first));
        Assert.Equal("file-value", Environment.GetEnvironmentVariable(second));
    }

    [Fact]
    public void Preserves_equals_hash_and_semicolon_in_unquoted_values()
    {
        using var scope = new EnvironmentScope();
        var name = scope.UniqueName("COMPLEX");
        var file = scope.WriteEnv($"{name}=password=a#b;c\n");
        scope.Set(DotEnvFileLoader.EnvironmentFileVariable, file);

        DotEnvFileLoader.Load(scope.ApplicationDirectory);

        Assert.Equal("password=a#b;c", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Supports_single_and_double_quoted_values()
    {
        using var scope = new EnvironmentScope();
        var single = scope.UniqueName("SINGLE");
        var @double = scope.UniqueName("DOUBLE");
        var file = scope.WriteEnv($"{single}=' value # one '\n{@double}=\"value=two\"\n");
        scope.Set(DotEnvFileLoader.EnvironmentFileVariable, file);

        DotEnvFileLoader.Load(scope.ApplicationDirectory);

        Assert.Equal(" value # one ", Environment.GetEnvironmentVariable(single));
        Assert.Equal("value=two", Environment.GetEnvironmentVariable(@double));
    }

    [Fact]
    public void Ignores_blank_lines_comments_and_export_prefix()
    {
        using var scope = new EnvironmentScope();
        var name = scope.UniqueName("EXPORTED");
        var file = scope.WriteEnv($"\n  # comment\nexport {name}=value\n");
        scope.Set(DotEnvFileLoader.EnvironmentFileVariable, file);

        DotEnvFileLoader.Load(scope.ApplicationDirectory);

        Assert.Equal("value", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Uses_the_fixed_windows_default_path()
    {
        Assert.Equal(@"C:\wxrobot\config\.env", DotEnvFileLoader.DefaultPath);
    }

    [Fact]
    public void Missing_default_file_is_optional()
    {
        using var scope = new EnvironmentScope();
        scope.Clear(DotEnvFileLoader.EnvironmentFileVariable);
        var missingDefault = Path.Combine(scope.Root, "missing-default.env");

        var loaded = DotEnvFileLoader.Load(missingDefault);

        Assert.Null(loaded);
    }

    [Fact]
    public void Explicit_missing_file_fails()
    {
        using var scope = new EnvironmentScope();
        var missing = Path.Combine(scope.Root, "missing.env");
        scope.Set(DotEnvFileLoader.EnvironmentFileVariable, missing);

        var error = Assert.Throws<InvalidOperationException>(
            () => DotEnvFileLoader.Load(scope.ApplicationDirectory));

        Assert.Contains(DotEnvFileLoader.EnvironmentFileVariable, error.Message);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_names_fail()
    {
        using var scope = new EnvironmentScope();
        var name = scope.UniqueName("DUPLICATE");
        var file = scope.WriteEnv($"{name}=first\n{name}=second\n");
        scope.Set(DotEnvFileLoader.EnvironmentFileVariable, file);

        var error = Assert.Throws<InvalidOperationException>(
            () => DotEnvFileLoader.Load(scope.ApplicationDirectory));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line 2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_utf8_files_fail_before_loading_corrupted_values()
    {
        using var scope = new EnvironmentScope();
        var file = scope.WriteEnvBytes([0x4e, 0x41, 0x4d, 0x45, 0x3d, 0xff, 0x0a]);
        scope.Set(DotEnvFileLoader.EnvironmentFileVariable, file);

        var error = Assert.Throws<InvalidOperationException>(
            () => DotEnvFileLoader.Load(scope.ApplicationDirectory));

        Assert.Contains("UTF-8", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-equals")]
    [InlineData("NOT A KEY=value")]
    [InlineData("NAME='unmatched")]
    [InlineData("NAME=\"unmatched")]
    public void Malformed_lines_fail(string line)
    {
        using var scope = new EnvironmentScope();
        var file = scope.WriteEnv(line + "\n");
        scope.Set(DotEnvFileLoader.EnvironmentFileVariable, file);

        var error = Assert.Throws<InvalidOperationException>(
            () => DotEnvFileLoader.Load(scope.ApplicationDirectory));

        Assert.Contains("line 1", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
        private readonly HashSet<string> _tracked = new(StringComparer.Ordinal);

        public EnvironmentScope()
        {
            Root = Path.Combine(Path.GetTempPath(), $"wechatrobot-dotenv-{Guid.NewGuid():N}");
            ApplicationDirectory = Directory.CreateDirectory(Path.Combine(Root, "api")).FullName;
            Track(DotEnvFileLoader.EnvironmentFileVariable);
        }

        public string Root { get; }
        public string ApplicationDirectory { get; }

        public string UniqueName(string suffix)
        {
            var name = $"WECHATROBOT_DOTENV_TEST_{Guid.NewGuid():N}_{suffix}";
            Track(name);
            return name;
        }

        public string WriteEnv(string content)
        {
            var path = Path.Combine(Root, $"{Guid.NewGuid():N}.env");
            File.WriteAllText(path, content);
            return path;
        }

        public string WriteEnvBytes(byte[] content)
        {
            var path = Path.Combine(Root, $"{Guid.NewGuid():N}.env");
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Set(string name, string value)
        {
            Track(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Clear(string name)
        {
            Track(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        private void Track(string name)
        {
            if (_tracked.Add(name))
                _previous[name] = Environment.GetEnvironmentVariable(name);
        }

        public void Dispose()
        {
            foreach (var item in _previous)
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
