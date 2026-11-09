using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Data;
using FileStash.Infrastructure.Native;
using FileStash.Infrastructure.Services;

namespace FileStash.Core.Tests;

/// <summary>把应用引擎需要的服务都装好的一份测试环境。</summary>
internal sealed class ApplyTestContext : IDisposable
{
    public ApplyTestContext()
    {
        Workspace = new TempWorkspace();
        Paths = Workspace.Paths;

        var factory = Workspace.ConnectionFactory;
        Migrator = new DatabaseMigrator(factory);
        Groups = new StashGroupRepository(factory);
        Items = new StashItemRepository(factory);
        Entries = new StashEntryRepository(factory);
        Operations = new OperationRepository(factory);
        OperationEntries = new OperationEntryRepository(factory);
        BlobStore = new BlobStore(Paths, TimeSpan.Zero);

        var settings = new SettingsService(Paths);
        settings.Load();
        Settings = settings;

        Git = new GitService();
        Stash = new StashService(Groups, Items, Entries, BlobStore, Git, settings);
        Planner = new ApplyPlanner(Entries, Git);
        Executor = new ApplyExecutor(
            BlobStore,
            Operations,
            OperationEntries,
            new AtomicFileWriter(),
            new RestartManagerLockInspector(),
            settings);
    }

    public TempWorkspace Workspace { get; }

    public AppPaths Paths { get; }

    public DatabaseMigrator Migrator { get; }

    public StashGroupRepository Groups { get; }

    public StashItemRepository Items { get; }

    public StashEntryRepository Entries { get; }

    public OperationRepository Operations { get; }

    public OperationEntryRepository OperationEntries { get; }

    public BlobStore BlobStore { get; }

    public SettingsService Settings { get; }

    public GitService Git { get; }

    public StashService Stash { get; }

    public ApplyPlanner Planner { get; }

    public ApplyExecutor Executor { get; }

    public async Task<string> CreateGroupAsync() => (await Stash.CreateGroupAsync("测试组")).Id;

    public void Dispose() => Workspace.Dispose();
}
