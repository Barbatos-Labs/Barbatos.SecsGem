// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// Answers an S14F11 query out of the three reading members every provider already has.
/// </summary>
/// <remarks>
/// <para>
/// This is <see cref="IObjectProvider.FindAsync"/>'s default, lifted into a type of its own
/// because a default interface member cannot be tested through anything but an implementation -
/// and what is worth testing here is the comparison, not the plumbing. <c>ATTRRELN</c> arrives
/// with its eight relations quoted from the reference; how two <c>ATTRDATA</c> items stand in one
/// of them is a decision this library had to make, because that item has no format of its own.
/// </para>
/// <para>
/// <strong>The decision, in full.</strong> Two numeric items are compared as numbers whatever
/// widths they arrived in - a <c>U1</c> of 25 is greater than a <c>U4</c> of 13 - because a host
/// asking for "slot count greater than 13" is asking about a number and not about an encoding. Two
/// text items are compared ordinally, which is this repository's comparison for everything off a
/// wire. Two binaries are compared for equality of their bytes and stand in no ordering. Anything
/// else - a list, or two different formats - matches nothing at all: this library claims no
/// comparison it cannot describe, and the honest answer to a query it cannot perform is that no
/// object matched rather than a guess about which did.
/// </para>
/// <para>
/// A provider that means something else by an ordering overrides the member and never reaches
/// here, which is the point of the default being a default.
/// </para>
/// </remarks>
internal static class ObjectRelations
{
    /// <summary>Runs the query against a provider's instances.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The specifications that matched.</returns>
    internal static async ValueTask<IReadOnlyList<string>> FindAsync(
        IObjectProvider provider,
        ObjectRelationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        var instances = await provider
            .GetInstancesAsync(request.ObjectType, cancellationToken)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            return [];
        }

        // One read for every instance, with the attribute named - so a provider that holds a
        // hundred objects reads one attribute a hundred times rather than everything once. That is
        // the cost of answering this out of the existing members, and it is why the member is
        // overridable.
        var values = await provider
            .GetAttributesAsync(
                new ObjectAttributeRequest
                {
                    Objects = instances,
                    ObjectType = request.ObjectType,
                    Attributes = [request.Attribute],
                },
                cancellationToken)
            .ConfigureAwait(false);

        List<string> matched = [];

        foreach (var entry in values)
        {
            var held = entry.Attributes.FirstOrDefault(attribute =>
                string.Equals(attribute.Name, request.Attribute, StringComparison.Ordinal));

            if (Matches(held?.Value, request.Relation, request.Value))
            {
                matched.Add(entry.ObjectSpecification);
            }
        }

        return matched;
    }

    /// <summary>Whether one value stands in a relation to another.</summary>
    /// <param name="held">What the object has, or null when it has not got the attribute.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="wanted">What to compare against, or null for <c>Present</c> and <c>Absent</c>.</param>
    /// <returns>Whether it matched.</returns>
    /// <remarks>
    /// <c>Present</c> and <c>Absent</c> are answered first and from <paramref name="held"/> alone,
    /// because they are the two relations that compare against nothing - and an attribute the
    /// object has not got matches no other relation at all. "Not equal to" is deliberately false
    /// for a missing attribute rather than true: an object that has no slot count is not an object
    /// whose slot count differs.
    /// </remarks>
    internal static bool Matches(SecsItem? held, ObjectAttributeRelation relation, SecsItem? wanted)
    {
        if (relation == ObjectAttributeRelation.Present)
        {
            return held is not null;
        }

        if (relation == ObjectAttributeRelation.Absent)
        {
            return held is null;
        }

        if (held is null || wanted is null)
        {
            return false;
        }

        if (TryNumber(held, out var left) && TryNumber(wanted, out var right))
        {
            return relation switch
            {
                ObjectAttributeRelation.EqualTo => left == right,
                ObjectAttributeRelation.NotEqualTo => left != right,
                ObjectAttributeRelation.LessThan => left < right,
                ObjectAttributeRelation.LessThanOrEqualTo => left <= right,
                ObjectAttributeRelation.GreaterThan => left > right,
                ObjectAttributeRelation.GreaterThanOrEqualTo => left >= right,
                _ => false,
            };
        }

        if (held.Format.IsText() && wanted.Format.IsText())
        {
            var order = string.CompareOrdinal(Text(held), Text(wanted));

            return relation switch
            {
                ObjectAttributeRelation.EqualTo => order == 0,
                ObjectAttributeRelation.NotEqualTo => order != 0,
                ObjectAttributeRelation.LessThan => order < 0,
                ObjectAttributeRelation.LessThanOrEqualTo => order <= 0,
                ObjectAttributeRelation.GreaterThan => order > 0,
                ObjectAttributeRelation.GreaterThanOrEqualTo => order >= 0,
                _ => false,
            };
        }

        // Two binaries: equality of the bytes, and nothing else. A slot map equals a slot map with
        // the same bytes in it, and stands in no ordering - "less than" over a byte array is not a
        // thing the reference names or this library will invent.
        if (held.Format == SecsFormat.Binary
            && wanted.Format == SecsFormat.Binary
            && held.TryGetSpan<byte>(out var heldBytes)
            && wanted.TryGetSpan<byte>(out var wantedBytes))
        {
            var same = heldBytes.SequenceEqual(wantedBytes);

            return relation switch
            {
                ObjectAttributeRelation.EqualTo => same,
                ObjectAttributeRelation.NotEqualTo => !same,
                _ => false,
            };
        }

        // Anything else - a list against a list, or a format against a different one - matches
        // nothing. This library claims no comparison it cannot describe, and a query that reaches
        // here has asked one it cannot: the honest answer is that no object matched, not a guess
        // about which did. A provider that can compare its own structures overrides FindAsync and
        // never arrives here.
        return false;
    }

    /// <summary>Reads any numeric item as a double, whatever width it arrived in.</summary>
    /// <remarks>
    /// A double rather than the widest integer, because <c>ATTRDATA</c> may be an <c>F4</c> or an
    /// <c>F8</c> and a query over a setpoint is as ordinary as one over a slot count. The cost is
    /// that two integers past 2^53 compare equal when they are not - which is a price this
    /// comparison pays and a provider with an index does not, since it never reaches here.
    /// </remarks>
    private static bool TryNumber(SecsItem item, out double value)
    {
        value = 0;

        if (item.Count == 0)
        {
            return false;
        }

        switch (item.Format)
        {
            case SecsFormat.U1: value = item.FirstValueOrDefault<byte>(); return true;
            case SecsFormat.U2: value = item.FirstValueOrDefault<ushort>(); return true;
            case SecsFormat.U4: value = item.FirstValueOrDefault<uint>(); return true;
            case SecsFormat.U8: value = item.FirstValueOrDefault<ulong>(); return true;
            case SecsFormat.I1: value = item.FirstValueOrDefault<sbyte>(); return true;
            case SecsFormat.I2: value = item.FirstValueOrDefault<short>(); return true;
            case SecsFormat.I4: value = item.FirstValueOrDefault<int>(); return true;
            case SecsFormat.I8: value = item.FirstValueOrDefault<long>(); return true;
            case SecsFormat.F4: value = item.FirstValueOrDefault<float>(); return true;
            case SecsFormat.F8: value = item.FirstValueOrDefault<double>(); return true;
            default: return false;
        }
    }

    private static string Text(SecsItem item) => item.Count == 0 ? string.Empty : item.GetString();
}
