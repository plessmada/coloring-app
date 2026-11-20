using ColoringApp.Api.Integrations;
using ColoringApp.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ColoringApp.Api.Tests;

public sealed class PrivateGenerationFilesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Development_uses_the_ignored_directory_at_the_checkout_root(bool linkedWorktree)
    {
        using var checkout = new GenerationTestEnvironment();
        var gitMarker = Path.Combine(checkout.StorageRoot, ".git");
        if (linkedWorktree)
        {
            File.WriteAllText(gitMarker, "gitdir: ../.git/worktrees/checkout");
        }
        else
        {
            Directory.CreateDirectory(gitMarker);
        }

        var contentRoot = Directory.CreateDirectory(Path.Combine(checkout.StorageRoot, "backend", "api")).FullName;
        var environment = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = contentRoot
        }).Environment;

        var files = new PrivateGenerationFiles(new ConfigurationBuilder().Build(), environment);
        var key = Guid.NewGuid();
        Assert.Equal(Path.Combine(checkout.StorageRoot, ".generation-data", "approved", $"{key:N}.png"),
            files.PathFor(GenerationFileKind.Image, key));
    }

    [Theory]
    [InlineData("other")]
    [InlineData(".generation-data/public")]
    public void Development_rejects_storage_in_another_checkout_directory_or_the_web_root(string relativePath)
    {
        using var checkout = new GenerationTestEnvironment();
        Directory.CreateDirectory(Path.Combine(checkout.StorageRoot, ".git"));
        var webRoot = Directory.CreateDirectory(Path.Combine(checkout.StorageRoot, ".generation-data", "public")).FullName;
        var environment = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = checkout.StorageRoot,
            WebRootPath = webRoot
        }).Environment;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GenerationStorage:Root"] = Path.Combine(checkout.StorageRoot, relativePath)
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new PrivateGenerationFiles(configuration, environment));
    }
}
