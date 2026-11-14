// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using System.Text;

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// Something a conversion could not carry across.
/// </summary>
/// <param name="Code">A stable code, for example <c>ITEM_LABELS_DROPPED</c>.</param>
/// <param name="Message">What was lost, in one sentence a human can act on.</param>
public sealed record SecsTranscodeWarning(string Code, string Message)
{
    /// <summary>Returns the warning as <c>CODE: message</c>.</summary>
    /// <returns>The formatted warning.</returns>
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>
/// What a conversion did, and what it could not keep.
/// </summary>
/// <param name="SourceFormat">The format that was read.</param>
/// <param name="TargetFormat">The format that was written.</param>
/// <param name="MessageCount">How many messages were converted.</param>
/// <param name="Warnings">Everything the target format could not represent.</param>
public sealed record SecsTranscodeReport(
    SecsFormatDescriptor SourceFormat,
    SecsFormatDescriptor TargetFormat,
    int MessageCount,
    IReadOnlyList<SecsTranscodeWarning> Warnings)
{
    /// <summary>Gets a value indicating whether nothing was lost.</summary>
    public bool IsLossless => Warnings.Count == 0;

    /// <summary>Returns a one-line summary.</summary>
    /// <returns>For example <c>sml -> json: 12 message(s), lossless</c>.</returns>
    public override string ToString()
        => $"{SourceFormat.Id} -> {TargetFormat.Id}: {MessageCount} message(s), " +
           (IsLossless ? "lossless" : $"{Warnings.Count} warning(s)");
}

/// <summary>
/// Converts between any two registered formats by routing through the hub.
/// </summary>
/// <remarks>
/// <para>
/// The spoke count is what makes this worth having: five formats would need twenty hand-written
/// converters, and a sixth would need ten more. Routing everything through
/// <see cref="SecsMessage"/> means <c>N</c> readers and <c>N</c> writers give <c>N x (N-1)</c>
/// conversions for free, and a new format costs two classes.
/// </para>
/// <para>
/// Conversion streams: messages are read, written and disposed one at a time, so converting a
/// 500 MB capture file needs memory for one message, not for the file.
/// </para>
/// </remarks>
/// <param name="registry">The registered formats.</param>
public sealed class SecsTranscoder(ISecsFormatRegistry registry)
{
    private readonly ISecsFormatRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>Gets the registry this transcoder resolves formats through.</summary>
    public ISecsFormatRegistry Registry => _registry;

    /// <summary>Converts a stream of messages from one format to another.</summary>
    /// <param name="sourceFormatId">The format to read, for example <see cref="SecsFormatIds.Sml"/>.</param>
    /// <param name="input">The source.</param>
    /// <param name="targetFormatId">The format to write, for example <see cref="SecsFormatIds.Json"/>.</param>
    /// <param name="output">The destination. Flushed, but not completed, by this call.</param>
    /// <param name="readOptions">Parsing options.</param>
    /// <param name="writeOptions">Formatting options.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <returns>What was converted, and anything the target format could not carry.</returns>
    /// <exception cref="SecsFormatException">Either format has no reader or no writer registered.</exception>
    public async ValueTask<SecsTranscodeReport> TranscodeAsync(
        string sourceFormatId,
        PipeReader input,
        string targetFormatId,
        PipeWriter output,
        SecsReadOptions? readOptions = null,
        SecsWriteOptions? writeOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (!_registry.TryGetReader(sourceFormatId, out var reader))
        {
            throw new SecsFormatException(sourceFormatId, $"No reader is registered. Registered formats: {KnownFormats()}.");
        }

        if (!_registry.TryGetWriter(targetFormatId, out var writer))
        {
            throw new SecsFormatException(targetFormatId, $"No writer is registered. Registered formats: {KnownFormats()}.");
        }

        var warnings = Compare(reader.Descriptor, writer.Descriptor);

        var count = await writer
            .WriteAllAsync(output, reader.ReadAllAsync(input, readOptions, cancellationToken), writeOptions, cancellationToken)
            .ConfigureAwait(false);

        return new SecsTranscodeReport(reader.Descriptor, writer.Descriptor, count, warnings);
    }

