// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Serialization.UnitTests;

/// <summary>
/// The five formats, the fixtures they all have to carry, and the plumbing to move bytes between
/// them.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through bytes rather than strings. Four of the five formats are text and one is
/// not, and <c>Encoding.UTF8.GetString</c> of a binary frame is not a round trip - so a harness that
/// worked in strings would quietly exclude the one format the others are all defined against.
/// </para>
/// <para>
/// The registry is built the way an application builds it, through
/// <c>AddSecsSerialization().AddSecsSml()</c> and the rest, so a spoke that registers a writer and
/// forgets its reader fails here rather than in somebody's fab.
/// </para>
/// </remarks>
internal static class Formats
{
    /// <summary>Every format identifier, in a stable order.</summary>
    public static readonly string[] All =
    [
        SecsFormatIds.Secs2Binary,
        SecsFormatIds.Sml,
        SecsFormatIds.Json,
        SecsFormatIds.Xml,
        SecsFormatIds.Ini,
    ];

    /// <summary>Every fixture name.</summary>
    public static readonly string[] Fixtures =
    [
        "names-and-labels",
        "every-format",
        "extremes",
        "binary",
        "nested",
        "empty",
        "text-escaping",
        "jis8",
    ];

    private static readonly ServiceProvider Provider = BuildProvider();

    /// <summary>Gets the transcoder every test shares.</summary>
    public static SecsTranscoder Transcoder { get; } = Provider.GetRequiredService<SecsTranscoder>();

