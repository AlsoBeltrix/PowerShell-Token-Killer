using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PtkMcpServer.Audit.Export;

/// <summary>
/// Destination TLS policy. An explicit SHA-256 certificate pin is scoped to
/// one HTTP request and never changes the machine or user trust store.
/// </summary>
internal static class AuditDestinationTls
{
    internal const int Sha256HexLength = 64;

    private static readonly HttpRequestOptionsKey<string> CertificatePinOption =
        new("ptk.destination.server-certificate-sha256");

    internal static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateServerCertificate,
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    internal static void ApplyPin(HttpRequestMessage request, string? certificateSha256)
    {
        if (!string.IsNullOrEmpty(certificateSha256))
            request.Options.Set(CertificatePinOption, certificateSha256);
    }

    internal static bool TryNormalizePin(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return true;

        var candidate = value.Trim().Replace(":", string.Empty, StringComparison.Ordinal);
        if (candidate.Length != Sha256HexLength ||
            !candidate.All(static character => Uri.IsHexDigit(character)))
        {
            return false;
        }

        normalized = candidate.ToUpperInvariant();
        return true;
    }

    internal static bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (!request.Options.TryGetValue(CertificatePinOption, out var expectedPin))
            return errors == SslPolicyErrors.None;

        if (certificate is null ||
            (errors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                       SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }
        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() ||
            now > certificate.NotAfter.ToUniversalTime())
        {
            return false;
        }

        var actualPin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expectedPin),
            Convert.FromHexString(actualPin));
    }
}
