// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using System.IO.Pipelines;

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// The message fixtures and the pipe plumbing every codec test shares.
/// </summary>
internal static class Frames
{
    /// <summary>Every fixture name.</summary>
    public static readonly string[] All =
    [
        "s1f1",
        "s1f2",
        "s2f41",
        "s6f11",
        "binary-2k",
        "deep",
        "empty-list",
        "every-format",
        "select.req",
        "linktest.rsp",
        "reject.req",
    ];

    /// <summary>The fixture names, as theory data.</summary>
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in All)
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>Builds the options the codec tests run against.</summary>
    /// <param name="t8">T8. Defaults to 5 seconds, which only ever elapses on a fake clock here.</param>
    /// <param name="maxMessageLength">The length guard. Defaults to 16 MiB.</param>
    /// <returns>The options.</returns>
    public static SecsGemOptions Options(TimeSpan? t8 = null, int? maxMessageLength = null) => new()
    {
        DeviceId = 7,
        T8 = t8 ?? TimeSpan.FromSeconds(5),
        MaxMessageLength = maxMessageLength ?? (16 * 1024 * 1024),
    };

    /// <summary>Builds one fixture frame. A fresh instance each call, because frames own their body.</summary>
    /// <param name="name">One of <see cref="Names"/>.</param>
    /// <returns>The frame. Dispose it.</returns>
    public static HsmsMessage Create(string name) => name switch
    {
        // A primary with no body at all - the header-only encode path.
        "s1f1" => HsmsMessage.FromData(new SecsMessage(1, 1), sessionId: 7, systemBytes: 0x1001),

        // Are You There / On Line Data: two ASCII items under a list.
        "s1f2" => HsmsMessage.FromData(
            new SecsMessage(1, 2, replyExpected: false)
            {
                Item = SecsItem.L(SecsItem.A("BARBATOS"), SecsItem.A("0.1.0")),
            },
            sessionId: 7,
            systemBytes: 0x1002),

        // Host Command Send: a list holding a list, with mixed integer widths.
        "s2f41" => HsmsMessage.FromData(
            new SecsMessage(2, 41)
            {
                Item = SecsItem.L(
                    SecsItem.A("START"),
                    SecsItem.L(
                        SecsItem.L(SecsItem.A("LOTID"), SecsItem.A("LOT-00042")),
                        SecsItem.L(SecsItem.A("PPID"), SecsItem.A("RECIPE-7")))),
            },
            sessionId: 7,
            systemBytes: 0x1003),

        // Event Report Send: the shape a real GEM link spends its day carrying.
        "s6f11" => HsmsMessage.FromData(
            new SecsMessage(6, 11)
            {
                Item = SecsItem.L(
                    SecsItem.U4(1u),
                    SecsItem.U4(7001u),
                    SecsItem.L(
                        SecsItem.L(
                            SecsItem.U4(100u),
                            SecsItem.L(
                                SecsItem.A("PRESSURE"),
                                SecsItem.F8(1013.25d),
                                SecsItem.Boolean(true))),
                        SecsItem.L(
                            SecsItem.U4(101u),
                            SecsItem.L(
                                SecsItem.A("TEMPERATURE"),
                                SecsItem.F4(23.5f),
                                SecsItem.Boolean(false))))),
            },
            sessionId: 7,
            systemBytes: 0x1004),

        // Past PooledPayloadThreshold, so the decoded item is pool-backed and must be disposed.
        "binary-2k" => HsmsMessage.FromData(
            new SecsMessage(7, 3) { Item = SecsItem.B(Pattern(2000)) },
            sessionId: 7,
            systemBytes: 0x1005),

        // Nesting, to prove depth survives a split as well as width does.
        "deep" => HsmsMessage.FromData(
            new SecsMessage(99, 1) { Item = Nest(8) },
            sessionId: 7,
            systemBytes: 0x1006),

        // The empty list and the empty value item: the degenerate lengths.
        "empty-list" => HsmsMessage.FromData(
            new SecsMessage(99, 3)
            {
                Item = SecsItem.L(SecsItem.L(), SecsItem.A(string.Empty), SecsItem.B()),
            },
            sessionId: 7,
            systemBytes: 0x1007),

        // One of each of the fifteen formats, in one message.
        "every-format" => HsmsMessage.FromData(
            new SecsMessage(99, 5)
            {
                Item = SecsItem.L(
                    SecsItem.L(),
                    SecsItem.B(1, 2, 3),
                    SecsItem.Boolean(true, false, true),
                    SecsItem.A("ascii"),
                    SecsItem.J("jis8"),
                    SecsItem.I1(-1, 0, 1),
                    SecsItem.I2(short.MinValue, 0, short.MaxValue),
                    SecsItem.I4(int.MinValue, 0, int.MaxValue),
                    SecsItem.I8(long.MinValue, 0, long.MaxValue),
                    SecsItem.U1(0, 128, 255),
                    SecsItem.U2(0, 32768, ushort.MaxValue),
                    SecsItem.U4(0u, uint.MaxValue),
                    SecsItem.U8(0ul, ulong.MaxValue),
                    SecsItem.F4(float.MinValue, 0f, float.MaxValue),
                    SecsItem.F8(double.MinValue, 0d, double.MaxValue)),
            },
            sessionId: 7,
            systemBytes: 0x1008),

        "select.req" => HsmsMessage.Control(MessageType.SelectRequest, systemBytes: 0x2001),

        "linktest.rsp" => HsmsMessage.Control(MessageType.LinkTestResponse, systemBytes: 0x2002),

        "reject.req" => HsmsMessage.Control(
            MessageType.RejectRequest,
            systemBytes: 0x2003,
            status: (byte)RejectReason.PtypeNotSupported),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a fixture."),
    };

    /// <summary>Encodes a frame to its wire bytes.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The complete frame, length prefix included.</returns>
    public static byte[] Encode(HsmsMessage frame, SecsGemOptions? options = null)
    {
        var encoder = new HsmsPipeEncoder(options ?? Options());
        var buffer = new ArrayBufferWriter<byte>();
        encoder.Encode(buffer, frame.Header, frame.Data);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Gets the running test's own cancellation token.
    /// </summary>
    /// <remarks>
    /// Passed to every framework call that accepts one, so that a test which stops making
    /// progress is cancelled by the runner instead of hanging the whole run.
    /// </remarks>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Reads exactly one frame from a reader.</summary>
    /// <param name="decoder">The decoder.</param>
    /// <param name="reader">The input.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The frame. Dispose it.</returns>
    public static async Task<HsmsMessage> DecodeOneAsync(
        HsmsPipeDecoder decoder,
        PipeReader reader,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in decoder.DecodeAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            return frame;
        }

        throw new InvalidOperationException("The link ended before a frame arrived.");
    }

    /// <summary>Reads every frame a byte buffer holds, written in one go.</summary>
    /// <param name="decoder">The decoder.</param>
    /// <param name="bytes">The bytes to feed.</param>
    /// <returns>The frames, in order. Dispose them.</returns>
    public static async Task<List<HsmsMessage>> DecodeAllAsync(HsmsPipeDecoder decoder, ReadOnlyMemory<byte> bytes)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes, Token).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        var frames = new List<HsmsMessage>();
        await foreach (var frame in decoder.DecodeAsync(pipe.Reader, Token).ConfigureAwait(false))
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>
    /// Asserts that two frames carry the same header fields and the same item tree.
    /// </summary>
    /// <param name="expected">The frame that was sent.</param>
    /// <param name="actual">The frame that came back.</param>
    /// <remarks>
    /// <see cref="SecsMessage.Name"/> is deliberately not compared: it is not a wire field, and a
    /// separate test pins the fact that the binary codec drops it.
    /// </remarks>
    public static void AssertSameFrame(HsmsMessage expected, HsmsMessage actual)
    {
        Assert.Equal(expected.Header.SessionId, actual.Header.SessionId);
        Assert.Equal(expected.Header.DeviceId, actual.Header.DeviceId);
        Assert.Equal(expected.Header.Stream, actual.Header.Stream);
        Assert.Equal(expected.Header.Function, actual.Header.Function);
        Assert.Equal(expected.Header.ReplyExpected, actual.Header.ReplyExpected);
        Assert.Equal(expected.Header.PType, actual.Header.PType);
        Assert.Equal(expected.Header.MessageType, actual.Header.MessageType);
        Assert.Equal(expected.Header.SystemBytes, actual.Header.SystemBytes);

        if (expected.IsControl)
        {
            Assert.Null(actual.Data);
            return;
        }

        Assert.NotNull(actual.Data);
        Assert.Equal(expected.Data!.S, actual.Data!.S);
        Assert.Equal(expected.Data.F, actual.Data.F);
        Assert.Equal(expected.Data.ReplyExpected, actual.Data.ReplyExpected);
        Assert.Equal(expected.Data.Item, actual.Data.Item);
    }

    /// <summary>
    /// Waits for a condition the decode loop will reach, failing loudly if it never does.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="what">What is being waited for, for the failure message.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    /// <remarks>
    /// This synchronises on task scheduling, not on protocol time. Every HSMS timer in these
    /// tests is driven by <c>FakeTimeProvider</c>, so nothing here waits out a protocol interval -
    /// it only waits for the decoder's own loop to get as far as the state under test, which it
    /// reaches in microseconds. The ceiling exists so a decoder that never gets there fails the
    /// test instead of hanging the run.
    /// </remarks>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 5_000; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(1, Token).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>Writes a header out as the ten bytes an S9 message reports it as.</summary>
    /// <param name="header">The header.</param>
    /// <returns>The ten bytes.</returns>
    public static byte[] HeaderBytes(MessageHeader header)
    {
        var bytes = new byte[MessageHeader.Length];
        header.WriteTo(bytes);
        return bytes;
    }

    /// <summary>Reads the MHEAD or SHEAD out of a received Stream 9 report.</summary>
    /// <param name="report">The S9 frame.</param>
    /// <returns>The ten bytes it names, or empty when it has no body.</returns>
    public static byte[] ReportedHeader(HsmsMessage report)
        => report.Data?.Item?.GetSpan<byte>().ToArray() ?? [];

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 31 % 251);
        }

        return bytes;
    }

    private static SecsItem Nest(int depth)
    {
        var item = SecsItem.L(SecsItem.A("bottom"), SecsItem.I4(depth));
        for (var i = 0; i < depth; i++)
        {
            item = SecsItem.L(SecsItem.U1((byte)i), item);
        }

        return item;
    }
}
