using System.IO.Compression;
using MaxPlanner.Application.Transfer;

namespace MaxPlanner.Infrastructure.Transfer;

/// <summary>
/// T-10 / B-12 checks of a ZIP container (XLSX workbook or CSV bundle) before any entry is parsed: the entry count,
/// the declared expanded size, and every entry name (no rooted paths, drive letters, or <c>..</c> segments, so nothing
/// can be addressed outside the archive). Entries are only ever read into memory, never extracted to disk.
/// </summary>
internal static class ArchiveGuard
{
    /// <param name="isExpected">For a CSV bundle: whether an entry name is one the format defines; null accepts any safe name.</param>
    /// <exception cref="TransferFormatException">TooLarge or UnsafeArchive.</exception>
    public static void Check(ZipArchive archive, Func<string, bool>? isExpected)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Entries.Count > TransferLimits.MaxArchiveEntries)
        {
            throw new TransferFormatException(TransferFailure.TooLarge, "entries");
        }

        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (!IsSafeName(entry.FullName))
            {
                throw new TransferFormatException(TransferFailure.UnsafeArchive, "entry name");
            }

            if (isExpected is not null && !entry.FullName.EndsWith('/') && !isExpected(entry.FullName))
            {
                throw new TransferFormatException(TransferFailure.UnsafeArchive, entry.FullName);
            }

            expanded += entry.Length;
            if (entry.Length < 0 || expanded > TransferLimits.MaxExpandedBytes)
            {
                throw new TransferFormatException(TransferFailure.TooLarge, "expanded size");
            }
        }
    }

    /// <summary>
    /// Reads one entry into memory, counting the bytes actually inflated so a header that understates the size cannot
    /// exceed <paramref name="remaining"/> (a ZIP bomb).
    /// </summary>
    public static byte[] ReadBounded(ZipArchiveEntry entry, ref long remaining)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            remaining -= read;
            if (remaining < 0)
            {
                throw new TransferFormatException(TransferFailure.TooLarge, "expanded size");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static bool IsSafeName(string name) =>
        !string.IsNullOrEmpty(name)
        && name[0] is not ('/' or '\\')
        && !name.Contains(':', StringComparison.Ordinal)
        && !name.Contains('\0', StringComparison.Ordinal)
        && name.Split('/', '\\').All(segment => segment is not ".." and not ".");
}
