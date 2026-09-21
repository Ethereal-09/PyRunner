using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PyRunner.Editor;
using PyRunner.Services;

namespace PyRunner.ViewModels;

public sealed record ScriptTemplateOption(ScriptTemplate Template, string Name, string Description)
{
    public override string ToString() => Name;
}

public sealed partial class ScriptTemplateViewModel : ObservableObject
{
    private readonly IScriptTemplateService _templates;
    private readonly IScriptPathService _paths;
    private readonly IScriptService _scripts;
    private readonly ILocalizationService _localization;

    public ObservableCollection<ScriptTemplateOption> TemplateOptions { get; } = [];
    public ObservableCollection<string> TargetDirectories { get; } = [];
    [ObservableProperty] private ScriptTemplateOption? selectedTemplate;
    [ObservableProperty] private string? selectedDirectory;
    [ObservableProperty] private string fileName = "script.py";
    [ObservableProperty] private string errorMessage = string.Empty;
    public string? CreatedFilePath { get; private set; }

    public string Title => _localization["Template_Title"];
    public string TemplateLabel => _localization["Template_Type"];
    public string DirectoryLabel => _localization["Template_Directory"];
    public string FileNameLabel => _localization["Template_FileName"];
    public string PreviewLabel => _localization["Template_Preview"];
    public string CreateText => _localization["Template_Create"];
    public string CancelText => _localization["Button_Cancel"];
    public string PreviewText => SelectedTemplate?.Template.Content ?? string.Empty;

    public ScriptTemplateViewModel(
        IScriptTemplateService templates,
        IScriptPathService paths,
        IScriptService scripts,
        ILocalizationService localization)
    {
        _templates = templates;
        _paths = paths;
        _scripts = scripts;
        _localization = localization;
        foreach (var template in templates.GetTemplates())
            TemplateOptions.Add(new(template, localization[template.NameKey], localization[template.DescriptionKey]));
        foreach (var path in paths.GetAll().Where(item => item.Enabled && Directory.Exists(item.Path)))
            TargetDirectories.Add(Path.GetFullPath(path.Path));
        SelectedTemplate = TemplateOptions.FirstOrDefault();
        SelectedDirectory = TargetDirectories.FirstOrDefault();
    }

    partial void OnSelectedTemplateChanged(ScriptTemplateOption? value)
    {
        OnPropertyChanged(nameof(PreviewText));
        ErrorMessage = string.Empty;
    }

    public bool Create()
    {
        ErrorMessage = string.Empty;
        if (SelectedTemplate is null || string.IsNullOrWhiteSpace(SelectedDirectory))
        {
            ErrorMessage = _localization["Template_Error_Directory"];
            return false;
        }
        var result = _templates.Create(SelectedTemplate.Template.Id, SelectedDirectory, FileName);
        if (!result.Succeeded || result.FilePath is null)
        {
            ErrorMessage = _localization[result.ErrorKey];
            return false;
        }
        _scripts.ImportFromPaths(_paths.GetAll().Where(path => path.Enabled).Select(path => path.Path).ToArray());
        CreatedFilePath = result.FilePath;
        return true;
    }
}
