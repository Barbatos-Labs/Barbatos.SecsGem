// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using System.IO.Pipelines;
using Barbatos.SecsGem.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Benchmarks;

/// <summary>
/// The four text spokes, read, written and transcoded.
/// </summary>
/// <remarks>
/// <para>
/// The registry is built the way an application builds it, through
/// <c>AddSecsSml()</c> and the rest, so these numbers come from the same
/// wiring a consumer gets rather than from a reader instantiated by hand.
/// </para>
/// <para>
/// The binary hub is absent on purpose. It is not a text format, so a string round trip through it
/// is not a round trip at all - its cost is <see cref="ItemCodecBenchmarks"/> and
/// <see cref="HsmsFrameBenchmarks"/>, which measure it directly.
/// </para>
/// <para>
/// Writing goes through a reused <see cref="MemoryStream"/>, so each figure carries one
/// <c>StreamPipeWriter</c> allocation that has nothing to do with the format. It is the same
/// constant in all four rows, which is what makes the rows comparable to each other rather than
/// absolute.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class FormatBenchmarks
{
    private readonly MemoryStream _stream = new(64 * 1024);
    private ServiceProvider _provider = null!;
    private SecsTranscoder _transcoder = null!;
    private ISecsFormatReader _reader = null!;
    private ISecsFormatWriter _writer = null!;
    private SecsMessage _message = null!;
    private byte[] _document = [];
    private string _sml = string.Empty;

    /// <summary>Gets or sets the format under measurement.</summary>
    [Params(SecsFormatIds.Sml, SecsFormatIds.Json, SecsFormatIds.Xml, SecsFormatIds.Ini)]
    public string Format { get; set; } = SecsFormatIds.Sml;

    /// <summary>Builds the registry, the message and the document to read back.</summary>
    /// <exception cref="InvalidOperationException">The format has no reader or no writer registered.</exception>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _provider = new ServiceCollection()
            .AddSecsSml()
            .AddSecsJson()
            .AddSecsXml()
            .AddSecsIni()
            .BuildServiceProvider();

        _transcoder = _provider.GetRequiredService<SecsTranscoder>();

        if (!_transcoder.Registry.TryGetWriter(Format, out var writer)
            || !_transcoder.Registry.TryGetReader(Format, out var reader))
        {
            throw new InvalidOperationException($"{Format} is missing a reader or a writer.");
        }

        _writer = writer!;
        _reader = reader!;
        _message = SampleTrees.Message(Shape.EventReport);

        _document = await WriteOnceAsync().ConfigureAwait(false);
        _sml = await SmlOfAsync().ConfigureAwait(false);
    }

    /// <summary>Releases the message and the provider.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _message.Dispose();
        _stream.Dispose();
        await _provider.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Writes one message in this format.</summary>
    /// <returns>The byte count, so nothing is optimised away.</returns>
    [Benchmark]
    public async Task<long> Write()
    {
        _stream.SetLength(0);
        _stream.Position = 0;

        var output = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));
        await _writer.WriteAsync(output, _message, SecsWriteOptions.Default, CancellationToken.None).ConfigureAwait(false);
        await output.CompleteAsync().ConfigureAwait(false);

        return _stream.Length;
    }

    /// <summary>Reads the document back in this format.</summary>
    /// <returns>The message count, so nothing is optimised away.</returns>
    [Benchmark]
    public async Task<int> Read()
    {
        var input = PipeReader.Create(new ReadOnlySequence<byte>(_document));
        var count = 0;

        await foreach (var message in _reader.ReadAllAsync(input, SecsReadOptions.Default, CancellationToken.None).ConfigureAwait(false))
        {
            message.Dispose();
            count++;
        }

        return count;
    }

    /// <summary>Converts SML into this format through the hub.</summary>
    /// <returns>The character count, so nothing is optimised away.</returns>
    [Benchmark]
    public async Task<int> TranscodeFromSml()
    {
        var (text, _) = await _transcoder
            .TranscodeTextAsync(SecsFormatIds.Sml, _sml, Format)
            .ConfigureAwait(false);

        return text.Length;
    }

    private async Task<byte[]> WriteOnceAsync()
    {
        using var buffer = new MemoryStream();
        var output = PipeWriter.Create(buffer, new StreamPipeWriterOptions(leaveOpen: true));

        await _writer.WriteAsync(output, _message, SecsWriteOptions.Default, CancellationToken.None).ConfigureAwait(false);
        await output.CompleteAsync().ConfigureAwait(false);

        return buffer.ToArray();
    }

    private async Task<string> SmlOfAsync()
    {
        if (!_transcoder.Registry.TryGetWriter(SecsFormatIds.Sml, out var sml))
        {
            throw new InvalidOperationException("No SML writer is registered.");
        }

        using var buffer = new MemoryStream();
        var output = PipeWriter.Create(buffer, new StreamPipeWriterOptions(leaveOpen: true));

        await sml!.WriteAsync(output, _message, SecsWriteOptions.Default, CancellationToken.None).ConfigureAwait(false);
        await output.CompleteAsync().ConfigureAwait(false);

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
