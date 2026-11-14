// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Xml;

/// <summary>
/// The XML shape Barbatos.SecsGem reads and writes.
/// </summary>
/// <remarks>
/// <para>
/// An item is an element named after its format, with its values as text content:
/// </para>
/// <code>
/// &lt;?xml version="1.0" encoding="utf-8"?&gt;
/// &lt;SecsMessages&gt;
///   &lt;SecsMessage name="GetEquipmentStatus" s="1" f="3" w="true"&gt;
///     &lt;List&gt;
///       &lt;ASCII label="MDLN"&gt;MDLN01&lt;/ASCII&gt;
///       &lt;U4&gt;1234&lt;/U4&gt;
///     &lt;/List&gt;
///   &lt;/SecsMessage&gt;
/// &lt;/SecsMessages&gt;
/// </code>
/// <para>
/// The reader is created with DTD processing prohibited and no entity resolver, so a document
/// from an untrusted source cannot pull in an external entity or expand a billion-laughs
/// payload. This matters more here than it might elsewhere: recipe and process-program files
/// arrive from equipment vendors and from the fab network.
/// </para>
/// </remarks>
public static class XmlFormat
{
    /// <summary>Describes the XML format.</summary>
    public static SecsFormatDescriptor Descriptor { get; } = new(
        SecsFormatIds.Xml,
        "XML",
        [".xml"],
        "application/xml",
        SecsFormatCapabilities.MultipleMessages
        | SecsFormatCapabilities.MessageNames
        | SecsFormatCapabilities.ItemLabels
        | SecsFormatCapabilities.Comments
        | SecsFormatCapabilities.ExactIntegers
        | SecsFormatCapabilities.BinaryPayload
        | SecsFormatCapabilities.Indentation);

    /// <summary>The root element of a multi-message document.</summary>
    public const string RootElement = "SecsMessages";

    /// <summary>The element that holds one message.</summary>
    public const string MessageElement = "SecsMessage";

    /// <summary>The attribute carrying <see cref="SecsMessage.Name"/>.</summary>
    public const string NameAttribute = "name";

    /// <summary>The attribute carrying the stream number.</summary>
    public const string StreamAttribute = "s";

    /// <summary>The attribute carrying the function number.</summary>
    public const string FunctionAttribute = "f";

    /// <summary>The attribute carrying the W-bit.</summary>
    public const string ReplyExpectedAttribute = "w";

    /// <summary>The attribute carrying <see cref="SecsItem.Label"/>.</summary>
    public const string LabelAttribute = "label";
}
