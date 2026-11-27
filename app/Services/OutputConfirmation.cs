namespace DesktopSplitter.Services;

internal enum ConfirmationStep { Wait, ShowGrace, Keep, NoOutput, OutputStopped }

internal sealed class OutputConfirmation
{
    internal const int GraceSeconds = 5;
    private const int MaxFrameGapMs = 2500;
    private readonly long _startupMs;
    private long? _graceStart;
    private long _lastFrame;

    internal OutputConfirmation(int startupSeconds) => _startupMs = startupSeconds * 1000L;

    internal void StartGrace(long now) => _graceStart = _lastFrame = now;

    internal ConfirmationStep Observe(long now, bool newFrame)
    {
        if (_graceStart is not long start)
        {
            if (now >= _startupMs) return ConfirmationStep.NoOutput;
            return newFrame ? ConfirmationStep.ShowGrace : ConfirmationStep.Wait;
        }
        if (now - _lastFrame > MaxFrameGapMs) return ConfirmationStep.OutputStopped;
        if (newFrame)
        {
            _lastFrame = now;
            if (now - start >= GraceSeconds * 1000L) return ConfirmationStep.Keep;
        }
        return ConfirmationStep.Wait;
    }
}
