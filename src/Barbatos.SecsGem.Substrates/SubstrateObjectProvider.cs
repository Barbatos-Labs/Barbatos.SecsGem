// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.Substrates;

/// <summary>
/// E90's substrates and substrate locations as stream 14 objects.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of E90's wire presence. The standard defines no messages: a substrate is an
/// object, its attributes are read with <c>GetAttr</c>, and the only other thing a host sees is
/// whichever collection events the equipment chose to define for the transitions. So where E87 had
/// five transactions and this to fall back on, E90 has only this.
/// </para>
/// <para>
/// <strong>States are readable and not writable.</strong> A host moves a substrate by telling the
/// machine to do something - a remote command, a process job - not by assigning
/// <c>SubstrateProcessingState</c> over S14F3, which would mark a wafer processed without
/// processing it. The settable attributes are the labels a host owns: <c>LotID</c>,
/// <c>SubstrateUsage</c>, <c>SubstrateType</c>, <c>SubstrateDestinationLocID</c> and
/// <c>AcquiredID</c>, and the destination goes through the tracker so a location that does not
/// exist is refused rather than stored.
/// </para>
/// <para>
/// The attribute names are spelled the way E90 spells them, and a host discovers them with S14F17
/// rather than assuming them.
/// </para>
/// </remarks>
public sealed class SubstrateObjectProvider : IObjectProvider
{
    /// <summary>The object type a substrate is - E90's own name for it.</summary>
    public const string SubstrateType = "Substrate";

    /// <summary>The object type a substrate location is.</summary>
    public const string SubstrateLocationType = "SubstrateLocation";

    private static readonly string[] SubstrateAttributeNames =
    [
        "SubstrateID",
        "SubstrateState",
        "SubstrateProcessingState",
        "SubstrateLocID",
        "SubstrateSourceLocID",
        "SubstrateDestinationLocID",
        "SlotID",
        "LotID",
        "SubstrateUsage",
        "SubstrateType",
        "AcquiredID",
    ];

    private static readonly string[] LocationAttributeNames =
    [
        "SubstrateLocID",
        "SubstrateLocState",
        "SubstrateID",
    ];

    private readonly SubstrateTracker _tracker;

    /// <summary>Exposes a tracker's substrates and locations as objects.</summary>
    /// <param name="tracker">The state machines.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tracker"/> is <see langword="null"/>.</exception>
    public SubstrateObjectProvider(SubstrateTracker tracker)
        => _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<string>>([SubstrateType, SubstrateLocationType]);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        cancellationToken.ThrowIfCancellationRequested();

        List<string> instances = [];

