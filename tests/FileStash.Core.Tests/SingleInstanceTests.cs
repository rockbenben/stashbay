using System.Collections.Concurrent;
using FileStash.Infrastructure.Ipc;

namespace FileStash.Core.Tests;

/// <summary>
/// 单实例机制：命名互斥体 + 命名管道 + 参数转发。
/// </summary>
/// <remarks>
/// 这套机制单独测（不启动整个应用）：它只涉及互斥体与管道，
/// 不碰注册表、不碰用户数据。
/// </remarks>
public sealed class SingleInstanceTests
{
    [Fact]
    public async Task LogonTakeoverCanWaitForOldInstanceWithoutImplicitWakeOrForwarding()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipe = $@"\\.\pipe\filestash-logon-test-{suffix}";
        var mutex = $@"Local\filestash-logon-test-{suffix}";
        using var primary = new CoordinatorOnOwnThread(pipe, TimeSpan.Zero, mutex);
        var received = new ConcurrentQueue<string[]>();
        var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = primary.Coordinator.RunServerAsync(args =>
        {
            received.Enqueue(args);
            if (args.SequenceEqual(["--quit"])) quit.TrySetResult();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        await primary.Coordinator.ServerReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var next = new CoordinatorOnOwnThread(pipe, TimeSpan.FromMilliseconds(50), mutex, wakePrimary: false);
        Assert.False(next.Coordinator.IsPrimary);
        Assert.True(await next.Coordinator.SendToPrimaryAsync(["--quit"], attempts: 1));
        await quit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(received);
        primary.Dispose();
        next.Run(coordinator => Assert.True(coordinator.TryTakeover(TimeSpan.FromSeconds(2))));
        await server;
    }

    [Fact]
    public async Task SecondInstance_ShouldForwardArgumentsToPrimary()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $@"\\.\pipe\filestash-test-{suffix}";
        var mutexName = $@"Local\filestash-test-{suffix}";

        using var primary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

        Assert.True(primary.Coordinator.IsPrimary);

        var received = new List<string[]>();
        var signal = new SemaphoreSlim(0);

        var serverTask = primary.Coordinator.RunServerAsync(
            args =>
            {
                lock (received)
                {
                    received.Add(args);
                }

                signal.Release();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        try
        {
            // 等管道建好再连，否则会撞上"主实例刚起来、管道还没建好"的窗口
            await primary.Coordinator.ServerReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 第二个实例：拿不到互斥体。
            using var secondary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

            Assert.False(secondary.Coordinator.IsPrimary);

            // 转发参数
            var sent = await secondary.Coordinator.SendToPrimaryAsync(["--add", @"C:\a.txt", @"C:\b.txt"]);
            Assert.True(sent);
        }
        finally
        {
            // 服务端循环是常驻的（Dispose 时才停），不用等它结束
        }

        // 主实例收到
        //
        // 注意：确认只代表"服务端收到了这条消息"，不代表"处理完了"。
        // 所以要等的是**那条真的消息**，不能等任意一个信号 ——
        // 协调器构造时会先发一条空的"叫醒"消息，它也会发信号。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string[]? payload = null;

        while (DateTime.UtcNow < deadline && payload is null)
        {
            lock (received)
            {
                payload = received.FirstOrDefault(args => args.Length > 0);
            }

            if (payload is null)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        Assert.NotNull(payload);
        Assert.Equal(["--add", @"C:\a.txt", @"C:\b.txt"], payload);
    }

    [Fact]
    public async Task SendToPrimaryAsync_ShouldNotWaitForHandlerToFinish()
    {
        // 确认的含义是"服务端收到了这条消息"，不是"命令做完了"。
        // 主实例正开着模态框时 UI 线程被占着，处理要一直等到模态框关掉；
        // 确认放在处理之后的话，第二个实例会白白等满 5 秒 × 5 次才放弃（实测卡 25 秒）。
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $@"\\.\pipe\filestash-test-{suffix}";
        var mutexName = $@"Local\filestash-test-{suffix}";

        using var primary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

        Assert.True(primary.Coordinator.IsPrimary);

        var release = new SemaphoreSlim(0);

        _ = primary.Coordinator.RunServerAsync(
            async _ => await release.WaitAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        await primary.Coordinator.ServerReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var secondary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

        Assert.False(secondary.Coordinator.IsPrimary);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var sent = await secondary.Coordinator.SendToPrimaryAsync(["--add", @"C:\a.txt"]);
        stopwatch.Stop();

        // 处理还堵着，转发就应当已经返回。
        // 真正吃重的是 Assert.True(sent)：老实现（处理完才回确认）会等满
        // 5 秒 × 5 次重试才放弃，返回 false。
        // 时间上限只是补一刀，不能卡太紧 —— 机器正忙的时候 3 秒也会超时，
        // 那种失败与被测行为无关。
        Assert.True(sent);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"转发耗时 {stopwatch.ElapsedMilliseconds} ms（处理没结束就不该等）");

        release.Release();
    }

    [Fact]
    public async Task AbandonedMutex_ShouldBeTakenOverAsPrimary()
    {
        // 前一个实例死在**持有锁**的状态上（用户从任务管理器杀了它）：
        // 互斥体变成"废弃"状态，WaitOne 会抛 AbandonedMutexException。
        // 抛出时所有权**已经转移给我们**了 —— 我们就是主实例。
        // 不接住这个异常的话，它会一路冒到启动流程，应用直接崩在启动那一刻。
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $@"\\.\pipe\filestash-test-{suffix}";
        var mutexName = $@"Local\filestash-test-{suffix}";

        var mutexCreated = new ManualResetEventSlim(false);
        var keeperOpened = new ManualResetEventSlim(false);

        // 持有者线程：拿到锁就等，等主线程把句柄握住再带着锁退出。
        var ownerThread = new Thread(() =>
        {
            var owned = new Mutex(true, mutexName);
            mutexCreated.Set();
            keeperOpened.Wait();

            // 线程带着锁退出 —— 互斥体就此变成废弃状态
            GC.KeepAlive(owned);
        })
        {
            IsBackground = true,
        };

        ownerThread.Start();
        mutexCreated.Wait(TestContext.Current.CancellationToken);

        // 关键：要有一份额外的句柄留在外面。
        // 没有它的话，拥有者线程一死，对象整个消失（没人持有句柄），
        // 下一个进程建的就是"新互斥体"，根本走不到废弃这条路上。
        using var keeper = new Mutex(false, mutexName);

        keeperOpened.Set();
        ownerThread.Join();

        // 管道那头先架一个服务端，免得构造函数里"叫醒老实例"重试 8 次才放弃
        using var server = new CoordinatorOnOwnThread(
            pipeName,
            TimeSpan.FromMilliseconds(250),
            $@"Local\filestash-other-{suffix}");

        _ = server.Coordinator.RunServerAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken);

        await server.Coordinator.ServerReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var coordinator = new SingleInstanceCoordinator(
            pipeName,
            TimeSpan.FromMilliseconds(250),
            skipInstanceCheck: false,
            mutexName);

        Assert.True(coordinator.IsPrimary);
    }

    [Fact]
    public async Task ASilentClient_ShouldNotWedgeThePipe()
    {
        // 服务端的读必须有时限。
        //
        // 一个连上来却一直不说话的客户端（同一用户下的别的程序，
        // 或者一个卡在写之前的客户端）会把服务端永远卡在这一次读上：
        // 管道服务是单线程的循环，卡住一条就等于卡住全部 ——
        // 之后所有实例都转发不进来，右键菜单「加入暂存」从此失灵，
        // 而且日志里一片干净（没有任何报错）。
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $@"\\.\pipe\filestash-test-{suffix}";
        var mutexName = $@"Local\filestash-test-{suffix}";

        using var primary = new CoordinatorOnOwnThread(
            pipeName,
            TimeSpan.FromMilliseconds(250),
            mutexName,
            messageReadTimeout: TimeSpan.FromMilliseconds(300));

        _ = primary.Coordinator.RunServerAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken);

        await primary.Coordinator.ServerReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 一个只连不说的客户端
        await using var silent = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut);

        await silent.ConnectAsync(2000, TestContext.Current.CancellationToken);

        using var secondary = new CoordinatorOnOwnThread(
            pipeName,
            TimeSpan.FromMilliseconds(250),
            mutexName,
            messageReadTimeout: TimeSpan.FromMilliseconds(300));

        // 后来的客户端照样要能转发进来（服务端把那条连接丢掉之后会自己恢复）
        var sent = await secondary.Coordinator.SendToPrimaryAsync(["--add", @"C:\a.txt"]);

        Assert.True(sent);
    }

