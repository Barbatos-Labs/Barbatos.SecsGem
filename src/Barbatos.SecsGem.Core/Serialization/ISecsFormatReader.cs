// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// Reads messages of one format into the hub representation, <see cref="SecsMessage"/>.
/// </summary>
/// <remarks>
/// <para>
/// One half of the Hub-and-Spoke contract. A spoke assembly implements this and
/// <see cref="ISecsFormatWriter"/>, registers both, and immediately gains conversion to and
/// from every other registered format - without this assembly knowing it exists.
/// </para>
/// <para>
/// The input is a <see cref="PipeReader"/> rather than a <see cref="string"/> or a
/// <see cref="Stream"/>, so an implementation never has to hold a whole document in memory,
/// and so a reader can sit directly on a socket or a file with no adapter in between.
/// </para>
/// </remarks>
public interface ISecsFormatReader
{
    /// <summary>Gets the format this reader understands.</summary>
    SecsFormatDescriptor Descriptor { get; }

    /// <summary>Reads every message from <paramref name="input"/>, lazily.</summary>
    /// <param name="input">The source. The reader completes it when it finishes.</param>
    /// <param name="options">Parsing options. <see langword="null"/> uses <see cref="SecsReadOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The messages, in document order. The <em>caller</em> owns each one and must dispose it -
    /// a message decoded from binary may hold pooled buffers.
    /// </returns>
    /// <exception cref="SecsFormatException">The input is not valid for this format.</exception>
    IAsyncEnumerable<SecsMessage> ReadAllAsync(
        PipeReader input,
        SecsReadOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes hub messages out in one format.
/// </summary>
/// <remarks>
/// The other half of the Hub-and-Spoke contract. <see cref="WriteAllAsync"/> exists separately
/// from <see cref="WriteAsync"/> because multi-message framing is format-specific: SML separates
/// messages with a blank line, JSON wraps them in an array, XML needs a single root element.
/// Only the writer knows how to bracket a sequence of its own format.
/// </remarks>
public interface ISecsFormatWriter
{
    /// <summary>Gets the format this writer produces.</summary>
    SecsFormatDescriptor Descriptor { get; }

    /// <summary>Writes one message as a complete document.</summary>
    /// <param name="output">The destination. Flushed, but not completed, by this call.</param>
    /// <param name="message">The message to write.</param>
    /// <param name="options">Formatting options. <see langword="null"/> uses <see cref="SecsWriteOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the message has been written and flushed.</returns>
    ValueTask WriteAsync(
        PipeWriter output,
        SecsMessage message,
        SecsWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a sequence of messages as one document, with whatever framing the format needs.</summary>
    /// <param name="output">The destination. Flushed, but not completed, by this call.</param>
    /// <param name="messages">The messages. Enumerated once.</param>
    /// <param name="options">Formatting options. <see langword="null"/> uses <see cref="SecsWriteOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of messages written.</returns>
    ValueTask<int> WriteAllAsync(
        PipeWriter output,
        IAsyncEnumerable<SecsMessage> messages,
        SecsWriteOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when a document is not valid for the format being read.
/// </summary>
public sealed class SecsFormatException : SecsException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="formatId">The format being read, for example <c>sml</c>.</param>
    /// <param name="message">What was wrong.</param>
    public SecsFormatException(string formatId, string message)
        : base($"[{formatId}] {message}")
        => FormatId = formatId;

    /// <summary>Initialises a new instance that names the line the problem was found on.</summary>
    /// <param name="formatId">The format being read.</param>
    /// <param name="message">What was wrong.</param>
    /// <param name="lineNumber">The one-based line number.</param>
    public SecsFormatException(string formatId, string message, int lineNumber)
        : base($"[{formatId}] line {lineNumber}: {message}")
    {
        FormatId = formatId;
        LineNumber = lineNumber;
    }

    /// <summary>Initialises a new instance wrapping an underlying parser failure.</summary>
    /// <param name="formatId">The format being read.</param>
    /// <param name="message">What was wrong.</param>
    /// <param name="innerException">The parser's own exception.</param>
    public SecsFormatException(string formatId, string message, Exception? innerException)
        : base($"[{formatId}] {message}", innerException)
        => FormatId = formatId;

    /// <summary>Gets the identifier of the format being read.</summary>
    public string FormatId { get; }

    /// <summary>Gets the one-based line the problem was found on, or <c>0</c> when not known.</summary>
    public int LineNumber { get; }
}
