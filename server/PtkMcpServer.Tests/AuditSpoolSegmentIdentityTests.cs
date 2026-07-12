using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditSpoolSegmentIdentityTests
{
    private static readonly Guid BootId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private const string CanonicalBootId = "1234567812344abc8def0123456789ab";
    private const string CanonicalName =
        "ptk-audit-1234567812344abc8def0123456789ab-01234567.jsonl";

    [Theory]
    [InlineData(0, "00000000")]
    [InlineData(1, "00000001")]
    [InlineData(99_999_998, "99999998")]
    [InlineData(99_999_999, "99999999")]
    public void Create_and_parse_round_trip_canonical_index_boundaries(
        int index,
        string encodedIndex)
    {
        var created = AuditSpoolSegmentIdentity.Create(BootId, index);
        var expected = $"ptk-audit-{CanonicalBootId}-{encodedIndex}.jsonl";

        Assert.Equal(expected, created.FileName);
        Assert.Equal(expected, created.ToString());
        Assert.Equal(AuditSpoolSegmentIdentity.FileNameLength, created.FileName.Length);
        Assert.True(AuditSpoolSegmentIdentity.TryParse(created.FileName, out var parsed));
        Assert.Equal(created, parsed);
        Assert.Equal(BootId, parsed.SupervisorBootId);
        Assert.Equal(index, parsed.Index);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(100_000_000)]
    [InlineData(int.MaxValue)]
    public void Create_rejects_every_index_range_outside_eight_decimal_digits(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuditSpoolSegmentIdentity.Create(BootId, index));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("12345678-1234-3abc-8def-0123456789ab")]
    [InlineData("12345678-1234-4abc-7def-0123456789ab")]
    [InlineData("12345678-1234-4abc-cdef-0123456789ab")]
    public void Create_rejects_a_non_rfc4122_uuidv4_boot_id(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            AuditSpoolSegmentIdentity.Create(Guid.Parse(value), 0));
    }

    [Fact]
    public void TryParse_accepts_every_lowercase_hex_digit_in_unconstrained_uuid_positions()
    {
        const string lowerHex = "0123456789abcdef";
        const int bootIdStart = 10;
        for (var position = 0; position < 32; position++)
        {
            if (position is 12 or 16)
                continue;

            foreach (var character in lowerHex)
            {
                var candidate = ReplaceAt(CanonicalName, bootIdStart + position, character);
                Assert.True(
                    AuditSpoolSegmentIdentity.TryParse(candidate, out _),
                    $"position={position}, character={character}");
            }
        }
    }

    [Theory]
    [InlineData('8')]
    [InlineData('9')]
    [InlineData('a')]
    [InlineData('b')]
    public void TryParse_accepts_each_rfc4122_variant_nibble(char variant)
    {
        var candidate = ReplaceAt(CanonicalName, 10 + 16, variant);

        Assert.True(AuditSpoolSegmentIdentity.TryParse(candidate, out _));
    }

    [Fact]
    public void TryParse_accepts_every_decimal_digit_at_each_index_position()
    {
        const string decimalDigits = "0123456789";
        const int indexStart = 43;
        for (var position = 0; position < 8; position++)
        {
            foreach (var character in decimalDigits)
            {
                var candidate = ReplaceAt(CanonicalName, indexStart + position, character);
                Assert.True(
                    AuditSpoolSegmentIdentity.TryParse(candidate, out _),
                    $"position={position}, character={character}");
            }
        }
    }

    [Fact]
    public void TryParse_rejects_a_non_lowercase_hex_character_at_every_uuid_position()
    {
        const int bootIdStart = 10;
        for (var position = 0; position < 32; position++)
        {
            var candidate = ReplaceAt(CanonicalName, bootIdStart + position, 'A');
            Assert.False(
                AuditSpoolSegmentIdentity.TryParse(candidate, out _),
                $"position={position}");
        }
    }

    [Fact]
    public void TryParse_rejects_a_non_decimal_character_at_every_index_position()
    {
        const int indexStart = 43;
        for (var position = 0; position < 8; position++)
        {
            var candidate = ReplaceAt(CanonicalName, indexStart + position, 'a');
            Assert.False(
                AuditSpoolSegmentIdentity.TryParse(candidate, out _),
                $"position={position}");
        }
    }

    [Fact]
    public void TryParse_rejects_every_non_v4_version_and_non_rfc4122_variant_nibble()
    {
        const string invalidVersions = "012356789abcdef";
        foreach (var version in invalidVersions)
        {
            var candidate = ReplaceAt(CanonicalName, 10 + 12, version);
            Assert.False(
                AuditSpoolSegmentIdentity.TryParse(candidate, out _),
                $"version={version}");
        }

        const string invalidVariants = "01234567cdef";
        foreach (var variant in invalidVariants)
        {
            var candidate = ReplaceAt(CanonicalName, 10 + 16, variant);
            Assert.False(
                AuditSpoolSegmentIdentity.TryParse(candidate, out _),
                $"variant={variant}");
        }
    }

    [Fact]
    public void TryParse_rejects_a_mutation_at_every_fixed_syntax_position()
    {
        var fixedPositions = Enumerable.Range(0, 10)
            .Concat([42])
            .Concat(Enumerable.Range(51, 6));

        foreach (var position in fixedPositions)
        {
            var candidate = ReplaceAt(CanonicalName, position, '~');
            Assert.False(
                AuditSpoolSegmentIdentity.TryParse(candidate, out _),
                $"position={position}");
        }
    }

    [Theory]
    [MemberData(nameof(NonCanonicalNames))]
    public void TryParse_rejects_noncanonical_whole_name_shapes(string? candidate)
    {
        Assert.False(AuditSpoolSegmentIdentity.TryParse(candidate, out var identity));
        Assert.Equal(default, identity);
    }

    public static TheoryData<string?> NonCanonicalNames => new()
    {
        null,
        string.Empty,
        "ptk-audit-1234567812344abc8def0123456789ab-01234567.jsonl ",
        " ptk-audit-1234567812344abc8def0123456789ab-01234567.jsonl",
        "PTK-audit-1234567812344abc8def0123456789ab-01234567.jsonl",
        "ptk-audit-1234567812344abc8def0123456789ab-01234567.JSONL",
        "ptk-audit-12345678-1234-4abc-8def-0123456789ab-01234567.jsonl",
        "ptk-audit-1234567812344ABC8DEF0123456789AB-01234567.jsonl",
        "ptk-audit-1234567812344abc8def0123456789ag-01234567.jsonl",
        "ptk-audit-1234567812343abc8def0123456789ab-01234567.jsonl",
        "ptk-audit-1234567812344abc7def0123456789ab-01234567.jsonl",
        "ptk-audit-1234567812344abccdef0123456789ab-01234567.jsonl",
        "ptk-audit-1234567812344abc8def0123456789ab-1234567.jsonl",
        "ptk-audit-1234567812344abc8def0123456789ab-001234567.jsonl",
        "ptk-audit-1234567812344abc8def0123456789ab-+1234567.jsonl",
        "ptk-audit-1234567812344abc8def0123456789ab-0123456a.jsonl",
        "ptk-audit-1234567812344abc8def0123456789ab-0123456٧.jsonl",
        "directory/ptk-audit-1234567812344abc8def0123456789ab-01234567.jsonl",
        "ptk-audit-1234567812344abc8def0123456789ab-01234567.jsonl.bak",
    };

    private static string ReplaceAt(string value, int index, char replacement)
    {
        var characters = value.ToCharArray();
        characters[index] = replacement;
        return new string(characters);
    }
}
