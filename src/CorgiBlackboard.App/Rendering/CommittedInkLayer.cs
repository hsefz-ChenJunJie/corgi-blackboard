using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App.Rendering;

/// <summary>
/// Retained committed ink: one <see cref="DrawingVisual"/> per stroke keyed by element ID, so a commit
/// only adds or removes the visuals it changed. Vector visuals only; no bitmap or render cache.
/// </summary>
public sealed class CommittedInkLayer : FrameworkElement
{
    private readonly VisualCollection _visuals;
    private readonly Dictionary<ElementId, DrawingVisual> _byId = [];
    private readonly HashSet<ElementId> _hidden = [];

    public CommittedInkLayer()
    {
        _visuals = new VisualCollection(this);
        IsHitTestVisible = false;
    }

    public int StrokeCount => _byId.Count;

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    /// <summary>Makes the visuals match committed surface content; unchanged strokes keep their visual.</summary>
    public void Sync(IReadOnlyList<Element> elements)
    {
        var strokes = elements.OfType<StrokeElement>().ToArray();
        var current = strokes.Select(stroke => stroke.Id).ToHashSet();
        foreach (var (id, visual) in _byId.ToArray())
        {
            if (!current.Contains(id))
            {
                _visuals.Remove(visual);
                _byId.Remove(id);
                _hidden.Remove(id);
            }
        }

        foreach (var stroke in strokes)
        {
            if (_byId.ContainsKey(stroke.Id))
            {
                continue;
            }

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                StrokeRenderer.Draw(context, stroke);
            }

            _visuals.Add(visual);
            _byId.Add(stroke.Id, visual);
        }
    }

    /// <summary>Hides committed strokes replaced by a pending erase preview and shows all others.</summary>
    public void Hide(ImmutableArray<StrokeElement> replaced)
    {
        var next = replaced.Select(stroke => stroke.Id).ToHashSet();
        foreach (var id in _hidden)
        {
            if (!next.Contains(id) && _byId.TryGetValue(id, out var visual))
            {
                visual.Opacity = 1;
            }
        }

        foreach (var id in next)
        {
            if (_byId.TryGetValue(id, out var visual))
            {
                visual.Opacity = 0;
            }
        }

        _hidden.Clear();
        _hidden.UnionWith(next);
    }
}
