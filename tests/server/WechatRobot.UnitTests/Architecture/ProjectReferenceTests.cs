using System.Xml.Linq;

namespace WechatRobot.UnitTests.Architecture;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void Domain_and_application_follow_the_approved_dependency_direction()
    {
        var solutionRoot = SolutionRoot.Find();
        var domain = ProjectFile.Load(solutionRoot, "src/server/WechatRobot.Domain/WechatRobot.Domain.csproj");
        var application = ProjectFile.Load(solutionRoot, "src/server/WechatRobot.Application/WechatRobot.Application.csproj");

        Assert.Empty(domain.ProjectReferences);
        Assert.Contains(application.ProjectReferences, reference => reference.TargetPath == domain.Path);
        Assert.DoesNotContain(application.ProjectReferences, reference =>
            reference.TargetPath.EndsWith("WechatRobot.Infrastructure.csproj", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ProjectFile
    {
        private ProjectFile(string path, IReadOnlyList<ProjectReference> projectReferences)
        {
            Path = path;
            ProjectReferences = projectReferences;
        }

        public string Path { get; }

        public IReadOnlyList<ProjectReference> ProjectReferences { get; }

        public static ProjectFile Load(string solutionRoot, string relativePath)
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionRoot, relativePath));
            var document = XDocument.Load(path);
            var projectDirectory = System.IO.Path.GetDirectoryName(path)!;
            var references = document
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => new ProjectReference(System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDirectory, include!))))
                .ToArray();

            return new ProjectFile(path, references);
        }
    }

    private sealed record ProjectReference(string TargetPath);

    private static class SolutionRoot
    {
        public static string Find()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(System.IO.Path.Combine(directory.FullName, "WechatRobot.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate WechatRobot.slnx from the test output directory.");
        }
    }
}
