using System.Buffers.Binary;
using System.IO.Compression;
using AwesomeAssertions;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Transfer;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// T-10 ZIP checks that run before any XLSX (or CSV bundle) entry is parsed: entry count, declared expanded size,
/// bounded inflation, and entry names that cannot address anything outside the archive.
/// </summary>
[Trait("Req", "T-10")]
[Trait("Req", "B-12")]
public sealed class ArchiveGuardTests
{
    [Fact]
    public void An_archive_with_more_entries_than_the_limit_is_too_large()
    {
        var entries = Enumerable.Range(0, TransferLimits.MaxArchiveEntries + 1).Select(i => ($"e{i}.xml", string.Empty)).ToArray();

        var check = () => Check(TestWorkbook.Zip(entries));

        check.Should().Throw<TransferFormatException>().Which.Should().Match<TransferFormatException>(e => e.Failure == TransferFailure.TooLarge && e.Detail == "entries");
    }

    [Fact]
    public void An_archive_at_the_entry_limit_passes()
    {
        var entries = Enumerable.Range(0, TransferLimits.MaxArchiveEntries).Select(i => ($"e{i}.xml", string.Empty)).ToArray();

        var check = () => Check(TestWorkbook.Zip(entries));

        check.Should().NotThrow();
    }

    [Fact]
    public void A_declared_expanded_size_over_the_limit_is_rejected_before_inflating_anything()
    {
        var zip = WithDeclaredSize(TestWorkbook.Zip(("xl/workbook.xml", "<x/>")), TransferLimits.MaxExpandedBytes + 1);

        var check = () => Check(zip);

        check.Should().Throw<TransferFormatException>().Which.Should().Match<TransferFormatException>(e => e.Failure == TransferFailure.TooLarge && e.Detail == "expanded size");
    }

    [Fact]
    public void The_expanded_size_limit_applies_to_the_sum_of_the_entries()
    {
        var half = (TransferLimits.MaxExpandedBytes / 2) + 1;
        var zip = WithDeclaredSize(TestWorkbook.Zip(("a.xml", "<a/>"), ("b.xml", "<b/>")), half);

        var check = () => Check(zip);

        check.Should().Throw<TransferFormatException>().Which.Failure.Should().Be(TransferFailure.TooLarge);
    }

    [Fact]
    public void A_workbook_whose_declared_size_is_over_the_limit_is_rejected_by_the_xlsx_reader()
    {
        var zip = WithDeclaredSize(TestWorkbook.Create([TestWorkbook.Metadata()]), TransferLimits.MaxExpandedBytes + 1);

        var read = () => TestWorkbook.Read(zip);

        read.Should().Throw<TransferFormatException>().Which.Failure.Should().Be(TransferFailure.TooLarge);
    }

    [Fact]
    public void Bounded_reading_stops_when_an_entry_inflates_past_the_remaining_budget()
    {
        using var archive = Open(TestWorkbook.Zip(("bomb.xml", new string('0', 100_000))));
        long remaining = 1_000;

        var read = () => ArchiveGuard.ReadBounded(archive.Entries[0], ref remaining);

        read.Should().Throw<TransferFormatException>().Which.Should().Match<TransferFormatException>(e => e.Failure == TransferFailure.TooLarge && e.Detail == "expanded size");
    }

    [Fact]
    public void Bounded_reading_returns_the_entry_and_charges_its_size()
    {
        using var archive = Open(TestWorkbook.Zip(("ok.xml", "<ok/>")));
        long remaining = 100;

        var bytes = ArchiveGuard.ReadBounded(archive.Entries[0], ref remaining);

        bytes.Should().HaveCount(5);
        remaining.Should().Be(95);
    }

    [Theory]
    [InlineData("../evil.xml")]
    [InlineData("xl/../../evil.xml")]
    [InlineData("xl/./sheet.xml")]
    [InlineData("/rooted.xml")]
    [InlineData("\\rooted.xml")]
    [InlineData("C:/Windows/evil.xml")]
    [InlineData("xl\\..\\evil.xml")]
    public void An_entry_name_that_could_escape_the_archive_is_unsafe(string name)
    {
        ArchiveGuard.IsSafeName(name).Should().BeFalse();
        var check = () => Check(TestWorkbook.Zip((name, "<x/>")));
        check.Should().Throw<TransferFormatException>().Which.Should().Match<TransferFormatException>(e => e.Failure == TransferFailure.UnsafeArchive && e.Detail == "entry name");
    }

    [Theory]
    [InlineData("[Content_Types].xml")]
    [InlineData("xl/worksheets/sheet1.xml")]
    [InlineData("docProps/app.xml")]
    [InlineData("xl/..data.xml")]
    public void Ordinary_package_entry_names_are_safe(string name) => ArchiveGuard.IsSafeName(name).Should().BeTrue();

    [Fact]
    public void An_unexpected_entry_is_rejected_when_the_format_defines_its_entries()
    {
        using var archive = Open(TestWorkbook.Zip(("Tasks.csv", "Id"), ("macro.bin", "x")));

        var check = () => ArchiveGuard.Check(archive, name => name.EndsWith(".csv", StringComparison.Ordinal));

        check.Should().Throw<TransferFormatException>().Which.Should().Match<TransferFormatException>(e => e.Failure == TransferFailure.UnsafeArchive && e.Detail == "macro.bin");
    }

    private static void Check(byte[] zip)
    {
        using var archive = Open(zip);
        ArchiveGuard.Check(archive, null);
    }

    private static ZipArchive Open(byte[] zip) => new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);

    /// <summary>Rewrites every central directory record to declare <paramref name="size"/> uncompressed bytes (a ZIP bomb's lie).</summary>
    private static byte[] WithDeclaredSize(byte[] zip, long size)
    {
        var bytes = (byte[])zip.Clone();
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014b50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), checked((uint)size));
            }
        }

        return bytes;
    }
}
