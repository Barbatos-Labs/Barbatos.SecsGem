// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// The converters the mapper starts with.
/// </summary>
/// <remarks>
/// These cover the types SECS-II has no format for and every fab nonetheless puts in a message.
/// Each is replaceable: register your own earlier in
/// <see cref="SecsSerializerOptions.Converters"/>, or pin one to a member with
/// <see cref="SecsItemAttribute.Converter"/>.
/// </remarks>
public static class SecsBuiltInConverters
{
    /// <summary>The default converter list, in priority order.</summary>
    public static IReadOnlyList<ISecsConverter> All { get; } =
    [
        new SecsDateTimeConverter(),
        new SecsDateTimeOffsetConverter(),
        new SecsDecimalConverter(),
        new SecsTimeSpanConverter(),
        new SecsGuidConverter(),
        new SecsDateOnlyConverter(),
        new SecsTimeOnlyConverter(),
    ];
}

/// <summary>
/// Maps <see cref="DateTime"/> using the SECS-II timestamp conventions.
/// </summary>
/// <remarks>
/// GEM equipment overwhelmingly uses a 16-character ASCII timestamp,
/// <c>YYYYMMDDhhmmsscc</c>, where <c>cc</c> is hundredths of a second - so that is the default.
/// Older equipment uses the 12-character <c>YYMMDDhhmmss</c> form, which is why
/// <see cref="SecsSerializerOptions.DateTimeFormat"/> exists. Reading accepts every supported
/// form regardless of the option, because what arrives is the equipment's choice, not ours.
/// </remarks>
public sealed class SecsDateTimeConverter : SecsConverter<DateTime>
{
    /// <summary>The 16-character GEM timestamp pattern.</summary>
    public const string Timestamp16Pattern = "yyyyMMddHHmmssff";

    /// <summary>The 12-character SECS-II timestamp pattern.</summary>
    public const string Timestamp12Pattern = "yyMMddHHmmss";

    /// <inheritdoc/>
    public override SecsItem Write(DateTime value, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.DateTimeFormat switch
        {
            SecsDateTimeFormat.Timestamp16 => SecsItem.A(value.ToString(Timestamp16Pattern, CultureInfo.InvariantCulture)),
            SecsDateTimeFormat.Timestamp12 => SecsItem.A(value.ToString(Timestamp12Pattern, CultureInfo.InvariantCulture)),
            SecsDateTimeFormat.Iso8601 => SecsItem.A(value.ToString("O", CultureInfo.InvariantCulture)),
            SecsDateTimeFormat.Ticks => SecsItem.I8(value.Ticks),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.DateTimeFormat, "Unknown timestamp format."),
        };
    }

    /// <inheritdoc/>
    public override DateTime Read(SecsItem item, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!item.Format.IsText())
        {
            return new DateTime(item.FirstValueOrDefault<long>(), DateTimeKind.Unspecified);
        }

        return Parse(item.GetString());
    }

    /// <summary>Parses any of the SECS-II timestamp spellings.</summary>
    /// <param name="text">The timestamp text.</param>
    /// <returns>The parsed value.</returns>
    /// <exception cref="SecsMappingException">The text is not a timestamp this converter knows.</exception>
    public static DateTime Parse(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();

        if (DateTime.TryParseExact(trimmed, Timestamp16Pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sixteen))
        {
            return sixteen;
        }

        if (DateTime.TryParseExact(trimmed, Timestamp12Pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var twelve))
        {
            return twelve;
        }

        // A 14-character variant (yyyyMMddHHmmss) turns up in the wild often enough to accept.
        if (DateTime.TryParseExact(trimmed, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fourteen))
        {
            return fourteen;
        }

        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var general)
            ? general
            : throw new SecsMappingException($"'{trimmed}' is not a SECS-II timestamp.");
    }
}

/// <summary>Maps <see cref="DateTimeOffset"/> through <see cref="SecsDateTimeConverter"/>'s rules.</summary>
public sealed class SecsDateTimeOffsetConverter : SecsConverter<DateTimeOffset>
{
    /// <inheritdoc/>
    public override SecsItem Write(DateTimeOffset value, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.DateTimeFormat == SecsDateTimeFormat.Iso8601
            ? SecsItem.A(value.ToString("O", CultureInfo.InvariantCulture))
            : new SecsDateTimeConverter().Write(value.UtcDateTime, options);
    }

    /// <inheritdoc/>
    public override DateTimeOffset Read(SecsItem item, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!item.Format.IsText())
        {
            return new DateTimeOffset(item.FirstValueOrDefault<long>(), TimeSpan.Zero);
        }

