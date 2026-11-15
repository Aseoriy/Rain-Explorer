using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace RainExplorer.Git.Tests;

// Clicking X once must close the window even when the close-time cleanup finishes
// instantly (no archive sessions open). Before the fix the first click only disposed
// the tabs (the Home page showed) and a second click was needed.
public sealed class WindowCloseTests
{
    [Fact]
    public void SynchronousRecloseInsideClosingLeavesWindowOpen()
    {
        // Documents the root cause: WPF ignores Close() made from inside OnClosing.
        RunSta(() =>
        {
            var window = new CleanupFirstWindow(deferReclose: false);
            window.Show();

            window.Close();
            Pump();

            Assert.False(window.ClosedRaised);
            window.ForceClose();
        });
    }

    [Fact]
    public void DeferredRecloseClosesWindowWithOneClick()
    {
        RunSta(() =>
        {
            var window = new CleanupFirstWindow(deferReclose: true);
            window.Show();

            window.Close();
            Pump();

            Assert.True(window.ClosedRaised);
        });
    }

    private sealed class CleanupFirstWindow : Window
    {
        private readonly bool _deferReclose;
        private bool _cleanedUp;

        public CleanupFirstWindow(bool deferReclose)
        {
            _deferReclose = deferReclose;
            Width = 50;
            Height = 50;
            Left = -10000;
            Top = -10000;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStyle = WindowStyle.None;
        }

        public bool ClosedRaised { get; private set; }

        public void ForceClose()
        {
            _cleanedUp = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_cleanedUp)
            {
                // Same shape as MainWindow: cancel, clean up (instant here), close again.
                e.Cancel = true;
                _cleanedUp = true;
                if (_deferReclose) MainWindow.CloseAfterClosingReturns(this);
                else
                {
                    try { Close(); }
                    catch (InvalidOperationException) { }
                }
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            ClosedRaised = true;
            base.OnClosed(e);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
