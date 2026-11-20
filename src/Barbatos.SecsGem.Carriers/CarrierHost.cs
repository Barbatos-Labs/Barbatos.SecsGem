// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// The host side of carrier management: the eight requests, and a method per action worth naming.
/// </summary>
/// <remarks>
/// <para>
/// A host in E87 is the end that decides. It tells a machine to proceed with a carrier whose
/// identifier the machine could not verify, holds a port for a carrier on its way, and takes a
/// carrier out when the lot is done - and every one of those is the same message, S3F17, with a
/// different string in it.
/// </para>
/// <para>
/// So there is a method per action for the ones a host sends often, and
/// <see cref="PerformAsync(CarrierActionRequest, CancellationToken)"/> for everything else -
/// including an action this library has never heard of, because <c>CARRIERACTION</c> is free text
/// and a fab may have agreed on one that is not in <see cref="CarrierActions"/>.
/// </para>
/// <para>
/// This end answers nothing: E87 gives the host no primary to receive, and the machine's own
/// reports are collection events and alarms, which belong to the GEM layer. <see cref="HandledMessages"/>
/// is empty and says so.
/// </para>
/// </remarks>
public sealed class CarrierHost
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the host side over a connection.</summary>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public CarrierHost(HsmsConnection connection, ILogger<CarrierHost>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<CarrierHost>.Instance;
    }

    /// <summary>Nothing: E87 gives a host no primary to answer.</summary>
    /// <remarks>
    /// Empty rather than absent, so an application can register this end the same way it registers
    /// every other and get nothing in the routing table - which is the correct outcome and not an
    /// oversight. What a machine tells a host unprompted, it tells it as a collection event.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } = [];

    /// <summary>Sends any carrier or port action - S3F17, answered by S3F18.</summary>
    /// <param name="request">What to ask for.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the machine answered, with the refusing state in <see cref="CarrierActionResult.ErrorText"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// <see cref="CarrierActionRequest.Argument"/> does not travel: S3F17 carries an action, a
    /// carrier and a port, and nothing else. The two actions that need an argument -
    /// <c>ChangeAccess</c> and <c>ChangeServiceStatus</c> - are attribute writes on stream 14 when
    /// they go over a wire. See the remarks on that property.
    /// </remarks>
    public async ValueTask<CarrierActionResult> PerformAsync(
        CarrierActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var reply = await RequestAsync(
            new SecsMessage(3, 17, replyExpected: true)
            {
                Name = "CarrierActionRequest",
                Item = CarrierCodec.ActionBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        var result = CarrierCodec.TryReadActionResult(reply.Item)
            ?? throw new SecsException("S3F18 carried no CAACK.");

        if (!result.IsAccepted)
        {
            _logger.LogInformation(
                "{Action} was refused with {Code}: {Reason}",
                request.Action,
                result.Acknowledgement,
                result.ErrorText);
        }

        return result;
    }

    /// <summary>Tells the machine to go ahead with a carrier it could not verify on its own.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the machine answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <remarks>Settles whichever verification is waiting - the identifier, the slot map, or both.</remarks>
    public ValueTask<CarrierActionResult> ProceedWithCarrierAsync(
        string carrierId,
        CancellationToken cancellationToken = default)
        => PerformAsync(Action(CarrierActions.ProceedWithCarrier, carrierId), cancellationToken);

    /// <summary>Tells the machine not to process a carrier.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the machine answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    public ValueTask<CarrierActionResult> CancelCarrierAsync(
        string carrierId,
        CancellationToken cancellationToken = default)
        => PerformAsync(Action(CarrierActions.CancelCarrier, carrierId), cancellationToken);

    /// <summary>Tells the machine a carrier is coming before it arrives.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the machine answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    public ValueTask<CarrierActionResult> NotifyCarrierAsync(
        string carrierId,
        CancellationToken cancellationToken = default)
        => PerformAsync(Action(CarrierActions.CarrierNotification, carrierId), cancellationToken);

    /// <summary>Ties an expected carrier identifier to a port, so the machine can verify it alone.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="portId">Which port.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the machine answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    public ValueTask<CarrierActionResult> BindCarrierAsync(
        string carrierId,
        byte portId,
        CancellationToken cancellationToken = default)
        => PerformAsync(Action(CarrierActions.BindCarrier, carrierId, portId), cancellationToken);

    /// <summary>Holds a port for a carrier that has not arrived - S3F17.</summary>
    /// <param name="carrierId">Which carrier, or an empty string to hold it for nothing in particular.</param>
    /// <param name="portId">Which port.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the machine answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The same reservation <see cref="ReserveAsync"/> asks for over S3F29, and the reason to
    /// prefer this one: <c>CAACK</c> can say which state refused it and <c>ACKC3</c> cannot.
    /// </remarks>
    public ValueTask<CarrierActionResult> ReserveAtPortAsync(
        string carrierId,
        byte portId,
        CancellationToken cancellationToken = default)
        => PerformAsync(Action(CarrierActions.ReserveAtPort, carrierId, portId), cancellationToken);

    /// <summary>Asks where a carrier is - S3F23, answered by S3F24.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The location, or <see langword="null"/> when the machine does not have that carrier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not carrier location data.</exception>
    /// <remarks>
    /// The number is the machine's own: E87 does not say what a location is, and a machine is free
    /// to report a port, a shelf or an internal station.
    /// </remarks>
    public async ValueTask<uint?> FindCarrierAsync(string carrierId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        using var reply = await RequestAsync(
            new SecsMessage(3, 23, replyExpected: true)
            {
                Name = "CarrierLocationRequest",
                Item = CarrierCodec.CarrierIdBody(carrierId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!CarrierCodec.TryReadLocation(reply.Item, out _, out var location))
        {
            throw new SecsException("S3F24 did not carry carrier location data.");
        }

        return location;
    }

    /// <summary>Asks what a port is doing and what is on it - S3F21, answered by S3F22.</summary>
    /// <param name="portId">Which port.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The status, or <see langword="null"/> when the machine does not have that port.</returns>
    /// <exception cref="SecsException">The reply was not storage port status data.</exception>
    /// <remarks>
    /// <c>PORTSTATUS</c> has no dictionary entry, so <see cref="CarrierPortStatus.Status"/> hands
    /// back whatever text arrived and <see cref="CarrierPortStatus.TransferState"/> is this
    /// library's reading of it, <see langword="null"/> when the word is not one of E87's four.
    /// Another vendor's equipment is entitled to a vocabulary of its own, and reading an
    /// unfamiliar word as the zero of the enum would turn it into a port nobody may use.
    /// </remarks>
    public async ValueTask<CarrierPortStatus?> ReadPortStatusAsync(
        byte portId,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(3, 21, replyExpected: true)
            {
                Name = "StoragePortStatusRequest",
                Item = CarrierCodec.PortBody(portId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!CarrierCodec.TryReadPortStatus(reply.Item, out var status, out var reported, out var carrierId))
        {
            throw new SecsException("S3F22 did not carry storage port status data.");
        }

        if (status.Length == 0 && carrierId.Length == 0 && reported == 0)
        {
            return null;
        }

        return new CarrierPortStatus
        {
            PortId = reported,
            Status = status,
            TransferState = Enum.TryParse<PortTransferState>(status, out var parsed) ? parsed : null,
            CarrierId = carrierId,
        };
    }

    /// <summary>Asks for a carrier's slot map - S3F27, answered by S3F28.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One byte a slot, or <see langword="null"/> when the machine will not say.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not slot map data.</exception>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> covers both of E87's reasons for not answering: there is no such
    /// carrier, or its slot map has not been agreed. The machine is not obliged to distinguish
    /// them, because S3F28 has no acknowledgement code - and the distinction a host actually needs
    /// is between "not read" and "read as empty", which this keeps.
    /// </para>
    /// <para>
    /// The codes inside are the equipment's, as they are everywhere else E87 touches a slot map.
    /// This library reads nothing into them; <c>Barbatos.SecsGem.MaterialFlow</c> is where one
    /// convention about them is written down.
    /// </para>
    /// </remarks>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadSlotMapAsync(
        string carrierId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        using var reply = await RequestAsync(
            new SecsMessage(3, 27, replyExpected: true)
            {
                Name = "SlotMapRequest",
                Item = CarrierCodec.CarrierIdBody(carrierId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!CarrierCodec.TryReadSlotMap(reply.Item, out var reported, out var slotMap))
        {
            throw new SecsException("S3F28 did not carry slot map data.");
        }

        // Two statements rather than a conditional, and not for taste. ReadOnlyMemory<byte> has an
        // implicit conversion from byte[], so `reported.Length == 0 ? null : slotMap` gives the
        // conditional a natural type of ReadOnlyMemory<byte> - the null goes through (byte[])null
        // and arrives as an *empty map* inside a nullable that has a value. That is precisely the
        // distinction this method promises to keep, inverted, and it compiles without a warning.
        if (reported.Length == 0)
        {
            return null;
        }

        return slotMap;
    }

    /// <summary>Asks for every storage location - S3F31, answered by S3F32.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Every storage location the machine has, which is empty when it has none.</returns>
    /// <exception cref="SecsException">The reply was not storage status data.</exception>
    /// <remarks>
    /// <para>
    /// The one request in E87's half of stream 3 that names nothing: S3F31 has no items, so it
    /// means <em>all of them</em>. That removes a distinction every other reader here has to keep
    /// - an empty answer means the machine has no storage, not that it will not say, because there
    /// was nothing named that it could be missing.
    /// </para>
    /// <para>
    /// A storage location is not a load port. It is somewhere inside the machine, addressed by
    /// <c>LOC</c>, and no E87 carrier action names one - a host can read storage and cannot drive
    /// it. <see cref="ReadPortStatusAsync"/> is the one for a port.
    /// </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<CarrierStorageStatus>> ReadStorageStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(3, 31, replyExpected: true) { Name = "StorageStatusRequest" },
            cancellationToken).ConfigureAwait(false);

        if (!CarrierCodec.TryReadStorageStatus(reply.Item, out var locations))
        {
            throw new SecsException("S3F32 did not carry storage status data.");
        }

        return locations;
    }

    /// <summary>Asks what carrier is on a port - S3F25, answered by S3F26.</summary>
    /// <param name="portId">Which port.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The identifier, or <see langword="null"/> when the machine will not say.</returns>
    /// <exception cref="SecsException">The reply was not a carrier identifier read acknowledgement.</exception>
    /// <remarks>
    /// <see langword="null"/> covers both of E87's reasons for not answering: there is nothing on
    /// the port, or its label has not been read. The machine is not obliged to distinguish them,
    /// because <c>ACKC3</c> has no code that would.
    /// </remarks>
    public async ValueTask<string?> ReadCarrierIdAsync(byte portId, CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(3, 25, replyExpected: true)
            {
                Name = "CarrierIdReadRequest",
                Item = CarrierCodec.PortBody(portId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!CarrierCodec.TryReadIdRead(reply.Item, out var acknowledgement, out var carrierId))
        {
            throw new SecsException("S3F26 did not carry a carrier identifier read acknowledgement.");
        }

        return acknowledgement is CarrierRequestAcknowledgement.Accepted && carrierId.Length > 0 ? carrierId : null;
    }

    /// <summary>Reserves a port for a carrier - S3F29, answered by S3F30.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="portId">Which port.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the machine took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// The older way to ask, kept because a machine may implement only this one. It cannot tell
    /// you why it said no; <see cref="ReserveAtPortAsync"/> can.
    /// </remarks>
    public async ValueTask<CarrierRequestAcknowledgement> ReserveAsync(
        string carrierId,
        byte portId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        using var reply = await RequestAsync(
            new SecsMessage(3, 29, replyExpected: true)
            {
                Name = "CarrierReservationRequest",
                Item = CarrierCodec.ReservationBody(carrierId, portId),
            },
            cancellationToken).ConfigureAwait(false);

        return CarrierCodec.TryReadRequestResult(reply.Item)
            ?? throw new SecsException("S3F30 carried no ACKC3.");
    }

    /// <summary>Asks for a carrier to be taken out - S3F33, answered by S3F34.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the machine took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public async ValueTask<CarrierRequestAcknowledgement> CarrierOutAsync(
        string carrierId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        using var reply = await RequestAsync(
            new SecsMessage(3, 33, replyExpected: true)
            {
                Name = "CarrierOutRequest",
                Item = CarrierCodec.CarrierIdBody(carrierId),
            },
            cancellationToken).ConfigureAwait(false);

        return CarrierCodec.TryReadRequestResult(reply.Item)
            ?? throw new SecsException("S3F34 carried no ACKC3.");
    }

    private static CarrierActionRequest Action(string action, string carrierId, byte? portId = null)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        return new CarrierActionRequest { Action = action, CarrierId = carrierId, PortId = portId };
    }

    private async ValueTask<SecsMessage> RequestAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }
}
