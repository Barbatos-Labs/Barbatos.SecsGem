// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// Every enhanced stream 2 body this package sends or reads, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Provenance, and it is better here than in most of this repository.</strong> Of the
/// sixteen catalogue rows S2F49 to S2F64, two - S2F49 and S2F50 - are marked as definitions rather
/// than as part of a representative subset. The other fourteen are representative, so the bodies
/// below are read off the item lists and the formats of the items.
/// </para>
/// <para>
/// What makes that reading a check rather than a guess is the pattern it has to fit: <strong>every
/// one of these is the GEM message it scopes, with <c>OBJSPEC</c> in front.</strong> That can be
/// verified against this repository's own GEM package rather than against prose, and it was -
/// S2F14 answers <c>L,n { ECV }</c> positionally with a zero-length item for an unknown
/// identifier, S2F42 answers <c>L,2 { HCACK, L,n { ... } }</c>, S5F1 reports an alarm as
/// <c>L,3 { ALCD, ALID, ALTX }</c>. Where a reading here disagreed with GEM's, GEM's won.
/// </para>
/// <para>
/// Three item names in this range are missing from the dictionary altogether - <c>RPACK</c>,
/// <c>URACK</c> and <c>DGDATA</c> - and each is inferred from a defined item doing the same job:
/// the two acknowledgements from <c>DRACK</c>, <c>LRACK</c> and <c>ERACK</c>, which are
/// <c>B[1]</c> with nought meaning accepted, and the diagnostic payload from <c>ATTRDATA</c>,
/// which is the other item in this package with no format of its own.
/// </para>
/// <para>
/// This is what the item lists say, and what that reads as:
/// </para>
/// <code>
/// S2F49  DATAID OBJSPEC RCMD CPNAME CPVAL   L,4 { DATAID, OBJSPEC, RCMD, L,n { L,2 { CPNAME, CPVAL } } }
/// S2F50  HCACK CPNAME CPACK                 L,2 { HCACK, L,n { L,2 { CPNAME, CPACK } } }
/// S2F51  RPTID OBJSPEC                      L,2 { RPTID, OBJSPEC }
/// S2F52  RPACK                              RPACK
/// S2F53  RPTID OBJSPEC                      L,2 { RPTID, OBJSPEC }
/// S2F54  URACK                              URACK
/// S2F55  OBJSPEC ECID                       L,2 { OBJSPEC, L,n { ECID } }
/// S2F56  ECV                                L,n { ECV }
/// S2F57  OBJSPEC SVID                       L,2 { OBJSPEC, L,n { SVID } }
/// S2F58  SV                                 L,n { SV }
/// S2F59  OBJSPEC VID                        L,2 { OBJSPEC, L,n { VID } }
/// S2F60  SV                                 L,n { SV }
/// S2F61  OBJSPEC ALID                       L,2 { OBJSPEC, L,n { ALID } }
/// S2F62  ALCD ALID ALTX                     L,n { L,3 { ALCD, ALID, ALTX } }
/// S2F63  OBJSPEC DATAID                     L,2 { OBJSPEC, DATAID }
/// S2F64  DATAID DGDATA                      L,2 { DATAID, DGDATA }
/// </code>
/// <para>
/// Tolerant read, exact write, as everywhere else: a request is written with the formats the
/// catalogue gives and read in the shapes a machine might actually send - a bare item where a list
/// of one was expected, an identifier of any unsigned width, a flat pair list as well as a nested
/// one.
/// </para>
/// </remarks>
internal static class EnhancedCodec
{
    /// <summary>S2F49 - a command addressed to an object.</summary>
    internal static SecsItem CommandBody(EnhancedCommandRequest request)
        => SecsItem.L(
            SecsItem.U4(request.DataId).Named("DATAID"),
            SecsItem.A(request.ObjectSpecification).Named("OBJSPEC"),
            SecsItem.A(request.Command).Named("RCMD"),
            ObjectCodec.ParameterListBody(request.Parameters));

