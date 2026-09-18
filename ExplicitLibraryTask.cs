using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExplicitTagShelf;

public class ExplicitLibraryTask : IScheduledTask
{
    private readonly ExplicitEngine _engine;
    private readonly ILogger<ExplicitLibraryTask> _logger;

    public ExplicitLibraryTask(ExplicitEngine engine, ILogger<ExplicitLibraryTask> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public string Name => "- ExplicitTagShelf: Mark Your Songs";

    public string Key => "ExplicitTagShelfLibrary";

    public string Description =>
        "Scheduled scans only mark tracks not decided yet. Force-refresh from plugin settings overwrites from catalogs.";

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
            _logger.LogError(ex, "ExplicitTagShelf failed");
            throw;
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        ];
    }
}
