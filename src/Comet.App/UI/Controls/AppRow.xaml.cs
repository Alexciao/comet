using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Controls;

public sealed partial class AppRow : UserControl
{
    public AppRow() => InitializeComponent();

    internal AppRow(string app)
        : this()
    {
        NameText.Text = app;
        AutomationProperties.SetName(RemoveButton, $"Remove {app}");
    }

    public event Action? RemoveRequested;

    private void OnRemoveClick(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke();
}
