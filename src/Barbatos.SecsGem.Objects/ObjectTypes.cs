// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Objects;

/// <summary>The answer to anything in stream 14 that is acknowledged - <c>OBJACK</c>.</summary>
/// <remarks>
/// Five codes, and unlike most acknowledgements in E5 they say something useful: three of them name
/// which part of the request was wrong. A host asking for an attribute by the wrong name gets a
/// different answer from one asking about an object that does not exist, and that is worth keeping.
/// </remarks>
public enum ObjectAcknowledgement : byte
{
    /// <summary>It worked.</summary>
    Successful = 0,

    /// <summary>It did not, and the reason is not one of the three below.</summary>
    Error = 1,

    /// <summary>Denied: that is not an object type this end knows.</summary>
    InvalidObjectType = 2,

    /// <summary>Denied: that object type exists, but not that instance of it.</summary>
    InvalidObjectInstance = 3,

    /// <summary>Denied: that object exists, but not that attribute of it.</summary>
    InvalidAttribute = 4,
}

/// <summary>An attribute of an object - <c>ATTRID</c> and <c>ATTRDATA</c>.</summary>
/// <remarks>
/// <para>
/// <see cref="Value"/> is a whole <see cref="SecsItem"/>, and it has to be. <c>ATTRDATA</c> is the
/// one item in E5 with no format of its own: an attribute is whatever the object model says it is -
/// a string, a number, a list of structures - and stream 14 is the protocol for carrying it rather
/// than a schema for it.
/// </para>
/// <para>
/// <strong>Who owns the item.</strong> Two rules, and between them they cover all four
/// directions - both are already used elsewhere in this library:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <strong>A value you hand to this library becomes the library's</strong>, and is disposed with
/// the message it travels in. That goes for a value returned from an
/// <see cref="IObjectProvider"/> and for one passed to a set method on
/// <see cref="ObjectServices"/>, so build a fresh item for each. This is the contract a GEM status
/// variable's read delegate has.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>A value this library hands you is a live view</strong> into the message it arrived in,
/// and is gone when the call returns - inside a provider, when the handler returns; from a reply,
/// when the method that read it returns. Copy anything kept past that. This is the contract every
/// event argument in this library carries.
/// </description>
/// </item>
/// </list>
/// <para>
/// The one place the library copies a value for you is the echo in a set reply, which has to: see
/// the remarks in the codec.
/// </para>
/// </remarks>
public sealed class ObjectAttribute
{
    /// <summary>What the attribute is called - <c>ATTRID</c>.</summary>
    public required string Name { get; init; }

    /// <summary>What it is - <c>ATTRDATA</c>.</summary>
    /// <remarks>See the remarks on this class for who disposes it; the answer depends on the direction.</remarks>
    public required SecsItem Value { get; init; }
}

/// <summary>What a get is asking for - S14F1 and S14F19.</summary>
/// <remarks>
/// One type for both, because the only difference is how many objects are named. S14F1 names one
/// and carries an object type with it; S14F19 names several and carries none, since a request
/// across several objects cannot assume they share a type.
/// </remarks>
public sealed class ObjectAttributeRequest
{
    /// <summary>Which objects - <c>OBJSPEC</c>.</summary>
    public required IReadOnlyList<string> Objects { get; init; }

    /// <summary>What type they are - <c>OBJTYPE</c>. Empty for a multi-object request, which does not carry one.</summary>
    public string ObjectType { get; init; } = string.Empty;

    /// <summary>Which attributes are wanted - <c>ATTRID</c>. An empty list asks for all of them.</summary>
    /// <remarks>
    /// "All of them" is this library reading a standard that gives no explicit marker for it: a
    /// zero-length <c>ATTRID</c> list is the only thing a request for everything can look like, and
    /// a provider is free to treat it as an error instead.
    /// </remarks>
    public IReadOnlyList<string> Attributes { get; init; } = [];
}

/// <summary>What a set is asking for - S14F3 and S14F21.</summary>
public sealed class ObjectAttributeWrite
{
    /// <summary>Which objects - <c>OBJSPEC</c>.</summary>
    public required IReadOnlyList<string> Objects { get; init; }

    /// <summary>What type they are - <c>OBJTYPE</c>. Empty for a multi-object set.</summary>
    public string ObjectType { get; init; } = string.Empty;

    /// <summary>The attributes and their new values.</summary>
    /// <remarks>Inside a provider these are live views into the message; see <see cref="ObjectAttribute"/>.</remarks>
    public required IReadOnlyList<ObjectAttribute> Attributes { get; init; }
}

