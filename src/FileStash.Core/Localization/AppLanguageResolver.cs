using System.Globalization;
using FileStash.Core.Models;

namespace FileStash.Core.Localization;

/// <summary>
/// 语言选项 → 区域性。
/// </summary>
/// <remarks>
/// 放在 Core 而不是 App：这一层是纯逻辑（不碰 WPF），可以单测。
/// 真正把它挂到 WPF 上的是 App 层的 <c>LanguageService.Apply</c>。
///
/// "跟随系统"取的是**系统 UI 语言**，所以这个静态字段必须在任何
/// <c>LanguageService.Apply</c> 之前初始化 —— Apply 之后
/// <see cref="CultureInfo.CurrentUICulture"/> 就变成上一次的设定值了，
/// 再问"系统是什么语言"就问到自己了。
/// </remarks>
public static class AppLanguageResolver
{
    /// <summary>系统 UI 语言：进程一启动就定下来，之后不再变。</summary>
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    /// <summary>按设置挑一个区域性。</summary>
    /// <param name="language">用户选的语言。</param>
    /// <param name="systemCulture">
    /// "跟随系统"时用哪个区域性。留空表示用进程启动时的系统 UI 语言。
    /// 显式传进来是为了能单测 —— 系统语言是进程级的状态，测试里没法可靠地摆出来。
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 枚举里有一种语言没在这里配区域性。加语言时漏了这一步的话，
    /// 界面会用系统语言显示，而下拉框里写着用户选的那门语言 —— 错得很安静。
    /// </exception>
    public static CultureInfo Resolve(AppLanguage language, CultureInfo? systemCulture = null) => language switch
    {
        // 跟随系统 ≠ 把系统区域性别名原样搬过来。
        // ResourceManager 找表是**按区域链**找的：de-AT → de → 中性表。
        // 我们只有 de-DE 卫星表，"de" 没有表 —— 于是奥地利/瑞士的德语用户
        // 会一路退到中文，而下拉框里写着"跟随系统"。同样地 fr-CA、pt-PT、
        // es-MX、zh-HK 全都会掉进这个坑。
        // 所以先把系统语言落到"我们真的备了卫星表"的那一门，再取它的区域性。
        AppLanguage.System => Resolve(ResolveSystemLanguage(systemCulture ?? SystemCulture)),
        AppLanguage.Chinese => new CultureInfo("zh-CN"),
        AppLanguage.English => new CultureInfo("en-US"),
        AppLanguage.TraditionalChinese => new CultureInfo("zh-TW"),
        AppLanguage.Japanese => new CultureInfo("ja-JP"),
        AppLanguage.Korean => new CultureInfo("ko-KR"),
        AppLanguage.Spanish => new CultureInfo("es-ES"),
        AppLanguage.PortugueseBrazil => new CultureInfo("pt-BR"),
        AppLanguage.French => new CultureInfo("fr-FR"),
        AppLanguage.German => new CultureInfo("de-DE"),
        AppLanguage.Italian => new CultureInfo("it-IT"),
        AppLanguage.Russian => new CultureInfo("ru-RU"),
        AppLanguage.Arabic => new CultureInfo("ar-SA"),
        AppLanguage.Hindi => new CultureInfo("hi-IN"),
        AppLanguage.Indonesian => new CultureInfo("id-ID"),
        AppLanguage.Thai => new CultureInfo("th-TH"),
        AppLanguage.Turkish => new CultureInfo("tr-TR"),
        AppLanguage.Vietnamese => new CultureInfo("vi-VN"),
        AppLanguage.Bengali => new CultureInfo("bn-BD"),

        // 抛异常而不是悄悄退回系统语言：退回系统语言是那种"没人会发现"的错 ——
        // 下拉框写着 English，界面显示中文，日志里一个异常都没有。
        // 异常消息用英文：它是给开发者看的（枚举加了值却忘了配区域性），
        // 而且 C# 里的中文字面量只有日志里才有（界面文字走资源表）。
        _ => throw new ArgumentOutOfRangeException(
            nameof(language),
            language,
            "No culture is mapped for this language. Add a case here when adding a new AppLanguage value."),
    };

    /// <summary>
    /// 系统语言 → 我们备了卫星表的最接近的一门语言。
    /// </summary>
    /// <remarks>
    /// 按 ISO 639-1 的两字母代码匹配，所以 de-AT、de-CH、fr-CA 这种地区变体
    /// 都能落到对应的语言上 —— 它们只是"备了表的语言"的地区变体而已。
    ///
    /// 中文要分开处理：简繁是两种语言，不是一个语言的地区变体。
    /// zh-HK / zh-MO / zh-TW / zh-Hant-* 走繁体，其余走简体。
    ///
    /// 没有任何对应卫星表的语言（比如希伯来语）落回**英文**而不是中文 ——
    /// 中性表是中文，那等于把一个不懂中文的用户推到看不懂的界面上。
    /// </remarks>
    public static AppLanguage ResolveSystemLanguage(CultureInfo culture)
    {
        string code;
        try
        {
            code = culture.TwoLetterISOLanguageName;
        }
        catch (ArgumentException)
        {
            // 空白区域性（InvariantCulture）没有两字母代码
            return AppLanguage.English;
        }

        return code switch
        {
            "zh" => IsTraditionalChinese(culture) ? AppLanguage.TraditionalChinese : AppLanguage.Chinese,
            "en" => AppLanguage.English,
            "ja" => AppLanguage.Japanese,
            "ko" => AppLanguage.Korean,
            "es" => AppLanguage.Spanish,
            "pt" => AppLanguage.PortugueseBrazil,
            "fr" => AppLanguage.French,
            "de" => AppLanguage.German,
            "it" => AppLanguage.Italian,
            "ru" => AppLanguage.Russian,
            "ar" => AppLanguage.Arabic,
            "hi" => AppLanguage.Hindi,
            "id" => AppLanguage.Indonesian,
            "th" => AppLanguage.Thai,
            "tr" => AppLanguage.Turkish,
            "vi" => AppLanguage.Vietnamese,
            "bn" => AppLanguage.Bengali,
            _ => AppLanguage.English,
        };
    }

    /// <summary>这份区域性是不是繁体中文。</summary>
    private static bool IsTraditionalChinese(CultureInfo culture)
    {
        var name = culture.Name;
        if (name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var region in new[] { "TW", "HK", "MO" })
        {
            if (name.EndsWith("-" + region, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
