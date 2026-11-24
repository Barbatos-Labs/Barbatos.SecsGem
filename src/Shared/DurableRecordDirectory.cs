// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Internal;

/// <summary>
/// A directory of sequence-numbered records, each written under a temporary name and committed
/// with a rename.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The mechanism four transfer stores share, written once.</strong> Streams 8, 13, 15 and
/// 21 each keep things a host sent - control programs, data sets, recipes, items - and each has a
/// store that must outlive the process. What they share is not a rule but a mechanism: a record is
/// written through to the disk under a temporary name and <c>File.Move</c> within one directory is
/// atomic, so a record is either whole or not there; a file name is a sequence number and nothing
/// else, so no name a host chose is ever part of a path; and a store adopts what it finds when it
/// is next constructed.
/// </para>
/// <para>
/// <strong>Shared source rather than a package, because no one of the four may depend on
/// another.</strong> They sit side by side on <c>Barbatos.SecsGem.Hsms</c> - a machine that serves
/// recipes must not drag in data sets - and the only package all four could take this from is the
/// transport, which is about sockets and sessions and has no business holding a file format. So it
/// is compiled into each of them, <see langword="internal"/>, the same arrangement and for a
/// related reason as <c>SecsSerializationRegistration</c>: the alternative to sharing is four
/// copies of the one thing that must not differ. That is not hypothetical - the first draft of
/// <c>RecipeFileStore.TryDelete</c> caught <see cref="IOException"/> where <c>GemFileSpool</c>'s
/// catches <see cref="UnauthorizedAccessException"/> as well, and a read-only file would have
/// thrown out of a transaction in one store and not the other.
/// </para>
/// <para>
/// <strong>The GEM spool uses the bottom half of this and not the top.</strong> A spool is a queue
/// with an order, a head, a capacity and an overflow policy, and it never parses a record to find
/// out what it holds - it sorts sequence numbers and enqueues them, where a keyed store parses
/// every record on the way in because the key is inside it. So the spool takes
/// <see cref="Records"/>, <see cref="PathFor"/>, <see cref="Delete"/> and
/// <see cref="WriteAtomicAsync"/>, keeps its own record header and its own reader, and uses none of
/// the field-and-tail framing. That is the line worth drawing: <strong>the disk mechanics are
/// shared, the record format is not</strong>. It kept a whole copy of both for two chapters, on the
/// argument that it is structurally a different thing - which was true of the framing and never of
/// the write.
/// </para>
/// <para>
/// <strong>The record is a list of entries, which is what lets one format serve four stores.</strong>
/// A control program is an identifier and a revision, a data set is a number and a declared length,
/// a recipe is a namespace and an identifier, an item is an identifier, a type, a length and any
/// number of attributes. None of those fits a fixed header, and every one of them fits a count and
/// a run of length-prefixed fields. A field's meaning is its position - which is what a SECS-II
/// body is, so the record mirrors the message it came out of.
/// </para>
/// <para>
/// The <strong>tail</strong> is the one entry that gets special treatment: it is written straight
/// from the caller's memory rather than copied in beside the fields, because a recipe or a control
/// program of a megabyte would otherwise want a megabyte buffer and anything over 85 KB of it is a
/// large object heap allocation. Whether a record has one is in the header, so a store can tell an
/// item that was listed with no contents from one whose contents are empty.
/// </para>
/// <para>
/// One instance per directory. Two would fight over the sequence numbers.
/// </para>
/// </remarks>
internal sealed class DurableRecordDirectory
{
    /// <summary>The extension appended to any file while it is being written, unless one is given.</summary>
    internal const string PartialExtension = ".writing";

    private const byte FormatVersion = 1;
    private const int HeaderLength = 8;

    private readonly string _recordExtension;
    private readonly string _partialExtension;
    private readonly ILogger _logger;

