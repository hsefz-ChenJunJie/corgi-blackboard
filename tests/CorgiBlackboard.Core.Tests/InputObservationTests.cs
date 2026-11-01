using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed class InputObservationTests
{
    [Fact]
    public void Shared_boundary_observes_content_chrome_and_ignored_samples_without_altering_arbitration()
    {
        // Given the same arbiter entry used by both WPF content and window chrome.
        var arbiter = new InputArbiter();
        var surface = SurfaceId.New();
        var observations = new List<InputObservation>();
        arbiter.Observed += observations.Add;
        var down = new PointerSample(1, 0, 1, new ScreenPoint(100, 100), PointerDeviceKind.Touch,
            PointerAction.Down, InputSurfaceKind.Content, surface, new ViewportTransform(1, 0, 0));

        // When the contact is duplicated, chrome interrupts it, and a promoted/orphan sample arrives.
        arbiter.Process(down);
        arbiter.Process(down with { Sequence = 1 });
        arbiter.Process(down with { PointerId = 2, Sequence = 2, SurfaceKind = InputSurfaceKind.Chrome, SurfaceId = null });
        arbiter.Process(down with { PointerId = 3, Sequence = 3, IsPromotedMouse = true });
        arbiter.Process(down with { PointerId = 4, Sequence = 4, Action = PointerAction.Up });

        // Then each raw sample has one truthful disposition, and cancel/navigation semantics remain intact.
        var samples = observations.Where(value => value.Kind == InputObservationKind.Sample).ToArray();
        Assert.Equal(new[] { PointerDisposition.Accepted, PointerDisposition.DuplicateDown, PointerDisposition.Accepted,
            PointerDisposition.PromotedMouse, PointerDisposition.Orphan }, samples.Select(value => value.Disposition!.Value));
        Assert.Equal(InputArbiterState.Idle, samples[0].Before);
        Assert.Equal(InputArbiterState.Editing, samples[0].After);
        Assert.Equal(InputSurfaceKind.Chrome, samples[2].Sample!.Value.SurfaceKind);
        Assert.Equal(2, samples[2].ContactsAfter);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
        Assert.Contains(observations, value => value.Kind == InputObservationKind.Transition &&
            value.Before == InputArbiterState.Editing && value.After == InputArbiterState.Cancel &&
            value.Reason == InputCancellationReason.SecondContact);
    }

    [Theory]
    [InlineData(InputCancellationReason.Wheel)]
    [InlineData(InputCancellationReason.Resize)]
    [InlineData(InputCancellationReason.DpiChanged)]
    [InlineData(InputCancellationReason.FocusLost)]
    public void Explicit_interruption_observes_release_barrier_and_original_reason(InputCancellationReason reason)
    {
        var arbiter = new InputArbiter();
        var observations = new List<InputObservation>();
        arbiter.Observed += observations.Add;
        arbiter.Process(new PointerSample(1, 0, 1, new ScreenPoint(10, 10), PointerDeviceKind.Touch,
            PointerAction.Down, InputSurfaceKind.Content, SurfaceId.New(), new ViewportTransform(1, 0, 0)));

        arbiter.Interrupt(reason);

        var interruption = Assert.Single(observations, value => value.Kind == InputObservationKind.Interruption);
        Assert.Equal(reason, interruption.Reason);
        Assert.Equal(InputArbiterState.Editing, interruption.Before);
        Assert.Equal(InputArbiterState.AwaitAllReleased, interruption.After);
        Assert.Equal(1, interruption.ContactsAfter);
    }

    [Fact]
    public void Invalid_sample_is_observed_and_original_validation_exception_is_preserved()
    {
        var arbiter = new InputArbiter();
        var observations = new List<InputObservation>();
        arbiter.Observed += observations.Add;
        var sample = new PointerSample(-1, 0, 1, new ScreenPoint(10, 10), PointerDeviceKind.Mouse,
            PointerAction.Down, InputSurfaceKind.Chrome, null, default);

        Assert.Throws<ArgumentOutOfRangeException>(() => arbiter.Process(sample));

        var observed = Assert.Single(observations);
        Assert.Equal(PointerDisposition.Invalid, observed.Disposition);
        Assert.Equal(sample, observed.Sample);
        Assert.Equal(InputArbiterState.Idle, arbiter.State);
    }
}
