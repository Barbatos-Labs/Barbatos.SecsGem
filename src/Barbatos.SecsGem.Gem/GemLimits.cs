// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem;

/// <summary>LIMITACK - what happened to one limit in a definition.</summary>
/// <remarks>
/// The codes are the ones <c>Barbatos.SecsGem.Catalog</c> documents for LIMITACK, which is a data
/// item rather than one of the message rows that capability has flagged as representative - see
/// <see cref="GemVariableLimit"/> for what that means here.
/// </remarks>
public enum GemVariableLimitAcknowledgement : byte
{
    /// <summary>Acknowledged; the limit is in force.</summary>
    Accepted = 0,

    /// <summary>The definition itself is wrong - a bound that is not a number, or no bounds at all.</summary>
    DefinitionError = 1,

    /// <summary>Understood, and not now.</summary>
    CannotPerformNow = 2,

    /// <summary>There is no such variable to put a limit on.</summary>
    NoSuchVariable = 3,

    /// <summary>The bounds are a number this equipment will not accept - an upper below its lower.</summary>
    ValueError = 4,
}

/// <summary>
/// One limit zone on a variable: the band its value is expected to stay inside.
/// </summary>
/// <remarks>
/// <para>
/// E30's limits monitoring exists so a host does not have to poll. The host says what band it
/// cares about, and the equipment reports a collection event when the value leaves it or comes
/// back - which is one message per crossing rather than one per second for ever.
/// </para>
/// <para>
/// <strong>The band is the deadband.</strong> E5 calls the bounds UPPERDB and LOWERDB, and the
/// reason is chatter: a value sitting exactly on a threshold would otherwise report a crossing
/// every time it wobbled. A value inside the band is in the zone and stays there until it is
/// outside, so a wobble inside the band is silent.
/// </para>
/// <para>
/// <strong>What is uncertain here, said once.</strong> Of the four messages in this capability,
/// <c>Barbatos.SecsGem.Catalog</c> flags S2F45, S2F46 and S2F48 as representative entries rather
/// than definitions - only the S2F47 request is firm. The body shapes this layer reads and writes
/// follow SEMI E5, and the catalogue's item lists corroborate them item for item and in order,
/// including the per-variable LVACK that sits beside the per-limit LIMITACK. What a test here can
/// prove is that both ends of this library agree; what only a copy of E5 can tell you is whether
/// they agree with somebody else's host. A fab integrating against one should check the nesting -
/// it is in one place, <c>GemLimitCodec</c>, by design.
/// </para>
/// </remarks>
public sealed class GemVariableLimit
{
    /// <summary>Initialises a limit.</summary>
    /// <param name="id">LIMITID.</param>
    /// <param name="lower">LOWERDB, the bottom of the band.</param>
    /// <param name="upper">UPPERDB, the top of the band.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="upper"/> is below <paramref name="lower"/>, or either is not a number.</exception>
    public GemVariableLimit(uint id, double lower, double upper)
    {
        if (double.IsNaN(lower) || double.IsNaN(upper))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lower),
                "A limit band needs two numbers. NaN is not a bound a value can be inside or outside of.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(upper, lower);

        Id = id;
        Lower = lower;
        Upper = upper;
    }

    /// <summary>Gets the limit identifier.</summary>
    public uint Id { get; }

    /// <summary>Gets the bottom of the band.</summary>
    public double Lower { get; }

    /// <summary>Gets the top of the band.</summary>
    public double Upper { get; }

    /// <summary>Whether a value is inside this band.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when it is between the bounds, inclusive.</returns>
    /// <remarks>
    /// Inclusive at both ends, so a value exactly on a bound is inside. That is the choice that
    /// makes a band of <c>[x, x]</c> mean "exactly x" rather than nothing at all.
    /// </remarks>
    public bool Contains(double value) => value >= Lower && value <= Upper;

    /// <summary>Returns the identifier and the band.</summary>
    /// <returns>For example <c>limit 1 [900, 1100]</c>.</returns>
    public override string ToString() => $"limit {Id} [{Lower}, {Upper}]";
}

/// <summary>The limits a host wants on one variable - one entry of S2F45.</summary>
public sealed class GemVariableLimitDefinition
{
    /// <summary>Initialises a definition.</summary>
    /// <param name="variableId">VID.</param>
    /// <param name="limits">The bands. An empty list removes every limit on this variable. Copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/>, or one of them, is <see langword="null"/>.</exception>
    public GemVariableLimitDefinition(uint variableId, IReadOnlyList<GemVariableLimit> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var copy = new GemVariableLimit[limits.Count];

        for (var i = 0; i < limits.Count; i++)
        {
            copy[i] = limits[i] ?? throw new ArgumentNullException(nameof(limits), $"Limit {i} is null.");
        }

        VariableId = variableId;
        Limits = copy;
    }