    [Fact]
    public async Task ConcurrentForwards_ShouldAllGetThrough()
    {
        // 资源管理器多选会同时拉起一堆进程，每个都来转发一条。
        // 服务端是单线程循环，但客户端在系统层面排队 ——
        // 所以几十条并发要能一条不丢地送到（丢一条 = 用户少了一个文件）。
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $@"\\.\pipe\filestash-test-{suffix}";
        var mutexName = $@"Local\filestash-test-{suffix}";

        using var primary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

        var received = new List<string[]>();

        _ = primary.Coordinator.RunServerAsync(
            args =>
            {
                // 次实例构造时还会发送一条空参数唤醒消息；它不属于下面的文件转发。
                // 把它算进完成条件会提前结束等待，最后一条文件消息可能仍在后台排队。
                if (args.Length == 0) return Task.CompletedTask;

                lock (received)
                {
                    received.Add(args);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        await primary.Coordinator.ServerReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var secondary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

        const int count = 8;

        var results = await Task.WhenAll(
            Enumerable.Range(0, count)
                .Select(i => secondary.Coordinator.SendToPrimaryAsync(["--add", $@"C:\f{i}.txt"])));

        Assert.All(results, sent => Assert.True(sent));

        // 等消息真的送到服务端（确认只代表"收到了"，不代表处理完了）
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            lock (received)
            {
                if (received.Count >= count)
                {
                    break;
                }
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        lock (received)
        {
            var paths = received
                .Select(args => args.LastOrDefault())
                .Where(path => path is not null)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(count, paths.Length);
            Assert.Equal(Enumerable.Range(0, count).Select(i => $@"C:\f{i}.txt").OrderBy(p => p, StringComparer.Ordinal), paths);
        }
    }

    [Fact]
    public async Task SendToPrimaryAsync_ShouldNotDependOnTheCallersSynchronizationContext()
    {
        // 转发不能依赖调用方的 SynchronizationContext。
        //
        // 调用方是 App.OnStartup —— WPF 的 UI 线程（async void）。
        // 默认的 await 会把续体 post 回 UI 线程；而 UI 线程此刻正卡在
        // Mutex.WaitOne 里（等互斥体接管，最长 3 秒）——
        // 管道那头的写入就跟着一起卡住。
        //
        // 实测（2026-11-01，主实例 + 两个第二实例同时起）：
        //   服务端 23.898 连上了，26.923 才读完消息 —— 中间 3 秒管道是空的；
        //   另一个实例的转发因此连丢三次（每次 750 ms 连接超时），2 秒后才挤进去。
        //
        // 管道服务是单线程循环，卡住一条就等于卡住全部。
        // 这个用例把上下文冻住：转发必须照样送达。
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $@"\\.\pipe\filestash-test-{suffix}";
        var mutexName = $@"Local\filestash-test-{suffix}";

        using var primary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

        var received = new List<string[]>();

        _ = primary.Coordinator.RunServerAsync(
            args =>
            {
                lock (received)
                {
                    received.Add(args);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        await primary.Coordinator.ServerReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var secondary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

        // 在一条"上下文被冻住"的线程上发 —— 模拟 UI 线程被互斥体等待占住
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sender = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new FrozenSynchronizationContext());

                var sent = secondary.Coordinator.SendToPrimaryAsync(["--add", @"C:\a.txt"])
                    .GetAwaiter()
                    .GetResult();

                result.SetResult(sent);
            }
            catch (Exception ex)
            {
                // 后台线程上抛出来的异常没人接 —— 直接把用例判失败
                result.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "filestash-test-frozen-context",
        };

        sender.Start();

        // 上下文冻住时，续体永远不会被派发，转发就永远完不成。
        // 所以这里必须给个上限 —— 不然用例会挂到测试运行器超时。
        var sent = await result.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.True(sent);

        // 服务端真的收到了那条消息。
        //
        // **不能只等任意一个信号**：协调器构造时会先发一条空的"叫醒"消息，
        // 它也会进 received；而确认是在派发之前就回的，
        // 所以"拿到确认"不等于"消息已经进了 received"。
        // 这里要等的是**那条真的消息**。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var found = false;

        while (DateTime.UtcNow < deadline && !found)
        {
            lock (received)
            {
                found = received.Any(args => args.SequenceEqual(["--add", @"C:\a.txt"]));
            }

            if (!found)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        Assert.True(found, "服务端没有收到转发过来的消息");
    }

    /// <summary>
    /// 一个**故意不派发**的同步上下文：模拟"调用方的 UI 线程被别的事占住"。
    /// </summary>
    private sealed class FrozenSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // 故意不派发
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            // 故意不派发（也不能等 —— 等下去就是死锁）
        }
    }

    [Fact]
    public void SkipInstanceCheck_ShouldAlwaysBePrimary()
    {
        var suffix = Guid.NewGuid().ToString("N");

        using var coordinator = new SingleInstanceCoordinator(
            $@"\\.\pipe\filestash-test-{suffix}",
            TimeSpan.FromMilliseconds(250),
            skipInstanceCheck: true,
            $@"Local\filestash-test-{suffix}");

        // 提权重试的进程不参与单实例检查：否则参数会被转发给主实例，进程随即退出
        Assert.True(coordinator.IsPrimary);
    }

    [Fact]
    public void TryTakeover_ShouldFailWhileTheOldInstanceIsAlive()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $@"\\.\pipe\filestash-test-{suffix}";
        var mutexName = $@"Local\filestash-test-{suffix}";

        using var primary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);
        using var secondary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

        Assert.True(primary.Coordinator.IsPrimary);
        Assert.False(secondary.Coordinator.IsPrimary);

        var taken = true;

        secondary.Run(c => taken = c.TryTakeover(TimeSpan.FromMilliseconds(300)));

        // 老实例活得好好的 —— 抢不到就是抢不到，不能把"主"这个身份抢过来
        Assert.False(taken);
        Assert.False(secondary.Coordinator.IsPrimary);
    }

    [Fact]
    public async Task Takeover_ShouldSucceedWhenTheOldInstanceStopsAnswering()
    {
        // 这就是"切语言后重启自己"那条路：
        // 老实例正在退出（不再回确认）→ 新实例转发不到 → 新实例自己接管。
        // 这条路不通的话，后果是老实例退了、新实例没接上，机器上一个都不剩。
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $@"\\.\pipe\filestash-test-{suffix}";
        var mutexName = $@"Local\filestash-test-{suffix}";

        using var primary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);
        using var secondary = new CoordinatorOnOwnThread(pipeName, TimeSpan.FromMilliseconds(250), mutexName);

        var serverTask = primary.Coordinator.RunServerAsync(
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        await primary.Coordinator.ServerReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 老实例临死前停止回确认：确认的含义是"这条命令我收下了"，
        // 收下之后马上就要没了，那不算数
        primary.Coordinator.StopServer();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var sent = await secondary.Coordinator.SendToPrimaryAsync(["--show"], attempts: 2);

        Assert.False(sent);

        // 老实例放手
        primary.Dispose();

        var taken = false;
        secondary.Run(c => taken = c.TryTakeover(TimeSpan.FromSeconds(5)));

        Assert.True(taken);
        Assert.True(secondary.Coordinator.IsPrimary);
    }

    /// <summary>
    /// 把协调器放在一条**专用线程**上创建，并让这条线程一直活到用例结束。
    /// </summary>
    /// <remarks>
    /// 互斥体的所有权按线程算，而且对持有者可重入 —— 同一个线程上再建一个实例，
    /// WaitOne 会立刻成功，它就误以为自己是主实例。
    ///
    /// 用 Task.Run 造"另一个线程"不可靠：async 用例 await 之后会把当前线程还回线程池，
    /// 排好的工作项可能正好被**同一条线程**领走 —— 全量跑的时候就会偶发地失败。
    ///
    /// 专用线程还顺带解决了另一个问题：主实例那条线程要是被线程池回收，
    /// 互斥体就变成"废弃"状态，后面的 WaitOne 会抛 AbandonedMutexException。
    /// </remarks>
    private sealed class CoordinatorOnOwnThread : IDisposable
    {
        private readonly ManualResetEventSlim _created = new(false);
        private readonly BlockingCollection<Action<SingleInstanceCoordinator>> _actions = new();
        private readonly Thread _thread;
        private Exception? _failure;
        private bool _disposed;

        public SingleInstanceCoordinator Coordinator { get; private set; } = null!;

        public CoordinatorOnOwnThread(
            string pipeName,
            TimeSpan takeoverTimeout,
            string mutexName,
            TimeSpan? messageReadTimeout = null,
            bool wakePrimary = true)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    Coordinator = new SingleInstanceCoordinator(
                        pipeName,
                        takeoverTimeout,
                        skipInstanceCheck: false,
                        mutexName,
                        messageReadTimeout,
                        wakePrimary);
                }
                catch (Exception ex)
                {
                    // 构造失败要让调用方看见，不能变成"卡在等创建完成"上直到超时
                    _failure = ex;
                }
                finally
                {
                    _created.Set();
                }

                foreach (var action in _actions.GetConsumingEnumerable())
                {
                    action(Coordinator);
                }

                // ReleaseMutex 只能由持有它的线程调用，所以释放也留在这条线程上
                if (_failure is null)
                {
                    Coordinator.Dispose();
                }
            })
            {
                IsBackground = true,
                Name = "filestash-test-owner",
            };

            _thread.Start();
            _created.Wait(TestContext.Current.CancellationToken);

            if (_failure is not null)
            {
                Dispose();
                throw new InvalidOperationException("协调器构造失败", _failure);
            }
        }

        /// <summary>
        /// 在协调器所属的那条线程上跑一段代码。
        /// </summary>
        /// <remarks>
        /// 必须走这条线程：互斥体的所有权按线程算，
        /// 接管（TryTakeover）要在持有者线程上做，释放才拿得走。
        /// </remarks>
        public void Run(Action<SingleInstanceCoordinator> action)
        {
            Exception? failure = null;

            using var done = new ManualResetEventSlim(false);

            _actions.Add(coordinator =>
            {
                try
                {
                    action(coordinator);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            done.Wait(TestContext.Current.CancellationToken);

            if (failure is not null)
            {
                throw failure;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _actions.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));

            _created.Dispose();
            _actions.Dispose();
        }
    }
}