    /// <summary>Opens a directory, creating it and clearing anything half-written.</summary>
    /// <param name="directory">Where the records live.</param>
    /// <param name="recordExtension">What a finished record is called, including the dot.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="partialExtension">
    /// What a file in the middle of being written is called, including the dot.
    /// <see cref="PartialExtension"/> unless a store has published one of its own.
    /// </param>
    /// <exception cref="IOException">The directory cannot be created or read.</exception>
    /// <remarks>
    /// <para>
    /// The directory is created and swept here rather than on first use, so a path that is wrong
    /// fails where an operator is looking and not when a host finally sends something. A leftover
    /// partial is deleted: its write never finished, so nothing was ever told the record arrived.
    /// </para>
    /// <para>
    /// <paramref name="partialExtension"/> exists for exactly one caller and is the whole price of
    /// the spool joining this: <c>GemFileSpool.PartialExtension</c> is <c>.spooling</c> and is
    /// <strong>public</strong>, so it could not be quietly changed to <c>.writing</c>. A parameter
    /// was the cheaper of the two moves - the other was changing a published constant - and it
    /// costs this class nothing, since the sweep and the write use whatever they are given.
    /// </para>
    /// </remarks>
    internal DurableRecordDirectory(
        string directory,
        string recordExtension,
        ILogger logger,
        string partialExtension = PartialExtension)
    {
        _recordExtension = recordExtension;
        _partialExtension = partialExtension;
        _logger = logger;

        Path = System.IO.Directory.CreateDirectory(directory).FullName;

        foreach (var path in System.IO.Directory.EnumerateFiles(Path, "*" + _partialExtension))
        {
            TryDelete(path);
            Swept++;
        }
    }

    /// <summary>Gets the directory, as a full path.</summary>
    internal string Path { get; }

    /// <summary>Gets how many unfinished writes the constructor removed.</summary>
    internal int Swept { get; }

