using System.Diagnostics;
using System.Windows.Threading;

namespace AVWorkstationToolkit.App.Services;

internal static class DispatcherResponsivenessProbe
{
    public static Task<TimeSpan> MeasureAsync(
        Dispatcher dispatcher,
        DispatcherPriority priority = DispatcherPriority.Input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _ = dispatcher.BeginInvoke(priority, () =>
        {
            stopwatch.Stop();
            registration.Dispose();
            completion.TrySetResult(stopwatch.Elapsed);
        });
        return completion.Task;
    }
}
