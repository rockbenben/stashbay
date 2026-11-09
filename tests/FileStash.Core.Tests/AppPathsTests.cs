using FileStash.Core.Utils;

namespace FileStash.Core.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _tempDir;

    public AppPathsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "filestash-apppaths-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TempDirectory.DeleteRecursively(_tempDir);
    }

    [Fact]
    public void AppPaths_WithExplicitPaths_SetsStashBaySubdirectories()
    {
        var local = Path.Combine(_tempDir, "local");
        var roaming = Path.Combine(_tempDir, "roaming");

        var paths = new AppPaths(local, roaming);

        Assert.Equal(Path.Combine(local, "StashBay"), paths.RootDirectory);
        Assert.Equal(Path.Combine(local, "StashBay", "stashbay.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(local, "StashBay", "blobs"), paths.BlobDirectory);
        Assert.Equal(Path.Combine(local, "StashBay", "blobs", "tmp"), paths.BlobTempDirectory);
        Assert.Equal(Path.Combine(local, "StashBay", "export"), paths.ExportDirectory);
        Assert.Equal(Path.Combine(local, "StashBay", "logs"), paths.LogDirectory);
        Assert.Equal(Path.Combine(roaming, "StashBay", "settings.json"), paths.SettingsPath);
    }

    [Fact]
    public void AppPaths_WhenLegacyFileStashDirectoryExists_FallsBackToLegacyDirectory()
    {
        var local = Path.Combine(_tempDir, "local");
        var roaming = Path.Combine(_tempDir, "roaming");
        var legacyLocal = Path.Combine(local, "FileStash");
        var legacyRoaming = Path.Combine(roaming, "FileStash");

        Directory.CreateDirectory(legacyLocal);
        Directory.CreateDirectory(legacyRoaming);
        var legacyDb = Path.Combine(legacyLocal, "filestash.db");
        var legacySettings = Path.Combine(legacyRoaming, "settings.json");
        File.WriteAllText(legacyDb, "");
        File.WriteAllText(legacySettings, "{}");

        var paths = new AppPaths(local, roaming);

        Assert.Equal(legacyLocal, paths.RootDirectory);
        Assert.Equal(legacyDb, paths.DatabasePath);
        Assert.Equal(legacySettings, paths.SettingsPath);
    }

    [Fact]
    public void AppPaths_EnsureDirectories_CreatesAllRequiredDirectories()
    {
        var local = Path.Combine(_tempDir, "local");
        var roaming = Path.Combine(_tempDir, "roaming");

        var paths = new AppPaths(local, roaming);
        paths.EnsureDirectories();

        Assert.True(Directory.Exists(paths.RootDirectory));
        Assert.True(Directory.Exists(paths.BlobDirectory));
        Assert.True(Directory.Exists(paths.BlobTempDirectory));
        Assert.True(Directory.Exists(paths.ExportDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
        Assert.True(Directory.Exists(Path.GetDirectoryName(paths.SettingsPath)));
    }

    [Fact]
    public void AppPaths_EnvironmentVariablePrecedence_StashBayHomeTakesPrecedenceOverLegacyHome()
    {
        var stashBayDir = Path.Combine(_tempDir, "stashbay-home");
        var fileStashDir = Path.Combine(_tempDir, "filestash-home");

        var oldStashBay = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        var oldFileStash = Environment.GetEnvironmentVariable(AppPaths.LegacyHomeVariable);

        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, stashBayDir);
            Environment.SetEnvironmentVariable(AppPaths.LegacyHomeVariable, fileStashDir);

            var paths = new AppPaths();
            Assert.Equal(Path.Combine(stashBayDir, "StashBay"), paths.RootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, oldStashBay);
            Environment.SetEnvironmentVariable(AppPaths.LegacyHomeVariable, oldFileStash);
        }
    }

    [Fact]
    public void AppPaths_LegacyEnvironmentVariable_WorksWhenStashBayHomeNotSet()
    {
        var fileStashDir = Path.Combine(_tempDir, "filestash-home");

        var oldStashBay = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        var oldFileStash = Environment.GetEnvironmentVariable(AppPaths.LegacyHomeVariable);

        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, null);
            Environment.SetEnvironmentVariable(AppPaths.LegacyHomeVariable, fileStashDir);

            var paths = new AppPaths();
            Assert.Equal(Path.Combine(fileStashDir, "StashBay"), paths.RootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, oldStashBay);
            Environment.SetEnvironmentVariable(AppPaths.LegacyHomeVariable, oldFileStash);
        }
    }
}
