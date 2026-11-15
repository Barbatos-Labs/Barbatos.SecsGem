// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using BenchmarkDotNet.Attributes;

namespace Barbatos.SecsGem.Benchmarks;

/// <summary>
/// The SECS-II item codec: a tree to bytes and back.
/// </summary>
/// <remarks>
/// <para>
/// This is the hub, so it is the one measurement every other number in the repository rests on. A
/// format spoke that looks slow is only slow relative to this.
/// </para>
/// <para>
/// Decode includes disposing the tree it produced, and that is deliberate rather than sloppy: a
/// decoded item above the pooled threshold holds rented memory, so a figure that left the return
/// out would be measuring half of a transaction and reporting it as the whole.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ItemCodecBenchmarks
{
    private readonly ArrayBufferWriter<byte> _writer = new(256 * 1024);
    private SecsItem _item = null!;
    private byte[] _encoded = [];

    /// <summary>Gets or sets the shape under measurement.</summary>
    [Params(Shape.Status, Shape.EventReport, Shape.Trace)]
    public Shape Tree { get; set; }

    /// <summary>Builds the tree and its encoded form once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _item = SampleTrees.Build(Tree);

        var buffer = new ArrayBufferWriter<byte>();
        _item.EncodeTo(buffer);
        _encoded = buffer.WrittenSpan.ToArray();
    }

    /// <summary>Releases the tree.</summary>
    [GlobalCleanup]
    public void Cleanup() => _item.Dispose();

    /// <summary>Encodes the tree into a writer that is reused across iterations.</summary>
    /// <returns>The byte count, so nothing is optimised away.</returns>
    [Benchmark]
    public int Encode()
    {
        _writer.ResetWrittenCount();
        _item.EncodeTo(_writer);
        return _writer.WrittenCount;
    }

    /// <summary>Decodes the tree out of a flat buffer and releases it again.</summary>
    /// <returns>The root item count, so nothing is optimised away.</returns>
    [Benchmark]
    public int Decode()
    {
        using var item = SecsItem.Decode(_encoded);
        return item.Count;
    }
}
