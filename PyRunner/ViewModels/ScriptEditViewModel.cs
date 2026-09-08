using CommunityToolkit.Mvvm.ComponentModel;
using PyRunner.Models;
using PyRunner.Services;

namespace PyRunner.ViewModels;

/// <summary>解释器下拉选项（Id=null 表示使用默认解释器）。</summary>
public sealed class InterpreterOption
{
    public int? Id { get; init; }
    public string Display { get; init; } = string.Empty;
    public override string ToString() => Display;
}

/// <summary>
/// 脚本编辑对话框 ViewModel：字段校验（字段级红字）、路径重复拦截（排除自身）、
/// 文案全部来自 ILocalizationService，VM 不接触 SQL。
/// </summary>
public sealed partial class ScriptEditViewModel : ObservableObject
{
    private readonly IScriptService _scriptService;
    private readonly IInterpreterService _interpreterService;
    private readonly ILocalizationService _localization;

    /// <summary>正在编辑的原记录 Id。</summary>
    private int _editingId;

    /// <summary>
    /// 编辑表单不暴露收藏字段，但保存时仍须携带原值，避免用表单 DTO 重建
    /// <see cref="Script"/> 时把未编辑的收藏状态重置为默认 false。
    /// </summary>
    private bool _editingIsFavorite;