    /// <summary>Converts text in one format to text in another.</summary>
    /// <param name="sourceFormatId">The format to read.</param>
    /// <param name="source">The document.</param>
    /// <param name="targetFormatId">The format to write.</param>
    /// <param name="readOptions">Parsing options.</param>
    /// <param name="writeOptions">Formatting options.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <returns>The converted document and the report.</returns>
    /// <remarks>
    /// The convenient overload, for tests, tools and small documents. It materialises both sides
    /// in memory; use <see cref="TranscodeAsync(string, PipeReader, string, PipeWriter, SecsReadOptions, SecsWriteOptions, CancellationToken)"/>
    /// for anything large.
    /// </remarks>
    public async ValueTask<(string Text, SecsTranscodeReport Report)> TranscodeTextAsync(
        string sourceFormatId,
        string source,
        string targetFormatId,
        SecsReadOptions? readOptions = null,
        SecsWriteOptions? writeOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var input = PipeReader.Create(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(source)));
        using var buffer = new MemoryStream();
        var output = PipeWriter.Create(buffer, new StreamPipeWriterOptions(leaveOpen: true));

        var report = await TranscodeAsync(
            sourceFormatId,
            input,
            targetFormatId,
            output,
            readOptions,
            writeOptions,
            cancellationToken).ConfigureAwait(false);

        await output.CompleteAsync().ConfigureAwait(false);
        return (Encoding.UTF8.GetString(buffer.ToArray()), report);
    }

