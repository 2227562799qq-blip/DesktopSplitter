using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopSplitter.Models;
using DesktopSplitter.Services;

namespace DesktopSplitter.ViewModels;

public sealed class ApplyConfirmer : IApplyConfirmer
{
    private readonly IHidingUi _ui;
    private readonly Action<string> _log;

    public ApplyConfirmer(IHidingUi ui, Action<string> log)
    {
        _ui = ui;
        _log = log;
    }

    public bool ConfirmApply(string summary, int timeoutSeconds, CancellationToken ct,
                             Process? compositor, MonitorInfo placeOn)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || compositor is null || compositor.HasExited) return false;

        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ICountdownHandle? dialog = null;
        WaitHandle? frames = null;
        try
        {
            dispatcher.Invoke(() =>
            {
                dialog = _ui.ShowApplyCountdown(summary, timeoutSeconds, placeOn,
                    () => decision.TrySetResult(true), () => decision.TrySetResult(false));
            });

            var clock = Stopwatch.StartNew();
            var policy = new OutputConfirmation(timeoutSeconds);
            string name = CompositorLauncher.ContentLiveEventName(compositor.Id);
            while (!decision.Task.IsCompleted && !ct.IsCancellationRequested && !compositor.HasExited)
            {
                frames ??= CompositorLauncher.OpenForWait(name);
                bool fresh = frames?.WaitOne(0) == true;
                switch (policy.Observe(clock.ElapsedMilliseconds, fresh))
                {
                    case ConfirmationStep.ShowGrace:
                        dispatcher.Invoke(() => dialog?.NoteAutoKeep(OutputConfirmation.GraceSeconds),
                            DispatcherPriority.Background, ct, TimeSpan.FromSeconds(2));
                        policy.StartGrace(clock.ElapsedMilliseconds);
                        _log("各分区已出画面，持续输出 5 秒后自动保留。期间可点“立即撤销”。");
                        break;
                    case ConfirmationStep.Keep:
                        // Input already queued on the UI thread gets to answer first.
                        long observedAt = clock.ElapsedMilliseconds;
                        dispatcher.Invoke(() =>
                        {
                            bool healthy = !ct.IsCancellationRequested && !compositor.HasExited &&
                                           clock.ElapsedMilliseconds - observedAt < 1000;
                            if (decision.TrySetResult(healthy) && healthy)
                                _log("已确认各分区持续输出，自动保留更改。");
                        }, DispatcherPriority.Background, ct, TimeSpan.FromSeconds(2));
                        break;
                    case ConfirmationStep.NoOutput:
                        if (decision.TrySetResult(false))
                            _log($"等待分屏画面超过 {timeoutSeconds} 秒，自动撤销。");
                        break;
                    case ConfirmationStep.OutputStopped:
                        if (decision.TrySetResult(false)) _log("等待保留期间画面停止更新，自动撤销。");
                        break;
                }
                if (!decision.Task.IsCompleted) ct.WaitHandle.WaitOne(75);
            }
            if (compositor.HasExited) _log("合成器已经退出，正在恢复之前的设置。");
            return !ct.IsCancellationRequested && !compositor.HasExited &&
                   decision.Task.IsCompletedSuccessfully && decision.Task.Result;
        }
        catch (OperationCanceledException) { return false; }
        catch (TimeoutException)
        {
            _log("确认窗口未及时响应，正在恢复之前的设置。");
            return false;
        }
        finally
        {
            frames?.Dispose();
            decision.TrySetResult(false);
            dispatcher.BeginInvoke(new Action(() => dialog?.CloseFromHelper()));
        }
    }
}
