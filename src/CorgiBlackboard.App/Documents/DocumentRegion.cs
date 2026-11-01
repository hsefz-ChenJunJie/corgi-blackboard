using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace CorgiBlackboard.App.Documents;

// Border has no peer by default; chrome hit regions must remain discoverable to UIA clients.
public sealed class DocumentRegion : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new RegionPeer(this);

    private sealed class RegionPeer(DocumentRegion owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(DocumentRegion);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
