using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExplicitTagger;

public class ExplicitLibraryTask : IScheduledTask
{
    private readonly ExplicitEngine _engine;
    private readonly ILogger<ExplicitLibraryTask> _logger;

    public ExplicitLibraryTask(ExplicitEngine engine, ILogger<ExplicitLibraryTask> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public string Name => "ExplicitFin";

    public string Key => "ExplicitFinLibrary";

    public string Description =>
        "Adds Jellyfin explicit tags and title marks from your tags or Deezer. Repairs playlists when titles change.";

    public string Category => "Library";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await _engine.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExplicitFin failed");
            throw;
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }
}