/// <summary>One object and its attributes, as a get answers - S14F2 and S14F20.</summary>
public sealed class ObjectAttributeValues
{
    /// <summary>Which object - <c>OBJSPEC</c>.</summary>
    public required string ObjectSpecification { get; init; }

    /// <summary>Its attributes.</summary>
    /// <remarks>
    /// Returned from a provider, these become the reply's and are disposed with it. Read from a
    /// reply, they are valid until the reply is disposed - which for every method on
    /// <see cref="ObjectServices"/> is when that method returns, so copy what you keep.
    /// </remarks>
    public required IReadOnlyList<ObjectAttribute> Attributes { get; init; }
}

/// <summary>What happened to one object in a set - S14F22.</summary>
public sealed class ObjectSetResult
{
    /// <summary>Which object - <c>OBJSPEC</c>.</summary>
    public required string ObjectSpecification { get; init; }

    /// <summary>Whether it took - <c>OBJACK</c>.</summary>
    public required ObjectAcknowledgement Acknowledgement { get; init; }
}

/// <summary>A parameter of an action - <c>CPNAME</c> and <c>CPVAL</c>.</summary>
/// <remarks>
/// Both are <c>A</c> items in the catalogue, so a parameter is a pair of strings rather than a
/// name and an item of any format. That is narrower than <c>ATTRDATA</c> deliberately: the
/// catalogue says <c>A</c>, and a machine that sends a number sends its digits. The reader takes
/// any format and reads its text form, so a <c>U4</c> arriving where text was expected is still
/// understood.
/// </remarks>
public sealed class ObjectParameter
{
    /// <summary>What the parameter is called - <c>CPNAME</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Its value - <c>CPVAL</c>.</summary>
    public string Value { get; init; } = string.Empty;
}

/// <summary>Telling an object to do something - S14F9 and S14F27.</summary>
/// <remarks>
/// One type for both, as with a get: the only difference is how many objects are named. An action
/// is a string because <c>RCMD</c> is an <c>A</c> item, which is the same reason the carrier
/// actions in the E87 package are strings - the vocabulary belongs to whatever standard defines
/// the object, and a library that made an enum of it would cap what its users can say.
/// </remarks>
public sealed class ObjectActionRequest
{
    /// <summary>Which objects - <c>OBJSPEC</c>.</summary>
    public required IReadOnlyList<string> Objects { get; init; }

    /// <summary>What type they are - <c>OBJTYPE</c>. Empty for a multi-object action, which does not carry one.</summary>
    public string ObjectType { get; init; } = string.Empty;

    /// <summary>What to do - <c>RCMD</c>.</summary>
    public required string Command { get; init; }

    /// <summary>What to do it with - <c>CPNAME</c> and <c>CPVAL</c>.</summary>
    public IReadOnlyList<ObjectParameter> Parameters { get; init; } = [];
}

/// <summary>What happened to one object in an action - S14F10 and S14F28.</summary>
public sealed class ObjectActionResult
{
    /// <summary>Which object - <c>OBJSPEC</c>.</summary>
    public required string ObjectSpecification { get; init; }

    /// <summary>Whether it was done - <c>OBJACK</c>.</summary>
    public required ObjectAcknowledgement Acknowledgement { get; init; }
}

/// <summary>Asking for an object to exist - S14F5.</summary>
/// <remarks>
/// The object is named by the equipment rather than by the asker: S14F6 carries an <c>OBJID</c>
/// back. A host that wants a particular name sets it as an attribute and leaves the machine to
/// agree or refuse.
/// </remarks>
public sealed class ObjectCreateRequest
{
    /// <summary>What kind of object - <c>OBJTYPE</c>.</summary>
    public required string ObjectType { get; init; }

    /// <summary>What it should start out as.</summary>
    /// <remarks>Inside a provider these are live views into the message; see <see cref="ObjectAttribute"/>.</remarks>
    public IReadOnlyList<ObjectAttribute> Attributes { get; init; } = [];
}

/// <summary>What the machine made, or why it did not - S14F6.</summary>
public sealed class ObjectCreateResult
{
    /// <summary>Whether it was made - <c>OBJACK</c>.</summary>
    public required ObjectAcknowledgement Acknowledgement { get; init; }

    /// <summary>What it is called - <c>OBJID</c>. Empty when nothing was made.</summary>
    public string ObjectId { get; init; } = string.Empty;
}

/// <summary>Asking for an object to stop existing - S14F7.</summary>
public sealed class ObjectDeleteRequest
{
    /// <summary>What kind of object - <c>OBJTYPE</c>.</summary>
    public string ObjectType { get; init; } = string.Empty;

    /// <summary>Which one - <c>OBJSPEC</c>.</summary>
    public required string ObjectSpecification { get; init; }
}
