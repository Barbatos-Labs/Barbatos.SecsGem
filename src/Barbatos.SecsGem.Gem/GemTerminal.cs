// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Gem;

/// <summary>ACKC10 - what happened to a message somebody wanted shown.</summary>
/// <remarks>
/// Three answers rather than two, and the difference matters to whoever sent the text: a message
/// the operator will never see is not the same as a terminal that is not there. The first is a
/// decision, the second is a fault.
/// </remarks>
public enum GemTerminalAcknowledgement : byte
{
    /// <summary>Accepted for display.</summary>
    Accepted = 0,

    /// <summary>It arrived and will not be shown.</summary>
    WillNotDisplay = 1,

    /// <summary>There is no such terminal, or it cannot be reached.</summary>
    TerminalNotAvailable = 2,

    /// <summary>3 - anything in the range E5 reserves for the equipment and does not name.</summary>
    /// <remarks>
    /// <c>ACKC10</c> names 3 to 63 as reserved, so a code in that range is something the reference
    /// anticipated and did not define. It reads as this member with the arriving code logged,
    /// rather than being cast to an enum value with no name - which is what this used to do.
    /// </remarks>
    Other = 3,
}

/// <summary>
/// Text somebody wants an operator to read.
/// </summary>
/// <remarks>
/// <para>
/// One type for all three ways E5 says it. S10F3 carries a single block, S10F5 carries several,
/// and S10F9 carries one to every terminal at once - which are the same message with
/// <see cref="Lines"/> and <see cref="IsBroadcast"/> between them, so the interface that displays
/// them is one method rather than three.
/// </para>
/// <para>
/// A single-block message is a one-entry <see cref="Lines"/>. That is a deliberate shape: a
/// terminal that lays out two lines and a terminal that lays out one are the same code, and the
/// alternative was a nullable string beside a nullable list.
/// </para>
/// </remarks>
public sealed class GemTerminalMessage
{
    /// <summary>Initialises a message.</summary>
    /// <param name="terminalId">TID. Meaningless for a broadcast, which goes to all of them.</param>
    /// <param name="lines">The blocks of text, in order. Copied.</param>
    /// <param name="isBroadcast">Whether this arrived as S10F9.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/>, or one of them, is <see langword="null"/>.</exception>
    public GemTerminalMessage(byte terminalId, IReadOnlyList<string> lines, bool isBroadcast = false)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var copy = new string[lines.Count];

        for (var i = 0; i < lines.Count; i++)
        {
            copy[i] = lines[i] ?? throw new ArgumentNullException(
                nameof(lines),
                $"Block {i} is null. A terminal cannot display a hole.");
        }

        TerminalId = terminalId;
        Lines = copy;
        IsBroadcast = isBroadcast;
    }

    /// <summary>Gets the terminal this is for. Zero is the usual value for equipment with one.</summary>
    public byte TerminalId { get; }

    /// <summary>Gets the blocks of text, in order. One entry for a single-block message.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Gets a value indicating whether this is for every terminal rather than one.</summary>
    /// <remarks>
    /// A broadcast is E5's way of saying something everybody should see, and
    /// <see cref="TerminalId"/> carries nothing for one. Equipment with a single terminal can treat
    /// it as any other message; equipment with several has to show it on all of them.
    /// </remarks>
    public bool IsBroadcast { get; }

    /// <summary>Returns where it is going and how much of it there is.</summary>
    /// <returns>For example <c>terminal 0: 2 block(s)</c>.</returns>
    public override string ToString() => IsBroadcast
        ? $"broadcast: {Lines.Count} block(s)"
        : $"terminal {TerminalId}: {Lines.Count} block(s)";
}

/// <summary>
/// Where a message from the host reaches an operator.
/// </summary>
/// <remarks>
/// <para>
/// E30's terminal services capability, and the one GEM capability whose whole content is outside
/// this library: a terminal is a screen, a line on an HMI, a row in an operator's work queue, or a
/// chime. So this is one method, and what it does with the text is the application's business.
/// </para>
/// <para>
/// The other direction - the operator typing something for the host - is
/// <c>GemEquipment.SendTerminalRequestAsync</c> rather than a method here, because the application
/// initiates it. Nothing calls into an operator.
/// </para>
/// <para>
/// It may be called from the connection's dispatch loop, so a slow one holds up every other
/// message on the link. An implementation that has to wait for a human has already got the wrong
/// answer: accept the message, return, and show it in your own time.
/// </para>
/// </remarks>
public interface IGemTerminal
{
    /// <summary>Shows a message to an operator.</summary>
    /// <param name="message">The message. Owns its own text, so it is safe to keep.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The ACKC10 to answer with.</returns>
    ValueTask<GemTerminalAcknowledgement> DisplayAsync(
        GemTerminalMessage message,
        CancellationToken cancellationToken);
}

/// <summary>
/// A terminal that remembers what it was asked to display and shows it to nobody.
/// </summary>
/// <remarks>
/// Enough for a simulator, a test, or equipment whose operator interface is somewhere else and
/// only needs the messages to arrive. Real equipment implements <see cref="IGemTerminal"/> over
/// whatever its operators actually look at.
/// </remarks>
public sealed class GemInMemoryTerminal : IGemTerminal
{
    private readonly ConcurrentQueue<GemTerminalMessage> _displayed = new();

    /// <summary>Gets or sets how many messages to keep. Older ones are dropped.</summary>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets or sets what every display is answered with.</summary>
    /// <remarks>
    /// Settable so a test can be a terminal that is switched off. The default accepts, because a
    /// terminal that refuses everything is the less useful half of the behaviour.
    /// </remarks>
    public GemTerminalAcknowledgement Acknowledgement { get; set; } = GemTerminalAcknowledgement.Accepted;

    /// <summary>Gets what has been displayed, oldest first.</summary>
    public IReadOnlyCollection<GemTerminalMessage> Displayed => _displayed;

    /// <inheritdoc/>
    public ValueTask<GemTerminalAcknowledgement> DisplayAsync(
        GemTerminalMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Acknowledgement == GemTerminalAcknowledgement.Accepted)
        {
            _displayed.Enqueue(message);

            while (_displayed.Count > Capacity && _displayed.TryDequeue(out _))
            {
                // Bounded, because a host that broadcasts every minute for a month should not be
                // able to grow a simulator's memory without limit.
            }
        }

        return ValueTask.FromResult(Acknowledgement);
    }
}
