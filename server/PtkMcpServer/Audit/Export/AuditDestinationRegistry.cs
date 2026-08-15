using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtkMcpServer.Audit.Export;

internal sealed record AuditDestinationDefinition(
    Guid DestinationId,
    AuditDestinationKind Kind,
    string OperatorLabel,
    Uri Endpoint,
    string Adapter,
    string CredentialReference,
    string Credential,
    long ConfigurationRevision,
    DateTimeOffset ActivatedUtc,
    bool Enabled,
    bool IncludeLegacyRecords = false,
    string? ServerCertificateSha256 = null)
{
    internal AuditExportSettings ToExportSettings(Uri? alertWebhook = null) =>
        new(Kind, Endpoint, Credential, alertWebhook, ServerCertificateSha256);

    internal string RedactedEndpoint =>
        $"{Endpoint.GetLeftPart(UriPartial.Authority)}/…";
}

internal sealed record AuditDestinationSetSnapshot(
    long Revision,
    IReadOnlyList<AuditDestinationDefinition> Destinations)
{
    internal static AuditDestinationSetSnapshot Empty { get; } = new(0, []);

    internal IReadOnlyList<Guid> EnabledDestinationIds => Destinations
        .Where(destination => destination.Enabled)
        .Select(destination => destination.DestinationId)
        .Order()
        .ToArray();
}

internal sealed record AuditDestinationDraft(
    AuditDestinationKind Kind,
    string OperatorLabel,
    Uri Endpoint,
    string Credential,
    string? ServerCertificateSha256 = null);

/// <summary>
/// Protected, versioned destination-set authority. Configuration mutations are
/// serialized and published by one atomic rename; an unsuccessful mutation
/// leaves both the durable and in-memory prior set unchanged. Credentials live
/// only in this owner-only file. Operator surfaces receive the opaque reference
/// and redacted endpoint, never the credential.
/// </summary>
internal sealed class AuditDestinationRegistry
{
    internal const string FileName = "destinations.json";
    private const int CurrentVersion = 2;
    private const int MaximumFileBytes = 256 * 1024;
    private const int MaximumDestinations = 16;
    private const int MaximumLabelLength = 128;
    private const int MaximumCredentialLength = 4096;

    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _path;
    private readonly Action? _beforePublishForTests;
    private AuditDestinationSetSnapshot _snapshot;

    private AuditDestinationRegistry(
        string root,
        AuditDestinationSetSnapshot snapshot,
        Action? beforePublishForTests = null)
    {
        _root = root;
        _path = Path.Combine(root, FileName);
        _snapshot = snapshot;
        _beforePublishForTests = beforePublishForTests;
    }

