// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// Options every format reader honours.
/// </summary>
public sealed class SecsReadOptions
{
    /// <summary>The default read options.</summary>
    public static SecsReadOptions Default { get; } = new();

    /// <summary>Gets the codec options used when a format carries binary SECS-II. Defaults to <see cref="SecsCodecOptions.Default"/>.</summary>
    public SecsCodecOptions Codec { get; init; } = SecsCodecOptions.Default;

    /// <summary>
    /// Gets a value indicating whether anything the format cannot account for is an error
    /// rather than something to skip. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Keep this on for files under version control, where a typo should fail the build. Turn it
    /// off when reading a log captured from equipment, where a half-written final message is
    /// expected and the first 10,000 good ones are still worth having.
    /// </remarks>
    public bool Strict { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether item labels in the document are kept on
    /// <see cref="SecsItem.Label"/>. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ReadItemLabels { get; init; } = true;

    /// <summary>Gets the largest document this reader will accept, in bytes. Defaults to 0 (no limit).</summary>
    /// <remarks>Set this when reading from an untrusted source.</remarks>
    public long MaxDocumentLength { get; init; }
}

/// <summary>
/// Options every format writer honours.
/// </summary>
public sealed class SecsWriteOptions
{
    /// <summary>The default write options: indented, labels written, newline as <c>\r\n</c>.</summary>
    public static SecsWriteOptions Default { get; } = new();

    /// <summary>Compact output: no indentation and no newlines where the format allows.</summary>
    public static SecsWriteOptions Compact { get; } = new() { Indent = false };

    /// <summary>Gets the codec options used when the format carries binary SECS-II.</summary>
    public SecsCodecOptions Codec { get; init; } = SecsCodecOptions.Default;

    /// <summary>Gets a value indicating whether output is indented for a human reader. Defaults to <see langword="true"/>.</summary>
    public bool Indent { get; init; } = true;

    /// <summary>Gets the number of spaces per indentation level. Defaults to 2.</summary>
    public int IndentSize { get; init; } = 2;

    /// <summary>Gets the newline sequence. Defaults to <c>\r\n</c>.</summary>
    /// <remarks>
    /// CRLF, not <see cref="Environment.NewLine"/>: a fixture written on Windows and on Linux
    /// must be byte-identical or the round-trip tests turn into a platform quiz.
    /// </remarks>
    public string NewLine { get; init; } = "\r\n";

    /// <summary>Gets a value indicating whether <see cref="SecsItem.Label"/> is written where the format can carry it. Defaults to <see langword="true"/>.</summary>
    public bool WriteItemLabels { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether 64-bit integers are written as strings in formats whose
    /// numbers are IEEE 754 doubles. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A <see cref="SecsFormat.U8"/> value above 2^53 cannot survive a JSON round trip as a
    /// number: the reader silently rounds it. Writing it as a string keeps it exact. Turn this
    /// off only when the consumer is a JSON parser that cannot cope with the quotes.
    /// </remarks>
    public bool WriteLargeIntegersAsStrings { get; init; } = true;

    /// <summary>Gets the number of bytes per line when writing a binary payload as text. Defaults to 16.</summary>
    public int BinaryBytesPerLine { get; init; } = 16;
}