        if (string.Equals(objectType, SubstrateType, StringComparison.Ordinal))
        {
            foreach (var substrate in _tracker.Substrates)
            {
                instances.Add(substrate.SubstrateId);
            }
        }
        else if (string.Equals(objectType, SubstrateLocationType, StringComparison.Ordinal))
        {
            foreach (var location in _tracker.Locations)
            {
                instances.Add(location.LocationId);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(instances);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(
        string objectSpecification,
        string objectType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(objectType);
        cancellationToken.ThrowIfCancellationRequested();

        // A substrate and a location can share a name as far as this package knows, so the type
        // decides when it is given and the substrates win when it is not - which is the commoner
        // question by a long way.
        var wantsLocation = string.Equals(objectType, SubstrateLocationType, StringComparison.Ordinal)
            || (objectType.Length == 0
                && _tracker.FindSubstrate(objectSpecification) is null
                && _tracker.FindLocation(objectSpecification) is not null);

        return ValueTask.FromResult<IReadOnlyList<string>>(
            wantsLocation ? LocationAttributeNames : SubstrateAttributeNames);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(
        ObjectAttributeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var wantsLocation = string.Equals(request.ObjectType, SubstrateLocationType, StringComparison.Ordinal);
        List<ObjectAttributeValues> values = [];

        foreach (var specification in request.Objects)
        {
            var attributes = Read(specification, request.Attributes, wantsLocation);

            if (attributes is null)
            {
                // Left out, which on S14F2 is the only way an absence can be said.
                continue;
            }

            values.Add(new ObjectAttributeValues
            {
                ObjectSpecification = specification,
                Attributes = attributes,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectAttributeValues>>(values);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(
        ObjectAttributeWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        List<ObjectSetResult> results = [];

        foreach (var specification in write.Objects)
        {
            results.Add(new ObjectSetResult
            {
                ObjectSpecification = specification,
                Acknowledgement = Write(specification, write.Attributes),
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectSetResult>>(results);
    }

    private List<ObjectAttribute>? Read(string specification, IReadOnlyList<string> wanted, bool wantsLocation)
    {
        if (!wantsLocation && _tracker.FindSubstrate(specification) is { } substrate)
        {
            return Collect(wanted, SubstrateAttributeNames, name => SubstrateAttribute(substrate, name));
        }

        return _tracker.FindLocation(specification) is { } location
            ? Collect(wanted, LocationAttributeNames, name => LocationAttribute(location, name))
            : null;
    }

    private static List<ObjectAttribute> Collect(
        IReadOnlyList<string> wanted,
        IReadOnlyList<string> all,
        Func<string, SecsItem?> read)
    {
        List<ObjectAttribute> attributes = [];

        foreach (var name in wanted.Count == 0 ? all : wanted)
        {
            if (read(name) is { } value)
            {
                attributes.Add(new ObjectAttribute { Name = name, Value = value });
            }
        }

        return attributes;
    }

    private static SecsItem? SubstrateAttribute(Substrate substrate, string name) => name switch
    {
        "SubstrateID" => SecsItem.A(substrate.SubstrateId),

        // E90 calls the transport state the substrate state, and this package calls it what E90
        // calls it on the wire while naming it for what it is in code.
        "SubstrateState" => SecsItem.A(substrate.TransportState.ToString()),
        "SubstrateProcessingState" => SecsItem.A(substrate.ProcessingState.ToString()),
        "SubstrateLocID" => SecsItem.A(substrate.LocationId),
        "SubstrateSourceLocID" => SecsItem.A(substrate.SourceLocationId),
        "SubstrateDestinationLocID" => SecsItem.A(substrate.DestinationLocationId),
        "SlotID" => SecsItem.U1(substrate.SlotId),
        "LotID" => SecsItem.A(substrate.LotId),
        "SubstrateUsage" => SecsItem.A(substrate.Usage),
        "SubstrateType" => SecsItem.A(substrate.SubstrateType),
        "AcquiredID" => SecsItem.A(substrate.AcquiredId),
        _ => null,
    };

    private static SecsItem? LocationAttribute(SubstrateLocation location, string name) => name switch
    {
        "SubstrateLocID" => SecsItem.A(location.LocationId),
        "SubstrateLocState" => SecsItem.A(location.State.ToString()),
        "SubstrateID" => SecsItem.A(location.SubstrateId ?? string.Empty),
        _ => null,
    };

    private ObjectAcknowledgement Write(string specification, IReadOnlyList<ObjectAttribute> attributes)
    {
        if (_tracker.FindSubstrate(specification) is null)
        {
            return _tracker.FindLocation(specification) is null
                ? ObjectAcknowledgement.InvalidObjectInstance

                // A location has nothing a host may set: its occupancy follows the substrates, and
                // its name is its name.
                : ObjectAcknowledgement.InvalidAttribute;
        }

        // Checked before anything is written, so a set naming one read-only attribute changes
        // nothing - the same rule every other provider in this stack follows.
        foreach (var attribute in attributes)
        {
            if (!IsSettable(attribute.Name))
            {
                return ObjectAcknowledgement.InvalidAttribute;
            }
        }

        foreach (var attribute in attributes)
        {
            var text = attribute.Value.Format.IsText() && attribute.Value.Count > 0
                ? attribute.Value.GetString().TrimEnd()
                : string.Empty;

            // Through the tracker rather than onto the field, so a destination that names no
            // declared location is refused here exactly as it would be anywhere else.
            var result = attribute.Name switch
            {
                "LotID" => _tracker.Describe(specification, lotId: text),
                "SubstrateUsage" => _tracker.Describe(specification, usage: text),
                "SubstrateType" => _tracker.Describe(specification, substrateType: text),
                "SubstrateDestinationLocID" => _tracker.Describe(specification, destinationLocationId: text),
                _ => _tracker.Describe(specification, acquiredId: text),
            };

            if (!result.IsAccepted)
            {
                return ObjectAcknowledgement.InvalidAttribute;
            }
        }

        return ObjectAcknowledgement.Successful;
    }

    private static bool IsSettable(string name)
        => name is "LotID"
            or "SubstrateUsage"
            or "SubstrateType"
            or "SubstrateDestinationLocID"
            or "AcquiredID";
}