    /// <summary>Reads S2F49.</summary>
    internal static bool TryReadCommand(SecsItem? body, out EnhancedCommandRequest? request)
    {
        request = null;

        if (body is not { IsList: true } list || list.Count < 3)
        {
            return false;
        }

        if (!list.Items[1].Format.IsText() || !list.Items[2].Format.IsText())
        {
            return false;
        }

        if (!ObjectCodec.TryReadParameterList(list.Count > 3 ? list.Items[3] : null, out var parameters))
        {
            return false;
        }

        request = new EnhancedCommandRequest
        {
            DataId = Identifier(list.Items[0]) is { } id ? (uint)Math.Min(id, uint.MaxValue) : 0u,
            ObjectSpecification = ObjectCodec.Text(list.Items[1]),
            Command = ObjectCodec.Text(list.Items[2]),
            Parameters = parameters,
        };

        return true;
    }

    /// <summary>S2F50 - the answer, and anything wrong with a parameter.</summary>
    internal static SecsItem CommandResultBody(EnhancedCommandResult result)
    {
        var entries = new SecsItem[result.Parameters.Count];

        for (var i = 0; i < result.Parameters.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(result.Parameters[i].Name).Named("CPNAME"),
                SecsItem.B((byte)result.Parameters[i].Acknowledgement).Named("CPACK"));
        }