    /// <summary>Converts one file to another, picking both formats from the file extensions.</summary>
    /// <param name="sourcePath">The file to read.</param>
    /// <param name="targetPath">The file to write. Overwritten if it exists.</param>
    /// <param name="readOptions">Parsing options.</param>
    /// <param name="writeOptions">Formatting options.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <returns>What was converted, and anything the target format could not carry.</returns>
    /// <exception cref="SecsFormatException">Either extension belongs to no registered format.</exception>
    public async ValueTask<SecsTranscodeReport> TranscodeFileAsync(
        string sourcePath,
        string targetPath,
        SecsReadOptions? readOptions = null,
        SecsWriteOptions? writeOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (!_registry.TryResolveFormatId(sourcePath, out var sourceFormatId))
        {
            throw new SecsFormatException(Path.GetExtension(sourcePath), $"No registered format claims this extension. Registered formats: {KnownFormats()}.");
        }

        if (!_registry.TryResolveFormatId(targetPath, out var targetFormatId))
        {
            throw new SecsFormatException(Path.GetExtension(targetPath), $"No registered format claims this extension. Registered formats: {KnownFormats()}.");
        }

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, useAsync: true);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0, useAsync: true);

        var output = PipeWriter.Create(target);
        var report = await TranscodeAsync(
            sourceFormatId,
            PipeReader.Create(source),
            targetFormatId,
            output,
            readOptions,
            writeOptions,
            cancellationToken).ConfigureAwait(false);

        await output.CompleteAsync().ConfigureAwait(false);
        return report;
    }

    /// <summary>Reads every message of a document held in memory.</summary>
    /// <param name="formatId">The format to read.</param>
    /// <param name="source">The document.</param>
    /// <param name="options">Parsing options.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The messages. The caller disposes them.</returns>
    /// <exception cref="SecsFormatException">No reader is registered for <paramref name="formatId"/>.</exception>
    public async ValueTask<List<SecsMessage>> ReadTextAsync(
        string formatId,
        string source,
        SecsReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!_registry.TryGetReader(formatId, out var reader))
        {
            throw new SecsFormatException(formatId, $"No reader is registered. Registered formats: {KnownFormats()}.");
        }

        var input = PipeReader.Create(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(source)));
        var messages = new List<SecsMessage>();
        await foreach (var message in reader.ReadAllAsync(input, options, cancellationToken).ConfigureAwait(false))
        {
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>Writes messages to a string in one format.</summary>
    /// <param name="formatId">The format to write.</param>
    /// <param name="messages">The messages.</param>
    /// <param name="options">Formatting options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The document.</returns>
    /// <exception cref="SecsFormatException">No writer is registered for <paramref name="formatId"/>.</exception>
    public async ValueTask<string> WriteTextAsync(
        string formatId,
        IEnumerable<SecsMessage> messages,
        SecsWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (!_registry.TryGetWriter(formatId, out var writer))
        {
            throw new SecsFormatException(formatId, $"No writer is registered. Registered formats: {KnownFormats()}.");
        }

        using var buffer = new MemoryStream();
        var output = PipeWriter.Create(buffer, new StreamPipeWriterOptions(leaveOpen: true));
        await writer.WriteAllAsync(output, ToAsync(messages, cancellationToken), options, cancellationToken).ConfigureAwait(false);
        await output.CompleteAsync().ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.ToArray());

        static async IAsyncEnumerable<SecsMessage> ToAsync(
            IEnumerable<SecsMessage> source,
            [EnumeratorCancellation] CancellationToken token)
        {
            foreach (var message in source)
            {
                token.ThrowIfCancellationRequested();
                yield return message;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lists what <paramref name="target"/> cannot carry of what <paramref name="source"/> can.
    /// </summary>
    /// <param name="source">The format being read.</param>
    /// <param name="target">The format being written.</param>
    /// <returns>One warning per capability the target lacks.</returns>
    private static IReadOnlyList<SecsTranscodeWarning> Compare(SecsFormatDescriptor source, SecsFormatDescriptor target)
    {
        var lost = source.Capabilities & ~target.Capabilities;
        if (lost == SecsFormatCapabilities.None)
        {
            return [];
        }

        var warnings = new List<SecsTranscodeWarning>(capacity: 4);

        if (lost.HasFlag(SecsFormatCapabilities.MultipleMessages))
        {
            warnings.Add(new SecsTranscodeWarning(
                "MULTIPLE_MESSAGES_UNSUPPORTED",
                $"{target.DisplayName} holds one message per document; only the first was written."));
        }

        if (lost.HasFlag(SecsFormatCapabilities.MessageNames))
        {
            warnings.Add(new SecsTranscodeWarning(
                "MESSAGE_NAMES_DROPPED",
                $"{target.DisplayName} cannot carry a message name."));
        }

        if (lost.HasFlag(SecsFormatCapabilities.ItemLabels))
        {
            warnings.Add(new SecsTranscodeWarning(
                "ITEM_LABELS_DROPPED",
                $"{target.DisplayName} cannot carry item labels."));
        }

        if (lost.HasFlag(SecsFormatCapabilities.Comments))
        {
            warnings.Add(new SecsTranscodeWarning(
                "COMMENTS_DROPPED",
                $"{target.DisplayName} has no comment syntax."));
        }

        if (lost.HasFlag(SecsFormatCapabilities.ExactIntegers))
        {
            warnings.Add(new SecsTranscodeWarning(
                "LARGE_INTEGERS_INEXACT",
                $"{target.DisplayName} may round 64-bit integers above 2^53. Set {nameof(SecsWriteOptions.WriteLargeIntegersAsStrings)} to keep them exact."));
        }

        if (lost.HasFlag(SecsFormatCapabilities.BinaryPayload))
        {
            warnings.Add(new SecsTranscodeWarning(
                "BINARY_PAYLOAD_LOSSY",
                $"{target.DisplayName} cannot represent arbitrary bytes without escaping."));
        }

        return warnings;
    }

    private string KnownFormats() => string.Join(", ", _registry.Formats.Select(static f => f.Id).Order(StringComparer.Ordinal));
}
