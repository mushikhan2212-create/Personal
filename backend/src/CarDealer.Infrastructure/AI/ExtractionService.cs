using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CarDealer.Application.AI;
using CarDealer.Application.Abstractions;
using CarDealer.Domain.Entities;
using CarDealer.Domain.Enums;
using CarDealer.Infrastructure.Persistence;
using CarDealer.Integrations.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarDealer.Infrastructure.AI;

/// <summary>What reading a message produced.</summary>
/// <param name="Fields">What the message stated. Empty when it stated nothing, or on failure.</param>
/// <param name="Redacted">How many details were removed before sending, for telling the operator.</param>
/// <param name="Notice">Why there is nothing, when there is nothing.</param>
public sealed record ExtractionOutcome(
    IReadOnlyList<ExtractedField> Fields,
    int Redacted,
    string? Notice);

/// <summary>
/// Reads a customer's message into a requirement the operator can check and save.
/// </summary>
/// <remarks>
/// <para>
/// The order here is the feature. Redact, then send, then check the answer against the message,
/// then hand it to a person. Nothing in this class writes a requirement: what comes back is a
/// proposal on a screen, and the operator saves it through the ordinary endpoint after reading
/// it. That is deliberate - a model that mis-reads a budget should cost somebody ten seconds,
/// not put a wrong number in a customer's record where nobody will ever re-check it.
/// </para>
///
/// <para>
/// <b>The raw message is never persisted and never leaves.</b> It is redacted on the way in, the
/// redacted form goes to the provider, and the audit row records the shape of the call - how long
/// the message was and how much was taken out - rather than any of its content. That is the
/// product owner's O4 decision, made structural: see <see cref="ExtractionRequest"/>, which
/// cannot be built from a raw string at all.
/// </para>
/// </remarks>
public sealed class ExtractionService
{
    /// <summary>
    /// The longest message this will read.
    /// </summary>
    /// <remarks>
    /// Generous for an enquiry and far short of a pasted conversation. The ceiling is about
    /// tokens rather than tidiness: on a rate-limited account one oversized paste spends the
    /// whole minute's allowance and refuses the next three attempts, which reads as the feature
    /// being broken.
    /// </remarks>
    public const int MaxMessageCharacters = 4_000;

    private readonly CarDealerDbContext _db;
    private readonly IAIProvider _ai;
    private readonly ITenantContext _tenant;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<ExtractionService> _logger;

    public ExtractionService(
        CarDealerDbContext db,
        IAIProvider ai,
        ITenantContext tenant,
        IDateTimeProvider clock,
        ILogger<ExtractionService> logger)
    {
        _db = db;
        _ai = ai;
        _tenant = tenant;
        _clock = clock;
        _logger = logger;
    }

    public bool ProviderConfigured => _ai.IsConfigured;

    /// <summary>
    /// Reads one message.
    /// </summary>
    /// <param name="message">What the customer wrote, exactly as pasted.</param>
    /// <param name="customer">
    /// Whose message it is. Used only to redact their own name, which no pattern can find and a
    /// record can: the operator opened this customer to paste this in, so the name most likely
    /// to appear is already known.
    /// </param>
    public async Task<ExtractionOutcome> ReadAsync(
        string? message, Customer? customer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new ExtractionOutcome([], 0, "There was no message to read.");
        }

        if (message.Length > MaxMessageCharacters)
        {
            return new ExtractionOutcome(
                [],
                0,
                $"That message is {message.Length:N0} characters and the limit is "
                + $"{MaxMessageCharacters:N0}. Paste the part that describes the car.");
        }

        var redacted = Redaction.Apply(message, Names(customer));
        var request = ExtractionRequest.From(redacted);

        if (!_ai.IsConfigured)
        {
            return new ExtractionOutcome(
                [],
                redacted.Removed,
                "No AI provider is configured, so the requirement has to be typed in.");
        }

        var audit = new AIRequest
        {
            TenantId = _tenant.TenantId,
            Provider = _ai.Name,
            Model = _ai.Model ?? "unknown",
            Operation = "extract",

            // The redacted text, hashed. Enough to tell a repeated paste from a new one without
            // keeping a word of what the customer wrote.
            InputHash = Fingerprint(request.Text),

            // The shape of the call, never its content. A message is the one input this system
            // handles that is personal data end to end, so the column that safely held a whole
            // RequirementBrief holds only measurements here.
            InputMetadataJson = JsonSerializer.Serialize(new
            {
                characters = message.Length,
                redactedItems = redacted.Removed,
                prompt = ExtractionPrompt.Version,
            }),
            Status = AIRequestStatus.Pending,
        };

        _db.AIRequests.Add(audit);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        AIExtractionResult result;

        try
        {
            result = await _ai.ExtractAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad, as in RecommendationService: every transport failure has the
            // same answer here, which is to tell the operator to type it themselves.
            _logger.LogWarning(ex, "Extraction call to {Provider} threw.", _ai.Name);
            result = AIExtractionResult.Failed(ex.Message, _ai.Name, _ai.Model);
        }

        audit.CompletedAtUtc = _clock.UtcNow;
        audit.Cost = result.Usage?.CostUsd;
        audit.TokenUsageJson = result.Usage is null ? null : JsonSerializer.Serialize(result.Usage);

        if (!result.Succeeded)
        {
            audit.Status = AIRequestStatus.Failed;
            audit.FailureReason = Trim(result.Failure);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return new ExtractionOutcome(
                [], redacted.Removed, $"The message could not be read: {result.Failure}");
        }

        var rejection = ExtractionGuards.Reject(request, result.Fields!);

        if (rejection is not null)
        {
            // Recorded rather than discarded, exactly as a rejected ranking is. How often a
            // provider's answers get thrown away is the number that decides whether to keep
            // paying for it, and this operation is the one where a bad answer costs most.
            audit.Status = AIRequestStatus.Rejected;
            audit.FailureReason = Trim(rejection);

            // The answer itself, kept precisely because it was refused. A rejection says which
            // rule broke; only the response says what the model actually produced, and without
            // it a report of "maxPrice came back as }, {" cannot be investigated at all - the
            // one case where the evidence is discarded is the one where it is needed.
            //
            // Safe to store for the same reason the accepted answer is: the model saw only
            // redacted text, so nothing it echoes can carry an identifier.
            audit.OutputMetadataJson = Trim(JsonSerializer.Serialize(result.Fields));

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogWarning(
                "Extraction from {Provider} rejected by a guard: {Reason}", _ai.Name, rejection);

            return new ExtractionOutcome(
                [], redacted.Removed, $"The reading was not used: {rejection}");
        }

        audit.Status = AIRequestStatus.Succeeded;

        // The fields only. They are the customer's stated requirements rather than their words,
        // which is the same distinction that lets a RequirementBrief be stored in full.
        audit.OutputMetadataJson = JsonSerializer.Serialize(result.Fields);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ExtractionOutcome(result.Fields!, redacted.Removed, null);
    }

    /// <summary>The customer's own name, in the parts a message might use.</summary>
    private static IEnumerable<string> Names(Customer? customer)
    {
        if (customer is null)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(customer.FirstName)) yield return customer.FirstName;
        if (!string.IsNullOrWhiteSpace(customer.LastName)) yield return customer.LastName;
    }

    private static string Fingerprint(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            ExtractionPrompt.Version + "|" + text)));

    private static string? Trim(string? text)
        => text is null ? null : text.Length <= 1000 ? text : text[..1000];
}
