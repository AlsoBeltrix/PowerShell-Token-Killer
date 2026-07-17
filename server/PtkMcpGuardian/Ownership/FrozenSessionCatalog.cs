using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using PtkSharedContracts;

namespace PtkMcpGuardian.Ownership;

/// <summary>
/// Guardian-lifetime view of the recovery templates frozen during startup.
/// It deliberately has no source, reload, or mutation surface.
/// </summary>
internal interface IFrozenSessionCatalog
{
    Sha256Digest CatalogDigest { get; }

    IReadOnlyList<RecoveryTemplate> Snapshot();

    bool TryGet(
        CanonicalAlias? name,
        [NotNullWhen(true)] out RecoveryTemplate? template);
}

/// <summary>
/// Owns an ordinally ordered, unique, bounded, deep-copied template set for
/// one guardian boot. Every returned template is another defensive copy.
/// </summary>
internal sealed class FrozenSessionCatalog : IFrozenSessionCatalog
{
    private static ReadOnlySpan<byte> CatalogDigestDomain =>
        "ptk.session-catalog/1\0"u8;

    private readonly RecoveryTemplate[] _templates;
    private readonly Dictionary<string, RecoveryTemplate> _templatesByName;

    internal FrozenSessionCatalog(IEnumerable<RecoveryTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var frozen = new List<RecoveryTemplate>();
        foreach (var template in templates)
        {
            if (template is null)
                throw new ArgumentException(
                    "Catalog templates cannot contain null.",
                    nameof(templates));
            if (frozen.Count == ContractLimits.MaximumTemplates)
                throw new ArgumentException(
                    $"Catalog cannot contain more than {ContractLimits.MaximumTemplates} templates.",
                    nameof(templates));

            frozen.Add(CloneTemplate(template));
        }

        frozen.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Name.Value, right.Name.Value));

        for (var index = 1; index < frozen.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(
                    frozen[index - 1].Name.Value,
                    frozen[index].Name.Value))
            {
                throw new ArgumentException(
                    "Catalog template names must be ordinally unique.",
                    nameof(templates));
            }
        }

        _templates = frozen.ToArray();
        _templatesByName = _templates.ToDictionary(
            template => template.Name.Value,
            StringComparer.Ordinal);
        CatalogDigest = ComputeCatalogDigest(_templates);
    }

    public Sha256Digest CatalogDigest { get; }

    public IReadOnlyList<RecoveryTemplate> Snapshot() =>
        Array.AsReadOnly(_templates.Select(CloneTemplate).ToArray());

    public bool TryGet(
        CanonicalAlias? name,
        [NotNullWhen(true)] out RecoveryTemplate? template)
    {
        if (name is null ||
            !_templatesByName.TryGetValue(name.Value, out var frozen))
        {
            template = null;
            return false;
        }

        template = CloneTemplate(frozen);
        return true;
    }

    private static RecoveryTemplate CloneTemplate(RecoveryTemplate template)
    {
        var bootstrap = template.GetBootstrapBytes();
        try
        {
            return new RecoveryTemplate(
                template.Name,
                template.Description,
                template.StartupTimeoutSeconds,
                template.DeclaredTarget,
                template.DeclaredIdentity,
                template.AllowColdBackground,
                template.TemplateDigest,
                template.BootstrapDigest,
                bootstrap);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bootstrap);
        }
    }

    private static Sha256Digest ComputeCatalogDigest(
        IEnumerable<RecoveryTemplate> templates)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(CatalogDigestDomain);

        Span<byte> lengthPrefix = stackalloc byte[sizeof(uint)];
        foreach (var template in templates)
        {
            var nameBytes = Encoding.UTF8.GetBytes(template.Name.Value);
            BinaryPrimitives.WriteUInt32BigEndian(
                lengthPrefix,
                checked((uint)nameBytes.Length));
            hash.AppendData(lengthPrefix);
            hash.AppendData(nameBytes);
            hash.AppendData(Convert.FromHexString(template.TemplateDigest.Value));
        }

        return new Sha256Digest(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
