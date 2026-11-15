# 隔离性能对照

要求 Windows x64、.NET 10 SDK（遵循 global.json）、PowerShell 7.4+。从仓库根目录运行：

~~~powershell
pwsh -NoProfile -File tools/performance.ps1 -Mode quick
pwsh -NoProfile -File tools/performance.ps1 -Mode full
pwsh -NoProfile -File tools/performance.ps1 -Mode first-write
pwsh -NoProfile -File tools/performance.ps1 -Mode undo
# 可选输出目录必须不存在；默认使用系统临时目录
pwsh -NoProfile -File tools/performance.ps1 -Mode undo -Out C:/Temp/stashbay-undo-new
~~~

| 模式 | 数据 | 轮数 | 计时样本 |
|---|---|---|---|
| quick | 1 MiB、16 MiB | 单项与业务链各 1 对 | 22，仅工具自检 |
| full | 256 MiB、2 GiB+1 字节 | 单项 5 对，完整链 3 对 | 90 |
| first-write | 2 GiB+1 字节 | 首次写入 10 对 | 20 |
| undo | 2 GiB+1 字节 | 完整准备链 12 对 | 120 个阶段、24 次撤销 |

单项为复制、重复暂存、首次写入；业务链为添加、导出、预览、覆盖执行、撤销。
双方使用同一随机实写源，AB/BA 交错；复制双方预热。复制核验 SHA-256，撤销核验覆盖前原文。
undo 保留原准备步骤，只将 UndoAsync 计入撤销时间。

## 隔离与资源

复制当前 Core/Infrastructure、构建文件及工具到新目录，在副本内构建。
不覆盖仓库 artifacts、bin/obj，不执行发布、安装或提交，不调用真实 Shell 注册。
建议预留约 25 GiB；峰值依赖文件系统。换输出卷会改变测量条件。
禁止与其他大文件测试、发布或安装器并行测量；不要停止用户进程或清空系统缓存。
正常完成只删除生成的 data-GUID，保留报告、源码与程序；失败/中断保留夹具以便调查。
Ctrl+C 请求协作取消；强杀可能留下目录。清理前核对绝对路径确属本次输出。
run.json 的 passed 才表示完整结束；缺少 summary 或仅有部分样本不能当作通过。

## 输出与口径

- run.json：模式、状态、SDK、CPU、系统和起止时间。
- source-manifest.json、source/：被测源码及冻结基线的哈希和副本。
- samples.json：阶段、变体、大小、轮次、耗时、进程 CPU 毫秒与进程读写字节。
- summary.json：中位数和范围；至少两对撤销时输出配对差异与 95% 百分位 bootstrap 区间。
- build.log、run.log、bin/：构建、运行证据和程序。

I/O 包含缓存，不等于物理磁盘传输。未清空缓存或 Flush(true)，不测掉电耐久性。
CPU 有计数粒度，quick 的 0 不代表无 CPU 成本。
配对中位数与两组中位数比值不是同一统计量；区间跨零不证明不存在任何回退。
bootstrap 使用 System.Random(18371)、20,000 次重采样；历史 Python 统计的随机数实现不同，
不要求区间末位一致。禁止剔除慢样本、根据中途结果提前停止或只报告最好一轮。

## 冻结基线

两份 Baseline 文件冻结于 2026-11-14 优化前，已包含完整性检查，不是无校验旧版或当前 HEAD。
其他执行器、模型和依赖用当前源码，比较算法，不声称重建历史二进制。
baseline-manifest.json 防止无意漂移。将来接口变化，应明确新增版本和迁移说明，
不要静默改写本基线或仅刷新校验和。

[历史验收及第 1–7 项汇总](../../docs/validation/2026-11-14/README.md)
