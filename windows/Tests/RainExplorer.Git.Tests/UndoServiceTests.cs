using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class UndoServiceTests
{
    [Fact]
    public void FailedActionRemainsAvailableForRetry()
    {
        UndoService service = UndoService.Instance;
        service.Clear();
        var action = new FailingAction();
        service.Push(action);

        try
        {
            Assert.Equal("The test operation failed.", service.Undo());
            Assert.True(service.CanUndo);
            Assert.False(service.CanRedo);
            Assert.Equal(1, action.InvocationCount);
        }
        finally
        {
            service.Clear();
        }
    }

    private sealed class FailingAction : UndoAction
    {
        public int InvocationCount { get; private set; }
        public override string Label => "Test operation";

        public override (string? error, UndoAction? redo) Invoke()
        {
            InvocationCount++;
            return ("The test operation failed.", null);
        }
    }
}
