using System.Windows;
using FileStash.Core.Models;
using FileStash.Infrastructure.Native;

namespace FileStash.App.Controls;

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// 主题切换。token 全部定义在 Resources/Themes 下：Light.xaml 是完整定义，
/// Dark.xaml 只含同名覆盖键。运行时把 Dark 插到 Light 之后即可切换。
/// </summary>
/// <remarks>
/// 控件样式必须用 DynamicResource 引用这些 token —— StaticResource 在 XAML 加载时解析一次、
/// 之后不再求值，运行时换字典它不跟。
/// </remarks>
internal static class ThemeService
{
    private static bool _windowHandlerRegistered;

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static bool IsDark => Current == AppTheme.Dark;

    public static void ApplyFromSettings(AppSettings settings) =>
        Apply(ThemeChoiceResolver.WantsDark(settings.Theme, settings.Theme != ThemeChoice.System || SystemTheme.IsLight())
            ? AppTheme.Dark
            : AppTheme.Light);

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null || !app.Dispatcher.CheckAccess() || (_windowHandlerRegistered && Current == theme))
        {
            return;
        }

        if (!_windowHandlerRegistered)
        {
            _windowHandlerRegistered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) =>
                {
                    if (ReferenceEquals(sender, args.OriginalSource) && sender is Window window)
                        WindowEffects.ApplyDarkTitleBar(window, IsDark);
                }));
        }

        var dictionaries = app.Resources.MergedDictionaries;

        // 先把已有的深色字典摘掉（可能在别的调用点已经加过）
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (IsTheme(dictionaries[i], "Dark.xaml"))
            {
                dictionaries.RemoveAt(i);
            }
        }

        if (theme == AppTheme.Dark)
        {
            var lightIndex = FindTheme(dictionaries, "Light.xaml");
            var dark = new ResourceDictionary
            {
                Source = new Uri("Resources/Themes/Dark.xaml", UriKind.Relative),
            };

            // 插在 Light 之后、Controls 之前
            dictionaries.Insert(lightIndex >= 0 ? lightIndex + 1 : 0, dark);
        }

        Current = theme;
        foreach (Window window in app.Windows)
        {
            WindowEffects.ApplyDarkTitleBar(window, IsDark);
            if (window is FileStash.App.Views.MainWindow shelf)
                WindowEffects.SetShelfChrome(shelf, shelf.ViewModel.IsCompact);
        }
    }

    private static int FindTheme(IList<ResourceDictionary> dictionaries, string fileName)
    {
        for (var i = 0; i < dictionaries.Count; i++)
        {
            if (IsTheme(dictionaries[i], fileName))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsTheme(ResourceDictionary dictionary, string fileName)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null && source.EndsWith(fileName, StringComparison.OrdinalIgnoreCase);
    }
}
