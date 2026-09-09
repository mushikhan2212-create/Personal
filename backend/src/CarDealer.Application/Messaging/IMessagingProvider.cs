namespace CarDealer.Application.Messaging;

/// <summary>
/// Gets a message to a customer, by whatever means the configured provider has.
/// </summary>
/// <remarks>
/// Master prompt section 10 requires the official WhatsApp Business and Meta APIs, and those
/// need Meta Business verification - weeks of calendar time that has nothing to do with code.
/// This exists so the screens, the templates and the customer wiring can be written once now
/// and keep working when that approval arrives.
///
/// <para>
/// The interface deliberately does <b>not</b> promise that calling it sends anything. A
/// provider may either send the message itself or hand back something a human must act on, and
/// <see cref="MessageDispatch"/> says which happened. Modelling only "send" would have forced
/// today's link-based provider to lie about what it did, and a screen built on that lie would
/// have to be rewritten rather than merely reconfigured.
/// </para>
///
/// <para>
/// Nothing here sends without a person: section 18 excludes autonomous customer messaging in
/// the first release, and a link a salesperson taps satisfies that by construction.
/// </para>
/// </remarks>
public interface IMessagingProvider
{
    /// <summary>Channel this provider reaches, for when a second one is added.</summary>
    string Channel { get; }

    /// <summary>What this provider can actually do, so screens can describe it honestly.</summary>
    MessagingCapabilities Capabilities { get; }

    /// <summary>
    /// Prepares - and, where the provider can, sends - one message.
    /// </summary>
    /// <remarks>
    /// Returns a failed <see cref="MessageDispatch"/> rather than throwing when the recipient
    /// cannot be reached. "This customer has no usable phone number" is an ordinary outcome a
    /// screen has to explain, not an exception.
    /// </remarks>
    Task<MessageDispatch> DispatchAsync(MessageDraft draft, CancellationToken ct = default);
}

/// <param name="CanSendDirectly">
/// True when the platform itself delivers the message. False for the link provider, where a
/// person taps and sends from their own device.
/// </param>
/// <param name="CanReceive">
/// True when replies come back into the platform. False until the Business API is in place -
/// which is why there is no unified inbox yet, and why open item O7's 24-hour window cannot be
/// evaluated: the window opens on an inbound message and there are none.
/// </param>
public sealed record MessagingCapabilities(bool CanSendDirectly, bool CanReceive);

/// <param name="ToPhone">The recipient's number as it is stored, in whatever shape that is.</param>
/// <param name="ToCountryCode">
/// The customer's ISO country, used to resolve a national number to an international one. Null
/// when unknown, which is a reason to refuse rather than to guess.
/// </param>
/// <param name="Body">The message. Already reviewed by a person before this is called.</param>
public sealed record MessageDraft(string? ToPhone, string? ToCountryCode, string Body);

public enum DispatchKind
{
    /// <summary>The message could not be prepared. <see cref="MessageDispatch.Reason"/> says why.</summary>
    Failed = 0,

    /// <summary>Prepared, and waiting for a person to send it from their own device.</summary>
    HandoffLink = 1,

    /// <summary>The platform delivered it.</summary>
    Sent = 2,
}

public sealed record MessageDispatch
{
    public required DispatchKind Kind { get; init; }

    /// <summary>Where to send the person. Set only for <see cref="DispatchKind.HandoffLink"/>.</summary>
    public string? HandoffUrl { get; init; }

    /// <summary>The provider's own id, for reconciling delivery later. Set only when sent.</summary>
    public string? ProviderMessageId { get; init; }

    /// <summary>The number the message was addressed to, normalised.</summary>
    public string? NormalizedPhone { get; init; }

    /// <summary>Why it failed, in words a salesperson can act on.</summary>
    public string? Reason { get; init; }

    public static MessageDispatch Failed(string reason) => new()
    {
        Kind = DispatchKind.Failed,
        Reason = reason,
    };

    public static MessageDispatch Handoff(string url, string normalizedPhone) => new()
    {
        Kind = DispatchKind.HandoffLink,
        HandoffUrl = url,
        NormalizedPhone = normalizedPhone,
    };
}
