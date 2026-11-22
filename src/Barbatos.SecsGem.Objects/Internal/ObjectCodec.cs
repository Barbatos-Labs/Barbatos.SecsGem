// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// Every stream 14 body this package sends or reads, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Provenance.</strong> Of stream 14's twenty-eight catalogue rows, exactly one - S14F1 -
/// is marked as a definition rather than as part of a representative subset. So, as with stream 3,
/// the bodies here are read off the item lists and the formats of the items, and they are a
/// careful reading rather than a quotation. Unlike stream 3, the subset this package implements
/// touches no item that is missing from the dictionary: <c>ENABLE</c>, the one undefined name in
/// the stream, appears only in S14F23, which is not here.
/// </para>
/// <para>
/// This is what the item lists say, and what that reads as:
/// </para>
/// <code>
/// S14F1   OBJSPEC OBJTYPE ATTRID              L,3 { OBJSPEC, OBJTYPE, L,n { ATTRID } }
/// S14F2   OBJSPEC ATTRID ATTRDATA             L,2 { OBJSPEC, L,n { L,2 { ATTRID, ATTRDATA } } }
/// S14F3   OBJSPEC OBJTYPE ATTRID ATTRDATA     L,3 { OBJSPEC, OBJTYPE, L,n { L,2 { ATTRID, ATTRDATA } } }
/// S14F4   ATTRID ATTRDATA OBJACK              L,2 { L,n { L,2 { ATTRID, ATTRDATA } }, OBJACK }
/// S14F13  -                                   (no body)
/// S14F14  OBJTYPE                             L,n { OBJTYPE }
/// S14F15  OBJTYPE                             OBJTYPE
/// S14F16  OBJSPEC                             L,n { OBJSPEC }
/// S14F17  OBJSPEC OBJTYPE                     L,2 { OBJSPEC, OBJTYPE }
/// S14F18  ATTRID                              L,n { ATTRID }
/// S14F19  OBJSPEC ATTRID                      L,2 { L,n { OBJSPEC }, L,n { ATTRID } }
/// S14F20  OBJSPEC ATTRID ATTRDATA             L,n { L,2 { OBJSPEC, L,m { L,2 { ATTRID, ATTRDATA } } } }
/// S14F21  OBJSPEC ATTRID ATTRDATA             L,2 { L,n { OBJSPEC }, L,m { L,2 { ATTRID, ATTRDATA } } }
/// S14F22  OBJSPEC ATTRID ATTRDATA OBJACK      L,n { L,3 { OBJSPEC, L,m { L,2 { ATTRID, ATTRDATA } }, OBJACK } }
/// </code>
/// <para>
/// Two things to notice, because both are the catalogue being specific rather than this library
/// being inventive. S14F2 has no <c>OBJACK</c> in it at all, so a get cannot report a failure -
/// an object that could not be read is simply absent from the answer. And S14F4 has no
/// <c>OBJSPEC</c>, because a single-object set already knows which object it was about.
/// </para>
/// <para>
/// <strong>Reading is looser than writing.</strong> An attribute list is accepted whether its
/// entries are two-element lists or a flat run of name-and-value pairs; a name list is accepted
/// whether it is a list of items or one bare item. Nothing in the item lists settles those, and
/// refusing a readable body over a wrapper nobody can see is not interoperability.
/// </para>
/// </remarks>
internal static class ObjectCodec
{
    // --------------------------------------------------------------- namelists

