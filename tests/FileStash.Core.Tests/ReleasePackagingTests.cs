using System.Diagnostics;
using System.Text;

namespace FileStash.Core.Tests;

public sealed class ReleasePackagingTests
{
    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public async Task PackagingMustRejectSqliteFromAnotherArchitecture(string invalidArchitecture)
    {
        using var workspace = new TempWorkspace();
        var packages = Path.Combine(workspace.Root, "packages");
        foreach (var architecture in new[] { "x64", "arm64" })
        {
            var expected = architecture == "x64" ? (ushort)0x8664 : (ushort)0xaa64;
            foreach (var form in new[] { "", "-needs-dotnet10" })
            {
                var profile = Path.Combine(packages, "publish-" + architecture + form);
                Directory.CreateDirectory(profile);
                WritePe(Path.Combine(profile, "StashBay.exe"), expected);
                if (form.Length > 0)
                    WritePe(Path.Combine(profile, "e_sqlite3.dll"),
                        architecture == invalidArchitecture ? (ushort)(expected == 0x8664 ? 0xaa64 : 0x8664) : expected);
            }
        }
        var installer = Path.Combine(workspace.Root, "setup.exe");
        WritePe(installer, 0x14c);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "FileStash.sln")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(repository.FullName, "tools", "package-release.ps1"),
            "-AppName", "StashBay", "-Tag", "v1.0.0", "-PublishRoot", packages, "-Installer", installer,
            "-OutputDirectory", Path.Combine(workspace.Root, "output"), "-SkipSmoke" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree:true); }
        var diagnostics = await output + await error;
        Assert.True(process.ExitCode != 0, "Packaging accepted a SQLite DLL from another architecture. " + diagnostics);
        Assert.Contains("e_sqlite3.dll", diagnostics);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(workspace.Root, "output"), "*.zip"));
    }

    // Minimal PE headers exercise the static architecture gate; real release smoke is separate.
    private static void WritePe(string path, ushort machine)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file, Encoding.UTF8);
        writer.Write((ushort)0x5a4d);
        file.Position=0x3c; writer.Write(0x80);
        file.Position=0x80; writer.Write(0x4550u); writer.Write(machine);
        file.SetLength(512);
    }
}
