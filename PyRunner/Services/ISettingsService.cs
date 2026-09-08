using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>应用设置服务契约（PRD §7.5 修正：JSON 文件存储）。</summary>
public interface ISettingsService
{
    /// <summary>当前设置的独立快照。调用方不得通过该对象写回设置。</summary>
    AppSettings Current { get; }

    /// <summary>在服务锁内原子应用一组变更，并调度防抖写入。</summary>
    void Update(Action<AppSettings> update);

    /// <summary>立即刷盘（窗口关闭前调用）。</summary>
    void Flush();

    /// <summary>立即原子写入；失败时向调用方抛出，用于首次引导完成事务。</summary>
    void FlushOrThrow();
}
