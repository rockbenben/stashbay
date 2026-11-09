using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;

namespace FileStash.App.Services;

/// <summary>
/// 把区域性挂到 WPF 上。
/// </summary>
/// <remarks>
/// 四处都要设，缺一不可：
/// ① 资源查表（ResourceManager）看的是 <see cref="CultureInfo.CurrentUICulture"/>；
/// ② WPF 绑定的日期 / 数字格式化看的是元素上的 <c>Language</c> 属性
///    （框架默认写死 en-US），跟 CurrentCulture 无关 —— 所以要 OverrideMetadata 改掉默认值；
/// ③ 从右往左的语言（阿拉伯语）整个界面要镜像，靠 <see cref="FrameworkElement.FlowDirection"/>；
/// ④ 文字的默认色要跟着主题走 —— 靠 <c>TextElement.Foreground</c> 与
///    <c>Control.Foreground</c> 一起继承下去（只设一个不稳，见 <see cref="ApplyPerWindow"/>）。
///
/// <see cref="FrameworkElement.LanguageProperty.OverrideMetadata"/> 对同一个类型只能调一次，
/// 第二次直接抛 <see cref="ArgumentException"/>；所以它只在启动路径上调用一次，
/// 运行中切语言靠重启应用。
/// </remarks>
public static class LanguageService
{
    private static bool _metadataApplied;
    private static bool _classHandlerRegistered;

    /// <summary>主题里"正文色"这个 token 的键名。</summary>
    private const string TextPrimaryKey = "Brush.TextPrimary";

    /// <summary>
    /// 界面排版方向：从右往左的语言（阿拉伯语）要整个镜像。
    /// </summary>
    public static FlowDirection Direction { get; private set; } = FlowDirection.LeftToRight;

    /// <summary>应用区域性：资源查表、日期数字格式化、排版方向都靠它。</summary>
    public static void Apply(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // 空区域性（InvariantCulture）没有 IetfLanguageTag，XmlLanguage 不认它
        var tag = string.IsNullOrEmpty(culture.IetfLanguageTag) ? "en-US" : culture.IetfLanguageTag;

        // 方向要在元数据覆盖之前定下来：窗口上的方向就取这个值
        Direction = culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        if (!_metadataApplied)
        {
            _metadataApplied = true;

            // 第二个参数是 inherits：**必须显式传 true**。
            // FrameworkPropertyMetadata(object) 这个便捷构造器把 inherits 留成了 false，
            // 而 Language 是继承属性 —— 用便捷构造器覆盖等于悄悄把继承关掉：
            // 根元素对了，子孙元素又退回框架默认（en-US）。
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(tag), FrameworkPropertyMetadataOptions.Inherits));
        }

        ApplyPerWindow();
    }

    /// <summary>
    /// 每个窗口都要单独设两件事：文字默认色、排版方向。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不在资源字典里写个 <c>&lt;Style TargetType="Window"&gt;</c> 就完事：
    /// <b>WPF 的隐式样式按元素的<b>精确类型</b>找</b>，
    /// <c>MainWindow</c> / <c>SettingsWindow</c> 这些派生类<b>不继承</b>基类的隐式样式 ——
    /// 那份样式一次也没落到窗口上。
    /// </para>
    /// <para>
    /// 后果实测过（不是推测）：
    /// <list type="bullet">
    /// <item>暗色主题下没套具名样式的文字<b>黑压黑</b>，直接看不见 ——
    /// 文字默认色原本就挂在那个隐式样式上（挂在 Window 上、靠继承传下去）。</item>
    /// <item>阿拉伯语<b>从来没镜像过</b>：动作条、下拉框全是 LTR。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 现在用类处理器兜住所有窗口。<c>Loaded</c> 在第一次布局之前触发，赶得上首帧 ——
    /// 晚一步就是先按 LTR 排一遍再跳成 RTL，肉眼看得见。
    /// </para>
    /// </remarks>
    private static void ApplyPerWindow()
    {
        // 类处理器对"同一个类型 + 同一个路由事件 + 同一个委托类型"只能注册一次，
        // 第二次会抛 ArgumentException。Apply 目前只在启动路径上走一次，
        // 但它是 public 的 —— 真有人调第二次，不能让应用起不来。
        if (_classHandlerRegistered)
        {
            return;
        }

        _classHandlerRegistered = true;

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, e) =>
            {
                // Loaded 是冒泡事件：子孙的 Loaded 也会冒到窗口这里，
                // 只认事件源就是窗口本身的那一次。
                if (!ReferenceEquals(s, e.OriginalSource) || s is not Window window)
                {
                    return;
                }

                window.FlowDirection = Direction;

                // 文字默认色：DynamicResource 跟着主题走（浅色/深色换的是同一个 token）。
                //
                // 两个属性都要设，因为它们不是一回事：
                //   · TextElement.Foreground 是附加属性，TextBlock 读它；
                //   · Control.Foreground 是 Control 自己的属性，控件的继承链走它。
                // 实测（探针程序）：只设 TextElement.Foreground 时，
                // 之后任何一次让 Control.Foreground 变化的改动都会把黑色重新推给子孙，
                // 文字又变回"暗色主题下看不见"。两个一起设才稳。
                window.SetResourceReference(TextElement.ForegroundProperty, TextPrimaryKey);
                window.SetResourceReference(Control.ForegroundProperty, TextPrimaryKey);
            }));
    }
}
