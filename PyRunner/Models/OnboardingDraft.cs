using CommunityToolkit.Mvvm.ComponentModel;

namespace PyRunner.Models;

/// <summary>首次启动引导的纯内存草稿；只有最终确认后才由启动协调器写入持久层。</summary>
public sealed class OnboardingDraft
{
    public IReadOnlyList<string> ScriptDirectories { get; init; } = Array.Empty<string>();
    public string? InterpreterPath { get; init; }
    public bool AutoRefreshScripts { get; init; } = true;
}

/// <summary>引导目录页展示模型。</summary>
public sealed class OnboardingPathItem
{
    public string Path { get; init; } = string.Empty;
    public int ScriptCount { get; init; }
    public string CountText { get; init; } = string.Empty;
    public string RemoveText { get; init; } = string.Empty;
}

/// <summary>引导解释器页展示与单选状态。</summary>
public sealed partial class OnboardingInterpreterItem : ObservableObject
{
    public string ExecutablePath { get; init; } = string.Empty;
    public string VersionText { get; init; } = string.Empty;
    public string TypeText { get; init; } = string.Empty;
    public string BadgeText { get; init; } = string.Empty;
    public bool IsRecommended { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class OnboardingCompletedEventArgs : EventArgs
{
    public OnboardingCompletedEventArgs(OnboardingDraft draft) => Draft = draft;
    public OnboardingDraft Draft { get; }
}