    /// <summary>Lists the records, in sequence order.</summary>
    /// <returns>Each sequence number and its path.</returns>
    /// <remarks>
    /// A file whose name is not a sequence number is left out and left alone: this directory did
    /// not write it, and deleting somebody else's file is not a store's business. Ordered, so that
    /// a store resolving two records for one key sees them in a repeatable order rather than in
    /// whatever order the file system enumerated.
    /// </remarks>
    internal IEnumerable<(long Sequence, string Path)> Records()
    {
        List<(long Sequence, string Path)> found = [];

        foreach (var path in System.IO.Directory.EnumerateFiles(Path, "*" + _recordExtension))
        {
            if (long.TryParse(
                System.IO.Path.GetFileNameWithoutExtension(path),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence))
            {
                found.Add((sequence, path));
            }
        }

        found.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));

        return found;
    }

    /// <summary>Where a record lives.</summary>
    /// <param name="sequence">Which one.</param>
    /// <returns>Its path.</returns>
    internal string PathFor(long sequence) => System.IO.Path.Combine(
        Path,
        sequence.ToString("D19", CultureInfo.InvariantCulture) + _recordExtension);

    /// <summary>Deletes a record, and does not mind if it cannot.</summary>
    /// <param name="sequence">Which one.</param>
    internal void Delete(long sequence) => TryDelete(PathFor(sequence));

    /// <summary>Writes a record, committing it with a rename.</summary>
    /// <param name="sequence">Which record. An existing one is replaced atomically.</param>
    /// <param name="fields">The small fields, in the order the store reads them back.</param>
    /// <param name="tail">The payload, or <see langword="null"/> for a record that carries none.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the record is on the disk.</returns>
    /// <exception cref="IOException">The write failed. Nothing was committed.</exception>
    internal async ValueTask WriteRecordAsync(
        long sequence,
        IReadOnlyList<ReadOnlyMemory<byte>> fields,
        ReadOnlyMemory<byte>? tail,
        CancellationToken cancellationToken)
    {
        var prefixLength = HeaderLength + fields.Sum(field => 4 + field.Length) + (tail is null ? 0 : 4);
        var prefix = new byte[prefixLength];

        prefix[0] = FormatVersion;
        prefix[1] = tail is null ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(4), fields.Count);

        var at = HeaderLength;

        foreach (var field in fields)
        {
            BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(at), field.Length);
            field.Span.CopyTo(prefix.AsSpan(at + 4));
            at += 4 + field.Length;
        }

        if (tail is { } payload)
        {
            BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(at), payload.Length);
        }

        await WriteAtomicAsync(
            sequence.ToString("D19", CultureInfo.InvariantCulture) + _recordExtension,
            prefix,
            tail ?? default,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a record back.</summary>
    /// <param name="path">Which file, as <see cref="Records"/> gave it.</param>
    /// <returns>The fields and the tail, or <see langword="null"/> when this is not a record this version can read.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Null covers three different disks and the store cannot tell them apart</strong>, by
    /// design: a record written by a later version, a record a tool truncated, and a file that
    /// cannot be read at all. All three mean the same thing to a store - do not serve this - and
    /// none of them means delete it, because a downgrade should not throw away a fab's work.
    /// </para>
    /// <para>
    /// The total length is checked against the file's, so a truncation is a refusal rather than a
    /// shorter payload handed to a host. The tail is a slice of the bytes that were read and not a
    /// copy of them.
    /// </para>
    /// </remarks>
    internal (IReadOnlyList<byte[]> Fields, ReadOnlyMemory<byte>? Tail)? TryReadRecord(string path)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "A record could not be read: {Path}.", path);

            return null;
        }

        if (bytes.Length < HeaderLength || bytes[0] != FormatVersion || bytes[1] > 1)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4));

        if (count < 0)
        {
            return null;
        }

        List<byte[]> fields = new(count);
        var at = HeaderLength;

        for (var field = 0; field < count; field++)
        {
            if (at + 4 > bytes.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
            at += 4;

            if (length < 0 || at + length > bytes.Length)
            {
                return null;
            }

            fields.Add(bytes.AsSpan(at, length).ToArray());
            at += length;
        }

        if (bytes[1] == 0)
        {
            return at == bytes.Length ? (fields, null) : null;
        }

        if (at + 4 > bytes.Length)
        {
            return null;
        }

        var tailLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
        at += 4;

        return tailLength >= 0 && at + tailLength == bytes.Length
            ? (fields, new ReadOnlyMemory<byte>(bytes, at, tailLength))
            : null;
    }

    /// <summary>Writes a small file of entries, whole, committing it with a rename.</summary>
    /// <param name="name">What it is called. No sequence number, so <see cref="Records"/> leaves it alone.</param>
    /// <param name="entries">The entries.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the file is on the disk.</returns>
    /// <remarks>
    /// For the state a store has that is not one of its contents - a namespace with nothing in it,
    /// a lock, a selection. Rewritten whole rather than appended to, which is affordable because
    /// these are lists of names where a record is a megabyte, and an append-only file would need
    /// compaction and a tombstone for every delete. Only the two-level stores have any: a store
    /// whose whole state is its contents needs none, and three of the four are that.
    /// </remarks>
    internal ValueTask WriteEntriesAsync(
        string name,
        IReadOnlyList<ReadOnlyMemory<byte>> entries,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[entries.Sum(entry => 4 + entry.Length)];
        var at = 0;

        foreach (var entry in entries)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at), entry.Length);
            entry.Span.CopyTo(bytes.AsSpan(at + 4));
            at += 4 + entry.Length;
        }

        return WriteAtomicAsync(name, bytes, default, cancellationToken);
    }

    /// <summary>Reads a small file of entries.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The entries. Empty when there is no such file, which is the ordinary first start.</returns>
    internal IReadOnlyList<byte[]> ReadEntries(string name)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(System.IO.Path.Combine(Path, name));
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The {Name} file could not be read and was taken as empty.", name);

            return [];
        }

        List<byte[]> entries = [];
        var at = 0;

        while (at + 4 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
            at += 4;

            if (length < 0 || at + length > bytes.Length)
            {
                // A file this directory did not finish writing cannot reach here - the rename is
                // the commit - so this is a truncation from outside, and what was read already is
                // kept rather than thrown away.
                _logger.LogWarning(
                    "The {Name} file ends mid-entry; {Read} entry/entries were read.",
                    name,
                    entries.Count);

                break;
            }

            entries.Add(bytes.AsSpan(at, length).ToArray());
            at += length;
        }

        return entries;
    }

    /// <summary>A string as an entry.</summary>
    /// <param name="value">The string.</param>
    /// <returns>Its UTF-8 bytes.</returns>
    internal static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>A number as an entry, four bytes big-endian.</summary>
    /// <param name="value">The number.</param>
    /// <returns>Its bytes.</returns>
    /// <remarks>
    /// Fixed width rather than the decimal text a path would have needed, because nothing reads
    /// these by eye: the record is read by the store that wrote it.
    /// </remarks>
    internal static byte[] Bytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);

        return bytes;
    }

    /// <summary>An entry as a string.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>It, as text.</returns>
    internal static string Text(byte[] entry) => Encoding.UTF8.GetString(entry);

    /// <summary>An entry as a number.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>It, or <see langword="null"/> when it is not four bytes.</returns>
    /// <remarks>
    /// Null rather than a throw or a zero, because a record whose field is the wrong width is a
    /// record this version cannot read - which every store already has an answer for, and that
    /// answer is never "treat it as nought".
    /// </remarks>
    internal static uint? Number(byte[] entry)
        => entry.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(entry) : null;

    /// <summary>Writes a file under a temporary name and commits it with a rename.</summary>
    /// <param name="name">What the finished file is called, inside this directory.</param>
    /// <param name="prefix">
    /// Everything before the payload: a header, fields, whatever the caller frames with. A
    /// <see cref="ReadOnlyMemory{T}"/> rather than an array so a caller framing into a rented
    /// buffer can hand over the used part of it, which is what the GEM spool does.
    /// </param>
    /// <param name="body">The payload, written straight from the caller's memory. Empty for none.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the file is committed.</returns>
    /// <exception cref="IOException">The write failed. Nothing was committed, and the partial is gone.</exception>
    /// <remarks>
    /// <strong>Internal rather than private because a store with its own record format still wants
    /// this.</strong> The GEM spool frames its records with a twenty-four byte header of its own
    /// and reads them back itself, so none of the field-and-tail layer is any use to it - but the
    /// write-through, the temporary name and the rename are exactly the same three things, and one
    /// copy of those per assembly is the point of this file. A caller using this directly is
    /// responsible for its own naming: nothing here parses <paramref name="name"/>.
    /// </remarks>
    internal async ValueTask WriteAtomicAsync(
        string name,
        ReadOnlyMemory<byte> prefix,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var partial = System.IO.Path.Combine(Path, name + _partialExtension);

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                PreallocationSize = prefix.Length + body.Length,
            };

            await using (var file = new FileStream(partial, options))
            {
                await file.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);

                if (!body.IsEmpty)
                {
                    // Straight from the caller's memory rather than copied in beside the header,
                    // for the reason this class's remarks give: a megabyte buffer per transfer is
                    // a large object heap allocation per transfer.
                    await file.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                }

                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The commit. Atomic within one directory, which is what makes a half-written record
            // impossible rather than merely unlikely.
            File.Move(partial, System.IO.Path.Combine(Path, name), overwrite: true);
        }
        catch
        {
            // A write that failed leaves no record, so it leaves no file either. The next
            // constructor would sweep this, but a store filling a disk should not need a restart
            // to stop littering.
            TryDelete(partial);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Deliberately not propagated, which is GemFileSpool.TryDelete's reason as well: every
            // caller has already decided the record is gone, and throwing here would turn a file
            // that will not delete into a transaction that never answers.
        }
    }
}
