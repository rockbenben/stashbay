using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FileStash.Infrastructure.Ipc;

/// <summary>
/// 单实例协调：命名互斥体 + 命名管道。
/// </summary>
/// <remarks>
/// 六条顺序上的讲究，都是实测踩出来的：
/// ① 互斥体必须是**字段**：写成局部变量，方法一返回它就成了垃圾，GC 收走时锁跟着释放，
///    第二个实例照样能启动，而且只在 Release 下偶发。
/// ② 叫醒老实例的信号要发在**等互斥体之前**：反过来写，常态路径就是先空等满超时才去叫窗口
///    （双击一次白转 1.2 秒）。
/// ③ 等待要分档：双击走 250ms（老实例活得好好的才是常态，这个等待必然走满），
///    "以管理员身份重开自己""切语言后重开自己"这类接管路径给 3s
///    （那一路上老实例已经在退了，接管是唯一目的，失败后果是老实例一退就一个都不剩）。
/// ④ 互斥体的**所有权是按线程的**，而且对持有者可重入：同一个线程再拿一次会立刻成功。
///    真实场景里第二个实例是另一个进程，不存在这个问题；
///    但自己写测试时必须在另一个线程上建第二个实例，否则它会误以为自己是主实例。
/// ⑤ 前一个实例被强杀（任务管理器）时，互斥体会变成"废弃"状态，
///    WaitOne 抛 AbandonedMutexException —— 抛出时所有权已经转移过来了，
///    这时候我们就是主实例。异常必须接住，否则应用在启动时崩掉。
/// ⑥ 转发失败 ≠ 只能退出：转发失败说明老实例没接住（它死了或正在死），
///    这时要**再等一次互斥体去接管**。直接退出去的后果是
///    "老实例退了、新实例没接上，机器上一个都不剩"。
///    反过来，老实例临死前要**停止回确认**（StopServer）——
///    确认的含义是"这条命令我收下了"，收下之后马上就要没了不算数。
/// ⑦ 管道 I/O 的 await 一律 ConfigureAwait(false)：调用方是 WPF 的 UI 线程，
///    而 UI 线程会卡在 Mutex.WaitOne 里（等互斥体，最长 3 秒）。
///    默认的 await 把续体 post 回 UI 线程，管道那头的写入就跟着卡住 ——
///    实测服务端连上了却 3 秒后才收到内容，另一个实例的转发连丢三次。
/// </remarks>
public sealed class SingleInstanceCoordinator : IDisposable
{
    /// <summary>服务端回给客户端的确认字节。</summary>
    private static readonly byte[] Ack = [1];

    private readonly Mutex? _mutex;
    private readonly string _pipeName;
    private readonly string _mutexName;
    private readonly TimeSpan _messageReadTimeout;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _serverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SingleInstanceCoordinator(
        string pipeName,
        TimeSpan takeoverTimeout,
        bool skipInstanceCheck,
        string? mutexName = null,
        TimeSpan? messageReadTimeout = null,
        bool wakePrimary = true)
    {
        _pipeName = pipeName;
        _mutexName = mutexName ?? MutexName;
        _messageReadTimeout = messageReadTimeout ?? TimeSpan.FromSeconds(5);

        // 提权重试的进程不参与单实例检查：否则参数会被转发给主实例，
        // 进程随即退出，提权重试根本不会执行
        if (skipInstanceCheck)
        {
            IsPrimary = true;
            return;
        }

        _mutex = new Mutex(true, _mutexName, out var createdNew);

        if (createdNew)
        {
            IsPrimary = true;
            return;
        }

        // 先把参数发给老实例（叫醒它），再等它放手
        if (wakePrimary) SendToPrimary([]);

        try
        {
            IsPrimary = _mutex.WaitOne(takeoverTimeout);
        }
        catch (AbandonedMutexException)
        {
            // 前一个实例死在**持有锁**的状态上（用户从任务管理器杀了它）：
            // 互斥体变成"废弃"状态。这种情况下 WaitOne 会抛异常，但抛出时
            // 所有权**已经转移给我们**了 —— 我们就是主实例。
            // 不接住它的话，异常会一路冒到 OnStartup，应用在启动时直接崩掉。
            IsPrimary = true;
        }
    }

