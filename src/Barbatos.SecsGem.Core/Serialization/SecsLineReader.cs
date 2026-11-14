// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// Reads a <see cref="PipeReader"/> one line at a time.
/// </summary>
/// <remarks>
/// <para>
/// The line-oriented spokes - SML and INI - share this. It keeps them genuinely streaming: the
/// reader holds one line, and the spoke holds one message, so converting a 500 MB capture never
/// needs 500 MB of memory. Accepts LF and CRLF, and counts lines so a parse error can name one.
/// </para>
/// <para>
/// A UTF-8 byte-order mark is removed from the first line. Files exported by Windows SECS
/// tooling routinely have one, and a parser that does not strip it fails on an invisible
/// character at column one.
/// </para>
/// </remarks>
/// <param name="input">The source.</param>
public sealed class SecsLineReader(PipeReader input)
{
    private readonly PipeReader _input = input ?? throw new ArgumentNullException(nameof(input));
    private bool _atStart = true;

    /// <summary>Gets the one-based number of the line most recently returned.</summary>
    public int LineNumber { get; private set; }

    /// <summary>Reads the next line.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The line without its terminator, or <see langword="null"/> at the end of the stream.</returns>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryReadLine(ref buffer, out var line))
            {
                _input.AdvanceTo(buffer.Start);
                LineNumber++;
                return Normalise(line);
            }

            if (result.IsCompleted)
            {
                // A final line with no terminator is still a line.
                if (buffer.IsEmpty)
                {
                    _input.AdvanceTo(buffer.End);
                    return null;
                }

                var last = SecsUtf8.GetString(buffer);
                _input.AdvanceTo(buffer.End);
                LineNumber++;
                return Normalise(last);
            }

            _input.AdvanceTo(buffer.Start, buffer.End);
        }

        // Local and non-async: SequenceReader<T> is a ref struct and cannot survive an await.
        static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out string line)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (reader.TryReadTo(out ReadOnlySequence<byte> lineBytes, (byte)'\n', advancePastDelimiter: true))
            {
                line = SecsUtf8.GetString(lineBytes);
                buffer = buffer.Slice(reader.Position);
                return true;
            }

            line = string.Empty;
            return false;
        }
    }

    private string Normalise(string line)
    {
        var result = line.TrimEnd('\r');
        if (_atStart)
        {
            _atStart = false;
            result = result.TrimStart('﻿');
        }

        return result;
    }
}
