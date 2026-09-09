using CarDealer.Application.Messaging;

namespace CarDealer.Infrastructure.Messaging;

/// <summary>
/// Prepares a WhatsApp message as a click-to-chat link for a person to send.
/// </summary>
/// <remarks>
/// WhatsApp's own sanctioned mechanism: <c>https://wa.me/&lt;number&gt;?text=&lt;message&gt;</c>
/// opens a chat on whatever device follows it, with the message typed in and waiting. Nothing
/// is sent until a human presses send.
///
/// <para>
/// This is the interim provider while Meta Business verification is pending. It is deliberately
/// <b>not</b> an automation of a personal WhatsApp account: the libraries that do that
/// reverse-engineer WhatsApp Web, get numbers banned, and are excluded by master prompt section
/// 18's "no bypassing access controls". Section 10 requires the official APIs, and this is the
/// part of official that needs no approval - a documented URL scheme, used as documented.
/// </para>
///
/// <para>
/// <see cref="MessagingCapabilities.CanReceive"/> is false and that is the honest limit of the
/// approach rather than a gap to be filled later by this class. The reply lands on the
/// salesperson's own phone, so the platform never sees it: no unified inbox, no conversation
/// history, and open item O7's 24-hour window cannot even be evaluated because it opens on an
/// inbound message. Those arrive with the Business API provider, alongside this one rather than
/// instead of it - a salesperson with WhatsApp open on their desk may well still prefer to send
/// by hand.
/// </para>
/// </remarks>
public sealed class WhatsAppLinkProvider : IMessagingProvider
{
    public string Channel => "whatsapp";

    public MessagingCapabilities Capabilities { get; } = new(
        CanSendDirectly: false,
        CanReceive: false);

    /// <summary>
    /// A practical ceiling on the pre-filled text.
    /// </summary>
    /// <remarks>
    /// Browsers and mobile deep-link handlers both truncate long URLs, and a truncated message
    /// arrives mangled rather than short. The template stays well inside this; the cap is here
    /// so that a customer note pasted into a message cannot silently corrupt it.
    /// </remarks>
    private const int MaxBodyLength = 1_500;

    public Task<MessageDispatch> DispatchAsync(MessageDraft draft, CancellationToken ct = default)
    {
        var resolved = PhoneNumber.Resolve(draft.ToPhone, draft.ToCountryCode);

        if (!resolved.IsResolved)
        {
            return Task.FromResult(MessageDispatch.Failed(resolved.Reason!));
        }

        if (string.IsNullOrWhiteSpace(draft.Body))
        {
            return Task.FromResult(MessageDispatch.Failed("The message is empty."));
        }

        var body = draft.Body.Length > MaxBodyLength
            ? draft.Body[..MaxBodyLength]
            : draft.Body;

        // Uri.EscapeDataString rather than a query-string builder: wa.me wants the text
        // percent-encoded in full, and the newlines the template uses have to survive as %0A
        // rather than being turned into plus signs by form encoding.
        var url = $"https://wa.me/{resolved.Digits}?text={Uri.EscapeDataString(body)}";

        return Task.FromResult(MessageDispatch.Handoff(url, resolved.Digits!));
    }
}
