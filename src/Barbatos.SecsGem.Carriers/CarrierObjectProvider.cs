// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// E87's carriers and load ports as stream 14 objects.
/// </summary>
/// <remarks>
/// <para>
/// This is where the three transactions this package does not implement went. S3F20 wants a
/// <c>CARRIERSTATUS</c>, S3F22 and S3F32 want a <c>PORTSTATUS</c>, S3F28 wants a <c>SLOTMAP</c>,
/// and the catalogue has no format for any of them. E87 does not need them to: a carrier and a
/// load port are objects with attributes, and object attributes travel on stream 14, where an
/// attribute may be any item at all. So a host reads a slot map by asking for an attribute, and
/// nothing here had to invent a wire format for one.
/// </para>
/// <para>
/// <strong>A state attribute is not a back door.</strong> Every state is readable and almost none
/// is writable. A host moves a carrier or a port by sending an action on S3F17, which goes through
/// <see cref="CarrierManager"/> and its rules; writing <c>AccessStatus</c> with S14F3 would put a
/// carrier into <see cref="CarrierAccessStatus.CarrierComplete"/> without accessing it and leave
/// the state machine as decoration. So those writes are refused with
/// <see cref="ObjectAcknowledgement.InvalidAttribute"/> and a reason.
/// </para>
/// <para>
/// The two exceptions are the two E87 means a host to set. <c>LotID</c> and <c>Usage</c> are
/// labels rather than states, so they are written straight through. <c>AccessMode</c> is a state,
/// and a write of it is turned into the <c>ChangeAccess</c> action - so it still cannot happen
/// during a transfer, and a host gets the same refusal either way it asks.
/// </para>
/// <para>
/// The attribute names are this equipment's, as they must be: a host discovers them with S14F17
/// rather than assuming them. They are spelled the way E87 spells them.
/// </para>
/// </remarks>
public sealed class CarrierObjectProvider : IObjectProvider
{
    /// <summary>The object type a carrier is - E87's own name for it.</summary>
    public const string CarrierType = "Carrier";

    /// <summary>The object type a load port is.</summary>
    public const string LoadPortType = "LoadPort";

    private readonly CarrierManager _manager;

    /// <summary>Exposes a manager's carriers and ports as objects.</summary>
    /// <param name="manager">The state machines.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <see langword="null"/>.</exception>
    public CarrierObjectProvider(CarrierManager manager)
        => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Both types, always: a machine with load ports has them whether or not a carrier is on
        // one, and a host discovering an empty list would conclude this equipment does not do E87.
        return ValueTask.FromResult<IReadOnlyList<string>>([CarrierType, LoadPortType]);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        cancellationToken.ThrowIfCancellationRequested();

        List<string> instances = [];

