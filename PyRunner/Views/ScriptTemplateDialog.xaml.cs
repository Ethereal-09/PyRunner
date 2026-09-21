using Microsoft.UI.Xaml.Controls;
using PyRunner.ViewModels;

namespace PyRunner.Views;

public sealed class ScriptTemplateDialog : ContentDialog
{
    public ScriptTemplateViewModel ViewModel { get; }
    public ScriptTemplateDialog(ScriptTemplateViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        Title = viewModel.Title;
        PrimaryButtonText = viewModel.CreateText;
        CloseButtonText = viewModel.CancelText;
        DefaultButton = ContentDialogButton.Primary;
        PrimaryButtonClick += OnPrimaryButtonClick;

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = viewModel.TemplateLabel });
        var templatePicker = new ComboBox { ItemsSource = viewModel.TemplateOptions, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(templatePicker, viewModel.TemplateLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(templatePicker, "TemplateTypeCombo");
        templatePicker.SetBinding(ComboBox.SelectedItemProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new("SelectedTemplate"), Mode = Microsoft.UI.Xaml.Data.BindingMode.TwoWay });
        panel.Children.Add(templatePicker);
        var description = new TextBlock { TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
        description.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new("SelectedTemplate.Description") });
        panel.Children.Add(description);
        panel.Children.Add(new TextBlock { Text = viewModel.DirectoryLabel });
        var directoryPicker = new ComboBox { ItemsSource = viewModel.TargetDirectories, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(directoryPicker, viewModel.DirectoryLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(directoryPicker, "TemplateDirectoryCombo");
        directoryPicker.SetBinding(ComboBox.SelectedItemProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new("SelectedDirectory"), Mode = Microsoft.UI.Xaml.Data.BindingMode.TwoWay });
        panel.Children.Add(directoryPicker);
        panel.Children.Add(new TextBlock { Text = viewModel.FileNameLabel });
        var fileName = new TextBox();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fileName, viewModel.FileNameLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(fileName, "TemplateFileName");
        fileName.SetBinding(TextBox.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new("FileName"), Mode = Microsoft.UI.Xaml.Data.BindingMode.TwoWay, UpdateSourceTrigger = Microsoft.UI.Xaml.Data.UpdateSourceTrigger.PropertyChanged });
        panel.Children.Add(fileName);
        panel.Children.Add(new TextBlock { Text = viewModel.PreviewLabel });
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap, Height = 220 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(preview, viewModel.PreviewLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(preview, "TemplatePreview");
        preview.SetBinding(TextBox.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new("PreviewText") });
        panel.Children.Add(preview);
        var error = new TextBlock { TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
        error.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new("ErrorMessage") });
        panel.Children.Add(error);
        Content = new ScrollViewer { Content = panel, MaxHeight = 560 };
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ViewModel.Create()) args.Cancel = true;
    }
}
