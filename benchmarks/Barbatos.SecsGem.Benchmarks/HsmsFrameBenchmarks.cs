// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using System.IO.Pipelines;
using Barbatos.SecsGem.Hsms;
using BenchmarkDotNet.Attributes;

namespace Barbatos.SecsGem.Benchmarks;

/// <summary>
/// The HSMS frame codec, and the question the dual-path decoder exists to answer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DecodeWhole"/> feeds each frame to the pipe in one piece, so the decoder finds it
/// complete and reads it in place out of the <c>ReadOnlySequence</c>. <see cref="DecodeChunked"/>
/// feeds the same bytes seven at a time, so every frame straddles a chunk boundary and takes the
/// gathering path instead. The gap between the two is the price of a fragmented network, and it is
/// the only honest way to state what the second path costs.
/// </para>
/// <para>
/// T8 is left at its default rather than switched off. The gathering path arms the timer and the
/// fast path never does, so that asymmetry belongs in the measurement: disabling it would flatter
/// the chunked arm by hiding work a real link pays for.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class HsmsFrameBenchmarks
{
    private const int FramesPerInvoke = 64;
    private const int ChunkSize = 7;

    private readonly ArrayBufferWriter<byte> _writer = new(256 * 1024);
    private readonly SecsGemOptions _options = new();
    private HsmsPipeEncoder _encoder = null!;
    private HsmsPipeDecoder _decoder = null!;
    private SecsMessage _message = null!;
    private MessageHeader _header;
    private byte[] _frame = [];

    /// <summary>Gets or sets the shape under measurement.</summary>
    [Params(Shape.Status, Shape.EventReport, Shape.Trace)]
    public Shape Tree { get; set; }

    /// <summary>Builds the encoder, the decoder and one encoded frame.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _encoder = new HsmsPipeEncoder(_options);
        _decoder = new HsmsPipeDecoder(_options);
        _message = SampleTrees.Message(Tree);

        // FromData builds the E37 header for an E5 message. This frame is a template for its
        // header only, so the body is detached before it goes out of scope: otherwise disposing
        // the template would dispose the message every benchmark below still needs.
        using var template = HsmsMessage.FromData(_message, sessionId: 1, systemBytes: 0x4241_5242);
        _header = template.Header;
        template.DetachData();

        var buffer = new ArrayBufferWriter<byte>();
        _encoder.Encode(buffer, _header, _message);
        _frame = buffer.WrittenSpan.ToArray();
    }

    /// <summary>Releases the message.</summary>
    [GlobalCleanup]
    public void Cleanup() => _message.Dispose();

    /// <summary>Encodes one frame: length prefix, ten-byte header and body.</summary>
    /// <returns>The byte count, so nothing is optimised away.</returns>
    [Benchmark]
    public int Encode()
    {
        _writer.ResetWrittenCount();
        return _encoder.Encode(_writer, _header, _message);
    }

    /// <summary>Decodes frames that arrive whole, which is the fast path.</summary>
    /// <returns>The frame count, so nothing is optimised away.</returns>
    [Benchmark(OperationsPerInvoke = FramesPerInvoke)]
    public Task<int> DecodeWhole() => DecodeAsync(chunkSize: 0);

    /// <summary>Decodes frames that straddle every chunk boundary, which is the gathering path.</summary>
    /// <returns>The frame count, so nothing is optimised away.</returns>
    [Benchmark(OperationsPerInvoke = FramesPerInvoke)]
    public Task<int> DecodeChunked() => DecodeAsync(ChunkSize);

    private async Task<int> DecodeAsync(int chunkSize)
    {
        var pipe = new Pipe();

        // The writer runs concurrently rather than filling the pipe first. Sixty-four Trace frames
        // are far past PauseWriterThreshold, so a fill-then-drain benchmark would deadlock on the
        // first flush that blocks, which is the same hazard the gathering path is written to avoid,
        // met from the other side.
        var filling = FillAsync(pipe.Writer, chunkSize);
        var decoded = 0;

        await foreach (var frame in _decoder.DecodeAsync(pipe.Reader, CancellationToken.None).ConfigureAwait(false))
        {
            frame.Dispose();

            if (++decoded == FramesPerInvoke)
            {
                break;
            }
        }

        await filling.ConfigureAwait(false);
        await pipe.Reader.CompleteAsync().ConfigureAwait(false);

        return decoded;
    }

    private async Task FillAsync(PipeWriter writer, int chunkSize)
    {
        for (var i = 0; i < FramesPerInvoke; i++)
        {
            if (chunkSize <= 0)
            {
                writer.Write(_frame);
                await writer.FlushAsync().ConfigureAwait(false);
                continue;
            }

            for (var offset = 0; offset < _frame.Length; offset += chunkSize)
            {
                writer.Write(_frame.AsSpan(offset, Math.Min(chunkSize, _frame.Length - offset)));
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }
}
