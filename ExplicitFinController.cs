using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ExplicitTagger;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("ExplicitFin")]
public sealed class ExplicitFinController : ControllerBase
{
    private readonly ExplicitEngine _engine;
    private readonly ITaskManager _tasks;

    public ExplicitFinController(ExplicitEngine engine, ITaskManager tasks)
    {
        _engine = engine;
        _tasks = tasks;
    }

    /// <summary>Queue a force scan that overwrites every track from catalogs.</summary>
    [HttpPost("ScanAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ScanAllResponse> ScanAll()
    {
        _engine.RequestForce();
        _tasks.CancelIfRunningAndQueue<ExplicitLibraryTask>();
        return Ok(new ScanAllResponse { Queued = true });
    }

    /// <summary>Strip an explicit symbol from all audio track titles.</summary>
    [HttpPost("RemoveSymbol")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RemoveSymbolResponse>> RemoveSymbol(
        [FromBody] RemoveSymbolRequest? request,
        CancellationToken cancellationToken)
    {
        var updated = await _engine.RemoveSymbolAsync(request?.Symbol, cancellationToken)
            .ConfigureAwait(false);
        return Ok(new RemoveSymbolResponse { Updated = updated });
    }
}

public sealed class ScanAllResponse
{
    public bool Queued { get; set; }
}

public sealed class RemoveSymbolRequest
{
    [MaxLength(64)]
    public string? Symbol { get; set; }
}

public sealed class RemoveSymbolResponse
{
    public int Updated { get; set; }
}
