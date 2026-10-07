using System.Windows;
using PrivacyGuard.App.Helpers;
using PrivacyGuard.App.ViewModels;
using PrivacyGuard.Core.Models;
using Wpf.Ui.Controls;

namespace PrivacyGuard.App.Views;

public partial class RuleEditorWindow : FluentWindow
{
    private readonly RuleEditorViewModel _viewModel;

    /// <summary>The saved rule, or null if the user cancelled.</summary>
    public Rule? Result { get; private set; }

    public RuleEditorWindow(Rule rule, bool isNew)
    {
        _viewModel = new RuleEditorViewModel(rule, isNew);
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.PreviewChanged += (_, _) =>
            MatchHighlighter.Render(PreviewBox, _viewModel.SampleText, _viewModel.Matches);

        Loaded += (_, _) => _viewModel.RefreshPreview();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshPreview();
        if (!_viewModel.CanSave) return;

        Result = _viewModel.ToRule();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