    /// <summary>S14F14, S14F16 and S14F18 - a list of names.</summary>
    internal static SecsItem NameListBody(IReadOnlyList<string> names, string item)
    {
        var entries = new SecsItem[names.Count];

        for (var i = 0; i < names.Count; i++)
        {
            entries[i] = SecsItem.A(names[i]).Named(item);
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S14F14, S14F16 and S14F18.</summary>
    /// <returns><see langword="false"/> when the body is not a list of names at all.</returns>
    internal static bool TryReadNameList(SecsItem? body, out List<string> names)
    {
        names = [];

        if (body is null)
        {
            return false;
        }

        // One bare name rather than a list of one: accepted, because a machine with a single
        // object type is not wrong about anything that matters.
        if (!body.IsList)
        {
            if (!body.Format.IsText())
            {
                return false;
            }

            names.Add(Text(body));
            return true;
        }

        foreach (var entry in body.Items)
        {
            if (entry.Format.IsText())
            {
                names.Add(Text(entry));
            }
        }

        return true;
    }

    /// <summary>S14F15 - one bare <c>OBJTYPE</c>.</summary>
    internal static SecsItem TextBody(string value, string item) => SecsItem.A(value).Named(item);

    /// <summary>Reads a bare text item, descending into a list of one if that is what arrived.</summary>
    internal static string? TryReadText(SecsItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (item.IsList)
        {
            return item.Count == 1 ? TryReadText(item.Items[0]) : null;
        }

        return item.Format.IsText() ? Text(item) : null;
    }

    /// <summary>S14F17 - <c>OBJSPEC</c> and <c>OBJTYPE</c>.</summary>
    internal static SecsItem AttributeNamesRequestBody(string objectSpecification, string objectType)
        => SecsItem.L(
            SecsItem.A(objectSpecification).Named("OBJSPEC"),
            SecsItem.A(objectType).Named("OBJTYPE"));

    /// <summary>Reads S14F17.</summary>
    internal static bool TryReadAttributeNamesRequest(
        SecsItem? body,
        out string objectSpecification,
        out string objectType)
    {
        objectSpecification = string.Empty;
        objectType = string.Empty;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        objectSpecification = Text(body.Items[0]);
        objectType = Text(body.Items[1]);

        return true;
    }

    // ------------------------------------------------------------------ a get

    /// <summary>S14F1 - GetAttr Request, one object.</summary>
    internal static SecsItem GetRequestBody(ObjectAttributeRequest request)
        => SecsItem.L(
            SecsItem.A(request.Objects.Count > 0 ? request.Objects[0] : string.Empty).Named("OBJSPEC"),
            SecsItem.A(request.ObjectType).Named("OBJTYPE"),
            NameListBody(request.Attributes, "ATTRID"));

    /// <summary>Reads S14F1.</summary>
    internal static bool TryReadGetRequest(SecsItem? body, out ObjectAttributeRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        if (!TryReadNameList(body.Items[2], out var attributes))
        {
            return false;
        }

        request = new ObjectAttributeRequest
        {
            Objects = [Text(body.Items[0])],
            ObjectType = Text(body.Items[1]),
            Attributes = attributes,
        };

        return true;
    }

    /// <summary>S14F19 - Multi-Object GetAttr Request.</summary>
    internal static SecsItem MultiGetRequestBody(ObjectAttributeRequest request)
        => SecsItem.L(
            NameListBody(request.Objects, "OBJSPEC"),
            NameListBody(request.Attributes, "ATTRID"));

    /// <summary>Reads S14F19.</summary>
    internal static bool TryReadMultiGetRequest(SecsItem? body, out ObjectAttributeRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        if (!TryReadNameList(body.Items[0], out var objects) || !TryReadNameList(body.Items[1], out var attributes))
        {
            return false;
        }

        request = new ObjectAttributeRequest { Objects = objects, Attributes = attributes };

        return true;
    }

    /// <summary>S14F2 - GetAttr Data, one object. No <c>OBJACK</c>: the item list has none.</summary>
    internal static SecsItem GetDataBody(ObjectAttributeValues? values)
        => values is null
            ? SecsItem.L()
            : SecsItem.L(
                SecsItem.A(values.ObjectSpecification).Named("OBJSPEC"),
                AttributeListBody(values.Attributes));

    /// <summary>Reads S14F2. <see langword="true"/> with <see langword="null"/> values is a zero-length list.</summary>
    internal static bool TryReadGetData(SecsItem? body, out ObjectAttributeValues? values)
    {
        values = null;

        if (body is null || !body.IsList)
        {
            return false;
        }

        if (body.Count == 0)
        {
            return true;
        }

        if (body.Count < 2 || !TryReadAttributeList(body.Items[1], out var attributes))
        {
            return false;
        }

        values = new ObjectAttributeValues
        {
            ObjectSpecification = Text(body.Items[0]),
            Attributes = attributes,
        };

        return true;
    }

    /// <summary>S14F20 - Multi-Object GetAttr Data.</summary>
    internal static SecsItem MultiGetDataBody(IReadOnlyList<ObjectAttributeValues> values)
    {
        var entries = new SecsItem[values.Count];

        for (var i = 0; i < values.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(values[i].ObjectSpecification).Named("OBJSPEC"),
                AttributeListBody(values[i].Attributes));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S14F20.</summary>
    internal static bool TryReadMultiGetData(SecsItem? body, out List<ObjectAttributeValues> values)
    {
        values = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        foreach (var entry in body.Items)
        {
            if (!entry.IsList || entry.Count < 2 || !TryReadAttributeList(entry.Items[1], out var attributes))
            {
                return false;
            }

            values.Add(new ObjectAttributeValues
            {
                ObjectSpecification = Text(entry.Items[0]),
                Attributes = attributes,
            });
        }

        return true;
    }

    // ------------------------------------------------------------------ a set

    /// <summary>S14F3 - SetAttr Request, one object.</summary>
    internal static SecsItem SetRequestBody(ObjectAttributeWrite write)
        => SecsItem.L(
            SecsItem.A(write.Objects.Count > 0 ? write.Objects[0] : string.Empty).Named("OBJSPEC"),
            SecsItem.A(write.ObjectType).Named("OBJTYPE"),
            AttributeListBody(write.Attributes));

    /// <summary>Reads S14F3.</summary>
    internal static bool TryReadSetRequest(SecsItem? body, out ObjectAttributeWrite? write)
    {
        write = null;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        if (!TryReadAttributeList(body.Items[2], out var attributes))
        {
            return false;
        }

        write = new ObjectAttributeWrite
        {
            Objects = [Text(body.Items[0])],
            ObjectType = Text(body.Items[1]),
            Attributes = attributes,
        };

        return true;
    }

    /// <summary>S14F21 - Multi-Object SetAttr Request.</summary>
    internal static SecsItem MultiSetRequestBody(ObjectAttributeWrite write)
        => SecsItem.L(
            NameListBody(write.Objects, "OBJSPEC"),
            AttributeListBody(write.Attributes));

    /// <summary>Reads S14F21.</summary>
    internal static bool TryReadMultiSetRequest(SecsItem? body, out ObjectAttributeWrite? write)
    {
        write = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        if (!TryReadNameList(body.Items[0], out var objects) || !TryReadAttributeList(body.Items[1], out var attributes))
        {
            return false;
        }

        write = new ObjectAttributeWrite { Objects = objects, Attributes = attributes };

        return true;
    }

    /// <summary>S14F4 - SetAttr Data: what was set, and whether it took. No <c>OBJSPEC</c>.</summary>
    internal static SecsItem SetDataBody(IReadOnlyList<ObjectAttribute> attributes, ObjectAcknowledgement acknowledgement)
        => SecsItem.L(
            EchoListBody(attributes),
            SecsItem.B((byte)acknowledgement).Named("OBJACK"));

    /// <summary>Reads the acknowledgement out of S14F4.</summary>
    /// <returns>The code, or <see langword="null"/> when there is none to read.</returns>
    /// <remarks>
    /// The echoed attributes are not returned. E5 has the reply carry what was set, and a caller
    /// that just sent them has them already - reading them back would mean copying every value out
    /// of a message to hand back something the caller passed in.
    /// </remarks>
    internal static byte? TryReadSetData(SecsItem? body)
    {
        if (body is null || !body.IsList)
        {
            return null;
        }

        // The code is the last item, which is where the item list puts it. A body of one item is a
        // machine that sent only the code, which is also readable.
        return body.Count switch
        {
            0 => null,
            1 => ReadCode(body.Items[0]),
            _ => ReadCode(body.Items[body.Count - 1]),
        };
    }

    /// <summary>S14F22 - Multi-Object SetAttr Data.</summary>
    internal static SecsItem MultiSetDataBody(
        IReadOnlyList<ObjectSetResult> results,
        IReadOnlyList<ObjectAttribute> attributes)
    {
        var entries = new SecsItem[results.Count];

        for (var i = 0; i < results.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(results[i].ObjectSpecification).Named("OBJSPEC"),
                EchoListBody(attributes),
                SecsItem.B((byte)results[i].Acknowledgement).Named("OBJACK"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S14F22.</summary>
    internal static bool TryReadMultiSetData(SecsItem? body, out List<ObjectSetResult> results)
    {
        results = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        foreach (var entry in body.Items)
        {
            if (!entry.IsList || entry.Count < 2)
            {
                return false;
            }

            // The code is the last item of the entry, so an entry that leaves the echoed
            // attributes out still reads.
            var code = ReadCode(entry.Items[entry.Count - 1]);

            if (code is null)
            {
                return false;
            }

            results.Add(new ObjectSetResult
            {
                ObjectSpecification = Text(entry.Items[0]),
                Acknowledgement = Acknowledgement(code.Value),
            });
        }

        return true;
    }

    // --------------------------------------------------------------- an action

    /// <summary>S14F9 - Object Action Request, one object.</summary>
    internal static SecsItem ActionBody(ObjectActionRequest request)
        => SecsItem.L(
            SecsItem.A(request.Objects.Count > 0 ? request.Objects[0] : string.Empty).Named("OBJSPEC"),
            SecsItem.A(request.ObjectType).Named("OBJTYPE"),
            SecsItem.A(request.Command).Named("RCMD"),
            ParameterListBody(request.Parameters));

    /// <summary>Reads S14F9.</summary>
    internal static bool TryReadAction(SecsItem? body, out ObjectActionRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        var command = Text(body.Items[2]);

        if (command.Length == 0)
        {
            return false;
        }

        if (!TryReadParameterList(body.Count > 3 ? body.Items[3] : null, out var parameters))
        {
            return false;
        }

        request = new ObjectActionRequest
        {
            Objects = [Text(body.Items[0])],
            ObjectType = Text(body.Items[1]),
            Command = command,
            Parameters = parameters,
        };

        return true;
    }

    /// <summary>S14F27 - Object Multi-Action Request. No <c>OBJTYPE</c>: several objects need not share one.</summary>
    internal static SecsItem MultiActionBody(ObjectActionRequest request)
        => SecsItem.L(
            NameListBody(request.Objects, "OBJSPEC"),
            SecsItem.A(request.Command).Named("RCMD"),
            ParameterListBody(request.Parameters));

    /// <summary>Reads S14F27.</summary>
    internal static bool TryReadMultiAction(SecsItem? body, out ObjectActionRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        if (!TryReadNameList(body.Items[0], out var objects))
        {
            return false;
        }

        var command = Text(body.Items[1]);

        if (command.Length == 0)
        {
            return false;
        }

        if (!TryReadParameterList(body.Count > 2 ? body.Items[2] : null, out var parameters))
        {
            return false;
        }

        request = new ObjectActionRequest { Objects = objects, Command = command, Parameters = parameters };

        return true;
    }

    /// <summary>S14F10 and S14F28 - what happened to each object.</summary>
    internal static SecsItem ActionResultBody(IReadOnlyList<ObjectActionResult> results)
    {
        // One result is written as the pair S14F10 describes; several as a list of them, which is
        // what S14F28 needs. A reader takes either, so the one-object case stays the simple shape
        // a machine expecting S14F10 will recognise.
        if (results.Count == 1)
        {
            return SecsItem.L(
                SecsItem.A(results[0].ObjectSpecification).Named("OBJSPEC"),
                SecsItem.B((byte)results[0].Acknowledgement).Named("OBJACK"));
        }

        var entries = new SecsItem[results.Count];

        for (var i = 0; i < results.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(results[i].ObjectSpecification).Named("OBJSPEC"),
                SecsItem.B((byte)results[i].Acknowledgement).Named("OBJACK"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S14F10 and S14F28, in either shape.</summary>
    internal static bool TryReadActionResults(SecsItem? body, out List<ObjectActionResult> results)
    {
        results = [];

        if (body is null || !body.IsList || body.Count == 0)
        {
            return false;
        }

        // A pair: one object specification and one code.
        if (!body.Items[0].IsList)
        {
            if (body.Count < 2 || ReadCode(body.Items[1]) is not { } single)
            {
                return false;
            }

            results.Add(new ObjectActionResult
            {
                ObjectSpecification = Text(body.Items[0]),
                Acknowledgement = Acknowledgement(single),
            });

            return true;
        }

        foreach (var entry in body.Items)
        {
            if (!entry.IsList || entry.Count < 2 || ReadCode(entry.Items[1]) is not { } code)
            {
                return false;
            }

            results.Add(new ObjectActionResult
            {
                ObjectSpecification = Text(entry.Items[0]),
                Acknowledgement = Acknowledgement(code),
            });
        }

        return true;
    }

    // ---------------------------------------------------- making and unmaking

    /// <summary>S14F5 - Create Object Request.</summary>
    internal static SecsItem CreateBody(ObjectCreateRequest request)
        => SecsItem.L(
            SecsItem.A(request.ObjectType).Named("OBJTYPE"),
            AttributeListBody(request.Attributes));

    /// <summary>Reads S14F5.</summary>
    internal static bool TryReadCreate(SecsItem? body, out ObjectCreateRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 1)
        {
            return false;
        }

        var objectType = Text(body.Items[0]);

        if (objectType.Length == 0)
        {
            return false;
        }

        List<ObjectAttribute> attributes = [];

        if (body.Count > 1 && !TryReadAttributeList(body.Items[1], out attributes))
        {
            return false;
        }

        request = new ObjectCreateRequest { ObjectType = objectType, Attributes = attributes };

        return true;
    }

    /// <summary>S14F6 - Create Object Acknowledge.</summary>
    internal static SecsItem CreateResultBody(ObjectCreateResult result)
        => SecsItem.L(
            SecsItem.B((byte)result.Acknowledgement).Named("OBJACK"),
            SecsItem.A(result.ObjectId).Named("OBJID"));

    /// <summary>Reads S14F6.</summary>
    internal static ObjectCreateResult? TryReadCreateResult(SecsItem? body)
    {
        if (body is null)
        {
            return null;
        }

        if (!body.IsList)
        {
            return ReadCode(body) is { } bare
                ? new ObjectCreateResult { Acknowledgement = Acknowledgement(bare) }
                : null;
        }

        if (body.Count == 0 || ReadCode(body.Items[0]) is not { } code)
        {
            return null;
        }

        return new ObjectCreateResult
        {
            Acknowledgement = Acknowledgement(code),
            ObjectId = body.Count > 1 ? Text(body.Items[1]) : string.Empty,
        };
    }

    /// <summary>S14F7 - Delete Object Request.</summary>
    internal static SecsItem DeleteBody(ObjectDeleteRequest request)
        => SecsItem.L(
            SecsItem.A(request.ObjectType).Named("OBJTYPE"),
            SecsItem.A(request.ObjectSpecification).Named("OBJSPEC"));

    /// <summary>Reads S14F7.</summary>
    internal static bool TryReadDelete(SecsItem? body, out ObjectDeleteRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        var specification = Text(body.Items[1]);

        if (specification.Length == 0)
        {
            return false;
        }

        request = new ObjectDeleteRequest
        {
            ObjectType = Text(body.Items[0]),
            ObjectSpecification = specification,
        };

        return true;
    }

    /// <summary>S14F8 - Delete Object Acknowledge.</summary>
    internal static SecsItem DeleteResultBody(string objectSpecification, ObjectAcknowledgement acknowledgement)
        => SecsItem.L(
            SecsItem.A(objectSpecification).Named("OBJSPEC"),
            SecsItem.B((byte)acknowledgement).Named("OBJACK"));

    /// <summary>Reads S14F8.</summary>
    /// <remarks>The code is the last item, so a machine that sends only the code still reads.</remarks>
    internal static ObjectAcknowledgement? TryReadDeleteResult(SecsItem? body)
    {
        if (body is null)
        {
            return null;
        }

        if (!body.IsList)
        {
            return ReadCode(body) is { } bare ? Acknowledgement(bare) : null;
        }

        return body.Count switch
        {
            0 => null,
            1 => ReadCode(body.Items[0]) is { } only ? Acknowledgement(only) : null,
            _ => ReadCode(body.Items[body.Count - 1]) is { } last ? Acknowledgement(last) : null,
        };
    }

    // ------------------------------------------------------------- the pieces

    /// <summary>Writes an action's parameters as two-element entries.</summary>
    internal static SecsItem ParameterListBody(IReadOnlyList<ObjectParameter> parameters)
    {
        var entries = new SecsItem[parameters.Count];

        for (var i = 0; i < parameters.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(parameters[i].Name).Named("CPNAME"),
                SecsItem.A(parameters[i].Value).Named("CPVAL"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads an action's parameters, nested or flat.</summary>
    /// <remarks>
    /// The same two shapes an attribute list is read in, and one more tolerance besides: a value
    /// that is not text is read as its digits, because <c>CPVAL</c> is an <c>A</c> item in the
    /// catalogue and a machine that sends a number has still said what it meant.
    /// </remarks>
    internal static bool TryReadParameterList(SecsItem? item, out List<ObjectParameter> parameters)
    {
        parameters = [];

        if (item is null)
        {
            return true;
        }

        if (!item.IsList)
        {
            return false;
        }

        if (item.Count == 0)
        {
            return true;
        }

        if (item.Items[0].IsList)
        {
            foreach (var entry in item.Items)
            {
                if (!entry.IsList || entry.Count < 2 || !entry.Items[0].Format.IsText())
                {
                    return false;
                }

                parameters.Add(new ObjectParameter { Name = Text(entry.Items[0]), Value = Value(entry.Items[1]) });
            }

            return true;
        }

        if (item.Count % 2 != 0)
        {
            return false;
        }

        for (var i = 0; i + 1 < item.Count; i += 2)
        {
            if (!item.Items[i].Format.IsText())
            {
                return false;
            }

            parameters.Add(new ObjectParameter { Name = Text(item.Items[i]), Value = Value(item.Items[i + 1]) });
        }

        return true;
    }

    /// <summary>Reads a parameter value, which the catalogue says is text and a machine may not send as text.</summary>
    internal static string Value(SecsItem item)
    {
        if (item.Format.IsText())
        {
            return Text(item);
        }

        if (item.IsList || item.Count == 0)
        {
            return string.Empty;
        }

        return item.Format switch
        {
            SecsFormat.Binary or SecsFormat.U1 => item.FirstValueOrDefault<byte>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.U2 => item.FirstValueOrDefault<ushort>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.U4 => item.FirstValueOrDefault<uint>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.U8 => item.FirstValueOrDefault<ulong>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.I1 => item.FirstValueOrDefault<sbyte>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.I2 => item.FirstValueOrDefault<short>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.I4 => item.FirstValueOrDefault<int>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.I8 => item.FirstValueOrDefault<long>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.F4 => item.FirstValueOrDefault<float>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.F8 => item.FirstValueOrDefault<double>().ToString(CultureInfo.InvariantCulture),
            SecsFormat.Boolean => item.FirstValueOrDefault<bool>().ToString(),
            _ => string.Empty,
        };
    }

    /// <summary>Writes a list of attributes as two-element entries.</summary>
    /// <remarks>
    /// The values belong to the caller until this item is disposed, and then they belong to
    /// nobody: they go into the tree as they are rather than being copied, which is what makes a
    /// provider returning a fresh item the right contract.
    /// </remarks>
    internal static SecsItem AttributeListBody(IReadOnlyList<ObjectAttribute> attributes)
    {
        var entries = new SecsItem[attributes.Count];

        for (var i = 0; i < attributes.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(attributes[i].Name).Named("ATTRID"),
                attributes[i].Value);
        }

        return SecsItem.L(entries);
    }

    /// <summary>S1F20 - Attribute Data, with the reason S14F2 has nowhere to put.</summary>
    /// <param name="attributes">The attributes, or an empty list.</param>
    /// <param name="errorCode">ERRCODE. Zero for none.</param>
    /// <param name="errorText">ERRTEXT. Empty for none.</param>
    /// <returns>The body.</returns>
    /// <remarks>
    /// Three items where S14F2 has two, and the third pair is the whole reason stream 1's one E39
    /// row is served at all: this is the only attribute get in the reference that can say why it
    /// failed. Neither <c>ERRCODE</c> nor <c>ERRTEXT</c> is tabled, so both cross as the caller
    /// gave them.
    /// </remarks>
    internal static SecsItem AttributeDataBody(
        IReadOnlyList<ObjectAttribute> attributes,
        uint errorCode,
        string errorText)
        => SecsItem.L(
            AttributeListBody(attributes),
            SecsItem.U4(errorCode).Named("ERRCODE"),
            SecsItem.A(errorText).Named("ERRTEXT"));

    /// <summary>Writes the attribute list a set reply echoes back, with the values copied.</summary>
    /// <remarks>
    /// <para>
    /// The copy is not optional, and this is the one place in the package where that is true. The
    /// attributes being echoed arrived in the <em>request</em>, which belongs to the transaction;
    /// putting those items into the reply would hand one item to two messages, and the first one
    /// disposed would take the other's values with it. The multi-object reply makes it worse again
    /// by echoing the same attributes once per object, which would put one item in a tree several
    /// times over.
    /// </para>
    /// <para>
    /// So each value is encoded and decoded into an item the reply owns. It costs an allocation
    /// per attribute per object, on a message that acknowledges a configuration change - which is
    /// the right place to spend it. The alternative, leaving <c>ATTRDATA</c> out of the reply
    /// altogether, would save the copy by sending a body the item list does not describe.
    /// </para>
    /// </remarks>
    private static SecsItem EchoListBody(IReadOnlyList<ObjectAttribute> attributes)
    {
        var entries = new SecsItem[attributes.Count];

        for (var i = 0; i < attributes.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(attributes[i].Name).Named("ATTRID"),
                ObjectAttributeStore.Decode(ObjectAttributeStore.Encode(attributes[i].Value)));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads a list of attributes, nested or flat.</summary>
    /// <remarks>
    /// The values are views into the message being read. They are valid while it is, which for a
    /// handler is the length of the call and for a reply is until the method that read it returns.
    /// </remarks>
    internal static bool TryReadAttributeList(SecsItem? item, out List<ObjectAttribute> attributes)
    {
        attributes = [];

        if (item is null || !item.IsList)
        {
            return false;
        }

        if (item.Count == 0)
        {
            return true;
        }

        // Nested: a list of two-element entries, which is what this library writes.
        if (item.Items[0].IsList)
        {
            foreach (var entry in item.Items)
            {
                if (!entry.IsList || entry.Count < 2 || !entry.Items[0].Format.IsText())
                {
                    return false;
                }

                attributes.Add(new ObjectAttribute { Name = Text(entry.Items[0]), Value = entry.Items[1] });
            }

            return true;
        }

        // Flat: name, value, name, value. An odd count means somebody lost one of them.
        if (item.Count % 2 != 0)
        {
            return false;
        }

        for (var i = 0; i + 1 < item.Count; i += 2)
        {
            if (!item.Items[i].Format.IsText())
            {
                return false;
            }

            attributes.Add(new ObjectAttribute { Name = Text(item.Items[i]), Value = item.Items[i + 1] });
        }

        return true;
    }

    /// <summary>Turns a code into an acknowledgement, with anything unknown reading as an error.</summary>
    internal static ObjectAcknowledgement Acknowledgement(byte code)
        => code <= (byte)ObjectAcknowledgement.InvalidAttribute
            ? (ObjectAcknowledgement)code
            : ObjectAcknowledgement.Error;

    /// <summary>Reads an acknowledgement code, descending into a list of one if that is what arrived.</summary>
    internal static byte? ReadCode(SecsItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (item.IsList)
        {
            return item.Count == 1 ? ReadCode(item.Items[0]) : null;
        }

        if (item.Count == 0)
        {
            return null;
        }

        if (item.Format is SecsFormat.Binary or SecsFormat.U1)
        {
            return item.FirstValueOrDefault<byte>();
        }

        if (item.Format.IsText())
        {
            return byte.TryParse(item.GetString().Trim(), CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        return item.Format switch
        {
            SecsFormat.U2 => Narrow(item.FirstValueOrDefault<ushort>()),
            SecsFormat.U4 => Narrow(item.FirstValueOrDefault<uint>()),
            SecsFormat.I1 => Narrow(item.FirstValueOrDefault<sbyte>()),
            SecsFormat.I2 => Narrow(item.FirstValueOrDefault<short>()),
            SecsFormat.I4 => Narrow(item.FirstValueOrDefault<int>()),
            _ => null,
        };

        static byte? Narrow(long value) => value is >= 0 and <= byte.MaxValue ? (byte)value : null;
    }

    internal static string Text(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;
}