    /// <summary>Gets the running test's own cancellation token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every format, as theory data.</summary>
    public static TheoryData<string> Ids
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in All)
            {
                data.Add(id);
            }

            return data;
        }
    }

    /// <summary>All twenty-five ordered pairs of formats, including each format with itself.</summary>
    /// <remarks>
    /// A format converted to itself is not a trivial case: it is the only one that exercises a
    /// reader and a writer that are supposed to agree exactly, with nothing in between to blame.
    /// </remarks>
    public static TheoryData<string, string> Pairs
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var source in All)
            {
                foreach (var target in All)
                {
                    data.Add(source, target);
                }
            }

            return data;
        }
    }

    /// <summary>Every pair paired with every fixture.</summary>
    public static TheoryData<string, string, string> PairsByFixture
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var source in All)
            {
                foreach (var target in All)
                {
                    foreach (var fixture in Fixtures)
                    {
                        data.Add(source, target, fixture);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>Gets what a format says it can carry.</summary>
    /// <param name="id">The format identifier.</param>
    /// <returns>The descriptor.</returns>
    public static SecsFormatDescriptor Descriptor(string id)
    {
        Assert.True(Transcoder.Registry.TryGetWriter(id, out var writer), $"No writer is registered for {id}.");
        return writer!.Descriptor;
    }

    /// <summary>Builds one fixture message. A fresh instance each call, because messages own their items.</summary>
    /// <param name="name">One of <see cref="Fixtures"/>.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage Create(string name) => name switch
    {
        // Names and labels, at several depths. Everything a format can lose that is not a value.
        "names-and-labels" => new SecsMessage(6, 11)
        {
            Name = "EventReportSend",
            Item = SecsItem.L(
                SecsItem.U4(1u).Named("DATAID"),
                SecsItem.U4(7001u).Named("CEID"),
                SecsItem.L(
                    SecsItem.L(
                        SecsItem.U4(100u).Named("RPTID"),
                        SecsItem.L(
                            SecsItem.A("PRESSURE").Named("VID"),
                            SecsItem.F8(1013.25d).Named("VALUE"))).Named("Report"))
                .Named("Reports")).Named("Body"),
        },

        // One item of each of the fifteen formats, in one message.
        "every-format" => new SecsMessage(99, 5)
        {
            Name = "EveryFormat",
            Item = SecsItem.L(
                SecsItem.L(),
                SecsItem.B(1, 2, 3),
                SecsItem.Boolean(true, false, true),
                SecsItem.A("ascii"),
                SecsItem.J("jis8"),
                SecsItem.I1(-1, 0, 1),
                SecsItem.I2(-2, 0, 2),
                SecsItem.I4(-4, 0, 4),
                SecsItem.I8(-8, 0, 8),
                SecsItem.U1(0, 1, 255),
                SecsItem.U2(0, 1, 65535),
                SecsItem.U4(0u, 1u, 4u),
                SecsItem.U8(0ul, 1ul, 8ul),
                SecsItem.F4(-1.5f, 0f, 1.5f),
                SecsItem.F8(-2.5d, 0d, 2.5d)),
        },

        // The values that break a format that routes numbers through a double.
        "extremes" => new SecsMessage(99, 7)
        {
            Name = "Extremes",
            Item = SecsItem.L(
                SecsItem.I8(long.MinValue, -1, 0, 1, long.MaxValue).Named("I8"),
                SecsItem.U8(0ul, 1ul, ulong.MaxValue).Named("U8"),
                SecsItem.I4(int.MinValue, int.MaxValue).Named("I4"),
                SecsItem.U4(0u, uint.MaxValue).Named("U4"),
                SecsItem.F4(float.MinValue, float.Epsilon, float.MaxValue).Named("F4"),
                SecsItem.F8(double.MinValue, double.Epsilon, double.MaxValue).Named("F8")),
        },

        // High bytes, and a payload past the pooling threshold.
        "binary" => new SecsMessage(7, 3)
        {
            Name = "Binary",
            Item = SecsItem.L(
                SecsItem.B(0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF).Named("Edges"),
                SecsItem.B(Pattern(300)).Named("Pattern")),
        },

        "nested" => new SecsMessage(99, 9)
        {
            Name = "Nested",
            Item = Nest(6),
        },

        // The degenerate lengths: a list of nothing, and value items of nothing.
        "empty" => new SecsMessage(99, 11)
        {
            Name = "Empty",
            Item = SecsItem.L(
                SecsItem.L(),
                SecsItem.A(string.Empty),
                SecsItem.B(),
                SecsItem.U4()),
        },

        // Characters each format has to escape, or has no business mangling.
        "text-escaping" => new SecsMessage(99, 13)
        {
            Name = "TextEscaping",
            Item = SecsItem.L(
                SecsItem.A("it's a 'quoted' value").Named("SmlQuotes"),
                SecsItem.A("double \"quotes\" and a backslash \\").Named("JsonEscapes"),
                SecsItem.A("angle <brackets> & an ampersand").Named("XmlEntities"),
                SecsItem.A("key = value ; comment # hash [section]").Named("IniDelimiters"),
                SecsItem.A("  leading and trailing  ").Named("Whitespace"),
                SecsItem.A("// not a comment").Named("SmlCommentMarker")),
        },

        "jis8" => new SecsMessage(99, 15)
        {
            Name = "Jis8",
            Item = SecsItem.L(
                SecsItem.J("あいう").Named("Hiragana"),
                SecsItem.J("漢字").Named("Kanji"),
                SecsItem.A("plain ascii").Named("Ascii")),
        },

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a fixture."),
    };

    /// <summary>Builds a document of several fixtures, to exercise multi-message support.</summary>
    /// <returns>The messages. Dispose them.</returns>
    public static List<SecsMessage> CreateDocument() =>
    [
        Create("names-and-labels"),
        Create("every-format"),
        Create("empty"),
    ];

    /// <summary>Writes messages in one format.</summary>
    /// <param name="id">The format identifier.</param>
    /// <param name="messages">The messages.</param>
    /// <param name="options">Formatting options.</param>
    /// <returns>The document, as bytes.</returns>
    public static async Task<byte[]> WriteAsync(
        string id,
        IEnumerable<SecsMessage> messages,
        SecsWriteOptions? options = null)
    {
        Assert.True(Transcoder.Registry.TryGetWriter(id, out var writer), $"No writer is registered for {id}.");

        using var buffer = new MemoryStream();
        var output = PipeWriter.Create(buffer, new StreamPipeWriterOptions(leaveOpen: true));

        await writer!.WriteAllAsync(output, ToAsync(messages, Token), options, Token).ConfigureAwait(false);
        await output.CompleteAsync().ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>Reads every message of a document.</summary>
    /// <param name="id">The format identifier.</param>
    /// <param name="document">The document.</param>
    /// <param name="options">Parsing options.</param>
    /// <returns>The messages. Dispose them.</returns>
    public static async Task<List<SecsMessage>> ReadAsync(
        string id,
        ReadOnlyMemory<byte> document,
        SecsReadOptions? options = null)
    {
        Assert.True(Transcoder.Registry.TryGetReader(id, out var reader), $"No reader is registered for {id}.");

        var input = PipeReader.Create(new ReadOnlySequence<byte>(document));
        var messages = new List<SecsMessage>();

        await foreach (var message in reader!.ReadAllAsync(input, options, Token).ConfigureAwait(false))
        {
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>Converts a document from one format to another.</summary>
    /// <param name="source">The format to read.</param>
    /// <param name="document">The document.</param>
    /// <param name="target">The format to write.</param>
    /// <param name="readOptions">Parsing options.</param>
    /// <param name="writeOptions">Formatting options.</param>
    /// <returns>The converted document, and what the target could not carry.</returns>
    public static async Task<(byte[] Bytes, SecsTranscodeReport Report)> TranscodeAsync(
        string source,
        ReadOnlyMemory<byte> document,
        string target,
        SecsReadOptions? readOptions = null,
        SecsWriteOptions? writeOptions = null)
    {
        var input = PipeReader.Create(new ReadOnlySequence<byte>(document));

        using var buffer = new MemoryStream();
        var output = PipeWriter.Create(buffer, new StreamPipeWriterOptions(leaveOpen: true));

        var report = await Transcoder
            .TranscodeAsync(source, input, target, output, readOptions, writeOptions, Token)
            .ConfigureAwait(false);

        await output.CompleteAsync().ConfigureAwait(false);

        return (buffer.ToArray(), report);
    }

    /// <summary>Collects every item's label, depth first, so two trees can be compared by label.</summary>
    /// <param name="root">The root item, or <see langword="null"/>.</param>
    /// <returns>One entry per item in the tree, in order.</returns>
    /// <remarks>
    /// Labels are deliberately outside <see cref="SecsItem"/> equality - they are not on the wire -
    /// so comparing them takes its own walk.
    /// </remarks>
    public static List<string?> Labels(SecsItem? root)
    {
        var labels = new List<string?>();
        Walk(root);
        return labels;

        void Walk(SecsItem? item)
        {
            if (item is null)
            {
                return;
            }

            labels.Add(item.Label);

            if (!item.IsList)
            {
                return;
            }

            foreach (var child in item.Items)
            {
                Walk(child);
            }
        }
    }

    /// <summary>Asserts that two messages carry the same S, F, W and item tree.</summary>
    /// <param name="expected">The message that went in.</param>
    /// <param name="actual">The message that came back.</param>
    /// <param name="because">What is being compared, for the failure message.</param>
    /// <remarks>
    /// Deliberately silent about <see cref="SecsMessage.Name"/> and
    /// <see cref="SecsItem.Label"/>: those survive only as far as the target format's declared
    /// capabilities allow, and the matrix asserts them against those capabilities separately.
    /// </remarks>
    public static void AssertSameTree(SecsMessage expected, SecsMessage actual, string because)
    {
        Assert.Equal(expected.S, actual.S);
        Assert.Equal(expected.F, actual.F);
        Assert.Equal(expected.ReplyExpected, actual.ReplyExpected);

        if (!Equals(expected.Item, actual.Item))
        {
            Assert.Fail($"The item tree did not survive {because}.{Environment.NewLine}"
                + $"  expected: {Render(expected.Item)}{Environment.NewLine}"
                + $"  actual:   {Render(actual.Item)}");
        }
    }

    /// <summary>Renders an item tree one line deep, for a readable assertion failure.</summary>
    public static string Render(SecsItem? item)
    {
        if (item is null)
        {
            return "(no body)";
        }

        if (!item.IsList)
        {
            return item.Format.IsText()
                ? $"<{item.Format.ToSmlToken()} [{item.Count}] '{item.GetString()}'>"
                : $"<{item.Format.ToSmlToken()} [{item.Count}]>";
        }

        var children = new List<string>(item.Count);
        foreach (var child in item.Items)
        {
            children.Add(Render(child));
        }

        return $"<L [{item.Count}] {string.Join(' ', children)}>";
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services
            .AddSecsSerialization()
            .AddSecsSml()
            .AddSecsJson()
            .AddSecsXml()
            .AddSecsIni();

        return services.BuildServiceProvider();
    }

    private static async IAsyncEnumerable<SecsMessage> ToAsync(
        IEnumerable<SecsMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 37 % 256);
        }

        return bytes;
    }

    private static SecsItem Nest(int depth)
    {
        var item = SecsItem.L(SecsItem.A("bottom"), SecsItem.I4(depth));
        for (var level = depth; level > 0; level--)
        {
            item = SecsItem.L(SecsItem.U1((byte)level).Named($"Level{level}"), item);
        }

        return item;
    }
}
