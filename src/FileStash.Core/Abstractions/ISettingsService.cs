using FileStash.Core.Models;

namespace FileStash.Core.Abstractions;

/// <summary>读写 settings.json。</summary>
public interface ISettingsService
{
    /// <summary>当前设置（未加载时是默认值）。</summary>
    AppSettings Current { get; }

    /// <summary>从磁盘读取；文件不存在或损坏时回退到默认值。</summary>
    void Load();

    /// <summary>原子写回磁盘。</summary>
    void Save(AppSettings settings);
}
