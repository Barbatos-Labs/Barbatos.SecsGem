// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// E87's carriers, load ports and storage locations as stream 14 objects.
/// </summary>
/// <remarks>
/// <para>
/// Object attributes travel on stream 14, where an attribute may be any item at all, and that is
/// why the four items the catalogue defines no format for are readable here without a format
/// having to be invented: <c>CARRIERSTATUS</c>, <c>PORTSTATUS</c> and <c>SLOTMAP</c> are all
/// served by name as attributes. Stream 3 carries them too now, and it can only because this is
/// where the answer already was - each of those readings is checked against the attribute here.
/// </para>
/// <para>
/// <strong>The storage location's three attributes are S3F32's item list.</strong> That
/// transaction reports every storage location as <c>LOC</c>, <c>PORTSTATUS</c> and
/// <c>CARRIERID</c> - an attribute list written by the one transaction E5 has on the subject - so
/// the type is read off a wire format rather than invented, and a test asserts the two routes
/// carry the same three items byte for byte. The shape is also the one
/// <c>Barbatos.SecsGem.Substrates</c> already serves for E90's substrate location: an identity, a
/// state, and what is in it.
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

    /// <summary>The object type a storage location is.</summary>
    /// <remarks>
    /// Advertised only by a machine that has at least one, unlike the other two - see
    /// <see cref="GetObjectTypesAsync"/>. Its attributes are
    /// <c>LocationID</c>, <c>TransferState</c> and <c>CarrierID</c>, which is S3F32's item list
    /// read as an attribute list, and nothing on it is writable: a storage location has no label a
    /// host owns, and its one state is material having moved.
    /// </remarks>
    public const string StorageLocationType = "StorageLocation";

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

        // The first two always: a machine with load ports has them whether or not a carrier is on
        // one, and a host discovering an empty list would conclude this equipment does not do E87.
        //
        // Storage locations only when there are some, and that asymmetry is the same answer S3F31
        // gives. A load port is hardware E87 assumes every machine has; a shelf is hardware it
        // does not, so "no storage locations" is a fact about this machine rather than a gap in
        // what it implements - and a host that discovered the type and then found no instances
        // would have learnt the same thing one round trip later.
        return ValueTask.FromResult<IReadOnlyList<string>>(_manager.StorageLocations.Count > 0
            ? [CarrierType, LoadPortType, StorageLocationType]
            : [CarrierType, LoadPortType]);
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
        else if (string.Equals(objectType, StorageLocationType, StringComparison.Ordinal))
        {
            foreach (var location in _manager.StorageLocations)
            {
                instances.Add(StorageSpecification(location.Location));
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

        var wantsStorage = string.Equals(objectType, StorageLocationType, StringComparison.Ordinal)
            || (objectType.Length == 0 && TryReadStorageSpecification(objectSpecification) is not null);

        if (wantsStorage)
        {
            return ValueTask.FromResult<IReadOnlyList<string>>(StorageAttributeNames);
        }

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

    /// <summary>What a storage location is called as an object - <c>LOC</c> with a prefix.</summary>
    /// <param name="location">The location.</param>
    /// <returns>Its object specification.</returns>
    /// <remarks>
    /// The same convention <see cref="PortSpecification"/> uses, and for the same reason: a bare
    /// number would collide with a carrier called "101". It also keeps the two kinds of place
    /// apart where <c>LOC</c> cannot - a shelf numbered 1 beside a load port 1 is
    /// <c>StorageLocation1</c> and <c>LoadPort1</c>, which are different objects whatever S3F24
    /// has room to say. <see cref="TryReadStorageSpecification"/> is this convention's only
    /// reader.
    /// </remarks>
    public static string StorageSpecification(uint location) => $"{StorageLocationType}{location}";

    /// <summary>Reads a storage location's object specification back into a <c>LOC</c>.</summary>
    /// <param name="objectSpecification">The specification.</param>
    /// <returns>The location, or <see langword="null"/> when it does not name one.</returns>
    public static uint? TryReadStorageSpecification(string objectSpecification)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);

        return objectSpecification.StartsWith(StorageLocationType, StringComparison.Ordinal)
            && uint.TryParse(
                objectSpecification.AsSpan(StorageLocationType.Length),
                System.Globalization.CultureInfo.InvariantCulture,
                out var location)
            ? location
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

    private static readonly string[] StorageAttributeNames =
    [
        "LocationID",
        "TransferState",
        "CarrierID",
    ];

    private List<ObjectAttribute>? Read(string specification, IReadOnlyList<string> wanted)
    {
        if (TryReadStorageSpecification(specification) is { } location)
        {
            var shelf = _manager.FindStorageLocation(location);

            return shelf is null
                ? null
                : Collect(wanted, StorageAttributeNames, name => StorageAttribute(shelf, name));
        }

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

    /// <summary>A storage location's three attributes, which are S3F32's three items.</summary>
    /// <remarks>
    /// <c>LocationID</c> is a <c>U4</c> because <c>LOC</c> is one, and <c>TransferState</c> is the
    /// state's name as text - byte for byte what S3F32 puts in <c>PORTSTATUS</c> and what a load
    /// port's own <c>TransferState</c> attribute carries, because a location borrows a port's
    /// state machine rather than having an eighth of its own.
    /// </remarks>
    private static SecsItem? StorageAttribute(CarrierStorageLocation location, string name) => name switch
    {
        "LocationID" => SecsItem.U4(location.Location),
        "TransferState" => SecsItem.A(location.TransferState.ToString()),

        // Empty rather than absent for an empty shelf, as a port's AssociatedCarrierID is: a host
        // asking for three attributes and getting two cannot tell which one went missing.
        "CarrierID" => SecsItem.A(location.CarrierId ?? string.Empty),
        _ => null,
    };

    private ObjectAcknowledgement Write(string specification, IReadOnlyList<ObjectAttribute> attributes)
    {
        if (TryReadStorageSpecification(specification) is { } location)
        {
            // Nothing on a storage location is writable, which makes it the only object type here
            // with no settable attribute at all. A carrier has LotID and Usage, a port has
            // AccessMode; a shelf has an identity it was configured with, a state that means
            // material has moved, and the name of what is sitting in it. None of those is a host's
            // to assign, and writing the state would move a box by saying so.
            return _manager.FindStorageLocation(location) is null
                ? ObjectAcknowledgement.InvalidObjectInstance
                : ObjectAcknowledgement.InvalidAttribute;
        }

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
