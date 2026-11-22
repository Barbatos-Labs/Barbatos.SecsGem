// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Material.UnitTests;

/// <summary>
/// A logger that keeps what it was told, so a test can assert on a diagnostic.
/// </summary>
/// <remarks>
/// Added for the stream 4 handoff protocol, where a control earned it: relaxing the arity guard on
/// S4F27 made a malformed body throw inside the dispatch loop instead of being dropped with a
/// reason, and no test could tell the two apart - both end with the event not being raised. The
/// log line is the only place "rejected, and here is why" exists.
/// </remarks>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <summary>Gets everything logged, in order.</summary>
    public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries;

    /// <summary>Gets everything logged at one level.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The messages, in order.</returns>
    public IReadOnlyList<string> From(LogLevel level)
        => [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
