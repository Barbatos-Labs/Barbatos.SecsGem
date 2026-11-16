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

    // ------------------------------------------------------------- the pieces

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

    private static string Text(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;
}
