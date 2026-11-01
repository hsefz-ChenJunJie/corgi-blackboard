using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App.Input;

/// <summary>
/// The single editing context of a main window: one <see cref="Workspace"/> for the board and every
/// document page, one <see cref="InputArbiter"/>, one WPF adapter and one <see cref="EditingSession"/>.
/// Content hosts attach a <see cref="ContentInputBinding"/> to this instance instead of creating their
/// own workspace; the toolbar changes tool settings only through <see cref="EditingSession.Select"/>.
/// </summary>
public sealed class EditingContext
{
    public EditingContext(Workspace workspace, ToolSettings settings)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Arbiter = new InputArbiter();
        Adapter = new WpfPointerAdapter(Arbiter);
        Session = new EditingSession(workspace, Arbiter, settings);
    }

    public Workspace Workspace { get; }
    public InputArbiter Arbiter { get; }
    public WpfPointerAdapter Adapter { get; }
    public EditingSession Session { get; }
}
