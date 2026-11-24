// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Text;
using Barbatos.SecsGem.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// A spool that survives the equipment's own restart, by keeping each held message in a file.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GemInMemorySpool"/> holds reports for a host that is not there, and loses them the
/// moment the equipment is upgraded, crashes or loses power - which are three of the more likely
/// reasons a host was not there. This one writes each message to disk before it reports it held,
/// and reads what is still there when it is next constructed.
/// </para>
/// <para>
/// <strong>One file per message, rather than one append-only log.</strong> A log is the obvious
/// shape and the wrong one here: removing the head means a read cursor that has to be as durable as
/// the log itself, the file has to be compacted or it grows without bound, and a record half
/// written when the power went has to be found with a checksum. A file per message replaces all of
/// that with a rename: the record is written under a temporary name, and <c>File.Move</c> within
/// one directory is atomic, so a message is either entirely a record or not a record at all. The
/// price is a directory entry per held message, which <see cref="Capacity"/> bounds.
/// </para>
/// <para>
/// The order is held in memory and the files are the durable copy, so counting, appending and
/// removing cost no directory scan - only <see cref="TryPeekAsync"/> reads a file, once per message
/// transmitted. The scan happens once, in the constructor.
/// </para>
/// <para>
/// Writes go through <see cref="FileOptions.WriteThrough"/>. That is a disk round trip per message
/// and it is the point: a buffered write the operating system has not flushed is exactly what a
/// power cut takes, and a spool that loses its contents on the failure it exists for would be
/// worse than no spool, because the equipment would have reported the message held.
/// </para>
/// <para>
/// One spool per directory. Two instances over the same directory would hand each other's messages
/// to the same host twice and fight over the sequence numbers.
/// </para>
/// </remarks>
public sealed class GemFileSpool : IGemSpool
{
    /// <summary>The extension a finished record carries.</summary>
    public const string RecordExtension = ".spool";

    /// <summary>The extension a record carries while it is being written.</summary>
    public const string PartialExtension = ".spooling";

    private const byte FormatVersion = 1;
    private const int HeaderLength = 24;
    private const int NoName = 0xFFFF;
    private const int MaxNameLength = 0xFFFE;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<long> _order = new();
    private readonly DurableRecordDirectory _records;
    private readonly ILogger _logger;
    private long _next;

    /// <summary>Opens a spool over a directory, adopting whatever is already in it.</summary>
    /// <param name="directory">Where the records live. Created if it does not exist.</param>
    /// <param name="capacity">How many messages it will hold. Must be positive.</param>
    /// <param name="overflow">What to do when it is full.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    /// <exception cref="IOException">The directory cannot be created or read.</exception>
    /// <remarks>
    /// <para>
    /// The directory is created and scanned here rather than on first use, so a path that is wrong
    /// fails at start-up where an operator is looking, not at three in the morning when a host
    /// finally goes away.
    /// </para>
    /// <para>
    /// Any leftover <c>.spooling</c> file is deleted: it is a record whose write did not finish, so
    /// <see cref="TryAppendAsync"/> never returned and nothing was ever told the message was held.
    /// A file whose name is not a sequence number is left alone - this spool did not write it.
    /// </para>
    /// </remarks>
    public GemFileSpool(
        string directory,
        int capacity = 1_000,
        GemSpoolOverflowPolicy overflow = GemSpoolOverflowPolicy.DiscardNewest,
        ILogger<GemFileSpool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Capacity = capacity;
        Overflow = overflow;
        _logger = logger ?? NullLogger<GemFileSpool>.Instance;

        // The directory mechanics are DurableRecordDirectory's: it creates the directory, sweeps
        // what a half-finished write left, and lists the records in sequence order. The record
        // FORMAT stays here - the header below, and the reader - because a spool is a queue and
        // not a keyed store, so none of that class's field-and-tail framing fits. The partial
        // extension is passed because this spool publishes its own.
        _records = new DurableRecordDirectory(directory, RecordExtension, _logger, PartialExtension);

        foreach (var (sequence, _) in _records.Records())
        {
            _order.Enqueue(sequence);
            _next = sequence + 1;
        }

        if (_order.Count > 0 || _records.Swept > 0)
        {
            _logger.LogInformation(
                "Spool directory {Directory} opened with {Held} message(s) held and {Partial} unfinished write(s) removed.",
                Directory,
                _order.Count,
                _records.Swept);
        }
    }

