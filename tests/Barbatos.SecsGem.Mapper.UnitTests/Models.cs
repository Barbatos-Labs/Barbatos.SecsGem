// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Mapping;

namespace Barbatos.SecsGem.Mapper.UnitTests;

/// <summary>
/// The shapes the mapper has to handle, each here because it maps differently.
/// </summary>
/// <remarks>
/// The source generator runs over this file, so every type in it gets a generated contract as well
/// as a reflected one - which is what lets the tests compare the two for every shape rather than
/// for the one somebody remembered.
/// </remarks>
internal static class Models
{
    /// <summary>A positional record with no attributes at all - the convention-only case.</summary>
    public sealed record Person(string Name, int Age, string[] Emails);

    /// <summary>A plain class with settable properties: order comes from declaration order.</summary>
    public sealed class Settable
    {
        public string Model { get; set; } = string.Empty;

        public uint Identifier { get; set; }

        public bool Enabled { get; set; }
    }

    /// <summary>Every member pins its own order, and they are declared in the wrong one on purpose.</summary>
    public sealed class FullyOrdered
    {
        [SecsItem(Order = 2)]
        public string Third { get; set; } = string.Empty;

        [SecsItem(Order = 0)]
        public string First { get; set; } = string.Empty;

        [SecsItem(Order = 1)]
        public string Second { get; set; } = string.Empty;
    }

    /// <summary>A member the mapper must leave alone.</summary>
    public sealed class WithIgnored
    {
        public uint Kept { get; set; }

        [SecsIgnore]
        public string Dropped { get; set; } = "not on the wire";

        public uint AlsoKept { get; set; }
    }

    /// <summary>Formats, labels, fixed widths and list-flattening, all pinned by attribute.</summary>
    [SecsMessage(2, 41, Name = "HostCommandSend")]
    public sealed class Pinned
    {
        [SecsItem(SecsFormat.U4, Name = "ECID")]
        public int Identifier { get; set; }

        [SecsItem(Length = 16, Name = "TIMESTAMP")]
        public string Timestamp { get; set; } = string.Empty;

        [SecsItem(Length = 4, Padding = '0', Name = "CODE")]
        public string Code { get; set; } = string.Empty;

        [SecsItem(AsList = true, Name = "LIMITS")]
        public int[] Limits { get; set; } = [];

        [SecsItem(Optional = true, Name = "NOTE")]
        public string? Note { get; set; }
    }

    /// <summary>A message type whose members are themselves mapped types - the closure case.</summary>
    [SecsMessage(6, 11)]
    public sealed class Report
    {
        public uint DataId { get; set; }

        public uint EventId { get; set; }

        public List<ReportValue> Values { get; set; } = [];

        public ReportHeader Header { get; set; } = new();
    }

    /// <summary>Reached only through <see cref="Report.Values"/>, never named by a call site.</summary>
    public sealed class ReportValue
    {
        public string Name { get; set; } = string.Empty;

        public double Value { get; set; }
    }

    /// <summary>Reached only through <see cref="Report.Header"/>.</summary>
    public sealed class ReportHeader
    {
        public string Equipment { get; set; } = string.Empty;

        public uint Sequence { get; set; }
    }

    /// <summary>A base class and a derived one: base-declared members come first.</summary>
    public class Identified
    {
        public uint Identifier { get; set; }
    }

    /// <summary>Derived from <see cref="Identified"/>.</summary>
    public sealed class Named : Identified
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>An enum member, which maps through its underlying type.</summary>
    public sealed class WithEnum
    {
        public ControlState State { get; set; }

        public uint Sequence { get; set; }
    }

    /// <summary>The control state of a piece of equipment, as GEM numbers them.</summary>
    public enum ControlState : byte
    {
        /// <summary>Equipment offline.</summary>
        EquipmentOffline = 1,

        /// <summary>Online, local control.</summary>
        OnlineLocal = 4,

        /// <summary>Online, remote control.</summary>
        OnlineRemote = 5,
    }

    /// <summary>
    /// Init-only properties that no constructor parameter covers: the generator leaves this one to
    /// reflection on purpose, because it cannot call an init accessor and reflection can.
    /// </summary>
    public sealed class InitOnly
    {
        public string Model { get; init; } = string.Empty;

        public uint Identifier { get; init; }
    }

    /// <summary>
    /// Order on one member and not the other - the shape BSG001 reports. Kept here so the
    /// analyzer has something real to fire on, and suppressed so this project still builds clean.
    /// </summary>
#pragma warning disable BSG001
    public sealed class PartiallyOrdered
    {
        [SecsItem(Order = 0)]
        public string First { get; set; } = string.Empty;

        public string Second { get; set; } = string.Empty;
    }
#pragma warning restore BSG001
}
