using System.Reflection;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class EscCancelFromAllActiveStates
{
    private static readonly AppState[] ActiveStates = new[]
    {
        AppState.Capturing,
        AppState.Ocr,
        AppState.Selecting,
        AppState.OverlayVisible,
        AppState.Translating,
        AppState.Displaying
    };

    private static void ForceSetState(WordInteractionCoordinator coord, AppState state, out WordInteractionCoordinator.OperationSlot slot)
    {
        slot = new WordInteractionCoordinator.OperationSlot(Guid.NewGuid(), new CancellationTokenSource(), state);

        var currentField = typeof(WordInteractionCoordinator).GetField("_current",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(currentField);
        currentField!.SetValue(coord, slot);

        var stateProp = typeof(WordInteractionCoordinator).GetProperty("State",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(stateProp);
        var stateSetMethod = stateProp!.GetSetMethod(true);
        Assert.NotNull(stateSetMethod);
        stateSetMethod!.Invoke(coord, new object[] { state });
    }

    [Theory]
    [InlineData(AppState.Capturing)]
    [InlineData(AppState.Ocr)]
    [InlineData(AppState.Selecting)]
    [InlineData(AppState.OverlayVisible)]
    [InlineData(AppState.Translating)]
    [InlineData(AppState.Displaying)]
    public async Task Esc_Cancels_From_State(AppState targetState)
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out _, out _, out _,
            out var overlay, out _, out var popup);

        ForceSetState(coord, targetState, out var slot);

        Assert.Equal(targetState, coord.State);
        Assert.False(slot.Cts.IsCancellationRequested);

        broker.RaiseHotkeyFired(HotkeyEventType.Escape);
        await Task.Delay(50);

        Assert.Equal(AppState.Idle, coord.State);
        Assert.True(slot.Cts.IsCancellationRequested);
        Assert.True(overlay.HideAllCount >= 1);
        Assert.True(popup.HideAllCount >= 1);

        slot.Cts.Dispose();
    }

    [Fact]
    public async Task Esc_Cancels_All_6_ActiveStates_ResetToIdle()
    {
        var statesResults = new List<(AppState State, bool Passed)>();

        foreach (var state in ActiveStates)
        {
            var coord = CoordinatorTestHelpers.CreateCoordinator(
                out _, out var broker, out _, out _,
                out _, out _, out _,
                out var overlay, out _, out var popup);

            ForceSetState(coord, state, out var slot);

            broker.RaiseHotkeyFired(HotkeyEventType.Escape);
            await Task.Delay(50);

            var passed = coord.State == AppState.Idle
                         && slot.Cts.IsCancellationRequested
                         && overlay.HideAllCount >= 1
                         && popup.HideAllCount >= 1;

            statesResults.Add((state, passed));
            slot.Cts.Dispose();
        }

        Assert.All(statesResults, r => Assert.True(r.Passed, $"State {r.State} failed"));
        Assert.Equal(6, statesResults.Count);
    }
}
