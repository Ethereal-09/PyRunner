using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PyRunner.Views;

/// <summary>
/// 参数化空状态用户控件（Phase E 交付 16）：图标 64×64 + 13px 说明 + 可选幽灵按钮，
/// 视觉口径见 PRD §6.8.1。实际实现口径（评审修 5 更正，旧注释「七类」不实）：
/// ① 无脚本路径（侧栏树覆盖层 → 打开设置）；② 有路径但零脚本登记（覆盖层 → 引导新增）；
/// ③ 搜索无结果；④ 无运行历史；⑤ 无解释器（侧栏底部 → 直达设置解释器分组，修 5 新增）；
/// ⑥ 工作区空提示（无按钮）。
/// 口径差异留痕：「收藏空」不使用本控件（收藏树节点恒持 Placeholder 子节点，非空态）；
/// 「目录路径失效」为节点级置灰（树重建 Directory.Exists 检查），非整屏空态；
/// 「分类下无脚本」N/A：路径管理收敛于设置弹窗单一写路径，空分类在 GroupBy 下
/// 不产生分组。ShowButton=false 时按钮不渲染（修 5 补齐 Visibility 生效链路）。
/// </summary>
public sealed partial class EmptyStateView : UserControl
{
    /// <summary>图标模板（IconLibrary.xaml 的 DataTemplate；颜色已绑主题 Token）。</summary>
    public DataTemplate? IconTemplate
    {
        get => (DataTemplate?)GetValue(IconTemplateProperty);
        set => SetValue(IconTemplateProperty, value);
    }

    public static readonly DependencyProperty IconTemplateProperty =
        DependencyProperty.Register(nameof(IconTemplate), typeof(DataTemplate), typeof(EmptyStateView), null);

    /// <summary>说明文字（调用方本地化后注入）。</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(EmptyStateView),
            new PropertyMetadata(string.Empty));

    /// <summary>引导按钮文字（ShowButton=false 时不渲染按钮）。</summary>
    public string ActionButtonText
    {
        get => (string)GetValue(ActionButtonTextProperty);
        set => SetValue(ActionButtonTextProperty, value);
    }

    public static readonly DependencyProperty ActionButtonTextProperty =
        DependencyProperty.Register(nameof(ActionButtonText), typeof(string), typeof(EmptyStateView),
            new PropertyMetadata(string.Empty));

    /// <summary>是否展示引导按钮（无按钮的空状态：工作区提示/无历史等）。</summary>
    public bool ShowButton
    {
        get => (bool)GetValue(ShowButtonProperty);
        set => SetValue(ShowButtonProperty, value);
    }

    public static readonly DependencyProperty ShowButtonProperty =
        DependencyProperty.Register(nameof(ShowButton), typeof(bool), typeof(EmptyStateView),
            new PropertyMetadata(false));

    /// <summary>引导按钮点击（调用方据此打开设置/新增/清除搜索等）。</summary>
    public event EventHandler? ActionRequested;

    // 留痕（评审修 5）：ShowButton 现经 XAML x:Bind 接 ActionButton.Visibility（见 EmptyStateView.xaml），
    // 本类不再需要回调感知；「目录路径失效/分类下无脚本」口径差异见类注释。

    /// <summary>UIA 冒烟入口：给引导按钮挂 AutomationId（如 EmptyAddButton）。</summary>
    public string ActionAutomationId
    {
        set => AutomationProperties.SetAutomationId(ActionButton, value);
    }

    public EmptyStateView()
    {
        InitializeComponent();
    }

    private void OnActionClick(object sender, RoutedEventArgs e) =>
        ActionRequested?.Invoke(this, EventArgs.Empty);
}
