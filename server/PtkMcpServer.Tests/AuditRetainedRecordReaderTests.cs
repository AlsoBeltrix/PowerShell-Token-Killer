using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditRetainedRecordReaderTests
{
    [Fact]
    public void Retained_history_uses_block_reads_and_bounded_metadata_queries()
    {
        var records = Enumerable.Range(0, 12)
            .Select(index => Encoding.UTF8.GetBytes(new string((char)('a' + index), 24_000)))
            .ToArray();
        var bytes = records.SelectMany(record => record.Append((byte)'\n')).ToArray();
        using var stream = new ObservedStream(bytes);

        AssertRecords(records, Read(stream));

        Assert.Equal(0, stream.ByteReads);
        Assert.InRange(stream.BlockReads, 1, 40);
        Assert.InRange(stream.LengthQueries, 0, 2);
        Assert.InRange(stream.PositionQueries, 0, 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16_384)]
    [InlineData(int.MaxValue)]
    public void Short_reads_preserve_records_across_block_and_utf8_boundaries(int maxRead)
    {
        var records = new[]
        {
            Encoding.UTF8.GetBytes(new string('a', 16_383)),
            Encoding.UTF8.GetBytes(new string('b', 16_384)),
            Encoding.UTF8.GetBytes(new string('c', 16_385)),
            Encoding.UTF8.GetBytes(new string('d', 16_381) + "雪🌲"),
            Encoding.UTF8.GetBytes("last")
        };
        using var stream = new ObservedStream(
            records.SelectMany(record => record.Append((byte)'\n')).ToArray(), maxRead);

        AssertRecords(records, Read(stream));
    }

    [Theory]
    [InlineData(256)]
    [InlineData(16_384)]
    [InlineData(65_536)]
    public void Exact_record_limit_is_accepted_and_one_extra_byte_is_refused(int limit)
    {
        using var exact = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', limit) + "\n"));
        Assert.Equal(limit, Assert.Single(Read(exact, limit)).Length);

        using var oversized = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', limit + 1) + "\n"));
        var exception = Assert.Throws<IOException>(() => Read(oversized, limit));
        Assert.Contains("configured bound", exception.Message);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(16_384)]
    [InlineData(16_385)]
    public void Incomplete_tail_is_refused_after_complete_records(int tailLength)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("complete\n" + new string('x', tailLength)));
        var exception = Assert.Throws<IOException>(() => Read(stream));
        Assert.Contains("torn tail", exception.Message);
    }

    [Fact]
    public void Early_eof_is_refused_even_at_a_record_boundary()
    {
        using var stream = new ObservedStream("complete\n"u8.ToArray(), extraLength: 1);
        var exception = Assert.Throws<IOException>(() => Read(stream));
        Assert.Contains("ended unexpectedly", exception.Message);
    }

    [Fact]
    public void Empty_segment_yields_no_records()
    {
        using var stream = new MemoryStream();
        Assert.Empty(Read(stream));
    }

    private static byte[][] Read(Stream stream, int limit = 65_536) =>
        FileAuditJournalSink.ReadRetainedRecords(stream, limit)
            .Select(record => record.ToArray()).ToArray();

    private static void AssertRecords(byte[][] expected, byte[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], actual[index]);
    }

    private sealed class ObservedStream(
        byte[] bytes,
        int maxRead = int.MaxValue,
        int extraLength = 0) : MemoryStream(bytes)
    {
        internal int ByteReads { get; private set; }
        internal int BlockReads { get; private set; }
        internal int LengthQueries { get; private set; }
        internal int PositionQueries { get; private set; }

        public override long Length
        {
            get { LengthQueries++; return base.Length + extraLength; }
        }

        public override long Position
        {
            get { PositionQueries++; return base.Position; }
            set => base.Position = value;
        }

        public override int ReadByte()
        {
            ByteReads++;
            return base.ReadByte();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            BlockReads++;
            return base.Read(buffer, offset, Math.Min(count, maxRead));
        }
    }
}
