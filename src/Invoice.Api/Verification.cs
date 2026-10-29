using Mitzh.Invoice.Core;

namespace Mitzh.Invoice.Api;

public record VerificationResult(string Status, string ProviderReference, string Reason, DateTimeOffset CheckedAt);
// Provider implementations must be based on an authorized provider's actual contract.
// An offline parser cannot assert tax authenticity, cancellation, or red-invoice status.
public interface IInvoiceVerifier
{
    bool IsConfigured
    {
        get;
    }

    Task<VerificationResult> VerifyAsync(InvoiceModel invoice, CancellationToken cancellationToken);
}

public sealed class UnconfiguredVerifier : IInvoiceVerifier
{
    public bool IsConfigured => false;

    public Task<VerificationResult> VerifyAsync(InvoiceModel invoice, CancellationToken cancellationToken) => Task.FromResult(new VerificationResult("NotConfigured", null, "未配置授权验真服务。", DateTimeOffset.UtcNow));
}
