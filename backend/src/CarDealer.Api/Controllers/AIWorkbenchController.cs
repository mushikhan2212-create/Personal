using System.Diagnostics;
using Asp.Versioning;
using CarDealer.Api.Authorization;
using CarDealer.Application.AI;
using CarDealer.Domain.Entities;
using CarDealer.Integrations.AI;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CarDealer.Api.Controllers;

/// <summary>
/// Tries one message against one model, and shows everything that happened.
/// </summary>
/// <remarks>
/// <para>
/// Tuning a prompt needs a loop tighter than "edit configuration, restart, open a customer,
/// paste, read a one-line notice". This is that loop: a message and a model id in, and the whole
/// of what came back out - what was actually sent after redaction, what the model answered before
/// any guard touched it, which guard refused it if one did, and what it cost.
/// </para>
///
/// <para>
/// <b>Development only.</b> It takes a model id from the caller, which in a deployed system would
/// let anybody with the permission spend the account's money on a model nobody chose. Outside
/// Development it answers 404 rather than 403, because the honest answer to "does this endpoint
/// exist in production" is no.
/// </para>
///
/// <para>
/// It writes nothing. No requirement, no audit row, no customer. That is the difference between
/// this and the real endpoint, and it is why the model id is safe to vary here - nothing
/// downstream can inherit a choice made for one experiment.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/ai")]
public sealed class AIWorkbenchController : ControllerBase
{
    private readonly AIOptions _options;
    private readonly IHttpClientFactory _factory;
    private readonly IHostEnvironment _environment;

    public AIWorkbenchController(
        IOptions<AIOptions> options, IHttpClientFactory factory, IHostEnvironment environment)
    {
        _options = options.Value;
        _factory = factory;
        _environment = environment;
    }

    /// <summary>Reads a message with a model of the caller's choosing.</summary>
    [HttpPost("read-message")]
    [HasPermission(Permissions.AiRecommend)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReadMessage(
        [FromBody] WorkbenchRequest request, CancellationToken ct = default)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "Give it a message to read." });
        }

        // The caller's model over the configured one, and nothing else overridable. The key,
        // the endpoint and the ceilings stay as configured, so an experiment cannot quietly
        // point the account's credential somewhere it was never meant to go.
        var options = new AIOptions
        {
            Provider = _options.Provider,
            Model = AIOptions.Clean(request.Model) is { Length: > 0 } named
                ? named
                : _options.Model,
            ApiKey = _options.ApiKey,
            BaseUrl = _options.BaseUrl,
            TimeoutSeconds = _options.TimeoutSeconds,
            MaxTokens = _options.MaxTokens,
            MaxCandidates = _options.MaxCandidates,
        };

        if (!options.IsConfigured)
        {
            return Ok(new { error = "No AI provider is configured." });
        }

        // The names a customer record would have contributed, supplied directly so a message can
        // be tried without inventing a customer to hang it on.
        var redacted = Redaction.Apply(request.Message, request.Names);
        var extraction = ExtractionRequest.From(redacted);

        var provider = AIProviderFactory.Create(options, _factory);
        var stopwatch = Stopwatch.StartNew();

        var result = await provider.ExtractAsync(extraction, ct).ConfigureAwait(false);

        stopwatch.Stop();

        return Ok(new
        {
            options.Model,
            prompt = ExtractionPrompt.Version,
            elapsedMs = stopwatch.ElapsedMilliseconds,

            // Exactly the bytes the provider received. The point of showing it is that redaction
            // is otherwise invisible: a count of what was removed is a claim, and this is the
            // evidence for it.
            redactedItems = redacted.Removed,
            sentToProvider = extraction.Text,

            result.Usage,

            // Why there is no answer at all - a rate limit, a rejected key, unparseable JSON.
            failure = result.Failure,

            // Why an answer was not believed. Null when the fields below would be used as they
            // stand, which is the state you are tuning towards.
            rejection = result.Succeeded
                ? ExtractionGuards.Reject(extraction, result.Fields!)
                : null,

            // Before the guards, always. Seeing what a model produced is the whole purpose, and
            // a workbench that hid the refused answer would hide the thing being diagnosed.
            fields = result.Fields,
        });
    }
}

/// <summary>One message to try, and what to try it with.</summary>
public sealed record WorkbenchRequest
{
    public string? Message { get; init; }

    /// <summary>A model id to use instead of the configured one. Optional.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Names to redact, standing in for a customer record.
    /// </summary>
    /// <remarks>
    /// The real endpoint takes these from the customer whose message it is. Here they are given
    /// directly, because a message worth testing is rarely attached to a customer worth creating.
    /// </remarks>
    public string[]? Names { get; init; }
}
