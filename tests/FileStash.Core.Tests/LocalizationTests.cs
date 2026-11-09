using System.Globalization;
using FileStash.Core.Localization;
using FileStash.Core.Models;

namespace FileStash.Core.Tests;

/// <summary>
/// 多语言这条线的护栏：语言选什么语言、每门语言的资源是不是真的取得到。
/// </summary>
/// <remarks>
/// "英文能不能出来"这件事没法靠看代码判断：卫星程序集没生成、资源基名对不上、
/// 卫星没被打进发布包 —— 三种故障的表现都是同一个：界面全是中文，而且不报错。
/// 所以得跑一次真取值的测试，而且每门语言都要跑一遍：
/// 18 门语言里只要有一门漏了卫星表，那门语言的用户打开应用看到的就是中文。
///
/// 期望值写死在这里，而不是"读出来跟英文不一样就行" ——
/// 后者那种判据下，一张漏翻的表也能蒙混过关：它跟英文不一样，可它也是中文。
/// </remarks>
public sealed class LocalizationTests
{
    [Fact]
    public void Resolve_Chinese_MustLandOnChinese()
    {
        Assert.Equal("zh-CN", AppLanguageResolver.Resolve(AppLanguage.Chinese).IetfLanguageTag);
    }

    [Fact]
    public void Resolve_English_MustLandOnEnglish()
    {
        Assert.Equal("en-US", AppLanguageResolver.Resolve(AppLanguage.English).IetfLanguageTag);
    }

    [Fact]
    public void Resolve_System_MustFollowTheGivenSystemCulture()
    {
        var culture = AppLanguageResolver.Resolve(AppLanguage.System, new CultureInfo("fr-FR"));

        Assert.Equal("fr-FR", culture.IetfLanguageTag);
    }

    /// <summary>
    /// 跟随系统：系统语言是地区变体时，要落到我们**真的备了卫星表**的那一门。
    /// </summary>
    /// <remarks>
    /// ResourceManager 是按区域链找表的：de-AT → de → 中性表。
    /// 我们只有 de-DE 的卫星表，"de" 没有表 —— 于是奥地利、瑞士的德语用户
    /// 会一路退到中文，而下拉框里写着"跟随系统"。
    /// </remarks>
    [Theory]
    [InlineData("de-AT", "de-DE")]
    [InlineData("de-CH", "de-DE")]
    [InlineData("fr-CA", "fr-FR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("es-MX", "es-ES")]
    [InlineData("en-AU", "en-US")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("zh-CN", "zh-CN")]
    public void Resolve_System_MustSnapRegionalVariantsToASupportedLanguage(string system, string expected)
    {
        var culture = AppLanguageResolver.Resolve(AppLanguage.System, new CultureInfo(system));

        Assert.Equal(expected, culture.IetfLanguageTag);
    }

    [Fact]
    public void Resolve_System_MustFallBackToEnglishForUnsupportedLanguages()
    {
        // 没有对应卫星表的语言（这里用希伯来语）退英文，不是退中文 ——
        // 中性表是中文，那等于把一个不懂中文的用户推到看不懂的界面上。
        var culture = AppLanguageResolver.Resolve(AppLanguage.System, new CultureInfo("he-IL"));

        Assert.Equal("en-US", culture.IetfLanguageTag);
    }

    [Fact]
    public void EveryMappedLanguage_MustHaveACulture()
    {
        // 枚举里每一种语言都要能解析出区域性。
        // 漏了一种，界面就会用系统语言显示，而下拉框里写着用户选的那门语言。
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var culture = AppLanguageResolver.Resolve(language);

            Assert.False(string.IsNullOrEmpty(culture.IetfLanguageTag));
        }
    }

    [Theory]
    [InlineData("zh-CN", "跟随系统")]
    [InlineData("en-US", "Follow the system")]
    [InlineData("zh-TW", "跟隨系統")]
    [InlineData("ja-JP", "システムに従う")]
    [InlineData("ko-KR", "시스템 따르기")]
    [InlineData("es-ES", "Seguir el sistema")]
    [InlineData("pt-BR", "Seguir o sistema")]
    [InlineData("fr-FR", "Suivre le système")]
    [InlineData("de-DE", "Der Systemsprache folgen")]
    [InlineData("it-IT", "Segui il sistema")]
    [InlineData("ru-RU", "Как в системе")]
    [InlineData("ar-SA", "اتّباع النظام")]
    [InlineData("hi-IN", "सिस्टम के अनुसार")]
    [InlineData("id-ID", "Ikuti sistem")]
    [InlineData("th-TH", "ตามระบบ")]
    [InlineData("tr-TR", "Sistemi izle")]
    [InlineData("vi-VN", "Theo hệ thống")]
    [InlineData("bn-BD", "সিস্টেম অনুসরণ করুন")]
    public void AppResources_MustActuallyResolveForEveryLanguage(string culture, string expected)
    {
        // 这条断言就是"卫星程序集到底有没有生成"的照妖镜：
        // 取不到的时候 ResourceManager 不报错，只是悄悄退回中性表（中文）。
        var original = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            Assert.Equal(expected, FileStash.App.Resources.Strings.LanguageSystem);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Theory]
    [InlineData("zh-CN", "未跟踪")]
    [InlineData("en-US", "Untracked")]
    [InlineData("ja-JP", "未追跡")]
    [InlineData("ko-KR", "추적되지 않음")]
    [InlineData("es-ES", "Sin seguimiento")]
    [InlineData("pt-BR", "Não rastreado")]
    [InlineData("fr-FR", "Non suivi")]
    [InlineData("de-DE", "Nicht verfolgt")]
    [InlineData("it-IT", "Non tracciato")]
    [InlineData("ru-RU", "Не отслеживается")]
    [InlineData("ar-SA", "غير متتبَّع")]
    [InlineData("hi-IN", "अनट्रैक्ड")]
    [InlineData("id-ID", "Tidak dilacak")]
    [InlineData("th-TH", "ไม่ได้ติดตาม")]
    [InlineData("tr-TR", "İzlenmiyor")]
    [InlineData("vi-VN", "Chưa theo dõi")]
    [InlineData("bn-BD", "অট্র্যাকড")]
    [InlineData("zh-TW", "未追蹤")]
    public void CoreResources_MustActuallyResolveForEveryLanguage(string culture, string expected)
    {
        // 核心库自己也带一张资源表（错误提示、目标名那些），
        // 它的卫星程序集跟界面的那张是两回事 —— 得单独验一遍。
        var original = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            Assert.Equal(expected, FileStash.Core.Resources.Strings.GitStatusUntracked);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
