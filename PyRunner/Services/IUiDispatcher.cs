namespace PyRunner.Services;

/// <summary>UI 通知调度抽象，使共享服务无需依赖具体 Page 或 Window。</summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }
    bool TryEnqueue(Action action);
}

/// <summary>无 UI 宿主和验证程序使用的同步调度器。</summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    public static InlineUiDispatcher Instance { get; } = new();
    private InlineUiDispatcher() { }
    public bool HasThreadAccess => true;
    public bool TryEnqueue(Action action)
    {
        action();
        return true;
    }
}
