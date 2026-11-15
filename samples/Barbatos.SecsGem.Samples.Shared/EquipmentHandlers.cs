// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;

namespace Barbatos.SecsGem.Samples.Shared;

/// <summary>
/// What the sample equipment does when a host talks to it.
/// </summary>
/// <remarks>
/// <para>
/// Shared so that the TCP equipment sample and the in-process loopback demo run the same
/// behaviour. Registering them is the only difference between the two: one goes through
/// <c>AddSecsGem(...).OnPrimary(...)</c> and the other through a
/// <see cref="SecsMessageRouterBuilder"/> by hand.
/// </para>
/// <para>
/// Each handler answers its transaction and returns. The connection never needs telling which
/// system bytes to echo - <see cref="SecsTransaction.ReplyAsync"/> stamps the right header itself,
/// which is the one part of a SECS reply that is easy to get silently wrong.
/// </para>
/// </remarks>
public static class EquipmentHandlers
{
    /// <summary>Registers every handler on a routing table.</summary>
    /// <param name="routes">The table.</param>
    /// <returns>The table, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The table is also the declaration of what this equipment implements, so a host asking for
    /// anything else gets S9F5 - or S9F3 for a stream that is not here at all - without a line of
    /// code to make it happen.
    /// </remarks>
    public static SecsMessageRouterBuilder RegisterOn(SecsMessageRouterBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        return routes
            .OnPrimary(1, 1, AreYouThereAsync)
            .OnPrimary(1, 13, EstablishCommunicationsAsync)
            .OnPrimary(2, 41, HostCommandAsync)
            .OnPrimary(6, 11, EventReportAsync);
    }

    /// <summary>Answers S1F1 with S1F2 On Line Data.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the reply.</param>
    /// <returns>A task that completes once the reply is away.</returns>
    public static async ValueTask AreYouThereAsync(SecsTransaction transaction, CancellationToken cancellationToken)
    {
        using var reply = SampleMessages.OnLineData();
        await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers S1F13 with S1F14, accepting the host.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the reply.</param>
    /// <returns>A task that completes once the reply is away.</returns>
    public static async ValueTask EstablishCommunicationsAsync(SecsTransaction transaction, CancellationToken cancellationToken)
    {
        using var reply = SampleMessages.EstablishCommunicationsAcknowledge();
        await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers S2F41 with S2F42, accepting any command.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the reply.</param>
    /// <returns>A task that completes once the reply is away.</returns>
    /// <remarks>
    /// Real equipment checks RCMD against what it can actually do and answers HCACK 1 when it
    /// cannot. Accepting everything keeps the sample about the protocol.
    /// </remarks>
    public static async ValueTask HostCommandAsync(SecsTransaction transaction, CancellationToken cancellationToken)
    {
        using var reply = SampleMessages.HostCommandAcknowledge();
        await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers S6F11 with S6F12 - the role a host plays, kept here so both ends share it.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the reply.</param>
    /// <returns>A task that completes once the reply is away.</returns>
    /// <remarks>
    /// A report sent with W=0 needs no answer, so this replies only when one was asked for.
    /// Replying to a notification throws, which is the right behaviour and the wrong thing to do
    /// in a handler that cannot tell the difference.
    /// </remarks>
    public static async ValueTask EventReportAsync(SecsTransaction transaction, CancellationToken cancellationToken)
    {
        if (!transaction.ReplyExpected)
        {
            return;
        }

        using var reply = SampleMessages.EventReportAcknowledge();
        await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
    }
}
