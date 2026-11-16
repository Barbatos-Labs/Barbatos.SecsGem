// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// Keeping an attribute value past the message it arrived in.
/// </summary>
/// <remarks>
/// <para>
/// An item belongs to its message, and a message goes back to the pool when its transaction ends.
/// So a store that holds attribute values cannot hold items: it holds the encoded bytes, and
/// builds a fresh item whenever one is asked for. The same reason the spool in the GEM package
/// holds encoded messages rather than live ones.
/// </para>
/// <para>
/// <see cref="SecsItem.ToByteArray"/> says it is for tests and diagnostics, and on a hot path that
/// is right - it allocates twice over. This is not a hot path: it runs once per attribute written
/// to or read out of an in-memory object store, which is a simulator and a test fixture. An
/// application whose object model is real will hold its attributes in whatever its own storage
/// holds and never come through here.
/// </para>
/// </remarks>
internal static class ObjectAttributeStore
{
    /// <summary>Encodes an attribute value for keeping.</summary>
    /// <param name="value">The value, which stays the caller's.</param>
    /// <returns>Its bytes.</returns>
    internal static ReadOnlyMemory<byte> Encode(SecsItem value) => value.ToByteArray();

    /// <summary>Builds a fresh item from kept bytes.</summary>
    /// <param name="encoded">The bytes.</param>
    /// <returns>An item the caller owns.</returns>
    internal static SecsItem Decode(ReadOnlyMemory<byte> encoded) => SecsItem.Decode(encoded);
}
