using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopSplitter.Models;

namespace DesktopSplitter.Views;

/// <summary>
/// The keep-or-revert safety dialog. The countdown here is cosmetic — the elevated helper owns
/// the real timer and will auto-revert on its own — so a stalled UI can never strand the display.
/// </summary>
public partial class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer;

    // The compositor's fullscreen window re-asserts itself topmost every couple
    // of seconds; without our own re-assert this dialog sinks behind it and the
    // user never sees the countdown. Re-assert twice as often so it stays on top.
    private readonly DispatcherTimer _topmostTimer;

    private DateTime _deadline;
    private bool _answered;

    // True once the countdown was retargeted at the auto-keep moment (see
    // RetargetForAutoKeep): the number then counts down to "auto-keep".
    private bool _autoKeepPending;

    public CountdownWindow(int timeoutSeconds) : this(timeoutSeconds, null, null) { }

    public CountdownWindow(int timeoutSeconds, string? heading, string? detail)
    {
        InitializeComponent();

        if (!string.IsNullOrEmpty(heading)) HeadingText.Text = heading;
        if (!string.IsNullOrEmpty(detail)) DetailText.Text = detail;

        TimeoutSeconds = Math.Max(1, timeoutSeconds);
        _deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdateCountdown();
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        UpdateCountdown();
    }

    public int TimeoutSeconds { get; }

    /// <summary>True when the user pressed Keep, false when they pressed Revert, null on timeout.</summary>
    public bool? Decision { get; private set; }

    /// <summary>Raised when the user answers. Argument is true for keep.</summary>
    public event Action<bool>? Answered;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _timer.Start();
        _topmostTimer.Start();
        Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _topmostTimer.Stop();
        base.OnClosed(e);
    }

    /// <summary>Keeps this dialog above the compositor's fullscreen topmost window.</summary>
    private void ReassertTopmost()
    {
        if (_answered) return;
        try
        {
            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch
        {
            // Best effort: the next tick retries.
        }
    }

    /// <summary>
    /// Retargets the countdown at the auto-keep moment: the number now counts down to
    /// "auto-keep" instead of "auto-revert", and the caption says so. The user can still
    /// press Revert during the grace period.
    /// </summary>
    public void RetargetForAutoKeep(int graceSeconds)
    {
        if (_answered) return;
        _autoKeepPending = true;
        _deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, graceSeconds));
        CountdownCaption.Text = "秒后自动保留";
        UpdateCountdown();
    }

    /// <summary>
    /// Centres the dialog on whichever display is primary. For a general display change that is
    /// the right place: it is where the user is looking, and the countdown auto-reverts anyway,
    /// so it does not need to dodge the affected monitor.
    /// </summary>
    public void PlaceOnPrimary()
    {
        try
        {
            MonitorInfo? primary = DesktopSplitter.Interop.DisplayConfig.Enumerate()
                .FirstOrDefault(m => m.IsPrimary);
            if (primary is not null) PlaceOn(primary);
        }
        catch
        {
            // Leave it wherever WPF put it rather than failing the confirmation.
        }
    }

    /// <summary>
    /// Positions the window on <paramref name="monitor"/> using PHYSICAL pixels, sidestepping
    /// WPF's DIP coordinates entirely — Left/Top are unreliable across monitors with different
    /// scaling, and this dialog must land precisely on the non-target display.
    /// </summary>
    public void PlaceOn(MonitorInfo monitor)
    {
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
        {
            helper.EnsureHandle();
            hwnd = helper.Handle;
        }
        if (hwnd == IntPtr.Zero) return;

        // Ask the window how big it actually is in physical pixels, then centre that.
        if (!GetWindowRect(hwnd, out RECT current)) return;
        int width = Math.Max(1, current.right - current.left);
        int height = Math.Max(1, current.bottom - current.top);

        PixelRect target = CountdownPlacement.Center(monitor, width, height);

        SetWindowPos(hwnd, HWND_TOPMOST, target.X, target.Y, target.W, target.H,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void UpdateCountdown()
    {
        double remaining = (_deadline - DateTime.UtcNow).TotalSeconds;
        if (remaining < 0) remaining = 0;

        CountdownText.Text = Math.Ceiling(remaining).ToString("0");

        if (remaining <= 0 && !_answered)
        {
            _timer.Stop();
            _topmostTimer.Stop();
            CountdownCaption.Text = _autoKeepPending ? "正在保留…" : "正在撤销…";
            KeepButton.IsEnabled = false;
            RevertButton.IsEnabled = false;
        }
    }

    /// <summary>Closes the dialog because the helper finished on its own.</summary>
    public void CloseFromHelper()
    {
        _timer.Stop();
        _topmostTimer.Stop();
        _answered = true;
        try { Close(); } catch (InvalidOperationException) { /* already closing */ }
    }

    private void Keep_Click(object sender, RoutedEventArgs e) => Answer(true);

    private void Revert_Click(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool keep)
    {
        if (_answered) return;
        _answered = true;
        _timer.Stop();
        _topmostTimer.Stop();
        Decision = keep;

        KeepButton.IsEnabled = false;
        RevertButton.IsEnabled = false;
        CountdownCaption.Text = keep ? "正在保留…" : "正在撤销…";

        Answered?.Invoke(keep);
    }

    // ------------------------------------------------------------------ P/Invoke

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
