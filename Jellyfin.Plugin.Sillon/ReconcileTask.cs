using Jellyfin.Plugin.Sillon.Requests;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Sillon;

/// <summary>Compares the live requests with Lidarr every five minutes, in case a webhook was lost; also runnable from the dashboard.</summary>
public sealed class ReconcileTask : IScheduledTask
{
    private readonly RequestService _requests;

    /// <summary>Initializes a new instance of the <see cref="ReconcileTask"/> class.</summary>
    /// <param name="requests">The request service.</param>
    public ReconcileTask(RequestService requests)
    {
        _requests = requests;
    }

    /// <inheritdoc />
    public string Name => "Rapprocher la liste d'envies avec Lidarr";

    /// <inheritdoc />
    public string Key => "SillonReconcile";

    /// <inheritdoc />
    public string Description => "Vérifie les albums de la liste d'envies auprès de Lidarr et rattrape ceux qui ne sont pas encore dans la bibliothèque de leur demandeur.";

    /// <inheritdoc />
    public string Category => "Sillon";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _requests.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(5).Ticks
        };
    }
}
