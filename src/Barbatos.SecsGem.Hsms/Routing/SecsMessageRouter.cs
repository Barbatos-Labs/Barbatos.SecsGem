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
    private readonly Dictionary<(byte Stream, byte Function), Func<IServiceProvider, SecsPrimaryHandler>> _deferred = [];
    private Func<IServiceProvider, SecsPrimaryHandler>? _deferredCatchAll;

    /// <summary>Gets the catch-all handler, when one has been registered directly.</summary>
    /// <remarks>
    /// A catch-all registered as a factory does not appear here, because it does not exist until a
    /// provider resolves it. Use <see cref="HasCatchAll"/> to ask the question either way.
    /// </remarks>
    public SecsPrimaryHandler? CatchAll { get; private set; }

    /// <summary>Gets a value indicating whether a catch-all has been registered, by either route.</summary>
    public bool HasCatchAll => CatchAll is not null || _deferredCatchAll is not null;

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
        Validate(stream, function);
        Claim(stream, function);

        _handlers.Add((stream, function), handler);
        return this;
    }

    /// <summary>Registers a handler that is built from the container when the router is resolved.</summary>
    /// <param name="stream">S. Must be 127 or less.</param>
    /// <param name="function">F. Must be odd.</param>
    /// <param name="factory">Builds the handler from the service provider.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stream"/> is above 127, or <paramref name="function"/> is even or zero.</exception>
    /// <exception cref="ArgumentException">A handler is already registered for this stream and function.</exception>
    /// <remarks>
    /// <para>
    /// A handler that needs a service cannot be a closure written during configuration, because the
    /// container does not exist yet. This overload hands it the provider instead, and it is the one
    /// any real equipment wants - a handler with no dependencies is a demonstration, not an
    /// application.
    /// </para>
    /// <para>
    /// <strong>Resolve inside the handler, not inside the factory</strong>, for anything that
    /// depends on the connection:
    /// </para>
    /// <code>
    /// // Right: resolved when a message arrives, long after the graph is complete.
    /// OnPrimary(1, 13, provider => (transaction, token) =>
    ///     provider.GetRequiredService&lt;MyService&gt;().HandleAsync(transaction, token));
    ///
    /// // Wrong, when MyService depends on HsmsConnection: this runs while the router is being
    /// // built, so it re-enters the half-built router and recurses until the stack runs out.
    /// OnPrimary(1, 13, provider => provider.GetRequiredService&lt;MyService&gt;().HandleAsync);
    /// </code>
    /// <para>
    /// The factory runs when the router is resolved, and the connection is resolved <em>after</em>
    /// the router it was given. Nothing here can detect the difference for you: a factory is opaque
    /// to the container, which is exactly why it breaks the cycle when used correctly and loops
    /// when it is not.
    /// </para>
    /// </remarks>
    public SecsMessageRouterBuilder OnPrimary(byte stream, byte function, Func<IServiceProvider, SecsPrimaryHandler> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Validate(stream, function);
        Claim(stream, function);

        _deferred.Add((stream, function), factory);
        return this;
    }

    private static void Validate(byte stream, byte function)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stream, (byte)127);

        if (function == 0 || function % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(function),
                function,
                "A received message to handle is a primary, and a primary has an odd function.");
        }
    }

    private void Claim(byte stream, byte function)
    {
        if (_handlers.ContainsKey((stream, function)) || _deferred.ContainsKey((stream, function)))
        {
            throw new ArgumentException($"S{stream}F{function} already has a handler.", nameof(function));
        }
    }

    /// <summary>Registers the handler for every primary no other handler claims.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">A catch-all is already registered.</exception>
    /// <remarks>Registering one turns off automatic S9F3 and S9F5 - see <see cref="ISecsMessageRouter.CatchAll"/>.</remarks>
    public SecsMessageRouterBuilder OnUnhandledPrimary(SecsPrimaryHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (HasCatchAll)
        {
            throw new ArgumentException("A catch-all handler is already registered.", nameof(handler));
        }

        CatchAll = handler;
        return this;
    }

    /// <summary>Registers a catch-all that is built from the container when the router is resolved.</summary>
    /// <param name="factory">Builds the handler from the service provider.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">A catch-all is already registered.</exception>
    /// <remarks>Registering one turns off automatic S9F3 and S9F5 - see <see cref="ISecsMessageRouter.CatchAll"/>.</remarks>
    public SecsMessageRouterBuilder OnUnhandledPrimary(Func<IServiceProvider, SecsPrimaryHandler> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (HasCatchAll)
        {
            throw new ArgumentException("A catch-all handler is already registered.", nameof(factory));
        }

        _deferredCatchAll = factory;
        return this;
    }

    /// <summary>Freezes what has been registered into a router.</summary>
    /// <returns>The router.</returns>
    /// <exception cref="InvalidOperationException">A handler was registered as a factory, which needs a provider to build.</exception>
    public ISecsMessageRouter Build() => Build(null);

    /// <summary>Freezes what has been registered into a router, building any deferred handler.</summary>
    /// <param name="provider">The provider to build deferred handlers from, or <see langword="null"/> when there are none.</param>
    /// <returns>The router.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="provider"/> is <see langword="null"/> and a handler was registered as a factory.</exception>
    public ISecsMessageRouter Build(IServiceProvider? provider)
    {
        if (provider is null && (_deferred.Count > 0 || _deferredCatchAll is not null))
        {
            throw new InvalidOperationException(
                "This routing table has handlers registered as factories, which can only be built from a "
                + "service provider. Call Build(provider), or register the handlers directly.");
        }

        if (_deferred.Count == 0 && _deferredCatchAll is null)
        {
            return new SecsMessageRouter(_handlers, CatchAll);
        }

        var resolved = new Dictionary<(byte Stream, byte Function), SecsPrimaryHandler>(_handlers);

        foreach (var (key, factory) in _deferred)
        {
            resolved[key] = factory(provider!)
                ?? throw new InvalidOperationException($"The factory for S{key.Stream}F{key.Function} returned null.");
        }

        var catchAll = CatchAll
            ?? (_deferredCatchAll is null
                ? null
                : _deferredCatchAll(provider!)
                  ?? throw new InvalidOperationException("The catch-all factory returned null."));

        return new SecsMessageRouter(resolved, catchAll);
    }
}
