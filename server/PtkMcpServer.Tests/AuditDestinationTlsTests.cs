using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Tests;

public sealed class AuditDestinationTlsTests
{
    [Fact]
    public void Pin_is_normalized_without_changing_any_trust_store()
    {
        var colonSeparated = string.Join(
            ':',
            Enumerable.Repeat("ab", AuditDestinationTls.Sha256HexLength / 2));

        Assert.True(AuditDestinationTls.TryNormalizePin(colonSeparated, out var normalized));
        Assert.Equal(
            string.Concat(Enumerable.Repeat(
                "AB",
                AuditDestinationTls.Sha256HexLength / 2)),
            normalized);
        Assert.False(AuditDestinationTls.TryNormalizePin("not-a-sha256", out _));
    }

    [Fact]
    public void Exact_pin_accepts_chain_error_but_never_name_mismatch_or_wrong_leaf()
    {
        using var expected = CreateCertificate("localhost");
        using var other = CreateCertificate("localhost");
        var request = new HttpRequestMessage(HttpMethod.Options, "https://localhost/");
        var pin = Convert.ToHexString(SHA256.HashData(expected.RawData));
        AuditDestinationTls.ApplyPin(request, pin);

        Assert.True(AuditDestinationTls.ValidateServerCertificate(
            request,
            expected,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(AuditDestinationTls.ValidateServerCertificate(
            request,
            other,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(AuditDestinationTls.ValidateServerCertificate(
            request,
            expected,
            null,
            SslPolicyErrors.RemoteCertificateNameMismatch));

        using var expired = CreateCertificate(
            "localhost",
            DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow.AddHours(-1));
        using var expiredRequest = new HttpRequestMessage(
            HttpMethod.Options,
            "https://localhost/");
        AuditDestinationTls.ApplyPin(
            expiredRequest,
            Convert.ToHexString(SHA256.HashData(expired.RawData)));
        Assert.False(AuditDestinationTls.ValidateServerCertificate(
            expiredRequest,
            expired,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Unpinned_destination_requires_normal_platform_validation()
    {
        using var certificate = CreateCertificate("localhost");
        using var request = new HttpRequestMessage(HttpMethod.Options, "https://localhost/");

        Assert.True(AuditDestinationTls.ValidateServerCertificate(
            request,
            certificate,
            null,
            SslPolicyErrors.None));
        Assert.False(AuditDestinationTls.ValidateServerCertificate(
            request,
            certificate,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors));
    }

    private static X509Certificate2 CreateCertificate(
        string dnsName,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={dnsName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddHours(1));
    }
}