    /// <summary>管道名。</summary>
    public string PipeName => _pipeName;

    /// <summary>我们是不是主实例。</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>管道服务是否已经就绪（可以连了）。</summary>
    public Task ServerReady => _serverReady.Task;

    /// <summary>
    /// 再等一次互斥体。"转发参数失败"之后用。
    /// </summary>
    /// <remarks>
    /// 转发失败 = 老实例没接住 = 它多半已经死了，或者正在死
    /// （切语言重启就是这条：老实例正在退出，新实例没抢到互斥体）。
    /// 这时候退出去，机器上就一个实例都不剩 ——
    /// 老实例退了、新实例没接上，应用直接消失了。
    ///
    /// 互斥体是老实例**最后**才放手的，所以这里再等一次通常就能接管。
    ///
    /// 等待必须在创建互斥体的**那条线程**上做：所有权按线程算，
    /// <c>ReleaseMutex</c> 只能由持有者线程调用。App 里这条线程是 UI 线程
    /// （协调器的构造与 Dispose 都在 UI 线程上）。
    /// </remarks>
    /// <returns>接管成功返回 true。</returns>
    public bool TryTakeover(TimeSpan timeout)
    {
        // skipInstanceCheck 的情况没有互斥体（本进程就是主实例）
        if (_mutex is null)
        {
            IsPrimary = true;
            return true;
        }

        try
        {
            if (!_mutex.WaitOne(timeout))
            {
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // 前一个实例死在**持有锁**的状态上：所有权已经转移给我们了 —— 我们就是主实例
            IsPrimary = true;
            return true;
        }

        IsPrimary = true;
        return true;
    }

    /// <summary>
    /// 停止管道服务：不再接新指令，也**不再回确认**。
    /// </summary>
    /// <remarks>
    /// 切语言重启时用。老实例马上就要退出，不能再对别的实例回确认 ——
    /// 确认的含义是"这条命令我收下了"，可它马上就要没了。
    /// 收到确认的实例会以为命令有人执行，自己退出；
    /// 结果就是老实例退了、新实例也没接上，机器上一个都没有。
    /// </remarks>
    public void StopServer() => _cancellation.Cancel();

    /// <summary>互斥体名字：当前用户 + 会话作用域。</summary>
    public static string MutexName => $@"Local\StashBay_SingleInstance_{UserSid}";

    /// <summary>命名管道名：必须带 \\.\pipe\ 前缀。</summary>
    public static string DefaultPipeName => $@"\\.\pipe\StashBay_Pipe_{UserSid}";

    private static string UserSid
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return identity.User?.Value ?? "default";
            }
            catch (Exception)
            {
                return "default";
            }
        }
    }

    /// <summary>
    /// 主实例：接收其它实例转发过来的参数。
    /// </summary>
    /// <remarks>
    /// 协议是**长度前缀 + 确认**：
    /// ① 长度前缀 —— 不能用 ReadToEnd（它要等到流结束才返回，两个客户端挤在同一个管道实例上时
    ///    会把两条消息拼在一起，JSON 直接解析失败）；
    /// ② 确认 —— 客户端要等服务端回一个字节才算"送到了"。服务端处理完一条就 Disconnect，
    ///    这个窗口里连上来的客户端数据会被丢掉，但客户端没收到确认就知道要重发。
    /// </remarks>
    public async Task RunServerAsync(Func<string[], Task> onMessage, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation.Token);

        while (!linked.IsCancellationRequested)
        {
            NamedPipeServerStream server;

            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200, linked.Token);
                continue;
            }

            await using (server)
            {
                _serverReady.TrySetResult();

                try
                {
                    await server.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                    continue;
                }

                var arguments = await ReadArgumentsAsync(server, linked.Token).ConfigureAwait(false);

                if (arguments is not null)
                {
                    // 已经在停止服务了：不回确认，也不处理。
                    //
                    // 确认的含义是"这条命令我收下了"，可我们马上就要没了 ——
                    // 收下之后马上就消失，那不算数。给了确认，收到确认的实例
                    // 就以为有人执行了、自己退出；老实例一退，机器上就一个都不剩。
                    //
                    // 这一条要盖住"停止"与"回确认"之间的竞态：
                    // 停止信号是刚发出来的，这条连接可能已经在路上了。
                    if (linked.IsCancellationRequested)
                    {
                        continue;
                    }

                    // 先回确认，再处理。
                    // 确认的含义是"服务端收到了这条消息"，不是"命令做完了" ——
                    // 处理要走 UI 线程（Dispatcher.InvokeAsync），主实例此刻正开着模态框的话，
                    // UI 线程被占着，处理要一直等到模态框关掉。
                    // 确认放在处理之后的话，第二个实例会白白等满 5 秒 × 5 次才放弃（实测卡 25 秒）。
                    try
                    {
                        await server.WriteAsync(Ack, linked.Token).ConfigureAwait(false);
                        await server.FlushAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException)
                    {
                    }

                    // 处理丢到后台去，管道不等它：
                    // NamedPipeServerStream 只开 1 个实例，管道被占着的时候后来的实例连都连不上。
                    // 顺序不会乱 —— 真正执行的是 Dispatcher.InvokeAsync，调度器自己排队。
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await onMessage(arguments);
                            }
                            catch (Exception)
                            {
                                // 处理失败不用再回确认：消息已经收到了，重发只会重复执行一遍
                            }
                        },
                        linked.Token);

                    // 回完确认，等客户端自己断开 —— 它收到确认就会关。
                    //
                    // 不等就关的话，客户端可能还没读到确认就收到 EOF，
                    // 它会以为"消息没送到"，于是重发一遍：
                    // 消息被执行两次（"写回原位置"被执行两次可不是小事）。
                    // 实测并发转发 8 条时能收到 9 条 —— 多出来的那条就是这么来的。
                    await DrainUntilClientClosesAsync(server, linked.Token).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>把参数发给主实例；服务端确认收到才算成功。</summary>
    /// <param name="args">要转发的参数。</param>
    /// <param name="attempts">
    /// 最多试几次。正常路径给足（老实例正忙着的时候宁可等也不丢命令），
    /// 接管路径给少一点 —— 那里转发失败还有"自己接管"这条退路，
    /// 不值得为一次注定失败的转发等满 8 轮。
    /// </param>
    public async Task<bool> SendToPrimaryAsync(string[] args, int attempts = 8)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
        var header = BitConverter.GetBytes(payload.Length);

        // 重试要够多：主实例正忙着（写几 GB 的文件、界面卡着）的时候，
        // 管道那头的实例换的瞬间会连不上 —— 那时候丢掉命令是最糟的结果
        // （用户双击了一下，什么都没发生）。
        // 正常情况下第一次就连上了，多出来的几次只是兜底。
        //
        // 下面每个 await 都带 ConfigureAwait(false) —— 这不是风格问题，是**正确性**问题。
        //
        // 调用方是 App.OnStartup，那是 WPF 的 UI 线程（async void）。默认的 await 会把续体
        // **post 回 UI 线程**，而 UI 线程此刻正卡在 Mutex.WaitOne 里（等互斥体接管，
        // 最长 3 秒）—— 管道那头的写入就跟着一起卡住。
        //
        // 实测（主实例 + 两个第二实例同时起，2026-11-01）：
        //   服务端 23.898 连上了，26.923 才读完消息 —— 中间 3 秒管道是空的；
        //   另一个实例的转发因此连丢三次（每次 750 ms 连接超时），2 秒后才挤进去。
        //
        // 管道服务是**单线程循环**，卡住一条就等于卡住全部。
        // ConfigureAwait(false) 把续体放到线程池上：管道 I/O 不再依赖 UI 线程有没有空。
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);

            try
            {
                await client.ConnectAsync((int)TimeSpan.FromMilliseconds(750).TotalMilliseconds)
                    .ConfigureAwait(false);

                await client.WriteAsync(header, CancellationToken.None).ConfigureAwait(false);
                await client.WriteAsync(payload, CancellationToken.None).ConfigureAwait(false);
                await client.FlushAsync(CancellationToken.None).ConfigureAwait(false);

                // 等确认：没收到就说明这条消息没真正到服务端，要重发
                var ack = new byte[1];
                var read = await client.ReadAsync(ack, CancellationToken.None).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                    .ConfigureAwait(false);

                if (read == 1)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                // 显式关掉：await using 生成的 DisposeAsync await 一样会捕获上下文，
                // 上下文被冻住时连接就关不掉，服务端那边要空等到读超时。
                await client.DisposeAsync().ConfigureAwait(false);
            }

            await Task.Delay(150).ConfigureAwait(false);
        }

        return false;
    }

    private Task<bool> SendToPrimary(string[] args) => SendToPrimaryAsync(args);

    private async Task<string[]?> ReadArgumentsAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var read = ReadPayloadAsync(server, ct);

        // 读一条消息**必须有时限**。
        //
        // 没有时限的话：一个连上来却一直不说话的客户端（同一用户下的别的程序，
        // 或者一个卡在写之前的客户端）会把服务端永远卡在这一次读上 ——
        // 管道服务是单线程的循环，卡住一条就等于卡住全部：
        // 之后所有实例都转发不进来，右键菜单「加入暂存」从此失灵，
        // 而且**没有任何报错**（日志里一片干净）。
        //
        // 时限靠"放弃这次读"实现，**不能靠取消令牌**：
        // 实测取消令牌根本中断不了命名管道上挂着的读（取消生效不了，
        // 管道一直堵着，实测 7 秒都没恢复）。
        // 放弃的做法是：不等它了，把这条连接关掉 —— 挂着的读随之失败，
        // 它的异常在下面吃掉（不然就是一个没人观察的任务异常）。
        if (!await CompletesInTimeAsync(read, _messageReadTimeout, ct).ConfigureAwait(false))
        {
            return null;
        }

        return await read.ConfigureAwait(false);
    }

    private static async Task<string[]?> ReadPayloadAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            var header = new byte[4];
            await server.ReadExactlyAsync(header, ct).ConfigureAwait(false);

            var length = BitConverter.ToInt32(header);

            if (length <= 0 || length > 4 * 1024 * 1024)
            {
                return null;
            }

            var payload = new byte[length];
            await server.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

            return JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(payload));
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>等客户端把连接关掉（它收到确认就会关）。</summary>
    private async Task DrainUntilClientClosesAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var drain = DrainCoreAsync(server, ct);

        if (!await CompletesInTimeAsync(drain, _messageReadTimeout, ct).ConfigureAwait(false))
        {
            return;
        }

        await drain.ConfigureAwait(false);
    }

    private static async Task DrainCoreAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var buffer = new byte[64];

        try
        {
            while (true)
            {
                var read = await server.ReadAsync(buffer, ct).ConfigureAwait(false);

                if (read == 0)
                {
                    // 客户端关了
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 等一个操作完成，最多等 <paramref name="timeout"/>；超时就把它**放弃**。
    /// </summary>
    /// <remarks>
    /// 放弃 = 不再等它，把它的异常吃掉（挂着的管道读在连接被关掉时会失败，
    /// 那个失败没人观察的话就是一个未观察到的任务异常）。
    ///
    /// 为什么不用取消令牌：命名管道上挂着的读**取消不掉**（实测）。
    /// 只能靠"不等了 + 把连接关掉"来脱身。
    /// </remarks>
    private static async Task<bool> CompletesInTimeAsync(Task operation, TimeSpan timeout, CancellationToken ct)
    {
        var completed = await Task.WhenAny(operation, Task.Delay(timeout, ct)).ConfigureAwait(false);

        if (completed == operation)
        {
            return true;
        }

        _ = operation.ContinueWith(
            t =>
            {
                _ = t.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return false;
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 没持有锁
        }

        _mutex?.Dispose();
        _cancellation.Dispose();
    }
}