        return SecsItem.L(
            SecsItem.B((byte)result.Acknowledgement).Named("HCACK"),
            SecsItem.L(entries));
    }

    /// <summary>Reads S2F50.</summary>
    /// <remarks>
    /// The code is inside the list, which is the trap GEM's own host side documents for S2F42: a
    /// reader that takes the body as a bare <c>HCACK</c> reads the length of a list as an
    /// acknowledgement. A bare code is accepted too, because a machine that sends one has still
    /// said what it meant.
    /// </remarks>
    internal static EnhancedCommandResult? TryReadCommandResult(SecsItem? body)
    {
        if (body is null)
        {
            return null;
        }

        if (!body.IsList)
        {
            return body.Count == 0
                ? null
                : EnhancedCommandResult.From((EnhancedCommandAcknowledgement)body.FirstValueOrDefault<byte>());
        }

        if (body.Count == 0)
        {
            return null;
        }

        var code = (EnhancedCommandAcknowledgement)Code(body.Items[0]);
        List<EnhancedParameterResult> parameters = [];

        if (body.Count > 1 && body.Items[1] is { IsList: true } list)
        {
            foreach (var entry in list.Items)
            {
                if (entry is not { IsList: true } pair || pair.Count < 2 || !pair.Items[0].Format.IsText())
                {
                    continue;
                }

                parameters.Add(new EnhancedParameterResult
                {
                    Name = ObjectCodec.Text(pair.Items[0]),
                    Acknowledgement = (EnhancedParameterAcknowledgement)Code(pair.Items[1]),
                });
            }
        }

        return new EnhancedCommandResult { Acknowledgement = code, Parameters = parameters };
    }

    /// <summary>S2F51 and S2F53 - a report and the object it is about.</summary>
    internal static SecsItem BindingBody(EnhancedReportBinding binding)
        => SecsItem.L(
            SecsItem.U4(binding.ReportId).Named("RPTID"),
            SecsItem.A(binding.ObjectSpecification).Named("OBJSPEC"));

    /// <summary>Reads S2F51 and S2F53.</summary>
    internal static bool TryReadBinding(SecsItem? body, out EnhancedReportBinding? binding)
    {
        binding = null;

        if (body is not { IsList: true } list || list.Count < 2 || !list.Items[1].Format.IsText())
        {
            return false;
        }

        if (Identifier(list.Items[0]) is not { } id)
        {
            return false;
        }

        binding = new EnhancedReportBinding
        {
            ReportId = (uint)Math.Min(id, uint.MaxValue),
            ObjectSpecification = ObjectCodec.Text(list.Items[1]),
        };

        return true;
    }

    /// <summary>S2F52 and S2F54 - a bare acknowledgement.</summary>
    internal static SecsItem BindResultBody(EnhancedReportAcknowledgement acknowledgement, string item)
        => SecsItem.B((byte)acknowledgement).Named(item);

    /// <summary>Reads S2F52 and S2F54, descending into a list of one if that is what arrived.</summary>
    internal static EnhancedReportBindResult? TryReadBindResult(SecsItem? body)
    {
        if (body is null)
        {
            return null;
        }

        if (body.IsList)
        {
            return body.Count == 1 ? TryReadBindResult(body.Items[0]) : null;
        }

        return body.Count == 0 ? null : new EnhancedReportBindResult { Code = Code(body) };
    }

    /// <summary>S2F55, S2F57, S2F59 and S2F61 - an object and a list of identifiers.</summary>
    internal static SecsItem ValueRequestBody(string objectSpecification, IReadOnlyList<uint> ids, string item)
    {
        var entries = new SecsItem[ids.Count];

        for (var i = 0; i < ids.Count; i++)
        {
            entries[i] = SecsItem.U4(ids[i]).Named(item);
        }

        return SecsItem.L(
            SecsItem.A(objectSpecification).Named("OBJSPEC"),
            SecsItem.L(entries));
    }

    /// <summary>Reads S2F55, S2F57, S2F59 and S2F61.</summary>
    internal static bool TryReadValueRequest(SecsItem? body, out string objectSpecification, out List<uint> ids)
    {
        objectSpecification = string.Empty;
        ids = [];

        if (body is not { IsList: true } list || list.Count < 1 || !list.Items[0].Format.IsText())
        {
            return false;
        }

        objectSpecification = ObjectCodec.Text(list.Items[0]);

        if (list.Count < 2)
        {
            // No identifier list at all reads the same as an empty one, which is a request for
            // everything - the rule GEM's S1F3 and S2F13 follow.
            return true;
        }

        var requested = list.Items[1];

        if (!requested.IsList)
        {
            // A bare identifier where a list of one was expected.
            if (Identifier(requested) is { } single)
            {
                ids.Add((uint)Math.Min(single, uint.MaxValue));
            }

            return true;
        }

        foreach (var entry in requested.Items)
        {
            if (Identifier(entry) is { } id)
            {
                ids.Add((uint)Math.Min(id, uint.MaxValue));
            }
        }

        return true;
    }

    /// <summary>S2F56, S2F58 and S2F60 - the values, positionally.</summary>
    /// <remarks>
    /// The values go into the tree as they are rather than being copied, which is what makes a
    /// provider returning a fresh item the right contract - the same arrangement stream 14 has for
    /// <c>ATTRDATA</c>.
    /// </remarks>
    internal static SecsItem ValueDataBody(IReadOnlyList<SecsItem> values) => SecsItem.L([.. values]);

    /// <summary>Reads S2F56, S2F58 and S2F60 as views into the reply.</summary>
    /// <remarks>
    /// The array is new and the items in it are not: <c>SecsItem.Items</c> is a span, which cannot
    /// leave the method, so the references are copied into a list the caller can hold. Each one
    /// still points into the reply and dies with it, which is why these come back through a
    /// callback rather than as a return value.
    /// </remarks>
    internal static bool TryReadValueData(SecsItem? body, out IReadOnlyList<SecsItem> values)
    {
        values = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        values = [.. body.Items];
        return true;
    }

    /// <summary>S2F62 - one entry per alarm.</summary>
    internal static SecsItem AlarmDataBody(IReadOnlyList<EnhancedAlarm> alarms)
    {
        var entries = new SecsItem[alarms.Count];

        for (var i = 0; i < alarms.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.B(alarms[i].Code).Named("ALCD"),
                SecsItem.U4((uint)Math.Min(alarms[i].Id, uint.MaxValue)).Named("ALID"),
                SecsItem.A(alarms[i].Text).Named("ALTX"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S2F62.</summary>
    internal static bool TryReadAlarmData(SecsItem? body, out List<EnhancedAlarm> alarms)
    {
        alarms = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        foreach (var entry in body.Items)
        {
            if (entry is not { IsList: true } triple || triple.Count < 2)
            {
                return false;
            }

            alarms.Add(new EnhancedAlarm
            {
                Code = Code(triple.Items[0]),
                Id = Identifier(triple.Items[1]) ?? 0UL,
                Text = triple.Count > 2 && triple.Items[2].Format.IsText()
                    ? ObjectCodec.Text(triple.Items[2])
                    : string.Empty,
            });
        }

        return true;
    }

    /// <summary>S2F63 - an object and a diagnostic.</summary>
    internal static SecsItem DiagnosticRequestBody(EnhancedDiagnosticRequest request)
        => SecsItem.L(
            SecsItem.A(request.ObjectSpecification).Named("OBJSPEC"),
            SecsItem.U4(request.DataId).Named("DATAID"));

    /// <summary>Reads S2F63.</summary>
    internal static bool TryReadDiagnosticRequest(SecsItem? body, out EnhancedDiagnosticRequest? request)
    {
        request = null;

        if (body is not { IsList: true } list || list.Count < 2 || !list.Items[0].Format.IsText())
        {
            return false;
        }

        if (Identifier(list.Items[1]) is not { } id)
        {
            return false;
        }

        request = new EnhancedDiagnosticRequest
        {
            ObjectSpecification = ObjectCodec.Text(list.Items[0]),
            DataId = (uint)Math.Min(id, uint.MaxValue),
        };

        return true;
    }

    /// <summary>S2F64 - the diagnostic echoed back with its data.</summary>
    internal static SecsItem DiagnosticDataBody(uint dataId, SecsItem? data)
        => SecsItem.L(
            SecsItem.U4(dataId).Named("DATAID"),
            data ?? SecsItem.L());

    /// <summary>Reads S2F64 as a view into the reply.</summary>
    internal static bool TryReadDiagnosticData(SecsItem? body, out uint dataId, out SecsItem? data)
    {
        dataId = 0;
        data = null;

        if (body is not { IsList: true } list || list.Count < 2)
        {
            return false;
        }

        dataId = Identifier(list.Items[0]) is { } id ? (uint)Math.Min(id, uint.MaxValue) : 0u;

        // A zero-length item is how this library says absent, here as everywhere: a diagnostic
        // the machine does not have comes back as an empty list rather than as a missing item.
        data = list.Items[1].IsList && list.Items[1].Count == 0 ? null : list.Items[1];

        return true;
    }

    /// <summary>
    /// Makes a provider's answer line up with what was asked for.
    /// </summary>
    /// <remarks>
    /// The guard that keeps a positional reply positional. S2F56, S2F58 and S2F60 carry values and
    /// no identifiers, so a provider that returns four values for five identifiers puts every
    /// reading after the gap against the wrong variable - and a host has no way to notice. Short
    /// answers are padded with zero-length items, which is E5's own way of saying "not this one",
    /// and long ones are trimmed. An empty request asked for everything, so there is nothing to
    /// line up against and the answer passes through.
    /// </remarks>
    internal static IReadOnlyList<SecsItem> Aligned(IReadOnlyList<SecsItem> values, int requested)
    {
        if (requested == 0 || values.Count == requested)
        {
            return values;
        }

        var aligned = new SecsItem[requested];

        for (var i = 0; i < requested; i++)
        {
            aligned[i] = i < values.Count ? values[i] : SecsItem.L();
        }

        return aligned;
    }

    /// <summary>Reads an acknowledgement byte out of whatever width carried it.</summary>
    private static byte Code(SecsItem item)
        => item.IsList || item.Count == 0 ? (byte)0 : item.FirstValueOrDefault<byte>();

    /// <summary>
    /// Reads an identifier of any unsigned width.
    /// </summary>
    /// <remarks>
    /// The catalogue types every identifier in this range as <c>U4</c>, and a machine that sends a
    /// <c>U1</c> for report two has still said two. <see langword="null"/> for an item that is not
    /// a number at all, which is how a malformed body is told from a zero.
    /// </remarks>
    private static ulong? Identifier(SecsItem item)
    {
        if (item.IsList || item.Count == 0)
        {
            return null;
        }

        return item.Format switch
        {
            SecsFormat.Binary or SecsFormat.U1 => item.FirstValueOrDefault<byte>(),
            SecsFormat.U2 => item.FirstValueOrDefault<ushort>(),
            SecsFormat.U4 => item.FirstValueOrDefault<uint>(),
            SecsFormat.U8 => item.FirstValueOrDefault<ulong>(),
            SecsFormat.I1 => Unsigned(item.FirstValueOrDefault<sbyte>()),
            SecsFormat.I2 => Unsigned(item.FirstValueOrDefault<short>()),
            SecsFormat.I4 => Unsigned(item.FirstValueOrDefault<int>()),
            SecsFormat.I8 => Unsigned(item.FirstValueOrDefault<long>()),
            _ => item.Format.IsText() && ulong.TryParse(ObjectCodec.Text(item), out var parsed) ? parsed : null,
        };
    }

    private static ulong? Unsigned(long value) => value < 0 ? null : (ulong)value;
}
