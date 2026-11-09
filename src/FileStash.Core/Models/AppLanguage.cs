namespace FileStash.Core.Models;

/// <summary>界面语言。</summary>
/// <remarks>
/// 存的是选项而不是区域性名：区域性名（zh-CN / en-US）会随系统的书写习惯变，
/// 而"简体中文 / English"这种粒度对用户来说才是稳定的东西。
///
/// <para>
/// <strong>已有的值不能改、不能挪位置。</strong>设置文件里存的是枚举的**名字**
/// （<c>JsonStringEnumConverter</c> + camelCase），改名字会让老用户的
/// <c>settings.json</c> 反序列化失败；改顺序（虽然对字符串序列化没影响）
/// 会让按数字读的老文件错位。所以新语言一律**往后加**。
/// </para>
///
/// <para>
/// 语言清单与 fillduck 的 chatgptshortcut 扩展保持一致（18 门），
/// 便于两个项目共用同一批译文与同一套语言名写法。
/// </para>
/// </remarks>
public enum AppLanguage
{
    /// <summary>跟随系统。</summary>
    System = 0,

    /// <summary>简体中文。</summary>
    Chinese = 1,

    /// <summary>English。</summary>
    English = 2,

    /// <summary>繁體中文。</summary>
    TraditionalChinese = 3,

    /// <summary>日本語。</summary>
    Japanese = 4,

    /// <summary>한국어。</summary>
    Korean = 5,

    /// <summary>Español。</summary>
    Spanish = 6,

    /// <summary>Português (Brasil)。</summary>
    PortugueseBrazil = 7,

    /// <summary>Français。</summary>
    French = 8,

    /// <summary>Deutsch。</summary>
    German = 9,

    /// <summary>Italiano。</summary>
    Italian = 10,

    /// <summary>Русский。</summary>
    Russian = 11,

    /// <summary>العربية（从右往左）。</summary>
    Arabic = 12,

    /// <summary>हिन्दी。</summary>
    Hindi = 13,

    /// <summary>Bahasa Indonesia。</summary>
    Indonesian = 14,

    /// <summary>ไทย。</summary>
    Thai = 15,

    /// <summary>Türkçe。</summary>
    Turkish = 16,

    /// <summary>Tiếng Việt。</summary>
    Vietnamese = 17,

    /// <summary>বাংলা。</summary>
    Bengali = 18,
}