    /// <summary>Gets the variable.</summary>
    public uint VariableId { get; }

    /// <summary>Gets the bands. Empty removes every limit on the variable.</summary>
    public IReadOnlyList<GemVariableLimit> Limits { get; }

    /// <summary>Returns the variable and how many bands it has.</summary>
    /// <returns>For example <c>variable 2: 2 limit(s)</c>.</returns>
    public override string ToString() => Limits.Count == 0
        ? $"variable {VariableId}: no limits"
        : $"variable {VariableId}: {Limits.Count} limit(s)";
}

/// <summary>What happened to one variable in a definition - one entry of S2F46.</summary>
public sealed class GemVariableLimitResult
{
    internal GemVariableLimitResult(
        uint variableId,
        bool accepted,
        IReadOnlyList<(uint Id, GemVariableLimitAcknowledgement Acknowledgement)> limits)
    {
        VariableId = variableId;
        Accepted = accepted;
        Limits = limits;
    }

    /// <summary>Gets the variable.</summary>
    public uint VariableId { get; }

    /// <summary>Gets LVACK: whether the equipment took the definition for this variable.</summary>
    public bool Accepted { get; }

    /// <summary>Gets the per-limit answers, in the order the limits were sent.</summary>
    public IReadOnlyList<(uint Id, GemVariableLimitAcknowledgement Acknowledgement)> Limits { get; }

    /// <summary>Returns the variable and whether it was taken.</summary>
    /// <returns>For example <c>variable 2: accepted</c>.</returns>
    public override string ToString() => $"variable {VariableId}: {(Accepted ? "accepted" : "rejected")}";
}

/// <summary>The answer to a definition - S2F46.</summary>
public sealed class GemVariableLimitReply
{
    internal GemVariableLimitReply(bool accepted, IReadOnlyList<GemVariableLimitResult> variables)
    {
        Accepted = accepted;
        Variables = variables;
    }

    /// <summary>Gets VLAACK: whether the equipment took the definition as a whole.</summary>
    /// <remarks>
    /// E5's code table for VLAACK is two values - accepted, or a mismatch - which is why this is a
    /// flag rather than an enum. The detail is per variable and per limit, in
    /// <see cref="Variables"/>.
    /// </remarks>
    public bool Accepted { get; }

    /// <summary>Gets what happened to each variable.</summary>
    public IReadOnlyList<GemVariableLimitResult> Variables { get; }

    /// <summary>Returns whether it was taken and for how many variables.</summary>
    /// <returns>For example <c>accepted, 2 variable(s)</c>.</returns>
    public override string ToString()
        => $"{(Accepted ? "accepted" : "mismatch")}, {Variables.Count} variable(s)";
}

/// <summary>Everything the equipment reports about one variable's limits - one entry of S2F48.</summary>
public sealed class GemVariableLimitAttributes
{
    internal GemVariableLimitAttributes(
        uint variableId,
        string units,
        double? minimum,
        double? maximum,
        IReadOnlyList<GemVariableLimit> limits)
    {
        VariableId = variableId;
        Units = units;
        Minimum = minimum;
        Maximum = maximum;
        Limits = limits;
    }

    /// <summary>Gets the variable.</summary>
    public uint VariableId { get; }

    /// <summary>Gets the units, or an empty string for a dimensionless value.</summary>
    public string Units { get; }

    /// <summary>Gets LIMITMIN, or <see langword="null"/> when the equipment documents none.</summary>
    /// <remarks>
    /// The <em>variable's</em> documented range, not a limit band: it is what tells a host whether
    /// the band it is about to ask for is one the value could ever be outside. This library's own
    /// equipment does not carry a range for a status variable and reports a zero-length item, which
    /// is E5's way of saying there is no documented bound - so this is <see langword="null"/>
    /// against it, and a number against a machine that does document one.
    /// </remarks>
    public double? Minimum { get; }

    /// <summary>Gets LIMITMAX, or <see langword="null"/> when the equipment documents none.</summary>
    public double? Maximum { get; }

    /// <summary>Gets the bands in force, in the order the equipment reported them.</summary>
    public IReadOnlyList<GemVariableLimit> Limits { get; }

    /// <summary>Returns the variable, its units and how many bands are in force.</summary>
    /// <returns>For example <c>variable 2 (mbar): 2 limit(s)</c>.</returns>
    public override string ToString() => Units.Length == 0
        ? $"variable {VariableId}: {Limits.Count} limit(s)"
        : $"variable {VariableId} ({Units}): {Limits.Count} limit(s)";
}
