// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// Keeps a member out of the mapping entirely.
/// </summary>
/// <remarks>
/// <para>
/// SECS-II is positional, which makes this attribute more consequential than its JSON
/// equivalent: removing a member does not just drop a value, it <em>shifts every member after
/// it</em> one position up the item list. That is the behaviour you want when the member is not
/// part of the protocol at all:
/// </para>
/// <code>
/// public sealed class CarrierEvent
/// {
///     public string CarrierId { get; set; } = string.Empty;   // item 1
///     public int SlotCount { get; set; }                      // item 2
///
///     [SecsIgnore]
///     public DateTime ReceivedAt { get; set; }                // local bookkeeping, never sent
/// }
/// </code>
/// <para>
/// Computed members with no setter are skipped automatically, so this is for the ones that do
/// have a setter and still should not travel.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class SecsIgnoreAttribute : Attribute;
