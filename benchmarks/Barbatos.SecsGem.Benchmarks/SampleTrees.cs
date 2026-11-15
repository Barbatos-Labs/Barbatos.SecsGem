// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Benchmarks;

/// <summary>
/// The three message shapes every codec benchmark is measured against.
/// </summary>
/// <remarks>
/// They are not small, medium and large for the sake of a spread. Each one is a thing a fab
/// actually puts on the wire, and each one lands on a different side of a decision inside the
/// codec, which is the only reason a third shape earns its place.
/// </remarks>
public enum Shape
{
    /// <summary>An S1F2 on-line data reply: two short ASCII items.</summary>
    /// <remarks>
    /// The traffic that never stops. A host polls this to find out whether the equipment is still
    /// answering, so its cost is paid thousands of times for every recipe that runs once.
    /// </remarks>
    Status,

    /// <summary>An S6F11 event report: three reports of four variables each.</summary>
    /// <remarks>
    /// The bread and butter of GEM, and the shape worth optimising for. Deeply nested, every item
    /// small, so the measurement is dominated by tree walking rather than by copying bytes.
    /// </remarks>
    EventReport,

    /// <summary>A trace report carrying a 64 KiB binary blob and 512 doubles.</summary>
    /// <remarks>
    /// Deliberately past <see cref="SecsCodecOptions.PooledPayloadThreshold"/>, which defaults to
    /// 1024 bytes. Below that threshold the codec allocates; above it the payload comes from
    /// <c>ArrayPool</c>. A suite that never crossed the line would report only half of the design.
    /// </remarks>
    Trace,
}

/// <summary>Builds the item trees and messages the benchmarks reuse.</summary>
internal static class SampleTrees
{
    /// <summary>Builds the item tree for a shape.</summary>
    public static SecsItem Build(Shape shape) => shape switch
    {
        Shape.Status => Status(),
        Shape.EventReport => EventReport(reports: 3, valuesPerReport: 4),
        Shape.Trace => Trace(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>Builds a whole message for a shape, with the stream and function that carries it.</summary>
    public static SecsMessage Message(Shape shape) => shape switch
    {
        Shape.Status => new SecsMessage(1, 2, replyExpected: false)
        {
            Name = "OnLineData",
            Item = Build(shape),
        },
        _ => new SecsMessage(6, 11, replyExpected: true)
        {
            Name = "EventReportSend",
            Item = Build(shape),
        },
    };

    private static SecsItem Status()
        => SecsItem.L(
            SecsItem.A("BRBTOS", 6),
            SecsItem.A("0.1.0"));

    private static SecsItem EventReport(int reports, int valuesPerReport)
    {
        var bodies = new SecsItem[reports];

        for (var r = 0; r < reports; r++)
        {
            var values = new SecsItem[valuesPerReport];

            for (var v = 0; v < valuesPerReport; v++)
            {
                values[v] = SecsItem.L(
                    SecsItem.U4((uint)(7_000 + (r * 100) + v)),
                    SecsItem.A($"VID-{r}-{v}"));
            }

            bodies[r] = SecsItem.L(
                SecsItem.U4((uint)(5_000 + r)),
                SecsItem.L(values));
        }

        return SecsItem.L(
            SecsItem.U4(1u),
            SecsItem.U4(7_001u),
            SecsItem.L(bodies));
    }

    private static SecsItem Trace()
        => SecsItem.L(
            SecsItem.U4(1u),
            SecsItem.U4(9_001u),
            SecsItem.B(Pattern(64 * 1024)),
            SecsItem.F8(Ramp(512)),
            EventReport(reports: 64, valuesPerReport: 16));

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];

        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i & 0xFF);
        }

        return bytes;
    }

    private static double[] Ramp(int length)
    {
        var values = new double[length];

        for (var i = 0; i < length; i++)
        {
            values[i] = i * 0.25d;
        }

        return values;
    }
}
