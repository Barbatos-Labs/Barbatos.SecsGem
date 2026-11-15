// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Frozen;

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// Handles one received primary message.
/// </summary>
/// <param name="transaction">The message, and the means to answer it.</param>
/// <param name="cancellationToken">Cancelled when the link closes or the host shuts down.</param>
/// <returns>A task that completes when the message has been dealt with.</returns>
/// <remarks>
/// Runs on the dispatch loop, not on the actor loop, so a slow handler does not delay control
/// messages or state transitions. It does apply back pressure: received primaries queue in a
/// bounded channel of <see cref="SecsGemOptions.PrimaryMessageQueueCapacity"/>, and once that
/// fills the connection stops reading, which is back pressure TCP already knows how to express.
/// </remarks>
public delegate ValueTask SecsPrimaryHandler(SecsTransaction transaction, CancellationToken cancellationToken);

/// <summary>
/// Decides which handler answers a received primary - and, just as importantly, which streams and
/// functions this entity claims to understand at all.
/// </summary>
/// <remarks>
/// <para>
/// The second half is what makes automatic S9F3 and S9F5 possible. "Unrecognized stream" and
/// "unrecognized function" are not errors a connection can detect on its own: it has to be told
/// what this equipment or host implements. A routing table is that declaration, which is why
/// dispatch and error reporting are one mechanism here rather than two.
/// </para>
/// <para>
/// Replace the registration to get handlers out of a container instead of out of delegates -
/// <c>builder.Services.AddSingleton&lt;ISecsMessageRouter, MyRouter&gt;()</c>.
/// </para>
/// </remarks>
public interface ISecsMessageRouter
{
    /// <summary>Gets a value indicating whether anything at all is registered.</summary>
    /// <remarks>
    /// When nothing is, the connection delivers nothing and answers every primary with S9F5 -
    /// which is correct but rarely what was meant, so it is also worth a warning in the log.
    /// </remarks>
    bool IsEmpty { get; }

    /// <summary>Finds the handler for a stream and function.</summary>
    /// <param name="stream">S.</param>
    /// <param name="function">F.</param>
    /// <param name="handler">The handler, when one is registered.</param>
    /// <returns><see langword="true"/> when this entity handles this message.</returns>
    bool TryGetHandler(byte stream, byte function, out SecsPrimaryHandler? handler);

    /// <summary>Gets a value indicating whether this entity implements any function of a stream.</summary>
    /// <param name="stream">S.</param>
    /// <returns><see langword="true"/> when the stream is known.</returns>
    /// <remarks>
    /// This is the S9F3 / S9F5 fork. An unknown stream is S9F3; a known stream with an unknown
    /// function is S9F5. Reporting the wrong one sends a host looking in the wrong place.
    /// </remarks>
    bool IsKnownStream(byte stream);

    /// <summary>Gets the catch-all handler, when one is registered.</summary>
    /// <remarks>
    /// A catch-all turns off automatic S9F3 and S9F5 entirely: an entity that claims every
    /// message cannot also claim not to recognise one. Register it for a proxy, a logger or a
    /// gateway - anything whose job is to forward rather than to implement.
    /// </remarks>
    SecsPrimaryHandler? CatchAll { get; }
}

/// <summary>
/// The default <see cref="ISecsMessageRouter"/>: a frozen table built once at startup.
/// </summary>
/// <remarks>
/// Frozen rather than concurrent, because the table is written during configuration and read on
/// every received message for the life of the process. Handlers cannot be added later, which is
/// deliberate - a routing table that changes under a running link makes S9F5 unreproducible.
/// </remarks>
public sealed class SecsMessageRouter : ISecsMessageRouter
{
    private readonly FrozenDictionary<ushort, SecsPrimaryHandler> _handlers;
    private readonly FrozenSet<byte> _streams;

    /// <summary>Initialises a new router.</summary>
    /// <param name="handlers">The handlers, keyed by stream and function.</param>
    /// <param name="catchAll">The catch-all handler, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handlers"/> is <see langword="null"/>.</exception>
    public SecsMessageRouter(
        IEnumerable<KeyValuePair<(byte Stream, byte Function), SecsPrimaryHandler>> handlers,
        SecsPrimaryHandler? catchAll = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var materialised = handlers.ToList();
        _handlers = materialised.ToFrozenDictionary(entry => Key(entry.Key.Stream, entry.Key.Function), entry => entry.Value);
        _streams = materialised.Select(entry => entry.Key.Stream).ToFrozenSet();
        CatchAll = catchAll;
    }

    /// <inheritdoc/>
    public bool IsEmpty => _handlers.Count == 0 && CatchAll is null;

    /// <inheritdoc/>
    public SecsPrimaryHandler? CatchAll { get; }

    /// <inheritdoc/>
    public bool TryGetHandler(byte stream, byte function, out SecsPrimaryHandler? handler)
        => _handlers.TryGetValue(Key(stream, function), out handler);

    /// <inheritdoc/>
    public bool IsKnownStream(byte stream) => _streams.Contains(stream);

    private static ushort Key(byte stream, byte function) => (ushort)((stream << 8) | function);
}

/// <summary>
/// Collects handlers while the container is being configured.
/// </summary>
/// <remarks>
/// Held as a singleton during configuration and frozen into a <see cref="SecsMessageRouter"/> the
/// first time one is resolved. Nothing here is thread-safe, because configuration is not
/// concurrent.
/// </remarks>
public sealed class SecsMessageRouterBuilder
{
    private readonly Dictionary<(byte Stream, byte Function), SecsPrimaryHandler> _handlers = [];

    /// <summary>Gets the catch-all handler, when one has been registered.</summary>
    public SecsPrimaryHandler? CatchAll { get; private set; }

    /// <summary>Registers the handler for one stream and function.</summary>
    /// <param name="stream">S. Must be 127 or less - the field is seven bits wide.</param>
    /// <param name="function">F. Must be odd: only a primary is received and answered.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stream"/> is above 127, or <paramref name="function"/> is even or zero.</exception>
    /// <exception cref="ArgumentException">A handler is already registered for this stream and function.</exception>
    public SecsMessageRouterBuilder OnPrimary(byte stream, byte function, SecsPrimaryHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stream, (byte)127);

        if (function == 0 || function % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(function),
                function,
                "A received message to handle is a primary, and a primary has an odd function.");
        }

        if (!_handlers.TryAdd((stream, function), handler))
        {
            throw new ArgumentException($"S{stream}F{function} already has a handler.", nameof(function));
        }

        return this;
    }

    /// <summary>Registers the handler for every primary no other handler claims.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">A catch-all is already registered.</exception>
    /// <remarks>Registering one turns off automatic S9F3 and S9F5 - see <see cref="ISecsMessageRouter.CatchAll"/>.</remarks>
    public SecsMessageRouterBuilder OnUnhandledPrimary(SecsPrimaryHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (CatchAll is not null)
        {
            throw new ArgumentException("A catch-all handler is already registered.", nameof(handler));
        }

        CatchAll = handler;
        return this;
    }

    /// <summary>Freezes what has been registered into a router.</summary>
    /// <returns>The router.</returns>
    public ISecsMessageRouter Build() => new SecsMessageRouter(_handlers, CatchAll);
}
