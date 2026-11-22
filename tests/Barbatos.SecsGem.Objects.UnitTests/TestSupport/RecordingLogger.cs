// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// A logger that keeps what it was told, so a test can assert on a diagnostic.
/// </summary>
/// <remarks>
/// Added for stream 14's last two pairs, where five controls earned it at once: S14F12 carries no
/// acknowledgement, so "I could not read your query" and "nothing matched" are the same reply - and
/// the log is the only place the difference exists. A reader guard that fails open is otherwise
/// indistinguishable from a legitimate empty result.
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