    public ScriptEditViewModel(
        IScriptService scriptService,
        IInterpreterService interpreterService,
        ILocalizationService localization)
    {
        _scriptService = scriptService;
        _interpreterService = interpreterService;
        _localization = localization;

        // 热切换订阅约定：语言变更时批量刷新全部展示属性（对话框关闭时经 DetachLocalization 退订）
        _localization.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(LabelName));
        OnPropertyChanged(nameof(LabelFilePath));
        OnPropertyChanged(nameof(FilePathPlaceholder));
        OnPropertyChanged(nameof(LabelCategory));
        OnPropertyChanged(nameof(LabelTags));
        OnPropertyChanged(nameof(LabelDescription));
        OnPropertyChanged(nameof(LabelInterpreter));
        OnPropertyChanged(nameof(LabelArguments));
        OnPropertyChanged(nameof(LabelWorkingDirectory));
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(BrowseText));
        RefreshInterpreterOptions();
    }

    /// <summary>对话框关闭时退订（服务层单例，避免事件持有瞬态 VM）。</summary>
    public void DetachLocalization() => _localization.LanguageChanged -= OnLanguageChanged;

    // ---- 表单字段 ----

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private string _tags = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private InterpreterOption? _selectedInterpreter;

    // ---- 字段级错误（非空时红字显示） ----

    [ObservableProperty]
    private string _nameError = string.Empty;

    [ObservableProperty]
    private string _pathError = string.Empty;

    /// <summary>启动参数字段错误（评审修复 4：引号未配对红字）。</summary>
    [ObservableProperty]
    private string _argumentsError = string.Empty;

    // ---- 展示属性 ----

    public string Title => _localization["ScriptEdit_Title_Edit"];

    public string LabelName => _localization["Field_Name"];
    public string LabelFilePath => _localization["Field_FilePath"];
    public string FilePathPlaceholder => _localization["Field_FilePath_Placeholder"];
    public string LabelCategory => _localization["Field_Category"];
    public string LabelTags => _localization["Field_Tags"];
    public string LabelDescription => _localization["Field_Description"];
    public string LabelInterpreter => _localization["Field_Interpreter"];
    public string LabelArguments => _localization["Field_Arguments"];
    public string LabelWorkingDirectory => _localization["Field_WorkingDirectory"];
    public string SaveText => _localization["Button_Save"];
    public string CancelText => _localization["Button_Cancel"];
    public string BrowseText => _localization["Button_Browse"];

    public List<InterpreterOption> Interpreters { get; private set; } = new();

    /// <summary>重建解释器下拉（保持当前选中 Id；语言切换时刷新「默认」词条）。</summary>
    private void RefreshInterpreterOptions()
    {
        var selectedId = SelectedInterpreter?.Id;

        Interpreters = new List<InterpreterOption>
        {
            new() { Id = null, Display = $"({_localization["Interpreter_Default"]})" },
        };
        Interpreters.AddRange(_interpreterService.GetAll()
            .Select(i => new InterpreterOption { Id = i.Id, Display = i.Name }));

        SelectedInterpreter = Interpreters.FirstOrDefault(o => o.Id == selectedId)
            ?? Interpreters[0];

        OnPropertyChanged(nameof(Interpreters));
    }

    /// <summary>对话框打开前加载已有脚本和解释器选项。</summary>
    public void Prepare(Script existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        _editingId = existing.Id;
        _editingIsFavorite = existing.IsFavorite;

        RefreshInterpreterOptions();

        Name = existing.Name;
        FilePath = existing.FilePath;
        Category = existing.Category ?? string.Empty;
        Tags = existing.Tags ?? string.Empty;
        Description = existing.Description ?? string.Empty;
        Arguments = existing.Arguments ?? string.Empty;
        WorkingDirectory = existing.WorkingDirectory ?? string.Empty;
        SelectedInterpreter = Interpreters.FirstOrDefault(o => o.Id == existing.InterpreterId)
            ?? Interpreters[0];

        NameError = PathError = ArgumentsError = string.Empty;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>校验并保存。成功返回 true（调用方关闭对话框）；失败填充字段错误返回 false。
    /// 由对话框 Closing 事件直接调用（非命令绑定）。</summary>
    public bool Save()
    {
        NameError = string.Empty;
        PathError = string.Empty;
        ArgumentsError = string.Empty;

        var valid = true;

        if (string.IsNullOrWhiteSpace(Name))
        {
            NameError = _localization["Error_NameRequired"];
            valid = false;
        }

        var path = FilePath.Trim();
        if (path.Length == 0)
        {
            PathError = _localization["Error_PathRequired"];
            valid = false;
        }
        else if (!File.Exists(path))
        {
            PathError = _localization["Error_PathNotFound"];
            valid = false;
        }
        else if (!path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            PathError = _localization["Error_PathNotPy"];
            valid = false;
        }

        var workDir = WorkingDirectory.Trim();
        if (workDir.Length > 0 && !Directory.Exists(workDir))
        {
            PathError = _localization["Error_DirectoryNotFound"];
            valid = false;
        }

        // 评审修复 4：启动参数引号配对字段级校验——奇数个双引号会在命令行拼装时
        // 吞没脚本路径（RunCoordinator.TryStart 另有同规则兜底拦截存量数据）
        if (Arguments.Count(ch => ch == '"') % 2 != 0)
        {
            ArgumentsError = _localization["Error_ArgumentsQuoteMismatch"];
            valid = false;
        }

        if (!valid) return false;

        var script = new Script
        {
            Id = _editingId,
            Name = Name.Trim(),
            FilePath = path,
            Category = Category.Trim(),
            Tags = Tags.Trim(),
            Description = Description.Trim(),
            Arguments = Arguments.Trim(),
            WorkingDirectory = WorkingDirectory.Trim(),
            InterpreterId = SelectedInterpreter?.Id,
            IsFavorite = _editingIsFavorite,
        };

        try
        {
            _scriptService.Update(script);

            return true;
        }
        catch (ValidationException ex)
        {
            // 服务层业务校验（类型化异常）：LocalizationKey 为本地化键
            PathError = _localization[ex.LocalizationKey];
            return false;
        }
        catch (Exception ex)
        {
            // 数据层/IO 等非业务故障：通用保存失败文案，避免经调用方 async 路径崩进程
            System.Diagnostics.Debug.WriteLine($"ScriptEditViewModel.Save: 保存失败（{ex.Message}）");
            PathError = _localization["Error_SaveFailed"];
            return false;
        }
    }
}