        var text = item.GetString().Trim();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : new DateTimeOffset(SecsDateTimeConverter.Parse(text), TimeSpan.Zero);
    }
}

/// <summary>
/// Maps <see cref="decimal"/> as ASCII text.
/// </summary>
/// <remarks>
/// SECS-II has no decimal format, and <see cref="SecsFormat.F8"/> would quietly lose precision -
/// which for a process parameter or a price is the wrong trade. Invariant text round-trips
/// exactly.
/// </remarks>
public sealed class SecsDecimalConverter : SecsConverter<decimal>
{
    /// <inheritdoc/>
    public override SecsItem Write(decimal value, SecsSerializerOptions options)
        => SecsItem.A(value.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    public override decimal Read(SecsItem item, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Format.IsText())
        {
            return decimal.TryParse(item.GetString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new SecsMappingException($"'{item.GetString()}' is not a decimal.");
        }

        return item.Format switch
        {
            SecsFormat.F8 => (decimal)item.FirstValueOrDefault<double>(),
            SecsFormat.F4 => (decimal)item.FirstValueOrDefault<float>(),
            _ => item.FirstValueOrDefault<long>(),
        };
    }
}

/// <summary>Maps <see cref="TimeSpan"/> as ASCII text in the invariant <c>c</c> format.</summary>
public sealed class SecsTimeSpanConverter : SecsConverter<TimeSpan>
{
    /// <inheritdoc/>
    public override SecsItem Write(TimeSpan value, SecsSerializerOptions options)
        => SecsItem.A(value.ToString("c", CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    public override TimeSpan Read(SecsItem item, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!item.Format.IsText())
        {
            return TimeSpan.FromTicks(item.FirstValueOrDefault<long>());
        }

        return TimeSpan.TryParse(item.GetString().Trim(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new SecsMappingException($"'{item.GetString()}' is not a time span.");
    }
}

/// <summary>Maps <see cref="Guid"/> as 16 binary bytes, falling back to text on the way in.</summary>
/// <remarks>
/// Binary rather than the 36-character text form: a carrier or job id travels in every message
/// of a conversation, and 16 bytes against 36 is a real saving on a busy link.
/// </remarks>
public sealed class SecsGuidConverter : SecsConverter<Guid>
{
    /// <inheritdoc/>
    public override SecsItem Write(Guid value, SecsSerializerOptions options) => SecsItem.B(value.ToByteArray());

    /// <inheritdoc/>
    public override Guid Read(SecsItem item, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Format.IsText())
        {
            return Guid.TryParse(item.GetString().Trim(), out var parsed)
                ? parsed
                : throw new SecsMappingException($"'{item.GetString()}' is not a GUID.");
        }

        var bytes = item.GetSpan<byte>();
        return bytes.Length == 16
            ? new Guid(bytes)
            : throw new SecsMappingException($"A GUID needs 16 bytes, found {bytes.Length}.");
    }
}

/// <summary>Maps <see cref="DateOnly"/> as 8-character ASCII <c>YYYYMMDD</c>.</summary>
public sealed class SecsDateOnlyConverter : SecsConverter<DateOnly>
{
    /// <inheritdoc/>
    public override SecsItem Write(DateOnly value, SecsSerializerOptions options)
        => SecsItem.A(value.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    public override DateOnly Read(SecsItem item, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);

        var text = item.Format.IsText() ? item.GetString().Trim() : item.FirstValueOrDefault<int>().ToString(CultureInfo.InvariantCulture);
        return DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
            ? exact
            : DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var general)
                ? general
                : throw new SecsMappingException($"'{text}' is not a date.");
    }
}

/// <summary>Maps <see cref="TimeOnly"/> as 6-character ASCII <c>hhmmss</c>.</summary>
public sealed class SecsTimeOnlyConverter : SecsConverter<TimeOnly>
{
    /// <inheritdoc/>
    public override SecsItem Write(TimeOnly value, SecsSerializerOptions options)
        => SecsItem.A(value.ToString("HHmmss", CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    public override TimeOnly Read(SecsItem item, SecsSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);

        var text = item.Format.IsText() ? item.GetString().Trim() : item.FirstValueOrDefault<int>().ToString("000000", CultureInfo.InvariantCulture);
        return TimeOnly.TryParseExact(text, "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
            ? exact
            : TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var general)
                ? general
                : throw new SecsMappingException($"'{text}' is not a time.");
    }
}