        if (string.Equals(objectType, CarrierType, StringComparison.Ordinal))
        {
            foreach (var carrier in _manager.Carriers)
            {
                instances.Add(carrier.CarrierId);
            }
        }
        else if (string.Equals(objectType, LoadPortType, StringComparison.Ordinal))
        {
            foreach (var port in _manager.Ports)
            {
                instances.Add(PortSpecification(port.PortId));
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

        var wantsPort = string.Equals(objectType, LoadPortType, StringComparison.Ordinal)
            || (objectType.Length == 0 && TryReadPortSpecification(objectSpecification) is not null);

        return ValueTask.FromResult<IReadOnlyList<string>>(wantsPort ? PortAttributeNames : CarrierAttributeNames);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(
        ObjectAttributeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<ObjectAttributeValues> values = [];

        foreach (var specification in request.Objects)
        {
            var attributes = Read(specification, request.Attributes);

            if (attributes is null)
            {
                // Left out, which on S14F2 is the only thing an absence can be said with.
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

    /// <summary>What a load port is called as an object - <c>PTN</c> with a prefix.</summary>
    /// <param name="portId">The port.</param>
    /// <returns>Its object specification.</returns>
    /// <remarks>
    /// E87 leaves the naming to the equipment, and a bare number would collide with a carrier
    /// called "1". This is the convention this package uses, and
    /// <see cref="TryReadPortSpecification"/> is its only reader - so a machine that names its
    /// ports differently changes those two methods and nothing else.
    /// </remarks>
    public static string PortSpecification(byte portId) => $"{LoadPortType}{portId}";

    /// <summary>Reads a load port's object specification back into a port number.</summary>
    /// <param name="objectSpecification">The specification.</param>
    /// <returns>The port, or <see langword="null"/> when it does not name one.</returns>
    public static byte? TryReadPortSpecification(string objectSpecification)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);

        return objectSpecification.StartsWith(LoadPortType, StringComparison.Ordinal)
            && byte.TryParse(
                objectSpecification.AsSpan(LoadPortType.Length),
                System.Globalization.CultureInfo.InvariantCulture,
                out var portId)
            ? portId
            : null;
    }

    private static readonly string[] CarrierAttributeNames =
    [
        "CarrierID",
        "IDStatus",
        "SlotMapStatus",
        "AccessStatus",
        "SlotMap",
        "Capacity",
        "SubstrateCount",
        "LotID",
        "Usage",
        "PortID",
    ];

    private static readonly string[] PortAttributeNames =
    [
        "PortID",
        "TransferState",
        "AccessMode",
        "AssociationState",
        "ReservationState",
        "AssociatedCarrierID",
        "ReservedForCarrierID",
    ];

    private List<ObjectAttribute>? Read(string specification, IReadOnlyList<string> wanted)
    {
        if (TryReadPortSpecification(specification) is { } portId)
        {
            var port = _manager.FindPort(portId);

            return port is null ? null : Collect(wanted, PortAttributeNames, name => PortAttribute(port, name));
        }

        var carrier = _manager.FindCarrier(specification);

        return carrier is null ? null : Collect(wanted, CarrierAttributeNames, name => CarrierAttribute(carrier, name));
    }

    private static List<ObjectAttribute> Collect(
        IReadOnlyList<string> wanted,
        IReadOnlyList<string> all,
        Func<string, SecsItem?> read)
    {
        List<ObjectAttribute> attributes = [];

        // An empty request is a request for everything, which is what stream 14 leaves a
        // zero-length ATTRID list meaning.
        foreach (var name in wanted.Count == 0 ? all : wanted)
        {
            if (read(name) is { } value)
            {
                attributes.Add(new ObjectAttribute { Name = name, Value = value });
            }
        }

        return attributes;
    }

    private static SecsItem? CarrierAttribute(Carrier carrier, string name) => name switch
    {
        "CarrierID" => SecsItem.A(carrier.CarrierId),
        "IDStatus" => SecsItem.A(carrier.IdStatus.ToString()),
        "SlotMapStatus" => SecsItem.A(carrier.SlotMapStatus.ToString()),
        "AccessStatus" => SecsItem.A(carrier.AccessStatus.ToString()),

        // The slot map as the bytes it is, which is the whole reason this lives on stream 14:
        // ATTRDATA has no format of its own, so a map needs no format invented for it.
        "SlotMap" => SecsItem.B(carrier.SlotMap.ToArray()),
        "Capacity" => SecsItem.U1(carrier.Capacity),
        "SubstrateCount" => SecsItem.U1(carrier.SubstrateCount),
        "LotID" => SecsItem.A(carrier.LotId),
        "Usage" => SecsItem.A(carrier.Usage),

        // A carrier that is not at a port says so with a zero-length item rather than with port 0.
        "PortID" => carrier.PortId is { } port ? SecsItem.U1(port) : SecsItem.U1(),
        _ => null,
    };

    private static SecsItem? PortAttribute(LoadPort port, string name) => name switch
    {
        "PortID" => SecsItem.U1(port.PortId),
        "TransferState" => SecsItem.A(port.TransferState.ToString()),
        "AccessMode" => SecsItem.A(port.AccessMode.ToString()),
        "AssociationState" => SecsItem.A(port.AssociationState.ToString()),
        "ReservationState" => SecsItem.A(port.ReservationState.ToString()),
        "AssociatedCarrierID" => SecsItem.A(port.AssociatedCarrierId ?? string.Empty),
        "ReservedForCarrierID" => SecsItem.A(port.ReservedForCarrierId ?? string.Empty),
        _ => null,
    };

    private ObjectAcknowledgement Write(string specification, IReadOnlyList<ObjectAttribute> attributes)
    {
        if (TryReadPortSpecification(specification) is { } portId)
        {
            return _manager.FindPort(portId) is null
                ? ObjectAcknowledgement.InvalidObjectInstance
                : WritePort(portId, attributes);
        }

        var carrier = _manager.FindCarrier(specification);

        return carrier is null ? ObjectAcknowledgement.InvalidObjectInstance : WriteCarrier(carrier, attributes);
    }

    private static ObjectAcknowledgement WriteCarrier(Carrier carrier, IReadOnlyList<ObjectAttribute> attributes)
    {
        // Checked before anything is written, so a set naming one read-only attribute changes
        // nothing at all - the same rule the in-memory object provider follows.
        foreach (var attribute in attributes)
        {
            if (attribute.Name is not ("LotID" or "Usage"))
            {
                return ObjectAcknowledgement.InvalidAttribute;
            }
        }

        foreach (var attribute in attributes)
        {
            var text = attribute.Value.Format.IsText() && attribute.Value.Count > 0
                ? attribute.Value.GetString().TrimEnd()
                : string.Empty;

            if (attribute.Name is "LotID")
            {
                carrier.LotId = text;
            }
            else
            {
                carrier.Usage = text;
            }
        }

        return ObjectAcknowledgement.Successful;
    }

    private ObjectAcknowledgement WritePort(byte portId, IReadOnlyList<ObjectAttribute> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Name is not "AccessMode")
            {
                return ObjectAcknowledgement.InvalidAttribute;
            }
        }

        foreach (var attribute in attributes)
        {
            var text = attribute.Value.Format.IsText() && attribute.Value.Count > 0
                ? attribute.Value.GetString().TrimEnd()
                : string.Empty;

            // Through the action rather than onto the field: the state machine refuses a mode
            // change during a transfer, and a host should get that refusal whichever message it
            // asked with.
            var result = _manager.Perform(new CarrierActionRequest
            {
                Action = CarrierActions.ChangeAccess,
                PortId = portId,
                Argument = text,
            });

            if (!result.IsAccepted)
            {
                return result.Acknowledgement is CarrierAcknowledgement.InvalidData
                    ? ObjectAcknowledgement.InvalidAttribute
                    : ObjectAcknowledgement.Error;
            }
        }

        return ObjectAcknowledgement.Successful;
    }
}
