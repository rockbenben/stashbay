namespace FileStash.App.ViewModels;

/// <summary>下拉框里的一项：值 + 中文显示名。</summary>
public sealed class EnumOption<T>(T value, string label)
{
    public T Value { get; } = value;

    public string Label { get; } = label;

    public override string ToString() => Label;
}