    /// <summary>Gets the directory the records live in, as a full path.</summary>
    public string Directory => _records.Path;

    /// <summary>Gets how many messages this spool will hold.</summary>
    public int Capacity { get; }

    /// <summary>Gets what happens when it is full.</summary>
    public GemSpoolOverflowPolicy Overflow { get; }

    /// <summary>Gets how many messages have been dropped because it was full.</summary>
    public int Discarded { get; private set; }

    /// <summary>Gets how many records were dropped because they could not be read back.</summary>
    /// <remarks>
    /// A record that cannot become a message is removed rather than retried. A spool that cannot be
    /// drained is worse than one that lost a message: the alternative is a transmit loop that stops
    /// on the same file every time a host asks, and never delivers the messages behind it. This
    /// counter is how that shows up as something other than silence.
    /// </remarks>
    public int Unreadable { get; private set; }

    /// <inheritdoc/>
    public async ValueTask<int> CountAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return _order.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="GemSpooledMessage.Name"/> is longer than 65,534 bytes of UTF-8.
    /// </exception>
    public async ValueTask<bool> TryAppendAsync(GemSpooledMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var nameLength = message.Name is null ? NoName : Encoding.UTF8.GetByteCount(message.Name);

        if (message.Name is not null && nameLength > MaxNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                nameLength,
                $"A spooled message name must fit in {MaxNameLength} bytes of UTF-8.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_order.Count >= Capacity)
            {
                if (Overflow == GemSpoolOverflowPolicy.DiscardNewest)
                {
                    Discarded++;
                    return false;
                }

                _records.Delete(_order.Dequeue());
                Discarded++;
            }

            var sequence = _next;

            await WriteRecordAsync(sequence, message, nameLength, cancellationToken).ConfigureAwait(false);

            // Only now: a sequence number consumed by a write that threw would leave a gap, which
            // is harmless, but an order that claims a record the disk never took would not be.
            _next = sequence + 1;
            _order.Enqueue(sequence);

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the head record from disk, and drops any it cannot read on the way - see
    /// <see cref="Unreadable"/>. That makes this the one method here that both reads a file and may
    /// change what is held.
    /// </remarks>
    public async ValueTask<GemSpooledMessage?> TryPeekAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (_order.Count > 0)
            {
                var sequence = _order.Peek();
                var path = _records.PathFor(sequence);
                var message = await TryReadRecordAsync(path, cancellationToken).ConfigureAwait(false);

                if (message is not null)
                {
                    return message;
                }

                _order.Dequeue();
                Unreadable++;
                _records.Delete(sequence);

                _logger.LogError(
                    "Spool record {Path} could not be read back and has been dropped; {Held} message(s) still held.",
                    path,
                    _order.Count);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A record whose file will not delete is still removed from the order, because the alternative
    /// is sending it again immediately and for ever. It stays on disk, so a later restart adopts it
    /// and the host sees it twice - which is why the failure is logged as a warning rather than
    /// swallowed.
    /// </remarks>
    public async ValueTask<bool> RemoveHeadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_order.TryDequeue(out var sequence))
            {
                return false;
            }

            _records.Delete(sequence);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask PurgeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (_order.TryDequeue(out var sequence))
            {
                _records.Delete(sequence);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void WriteHeader(Span<byte> header, GemSpooledMessage message, int nameLength)
    {
        header[0] = (byte)'B';
        header[1] = (byte)'S';
        header[2] = (byte)'P';
        header[3] = (byte)'L';
        header[4] = FormatVersion;
        header[5] = message.Stream;
        header[6] = message.Function;
        header[7] = message.ReplyExpected ? (byte)1 : (byte)0;

        // UTC ticks and the offset separately, so a DateTimeOffset comes back as the same instant
        // *and* the same offset. A host reading SpoolStartTime after a restart is being told when
        // the oldest held message was held, and in the zone the equipment said it in.
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], message.SpooledAt.UtcTicks);
        BinaryPrimitives.WriteInt16LittleEndian(header[16..], (short)message.SpooledAt.Offset.TotalMinutes);
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)nameLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], message.Body.Length);
    }

    private static async ValueTask<GemSpooledMessage?> TryReadRecordAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadRecordAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception
            is FileNotFoundException
            or DirectoryNotFoundException
            or EndOfStreamException
            or ArgumentOutOfRangeException)
        {
            // Gone, truncated, or carrying numbers that are not a date. None of the three can
            // become a message and none is a fault an operator can fix, so they are a dropped
            // record. Anything else - a disk error, a permission error - is left to propagate,
            // because deleting data on the strength of a transient failure is not a repair.
            return null;
        }
    }

    private static async ValueTask<GemSpooledMessage?> ReadRecordAsync(string path, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        await using var file = new FileStream(path, options);

        var length = file.Length;

        if (length is < HeaderLength or > int.MaxValue)
        {
            return null;
        }

        var header = ArrayPool<byte>.Shared.Rent(HeaderLength);

        byte stream;
        byte function;
        bool replyExpected;
        long ticks;
        short offsetMinutes;
        int nameLength;
        int bodyLength;

        try
        {
            await file.ReadExactlyAsync(header.AsMemory(0, HeaderLength), cancellationToken).ConfigureAwait(false);

            var span = header.AsSpan(0, HeaderLength);

            if (span[0] != (byte)'B'
                || span[1] != (byte)'S'
                || span[2] != (byte)'P'
                || span[3] != (byte)'L'
                || span[4] != FormatVersion)
            {
                return null;
            }

            stream = span[5];
            function = span[6];
            replyExpected = (span[7] & 1) != 0;
            ticks = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
            offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(span[16..]);
            nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[18..]);
            bodyLength = BinaryPrimitives.ReadInt32LittleEndian(span[20..]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        var named = nameLength != NoName;

        // The lengths against the file's own length. With an atomic rename a record is whole or
        // absent, so this catches a file something else truncated or wrote - not a torn write.
        if (bodyLength < 0
            || ticks < DateTime.MinValue.Ticks
            || ticks > DateTime.MaxValue.Ticks
            || offsetMinutes is < -840 or > 840
            || HeaderLength + (named ? nameLength : 0) + (long)bodyLength != length)
        {
            return null;
        }

        string? name = null;

        if (named && nameLength > 0)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(nameLength);

            try
            {
                await file.ReadExactlyAsync(buffer.AsMemory(0, nameLength), cancellationToken).ConfigureAwait(false);
                name = Encoding.UTF8.GetString(buffer.AsSpan(0, nameLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else if (named)
        {
            name = string.Empty;
        }

        // Not pooled and not zeroed: the message owns this for as long as it is held, so it cannot
        // come from a pool, and every byte of it is about to be overwritten by the read.
        byte[] body = bodyLength == 0 ? [] : GC.AllocateUninitializedArray<byte>(bodyLength);

        if (bodyLength > 0)
        {
            await file.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        }

        var spooledAt = new DateTimeOffset(ticks, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(offsetMinutes));

        return new GemSpooledMessage(stream, function, replyExpected, body, spooledAt, name);
    }

    private async ValueTask WriteRecordAsync(
        long sequence,
        GemSpooledMessage message,
        int nameLength,
        CancellationToken cancellationToken)
    {
        var prefixLength = HeaderLength + (nameLength == NoName ? 0 : nameLength);
        var prefix = ArrayPool<byte>.Shared.Rent(prefixLength);

        try
        {
            WriteHeader(prefix, message, nameLength);

            if (message.Name is not null)
            {
                Encoding.UTF8.GetBytes(message.Name, prefix.AsSpan(HeaderLength));
            }

            // The write-through, the temporary name and the rename are DurableRecordDirectory's,
            // and the header above is this spool's: the disk mechanics are shared across five
            // packages and the record format is not. The partial is named by appending this
            // spool's own PartialExtension to the record's name, so it reads
            // `<sequence>.spool.spooling` where it used to read `<sequence>.spooling` - a name
            // nothing can observe except by looking during a write, and both shapes are swept.
            await _records.WriteAtomicAsync(
                sequence.ToString("D19", CultureInfo.InvariantCulture) + RecordExtension,
                prefix.AsMemory(0, prefixLength),
                message.Body,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prefix);
        }
    }
}
