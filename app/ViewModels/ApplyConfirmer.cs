using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DesktopSplitter.Services;

namespace DesktopSplitter.ViewModels;

/// <summary>
/// Bridges <see cref="ApplyService"/> (background thread) to the "Keep these display settings?"
/// countdown on the UI thread.
///
/// The window's own timer only animates the number — THIS wait is the authority, and it defaults
/// to revert. That ordering matters: if the change left the user unable to see or click the
/// dialog, doing nothing must undo it.
///
/// Auto-keep: while the countdown runs, a background wait watches for the compositor's
/// content-live signal — real captured frames presented on every segment, which is what proves
/// the user can actually see the split (a first Present alone does not: the compositor clears
/// to black and presents unconditionally, so a first frame can be all black). Only that signal,
/// plus a short grace period in which the user can still veto with "Revert", answers for the
/// user. A compositor that never proves visible output never auto-keeps — the countdown lapses
/// and the change reverts exactly as before. Auto-keep never extends the countdown: content
/// that arrives too late to leave a full grace period inside the budget never auto-keeps.
/// </summary>
public sealed class ApplyConfirmer : IApplyConfirmer
{
    /// <summary>
    /// Seconds of proven visible output the user gets to veto an auto-keep.
    /// The grace period always fits inside the confirm countdown (see above).
    /// </summary>
    public const int AutoConfirmGraceSeconds = 5;

    private readonly IHidingUi _ui;
    private readonly Action<string> _log;

    public ApplyConfirmer(IHidingUi ui, Action<string> log)
    {
        _ui = ui;
        _log = log;
    }

    public bool ConfirmApply(string summary, int timeoutSeconds, CancellationToken ct, Process? compositor = null)
    {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return true;   // headless: nothing to confirm against

        using var answered = new ManualResetEventSlim(false);
        int keep = 0;
        ICountdownHandle? handle = null;
        DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        dispatcher.Invoke(() =>
        {
            handle = _ui.ShowApplyCountdown(
                summary, timeoutSeconds,
                onKeep: () => { Interlocked.Exchange(ref keep, 1); answered.Set(); },
                onRevert: () => { Interlocked.Exchange(ref keep, 0); answered.Set(); });
        });

        // Auto-keep runs on a background task so the user's answer and the
        // content signal race each other; whichever answers first wins. The
        // task is cancelled as soon as this method returns either way.
        using var autoCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (compositor is not null && !compositor.HasExited)
        {
            ICountdownHandle? shown = handle;
            Action autoKeep = () => { Interlocked.Exchange(ref keep, 1); answered.Set(); };
            _ = Task.Run(() => WaitForAutoKeep(compositor, dispatcher, shown, answered, autoKeep,
                                               deadlineUtc, autoCts.Token),
                         autoCts.Token);
        }

        bool gotAnswer;
        try
        {
            gotAnswer = answered.Wait(TimeSpan.FromSeconds(timeoutSeconds + 1), ct);
        }
        catch (OperationCanceledException)
        {
            autoCts.Cancel();
            dispatcher.Invoke(() => handle?.CloseFromHelper());
            return false;
        }
        finally
        {
            autoCts.Cancel();
        }

        dispatcher.Invoke(() => handle?.CloseFromHelper());

        if (!gotAnswer)
        {
            _log($"{timeoutSeconds} 秒内未收到答复——自动撤销此次更改。");
            return false;
        }
        return Volatile.Read(ref keep) == 1;
    }

    private void WaitForAutoKeep(Process compositor,
        System.Windows.Threading.Dispatcher dispatcher, ICountdownHandle? handle,
        ManualResetEventSlim answered, Action autoKeep,
        DateTime deadlineUtc, CancellationToken ct)
    {
        try
        {
            TimeSpan budget = deadlineUtc - DateTime.UtcNow;
            if (budget <= TimeSpan.Zero) return;

            CompositorLauncher.ContentLiveWait wait =
                CompositorLauncher.WaitForContentLive(compositor, budget, ct, _log);
            if (wait != CompositorLauncher.ContentLiveWait.Live) return;

            // Content is proven visible. Grace period: the user can still see
            // the split and veto it; the window retargets its countdown to the
            // auto-keep moment so the number on screen stays honest.
            DateTime graceEnd = DateTime.UtcNow.AddSeconds(AutoConfirmGraceSeconds);
            if (graceEnd > deadlineUtc) graceEnd = deadlineUtc;
            TimeSpan grace = graceEnd - DateTime.UtcNow;
            if (grace < TimeSpan.FromSeconds(1)) return;   // too late to be fair: leave it to the user

            int graceSeconds = (int)Math.Ceiling(grace.TotalSeconds);
            _log($"合成器已确认出画面，{graceSeconds} 秒后自动保留（要点撤销请点“立即撤销”）。");
            try
            {
                dispatcher.Invoke(() => handle?.NoteAutoKeep(graceSeconds));
            }
            catch (Exception)
            {
                // Window already closing: the wait below still resolves.
            }

            while (DateTime.UtcNow < graceEnd)
            {
                if (answered.IsSet) return;   // user answered first
                if (ct.IsCancellationRequested) return;
                Thread.Sleep(100);
            }
            if (answered.IsSet) return;
            if (DateTime.UtcNow >= deadlineUtc) return;   // budget gone: let the lapse revert

            _log("合成器已出画面，自动保留更改。");
            autoKeep();
        }
        catch (OperationCanceledException)
        {
            // Confirmer finished first (user answered, timed out, or cancelled).
        }
        catch (Exception ex)
        {
            _log($"等待合成器确认画面时出错（已忽略，不自动保留）：{ex.Message}");
        }
    }
}
