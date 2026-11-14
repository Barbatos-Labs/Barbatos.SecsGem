// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// What a serialization format can express.
/// </summary>
/// <remarks>
/// <see cref="SecsTranscoder"/> compares the capabilities of the source and target formats
/// before converting, and reports every difference as a warning. That is how a caller learns
/// that writing to INI dropped a comment, or that JSON cannot hold a 64-bit unsigned value
/// exactly, instead of discovering it from a corrupted recipe three months later.
/// </remarks>
[Flags]
public enum SecsFormatCapabilities
{
    /// <summary>Nothing beyond a single message with an item tree.</summary>
    None = 0,

    /// <summary>Several messages can live in one document or stream.</summary>
    MultipleMessages = 1 << 0,

    /// <summary>A message can carry <see cref="SecsMessage.Name"/>.</summary>
    MessageNames = 1 << 1,

    /// <summary>An item can carry <see cref="SecsItem.Label"/>.</summary>
    ItemLabels = 1 << 2,

    /// <summary>The format has a comment syntax, so a document can be annotated.</summary>
    Comments = 1 << 3,

    /// <summary>64-bit integers survive a round trip exactly.</summary>
    /// <remarks>
    /// JSON does not have this by default: a reader that parses numbers as IEEE 754 doubles
    /// loses precision above 2^53, which is why the JSON writer can be told to emit U8 and I8
    /// as strings.
    /// </remarks>
    ExactIntegers = 1 << 4,

    /// <summary>Arbitrary bytes survive a round trip without escaping loss.</summary>
    BinaryPayload = 1 << 5,

    /// <summary>Output can be indented for a human reader.</summary>
    Indentation = 1 << 6,

    /// <summary>The capabilities of a format that loses nothing: everything above.</summary>
    Lossless = MultipleMessages | MessageNames | ItemLabels | Comments | ExactIntegers | BinaryPayload | Indentation,
}

/// <summary>
/// Identifies a serialization format and states what it can carry.
/// </summary>
/// <param name="Id">
/// The stable, lower-case identifier used to look the format up - <c>sml</c>, <c>json</c>,
/// <c>xml</c>, <c>ini</c>, <c>secs2</c>. This is the contract between a spoke and the registry.
/// </param>
/// <param name="DisplayName">A human-readable name, for logs and error messages.</param>
/// <param name="FileExtensions">
/// The file extensions this format owns, each including the dot and in lower case. Used to
/// resolve a format from a path.
/// </param>
/// <param name="MediaType">The IANA media type, for HTTP and for tooling.</param>
/// <param name="Capabilities">What the format can express.</param>
/// <remarks>
/// A new format needs nothing from this assembly beyond one of these, an
/// <see cref="ISecsFormatReader"/> and an <see cref="ISecsFormatWriter"/>. Nothing here is a
/// closed enumeration, so adding YAML, CSV or Protobuf is additive by construction.
/// </remarks>
public sealed record SecsFormatDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<string> FileExtensions,
    string MediaType,
    SecsFormatCapabilities Capabilities)
{
    /// <summary>Gets a value indicating whether this format supports <paramref name="capability"/>.</summary>
    /// <param name="capability">The capability to test.</param>
    /// <returns><see langword="true"/> when supported.</returns>
    public bool Supports(SecsFormatCapabilities capability) => (Capabilities & capability) == capability;

    /// <summary>Returns the format's identifier.</summary>
    /// <returns><see cref="Id"/>.</returns>
    public override string ToString() => Id;
}

/// <summary>
/// The identifiers of the formats that ship with Barbatos.SecsGem.
/// </summary>
/// <remarks>
/// Constants, not an enum: a third-party spoke registers its own identifier, and an enum would
/// make this assembly the gatekeeper of every format that will ever exist.
/// </remarks>
public static class SecsFormatIds
{
    /// <summary>Native SECS-II binary, framed as HSMS does it: a 4-byte length, a 10-byte header, then the body.</summary>
    public const string Secs2Binary = "secs2";

    /// <summary>SML - SECS Message Language.</summary>
    public const string Sml = "sml";

    /// <summary>JSON.</summary>
    public const string Json = "json";

    /// <summary>XML.</summary>
    public const string Xml = "xml";

    /// <summary>INI, with path-addressed keys.</summary>
    public const string Ini = "ini";
}
