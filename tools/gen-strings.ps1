# 生成强类型资源访问器：中性 resx 里的每个键生成一个静态属性。
# 用法：pwsh tools/gen-strings.ps1 -Assembly FileStash.App
param(
    [Parameter(Mandatory = $true)]
    [string]$Assembly
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$resxPath = Join-Path $repoRoot "src\$Assembly\Resources\Strings.resx"
$outPath = Join-Path $repoRoot "src\$Assembly\Resources\Strings.cs"

[xml]$resx = Get-Content -LiteralPath $resxPath -Raw -Encoding UTF8
$names = @($resx.root.data | ForEach-Object { $_.name } | Sort-Object)

$header = @"
using System.Globalization;
using System.Resources;

namespace $Assembly.Resources;

/// <summary>
/// 界面文字。View 里用 <c>x:Static</c> 引用，不写死字面量。
/// </summary>
/// <remarks>
/// 中性资源（Strings.resx）放中文原文，其余每门语言一张 <c>Strings.&lt;文化&gt;.resx</c>
/// 卫星程序集（en / ja-JP / de-DE / ar-SA …，共 18 门）。
/// 没有 zh-CN 卫星：中性资源本来就是中文，所有 zh-* 区域设置都会回退到它，
/// 再放一份字节相同的中文表只会带来漂移。
/// </remarks>
public static class Strings
{
    private static readonly ResourceManager ResourceManager =
        new("$Assembly.Resources.Strings", typeof(Strings).Assembly);

"@

$body = ($names | ForEach-Object { "    public static string $_ => Get(nameof($_));" }) -join "`r`n"

$footer = @"

    /// <summary>带占位符的文案：按当前界面语言格式化。</summary>
    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, format, args);

    private static string Get(string key) =>
        ResourceManager.GetString(key) ?? key;
}
"@

$content = ($header + $body + $footer) -replace "`r?`n", "`r`n"
[System.IO.File]::WriteAllText($outPath, $content, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "已生成 $outPath（$($names.Count) 个键）"
