using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CorgiBlackboard.Core.Input;

namespace CorgiBlackboard.App.Controls;

public partial class TeachingToolbar : UserControl
{
    public TeachingToolbar() => InitializeComponent();

    public event EventHandler? ToolChanged;
    public event EventHandler? DiagnosticsRequested;
    public event EventHandler? OpenImageRequested;
    public event EventHandler? DocumentListRequested;

    public ToolSettings Selection
    {
        get
        {
            var selected = Tools.Children.OfType<ToggleButton>()
                .Where(button => button.IsChecked == true)
                .ToDictionary(button => Group(AutomationProperties.GetAutomationId(button)));
            var color = ((SolidColorBrush)selected["Tool.Color"].Background).Color;
            return new ToolSettings(
                ReferenceEquals(selected["Tool"], EraserButton) ? InputTool.Eraser : InputTool.Ink,
                ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B,
                Size(selected["Tool.Width"]), Size(selected["Tool.EraserSize"]));
        }
    }

    private static string Group(string id) => id[..id.LastIndexOf('.')];

    private static double Size(ToggleButton button)
    {
        var id = AutomationProperties.GetAutomationId(button);
        return double.Parse(id[(id.LastIndexOf('.') + 1)..], CultureInfo.InvariantCulture);
    }

    private void OpenImage(object sender, RoutedEventArgs e) => OpenImageRequested?.Invoke(this, EventArgs.Empty);
    private void ShowDocuments(object sender, RoutedEventArgs e) => DocumentListRequested?.Invoke(this, EventArgs.Empty);

    private void SelectTool(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not ToggleButton selected)
            return;

        var id = AutomationProperties.GetAutomationId(selected);
        var group = id[..id.LastIndexOf('.')];
        foreach (var button in Tools.Children.OfType<ToggleButton>())
        {
            var otherId = AutomationProperties.GetAutomationId(button);
            if (otherId[..otherId.LastIndexOf('.')] == group)
                button.IsChecked = ReferenceEquals(button, selected);
        }

        ToolChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenDiagnostics(object sender, RoutedEventArgs e) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
}
