// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// The four nested bodies of limits monitoring, in one place.
/// </summary>
/// <remarks>
/// <para>
/// S2F45 is the deepest body in this layer - a list of variables, each a list of limits, each a
/// pair of bounds - and S2F46 is the same shape again with acknowledgements where the bounds were.
/// Both ends read and write them, so they live here rather than twice: a structure defined in two
/// places agrees until somebody fixes one of them.
/// </para>
/// <para>
/// It is also the place to check against a copy of SEMI E5, because three of the four message rows
/// are representative entries in this repository's catalogue rather than definitions. The shapes
/// below are E5's as far as the catalogue's item lists corroborate them, which is item for item
/// and in order - see <see cref="GemVariableLimit"/> for the whole of that caveat.
/// </para>
/// <code>
/// S2F45  L,2 { DATAID, L,a { L,2 { VID, L,b { L,2 { LIMITID, L,2 { UPPERDB, LOWERDB } } } } } }
/// S2F46  L,2 { VLAACK, L,a { L,2 { VID, L,2 { LVACK, L,b { L,2 { LIMITID, LIMITACK } } } } } }
/// S2F47  L,a { VID }
/// S2F48  L,a { L,2 { VID, L,2 { L,3 { UNITS, LIMITMIN, LIMITMAX },
///                              L,b { L,2 { LIMITID, L,2 { UPPERDB, LOWERDB } } } } } }
/// </code>
/// </remarks>
internal static class GemLimitCodec
{
    /// <summary>One variable in an S2F45, with the bands that could not be read kept as nulls.</summary>
    /// <param name="VariableId">VID.</param>
    /// <param name="Limits">
    /// Each LIMITID and its band, or <see langword="null"/> where the bounds were readable and not
    /// a band - an upper below its lower, or something that is not a number at all.
    /// </param>
    /// <remarks>
    /// A null rather than a dropped entry, and rather than a <see cref="GemVariableLimit"/> with
    /// made-up bounds: E5 gives S2F46 a LIMITACK per band so that a host which sent twenty learns
    /// which one it got wrong, and that answer is impossible if the reader has already normalised
    /// the bad one away. <see cref="GemVariableLimit"/> cannot hold an invalid band, which is why
    /// this type exists rather than the public one.
    /// </remarks>
    internal sealed record GemLimitRequestEntry(uint VariableId, List<(uint Id, GemVariableLimit? Band)> Limits);

    /// <summary>Reads S2F45's variable list.</summary>
    /// <param name="body">The inbound body.</param>
    /// <param name="entries">What the host asked for.</param>
    /// <returns><see langword="false"/> when the body is not the shape above at all.</returns>
    internal static bool TryReadDefinitions(SecsItem? body, out List<GemLimitRequestEntry> entries)
    {
        entries = [];

        if (body is not { IsList: true, Count: 2 } || !body[1].IsList)
        {
            return false;
        }

        foreach (var entry in body[1].Items)
        {
            if (entry is not { IsList: true, Count: 2 }
                || !GemItems.TryReadId(entry[0], out var variableId)
                || variableId > uint.MaxValue
                || !entry[1].IsList)
            {
                return false;
            }

            List<(uint Id, GemVariableLimit? Band)> limits = [];

            foreach (var limit in entry[1].Items)
            {
                if (limit is not { IsList: true, Count: 2 }
                    || !GemItems.TryReadId(limit[0], out var limitId)
                    || limitId > uint.MaxValue
                    || limit[1] is not { IsList: true, Count: 2 })
                {
                    return false;
                }

                var upper = ReadNumber(limit[1][0]);
                var lower = ReadNumber(limit[1][1]);

                limits.Add(double.IsNaN(upper) || double.IsNaN(lower) || upper < lower
                    ? ((uint)limitId, null)
                    : ((uint)limitId, new GemVariableLimit((uint)limitId, lower, upper)));
            }

            entries.Add(new GemLimitRequestEntry((uint)variableId, limits));
        }

        return true;
    }