    internal static AuditDestinationRegistry Open(
        string auditRootDirectory,
        AuditExportSettings legacySettings,
        out string? failure,
        Action? beforePublishForTests = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditRootDirectory);
        ArgumentNullException.ThrowIfNull(legacySettings);
        failure = null;
        try
        {
            var path = Path.Combine(auditRootDirectory, FileName);
            if (File.Exists(path))
            {
                var bytes = SecureAuditStorage.ReadProtectedFile(
                    path,
                    MaximumFileBytes,
                    requireProtectedParent: false,
                    verifyWithoutMutation: true);
                return new AuditDestinationRegistry(
                    auditRootDirectory,
                    Parse(bytes),
                    beforePublishForTests);
            }

            var registry = new AuditDestinationRegistry(
                auditRootDirectory,
                AuditDestinationSetSnapshot.Empty,
                beforePublishForTests);
            if (!legacySettings.IsConfigured)
                return registry;

            var legacy = LegacyDefinition(legacySettings, auditRootDirectory);
            var migrated = new AuditDestinationSetSnapshot(1, [legacy]);
            if (!registry.TryPublish(migrated, out _))
            {
                failure = "export.destination_migration_unwritable";
                return registry;
            }
            registry._snapshot = migrated;
            return registry;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = "export.destination_configuration_unreadable";
            return new AuditDestinationRegistry(
                auditRootDirectory,
                AuditDestinationSetSnapshot.Empty,
                beforePublishForTests);
        }
    }

    internal AuditDestinationSetSnapshot Snapshot()
    {
        lock (_gate)
            return _snapshot;
    }

    internal IReadOnlyList<Guid> EnabledDestinationIds()
    {
        lock (_gate)
        {
            if (!TryRefreshLocked(out var failure))
                throw new IOException($"Destination configuration unavailable: {failure}.");
            return _snapshot.EnabledDestinationIds;
        }
    }

    internal bool TryRefresh(out string failure)
    {
        lock (_gate)
            return TryRefreshLocked(out failure);
    }

    internal bool TryAdd(
        AuditDestinationDraft draft,
        bool confirmedSensitiveDuplication,
        DateTimeOffset activatedUtc,
        out AuditDestinationDefinition? created,
        out string failure)
    {
        created = null;
        if (!TryNormalize(draft, out var normalized, out failure))
            return false;

        lock (_gate)
        {
            if (!TryRefreshLocked(out failure))
                return false;
            if (_snapshot.Destinations.Count >= MaximumDestinations)
            {
                failure = "destination_limit";
                return false;
            }
            if (_snapshot.Destinations.Count > 0 && !confirmedSensitiveDuplication)
            {
                failure = "sensitive_duplication_confirmation_required";
                return false;
            }
            if (_snapshot.Destinations.Any(destination =>
                    string.Equals(
                        destination.OperatorLabel,
                        normalized.OperatorLabel,
                        StringComparison.OrdinalIgnoreCase)))
            {
                failure = "label_exists";
                return false;
            }

            var revision = checked(_snapshot.Revision + 1);
            var destinationId = Guid.NewGuid();
            created = new AuditDestinationDefinition(
                destinationId,
                normalized.Kind,
                normalized.OperatorLabel,
                normalized.Endpoint,
                AuditExportSettings.KindText(normalized.Kind),
                CredentialReference(destinationId, revision),
                normalized.Credential,
                revision,
                activatedUtc.ToUniversalTime(),
                Enabled: true,
                ServerCertificateSha256: normalized.ServerCertificateSha256);
            var proposed = new AuditDestinationSetSnapshot(
                revision,
                [.. _snapshot.Destinations, created]);
            if (!TryPublish(proposed, out failure))
            {
                created = null;
                return false;
            }
            _snapshot = proposed;
            failure = string.Empty;
            return true;
        }
    }

    internal bool TryUpdate(
        Guid destinationId,
        AuditDestinationDraft draft,
        DateTimeOffset activatedUtc,
        out AuditDestinationDefinition? updated,
        out string failure)
    {
        updated = null;
        if (!TryNormalize(draft, out var normalized, out failure))
            return false;

        lock (_gate)
        {
            if (!TryRefreshLocked(out failure))
                return false;
            var index = IndexOf(destinationId);
            if (index < 0)
            {
                failure = "destination_not_found";
                return false;
            }
            if (_snapshot.Destinations.Where((_, candidate) => candidate != index).Any(destination =>
                    string.Equals(
                        destination.OperatorLabel,
                        normalized.OperatorLabel,
                        StringComparison.OrdinalIgnoreCase)))
            {
                failure = "label_exists";
                return false;
            }

            var prior = _snapshot.Destinations[index];
            var revision = checked(_snapshot.Revision + 1);
            updated = prior with
            {
                Kind = normalized.Kind,
                OperatorLabel = normalized.OperatorLabel,
                Endpoint = normalized.Endpoint,
                Adapter = AuditExportSettings.KindText(normalized.Kind),
                CredentialReference = string.IsNullOrEmpty(normalized.Credential)
                    ? prior.CredentialReference
                    : CredentialReference(destinationId, revision),
                Credential = string.IsNullOrEmpty(normalized.Credential)
                    ? prior.Credential
                    : normalized.Credential,
                ServerCertificateSha256 = normalized.ServerCertificateSha256,
                ConfigurationRevision = revision,
                ActivatedUtc = activatedUtc.ToUniversalTime(),
            };
            var definitions = _snapshot.Destinations.ToArray();
            definitions[index] = updated;
            var proposed = new AuditDestinationSetSnapshot(revision, definitions);
            if (!TryPublish(proposed, out failure))
            {
                updated = null;
                return false;
            }
            _snapshot = proposed;
            failure = string.Empty;
            return true;
        }
    }

    internal bool TrySetEnabled(
        Guid destinationId,
        bool enabled,
        bool hasPendingObligations,
        out string failure)
    {
        lock (_gate)
        {
            if (!TryRefreshLocked(out failure))
                return false;
            var index = IndexOf(destinationId);
            if (index < 0)
            {
                failure = "destination_not_found";
                return false;
            }
            if (!enabled && hasPendingObligations)
            {
                failure = "pending_obligations_require_abandonment";
                return false;
            }
            var prior = _snapshot.Destinations[index];
            if (prior.Enabled == enabled)
            {
                failure = string.Empty;
                return true;
            }
            var revision = checked(_snapshot.Revision + 1);
            var definitions = _snapshot.Destinations.ToArray();
            definitions[index] = prior with
            {
                Enabled = enabled,
                ConfigurationRevision = revision,
            };
            var proposed = new AuditDestinationSetSnapshot(revision, definitions);
            if (!TryPublish(proposed, out failure))
            {
                return false;
            }
            _snapshot = proposed;
            failure = string.Empty;
            return true;
        }
    }

    internal bool TryRemove(
        Guid destinationId,
        bool hasPendingObligations,
        out string failure)
    {
        lock (_gate)
        {
            if (!TryRefreshLocked(out failure))
                return false;
            var index = IndexOf(destinationId);
            if (index < 0)
            {
                failure = "destination_not_found";
                return false;
            }
            if (hasPendingObligations)
            {
                failure = "pending_obligations_require_abandonment";
                return false;
            }
            var revision = checked(_snapshot.Revision + 1);
            var definitions = _snapshot.Destinations
                .Where((_, candidate) => candidate != index)
                .ToArray();
            var proposed = new AuditDestinationSetSnapshot(revision, definitions);
            if (!TryPublish(proposed, out failure))
            {
                return false;
            }
            _snapshot = proposed;
            failure = string.Empty;
            return true;
        }
    }

    private int IndexOf(Guid destinationId)
    {
        for (var index = 0; index < _snapshot.Destinations.Count; index++)
        {
            if (_snapshot.Destinations[index].DestinationId == destinationId)
                return index;
        }
        return -1;
    }

    private bool TryPublish(
        AuditDestinationSetSnapshot snapshot,
        out string failure)
    {
        var temporaryPath = Path.Combine(
            _root,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = Serialize(snapshot);
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            _beforePublishForTests?.Invoke();
            if (!TryReadDurableRevision(out var durableRevision))
            {
                SecureAuditStorage.TryDelete(temporaryPath);
                failure = "configuration_unreadable";
                return false;
            }
            if (durableRevision != _snapshot.Revision)
            {
                SecureAuditStorage.TryDelete(temporaryPath);
                failure = "configuration_conflict";
                return false;
            }
            if (File.Exists(_path))
            {
                SecureAuditStorage.ReplaceAtomically(
                    temporaryPath,
                    _path,
                    _root);
            }
            else
            {
                SecureAuditStorage.PublishAtomically(
                    temporaryPath,
                    _path,
                    _root);
            }
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            SecureAuditStorage.TryDelete(temporaryPath);
            failure = "configuration_unwritable";
            return false;
        }
    }

    private bool TryRefreshLocked(out string failure)
    {
        try
        {
            if (!File.Exists(_path))
            {
                if (_snapshot.Revision == 0)
                {
                    failure = string.Empty;
                    return true;
                }

                failure = "configuration_unreadable";
                return false;
            }

            var bytes = SecureAuditStorage.ReadProtectedFile(
                _path,
                MaximumFileBytes,
                requireProtectedParent: false,
                verifyWithoutMutation: true);
            var durable = Parse(bytes);
            if (durable.Revision < _snapshot.Revision)
            {
                failure = "configuration_regressed";
                return false;
            }

            _snapshot = durable;
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = "configuration_unreadable";
            return false;
        }
    }

    private bool TryReadDurableRevision(out long revision)
    {
        try
        {
            if (!File.Exists(_path))
            {
                revision = 0;
                return true;
            }

            var bytes = SecureAuditStorage.ReadProtectedFile(
                _path,
                MaximumFileBytes,
                requireProtectedParent: false,
                verifyWithoutMutation: true);
            revision = Parse(bytes).Revision;
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            revision = -1;
            return false;
        }
    }

    private static byte[] Serialize(AuditDestinationSetSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(new DestinationSetFile
        {
            Version = CurrentVersion,
            Revision = snapshot.Revision,
            Destinations = snapshot.Destinations.Select(destination => new DestinationFile
            {
                DestinationId = destination.DestinationId.ToString("D"),
                Kind = AuditExportSettings.KindText(destination.Kind),
                OperatorLabel = destination.OperatorLabel,
                Endpoint = destination.Endpoint.AbsoluteUri,
                Adapter = destination.Adapter,
                CredentialReference = destination.CredentialReference,
                Credential = destination.Credential,
                ConfigurationRevision = destination.ConfigurationRevision,
                ActivatedUtc = destination.ActivatedUtc,
                Enabled = destination.Enabled,
                IncludeLegacyRecords = destination.IncludeLegacyRecords,
                ServerCertificateSha256 = destination.ServerCertificateSha256,
            }).ToArray(),
        });

    private static AuditDestinationSetSnapshot Parse(ReadOnlySpan<byte> bytes)
    {
        var file = JsonSerializer.Deserialize<DestinationSetFile>(bytes) ??
            throw new InvalidDataException("Destination set is empty.");
        if (file.Version is not (1 or CurrentVersion) || file.Revision < 0 ||
            file.Destinations is null || file.Destinations.Length > MaximumDestinations)
        {
            throw new InvalidDataException("Destination set version or bounds are invalid.");
        }
        var ids = new HashSet<Guid>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<AuditDestinationDefinition>(file.Destinations.Length);
        foreach (var entry in file.Destinations)
        {
            if (entry is null ||
                !Guid.TryParseExact(entry.DestinationId, "D", out var destinationId) ||
                !ids.Add(destinationId) ||
                string.IsNullOrWhiteSpace(entry.OperatorLabel) ||
                entry.OperatorLabel.Length > MaximumLabelLength ||
                !labels.Add(entry.OperatorLabel) ||
                string.IsNullOrWhiteSpace(entry.CredentialReference) ||
                entry.Credential is null ||
                entry.Credential.Length > MaximumCredentialLength ||
                entry.ConfigurationRevision < 1 ||
                entry.ConfigurationRevision > file.Revision)
            {
                throw new InvalidDataException("Destination set entry is invalid.");
            }
            var kind = AuditExportSettings.ParseKind(entry.Kind);
            var endpoint = AuditExportSettings.ParseEndpoint(entry.Endpoint);
            if (kind == AuditDestinationKind.None || endpoint is null ||
                !string.Equals(entry.Adapter, AuditExportSettings.KindText(kind), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Destination adapter is invalid.");
            }
            definitions.Add(new AuditDestinationDefinition(
                destinationId,
                kind,
                entry.OperatorLabel.Trim(),
                endpoint,
                entry.Adapter!,
                entry.CredentialReference!,
                entry.Credential,
                entry.ConfigurationRevision,
                entry.ActivatedUtc.ToUniversalTime(),
                entry.Enabled,
                entry.IncludeLegacyRecords,
                file.Version >= 2
                    ? NormalizeStoredPin(entry.ServerCertificateSha256)
                    : null));
        }
        return new AuditDestinationSetSnapshot(file.Revision, definitions);
    }

    private static bool TryNormalize(
        AuditDestinationDraft draft,
        out AuditDestinationDraft normalized,
        out string failure)
    {
        normalized = draft;
        if (draft.Kind == AuditDestinationKind.None)
        {
            failure = "invalid_kind";
            return false;
        }
        var label = draft.OperatorLabel?.Trim();
        if (string.IsNullOrWhiteSpace(label) || label.Length > MaximumLabelLength)
        {
            failure = "invalid_label";
            return false;
        }
        var endpoint = AuditExportSettings.ParseEndpoint(draft.Endpoint?.AbsoluteUri);
        if (endpoint is null || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            failure = "invalid_endpoint";
            return false;
        }
        var credential = draft.Credential?.Trim() ?? string.Empty;
        if (credential.Length > MaximumCredentialLength)
        {
            failure = "invalid_credential";
            return false;
        }
        if (!AuditDestinationTls.TryNormalizePin(
                draft.ServerCertificateSha256,
                out var serverCertificateSha256))
        {
            failure = "invalid_server_certificate_sha256";
            return false;
        }
        if (serverCertificateSha256 is not null && endpoint.Scheme != Uri.UriSchemeHttps)
        {
            failure = "certificate_pin_requires_https";
            return false;
        }
        normalized = new AuditDestinationDraft(
            draft.Kind,
            label,
            endpoint,
            credential,
            serverCertificateSha256);
        failure = string.Empty;
        return true;
    }

    private static AuditDestinationDefinition LegacyDefinition(
        AuditExportSettings settings,
        string root)
    {
        var endpoint = settings.Endpoint ??
            throw new InvalidDataException("Legacy destination endpoint is missing.");
        var identity = Encoding.UTF8.GetBytes(
            $"{Path.GetFullPath(root)}\n{AuditExportSettings.KindText(settings.Kind)}\n{endpoint.AbsoluteUri}");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(identity, digest);
        var idBytes = digest[..16].ToArray();
        idBytes[7] = (byte)((idBytes[7] & 0x0f) | 0x40);
        idBytes[8] = (byte)((idBytes[8] & 0x3f) | 0x80);
        var destinationId = new Guid(idBytes);
        CryptographicOperations.ZeroMemory(identity);
        return new AuditDestinationDefinition(
            destinationId,
            settings.Kind,
            "migrated destination",
            endpoint,
            AuditExportSettings.KindText(settings.Kind),
            CredentialReference(destinationId, 1),
            settings.Credential ?? string.Empty,
            1,
            DateTimeOffset.UtcNow,
            Enabled: true,
            IncludeLegacyRecords: true);
    }

    private static string CredentialReference(Guid destinationId, long revision) =>
        $"credential:{destinationId:D}:r{revision}";

    private static string? NormalizeStoredPin(string? value)
    {
        if (!AuditDestinationTls.TryNormalizePin(value, out var normalized))
            throw new InvalidDataException("Destination certificate pin is invalid.");
        return normalized;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class DestinationSetFile
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("revision")] public long Revision { get; set; }
        [JsonPropertyName("destinations")] public DestinationFile?[]? Destinations { get; set; }
    }

    private sealed class DestinationFile
    {
        [JsonPropertyName("destination_id")] public string? DestinationId { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("operator_label")] public string? OperatorLabel { get; set; }
        [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
        [JsonPropertyName("adapter")] public string? Adapter { get; set; }
        [JsonPropertyName("credential_reference")] public string? CredentialReference { get; set; }
        [JsonPropertyName("credential")] public string? Credential { get; set; }
        [JsonPropertyName("configuration_revision")] public long ConfigurationRevision { get; set; }
        [JsonPropertyName("activated_utc")] public DateTimeOffset ActivatedUtc { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("include_legacy_records")] public bool IncludeLegacyRecords { get; set; }
        [JsonPropertyName("server_certificate_sha256")] public string? ServerCertificateSha256 { get; set; }
    }
}
