// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// The one rule this package keeps about reading a state out of a word.
/// </summary>
/// <remarks>
/// <para>
/// E87's states travel as text here - <c>PORTSTATUS</c>, <c>CARRIERSTATUS</c> and the stream 14
/// state attributes are all written as the state's own name - because the items that carry them
/// have no entry in the catalogue's dictionary and a name is the one encoding that needs no
/// agreement about numbers. Reading one back has to be the exact inverse, and
/// <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> is not: <strong>it also accepts the
/// number</strong>. <c>"2"</c> parses to whichever state happens to be 2.
/// </para>
/// <para>
/// That is not a small tolerance, it is a wrong answer waiting for the right machine. The one
/// numeric vocabulary in this area that the catalogue <em>does</em> define disagrees with this
/// library's: <c>ACCESSMODE</c> has 0 for automatic and 1 for manual, where
/// <see cref="PortAccessMode"/> has <see cref="PortAccessMode.Manual"/> at 0. So a host that sent
/// the dictionary's number would have got the opposite mode, silently, from a reader that meant
/// to be generous. Numbers are not this package's to read; names are.
/// </para>
/// </remarks>
internal static class CarrierWords
{
    /// <summary>Reads a state from its name, and only from its name.</summary>
    /// <typeparam name="TState">The state machine.</typeparam>
    /// <param name="word">What arrived.</param>
    /// <param name="ignoreCase">Whether the name may differ in case - for a word a person typed rather than one a machine sent.</param>
    /// <returns>The state, or <see langword="null"/> when the word is not one of its names.</returns>
    /// <remarks>
    /// Parsed and then checked against the name it parsed to, which is what makes a number fail:
    /// <c>"2"</c> parses, and the name of what it parsed to is not <c>"2"</c>. The same check
    /// rejects a flags-style list of names, since no single state is called
    /// <c>"NotAccessed, InAccess"</c>.
    /// </remarks>
    internal static TState? ReadName<TState>(string? word, bool ignoreCase = false)
        where TState : struct, Enum
        => word is not null
            && Enum.TryParse<TState>(word, ignoreCase, out var parsed)
            && Enum.GetName(parsed) is { } name
            && string.Equals(name, word, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? parsed
                : null;
}
