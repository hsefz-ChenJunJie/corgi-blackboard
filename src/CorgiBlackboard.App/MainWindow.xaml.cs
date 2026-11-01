using System.Windows;
using CorgiBlackboard.App.Diagnostics;
using CorgiBlackboard.App.Documents;
using CorgiBlackboard.App.Input;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Infrastructure.Diagnostics;

namespace CorgiBlackboard.App;

public partial class MainWindow : Window
{
    private readonly DiagnosticSession? _diagnostics;
    private readonly WorkspaceDiagnostics? _workspaceDiagnostics;

    public MainWindow(DiagnosticSession? diagnostics = null) : this(new DiagnosticStartup<DiagnosticSession>(diagnostics, null)) { }

    internal MainWindow(DiagnosticStartup<DiagnosticSession> startup)
    {
        var diagnostics = startup.Session;
        _diagnostics = diagnostics;
        InitializeComponent();
        DiagnosticStartupStatus.Text = startup.StatusText;
        DiagnosticStartupStatus.Visibility = startup.IsDegraded ? Visibility.Visible : Visibility.Collapsed;
        Editing = new EditingContext(new Workspace(), Toolbar.Selection);
        _diagnostics?.TryRecord(DiagnosticProducers.ToolSelected(Toolbar.Selection));
        if (diagnostics is not null) _workspaceDiagnostics = new WorkspaceDiagnostics(Workspace, Input, diagnostics);
        Board.Attach(Editing, this);
        Documents = new ImageWindowHost(Workspace, Input, DocumentWindowLayer, this);
        InitializeImages();
        InitializeDesktopQa();
        Toolbar.DiagnosticsRequested += (_, _) =>
        {
            startup.OpenDiagnostics(session =>
            {
                var window = new DiagnosticsWindow(session) { Owner = this };
                window.ShowDialog();
            }, message => MessageBox.Show(this, message, "诊断日志不可用", MessageBoxButton.OK, MessageBoxImage.Warning));
        };
        Toolbar.ToolChanged += (_, _) =>
        {
            var selection = Toolbar.Selection;
            Editing.Session.Select(selection);
            _diagnostics?.TryRecord(DiagnosticProducers.ToolSelected(selection));
        };
        LocationChanged += (_, _) => Input.Interrupt(InputCancellationReason.LayoutChanged);
        Toolbar.OpenImageRequested += (_, _) => OpenImageRequested?.Invoke(this, EventArgs.Empty);
        Toolbar.DocumentListRequested += (_, _) =>
        {
            var menu = Documents.CreateWindowMenu();
            menu.PlacementTarget = Toolbar.DocumentListButton;
            menu.IsOpen = true;
        };
        Closed += (_, _) =>
        {
            Documents.Dispose();
            _workspaceDiagnostics?.Dispose();
        };
    }

    public EditingContext Editing { get; }
    public Workspace Workspace => Editing.Workspace;
    public InputArbiter Input => Editing.Arbiter;
    public ImageWindowHost Documents { get; }
    public event EventHandler? OpenImageRequested;
}