    /// <summary>Writes S2F45's body.</summary>
    /// <param name="dataId">DATAID.</param>
    /// <param name="definitions">The limits to ask for.</param>
    /// <returns>The body item.</returns>
    internal static SecsItem WriteDefinitions(SecsItem dataId, IReadOnlyList<GemVariableLimitDefinition> definitions)
    {
        var variables = new SecsItem[definitions.Count];

        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            var limits = new SecsItem[definition.Limits.Count];

            for (var j = 0; j < limits.Length; j++)
            {
                var limit = definition.Limits[j];

                // Upper before lower, which is the order E5 lists UPPERDB and LOWERDB in and the
                // one thing about this body it would be easiest to get backwards.
                limits[j] = SecsItem.L(
                    SecsItem.U4(limit.Id).Named("LIMITID"),
                    SecsItem.L(
                        SecsItem.F8(limit.Upper).Named("UPPERDB"),
                        SecsItem.F8(limit.Lower).Named("LOWERDB")));
            }

            variables[i] = SecsItem.L(
                GemItems.IdItem(definition.VariableId).Named("VID"),
                SecsItem.L(limits));
        }

        return SecsItem.L(dataId, SecsItem.L(variables));
    }

    /// <summary>Writes S2F46's body.</summary>
    /// <param name="accepted">VLAACK.</param>
    /// <param name="results">What happened to each variable.</param>
    /// <returns>The body item.</returns>
    internal static SecsItem WriteResults(bool accepted, IReadOnlyList<GemVariableLimitResult> results)
    {
        var variables = new SecsItem[results.Count];

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var limits = new SecsItem[result.Limits.Count];

            for (var j = 0; j < limits.Length; j++)
            {
                limits[j] = SecsItem.L(
                    SecsItem.U4(result.Limits[j].Id).Named("LIMITID"),
                    SecsItem.B((byte)result.Limits[j].Acknowledgement).Named("LIMITACK"));
            }

            variables[i] = SecsItem.L(
                GemItems.IdItem(result.VariableId).Named("VID"),
                SecsItem.L(
                    SecsItem.B(result.Accepted ? (byte)0 : (byte)1).Named("LVACK"),
                    SecsItem.L(limits)));
        }

        return SecsItem.L(
            SecsItem.B(accepted ? (byte)0 : (byte)1).Named("VLAACK"),
            SecsItem.L(variables));
    }

    /// <summary>Reads S2F46's body.</summary>
    /// <param name="body">The reply body.</param>
    /// <returns>What happened, or <see langword="null"/> when the body is not the shape above.</returns>
    internal static GemVariableLimitReply? TryReadResults(SecsItem? body)
    {
        if (body is not { IsList: true, Count: 2 }
            || !GemItems.TryReadCode(body[0], out var vlaack)
            || !body[1].IsList)
        {
            return null;
        }

        List<GemVariableLimitResult> results = [];

        foreach (var entry in body[1].Items)
        {
            if (entry is not { IsList: true, Count: 2 }
                || !GemItems.TryReadId(entry[0], out var variableId)
                || entry[1] is not { IsList: true, Count: 2 }
                || !GemItems.TryReadCode(entry[1][0], out var lvack)
                || !entry[1][1].IsList)
            {
                continue;
            }

            List<(uint Id, GemVariableLimitAcknowledgement Acknowledgement)> limits = [];

            foreach (var limit in entry[1][1].Items)
            {
                if (limit is { IsList: true, Count: 2 }
                    && GemItems.TryReadId(limit[0], out var limitId)
                    && GemItems.TryReadCode(limit[1], out var limitack))
                {
                    limits.Add(((uint)limitId, (GemVariableLimitAcknowledgement)limitack));
                }
            }

            results.Add(new GemVariableLimitResult((uint)variableId, lvack == 0, limits));
        }

        return new GemVariableLimitReply(vlaack == 0, results);
    }

    /// <summary>Writes one S2F48 entry.</summary>
    /// <param name="variableId">VID.</param>
    /// <param name="units">UNITS.</param>
    /// <param name="minimum">LIMITMIN, or null for a zero-length item.</param>
    /// <param name="maximum">LIMITMAX, or null for a zero-length item.</param>
    /// <param name="limits">The bands in force.</param>
    /// <returns>The entry item.</returns>
    internal static SecsItem WriteAttributes(
        uint variableId,
        string units,
        double? minimum,
        double? maximum,
        IReadOnlyList<GemVariableLimit> limits)
    {
        var bands = new SecsItem[limits.Count];

        for (var i = 0; i < bands.Length; i++)
        {
            bands[i] = SecsItem.L(
                SecsItem.U4(limits[i].Id).Named("LIMITID"),
                SecsItem.L(
                    SecsItem.F8(limits[i].Upper).Named("UPPERDB"),
                    SecsItem.F8(limits[i].Lower).Named("LOWERDB")));
        }

        return SecsItem.L(
            GemItems.IdItem(variableId).Named("VID"),
            SecsItem.L(
                SecsItem.L(
                    SecsItem.A(units).Named("UNITS"),

                    // A zero-length item where there is no documented bound, which is what E5 uses
                    // and what the equipment constant namelist already does for ECMIN and ECMAX.
                    minimum is null ? SecsItem.L() : SecsItem.F4((float)minimum.Value).Named("LIMITMIN"),
                    maximum is null ? SecsItem.L() : SecsItem.F4((float)maximum.Value).Named("LIMITMAX")),
                SecsItem.L(bands)));
    }

    /// <summary>Reads S2F48's body.</summary>
    /// <param name="body">The reply body.</param>
    /// <returns>One entry per variable the equipment reported.</returns>
    internal static List<GemVariableLimitAttributes> ReadAttributes(SecsItem? body)
    {
        List<GemVariableLimitAttributes> attributes = [];

        if (body is not { IsList: true })
        {
            return attributes;
        }

        foreach (var entry in body.Items)
        {
            if (entry is not { IsList: true, Count: 2 }
                || !GemItems.TryReadId(entry[0], out var variableId)
                || entry[1] is not { IsList: true, Count: 2 }
                || entry[1][0] is not { IsList: true, Count: 3 }
                || !entry[1][1].IsList)
            {
                continue;
            }

            var facts = entry[1][0];

            List<GemVariableLimit> limits = [];

            foreach (var limit in entry[1][1].Items)
            {
                if (limit is not { IsList: true, Count: 2 }
                    || !GemItems.TryReadId(limit[0], out var limitId)
                    || limit[1] is not { IsList: true, Count: 2 })
                {
                    continue;
                }

                var upper = ReadNumber(limit[1][0]);
                var lower = ReadNumber(limit[1][1]);

                if (!double.IsNaN(upper) && !double.IsNaN(lower) && upper >= lower)
                {
                    limits.Add(new GemVariableLimit((uint)limitId, lower, upper));
                }
            }

            attributes.Add(new GemVariableLimitAttributes(
                (uint)variableId,
                GemItems.ReadText(facts[0]),
                ReadOptionalNumber(facts[1]),
                ReadOptionalNumber(facts[2]),
                limits));
        }

        return attributes;
    }

    /// <summary>Reads a bound in whatever numeric format the other side chose.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The value, or NaN when it is not a number at all.</returns>
    /// <remarks>
    /// E5 types UPPERDB and LOWERDB <c>F8</c>, but a host that sent a limit of 1000 as a <c>U4</c>
    /// has said the same thing - and refusing it would be refusing arithmetic rather than a
    /// definition. NaN is the one value that cannot mean a bound, which is what makes it usable as
    /// "there was not one".
    /// </remarks>
    internal static double ReadNumber(SecsItem item) => item.Format switch
    {
        SecsFormat.F8 => item.FirstValueOrDefault<double>(),
        SecsFormat.F4 => item.FirstValueOrDefault<float>(),
        SecsFormat.U1 => item.FirstValueOrDefault<byte>(),
        SecsFormat.U2 => item.FirstValueOrDefault<ushort>(),
        SecsFormat.U4 => item.FirstValueOrDefault<uint>(),
        SecsFormat.U8 => item.FirstValueOrDefault<ulong>(),
        SecsFormat.I1 => item.FirstValueOrDefault<sbyte>(),
        SecsFormat.I2 => item.FirstValueOrDefault<short>(),
        SecsFormat.I4 => item.FirstValueOrDefault<int>(),
        SecsFormat.I8 => item.FirstValueOrDefault<long>(),
        _ => double.NaN,
    };

    private static double? ReadOptionalNumber(SecsItem item)
    {
        if (item.Count == 0)
        {
            return null;
        }

        var value = ReadNumber(item);

        return double.IsNaN(value) ? null : value;
    }
}
