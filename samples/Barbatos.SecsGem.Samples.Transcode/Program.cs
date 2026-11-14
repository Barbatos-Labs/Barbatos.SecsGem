// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem;
using Barbatos.SecsGem.Mapping;
using Barbatos.SecsGem.Samples.Transcode;
using Barbatos.SecsGem.Serialization;
using Barbatos.SecsGem.Serialization.Sml;
using Microsoft.Extensions.DependencyInjection;

// ---------------------------------------------------------------------------------------------
// Everything is reached through dependency injection, even in a console tool: one
// AddSecsSerialization() plus one AddSecsXxx() per format is the whole wiring. Note what is NOT
// referenced here - the HSMS engine. A tool that converts files has no use for a socket.
// ---------------------------------------------------------------------------------------------
var services = new ServiceCollection()
    .AddSecsSerialization()
    .AddSecsSml()
    .AddSecsJson()
    .AddSecsXml()
    .AddSecsIni()
    .AddSecsMapper();

await using var provider = services.BuildServiceProvider();

var transcoder = provider.GetRequiredService<SecsTranscoder>();
var mapper = provider.GetRequiredService<SecsSerializerOptions>();

Console.WriteLine($"Registered formats: {string.Join(", ", transcoder.Registry.Formats.Select(f => f.Id).Order(StringComparer.Ordinal))}");
Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// 1. A record with no attributes whatsoever.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("=== 1. Convention only: record Person(string, int, string[]) ===");

var person = new Person("Pham The Hung", 36, ["hung@barbatos.dev", "labs@barbatos.dev"]);
using var personItem = SecsSerializer.Serialize(person, mapper);
Console.WriteLine(personItem.ToSml());

var personBack = SecsSerializer.Deserialize<Person>(personItem, mapper)!;
Console.WriteLine($"round trip -> {personBack.Name}, {personBack.Age}, [{string.Join(", ", personBack.Emails)}]");
Console.WriteLine($"equal      -> {personBack.Name == person.Name && personBack.Age == person.Age && personBack.Emails.SequenceEqual(person.Emails)}");
Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// 2. A GEM message built from an attributed type.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("=== 2. [SecsMessage]: object -> whole SECS message ===");

using var request = SecsSerializer.ToMessage(
    new StatusVariableRequest { StatusVariableIds = [1001, 1002, 1003] },
    mapper);

Console.Write(request.ToSml());
Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// 3. The complex GEM event report, through every format and back.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("=== 3. S6F11 event report: object -> sml -> json -> xml -> ini -> sml ===");

var report = new EventReportSend
{
    DataId = 1,
    CollectionEventId = 7_001,
    Timestamp = new DateTime(2026, 11, 14, 9, 30, 15, 250, DateTimeKind.Utc),
    ControlState = EquipmentControlState.OnlineRemote,
    ReceivedAt = DateTimeOffset.UtcNow,
    Reports =
    [
        new EventReport
        {
            ReportId = 5_001,
            Values =
            [
                new VariableValue { Name = "PRESSURE", Value = "1013.25" },
                new VariableValue { Name = "TEMPERATURE", Value = "23.4" },
            ],
        },
        new EventReport
        {
            ReportId = 5_002,
            Values = [new VariableValue { Name = "LOTID", Value = "LOT-2026-1114" }],
        },
    ],
};

using var reportMessage = SecsSerializer.ToMessage(report, mapper);
var sml = reportMessage.ToSml();
Console.Write(sml);
Console.WriteLine();

var current = sml;
var from = SecsFormatIds.Sml;

foreach (var to in new[] { SecsFormatIds.Json, SecsFormatIds.Xml, SecsFormatIds.Ini, SecsFormatIds.Sml })
{
    var (text, conversion) = await transcoder.TranscodeTextAsync(from, current, to);
    Console.WriteLine($"--- {from} -> {to}: {conversion} ---");
    foreach (var warning in conversion.Warnings)
    {
        Console.WriteLine($"    ! {warning}");
    }

    Console.WriteLine(Indent(text));
    current = text;
    from = to;
}

// ---------------------------------------------------------------------------------------------
// 4. Did the round trip survive? Compare the item trees, not the text.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("=== 4. Verification ===");

var roundTripped = SmlConvert.ParseSml(current);
Console.WriteLine($"sml -> json -> xml -> ini -> sml preserved the message: {reportMessage.Equals(roundTripped)}");

var reportBack = SecsSerializer.FromMessage<EventReportSend>(roundTripped, mapper)!;
Console.WriteLine($"deserialized back: CEID={reportBack.CollectionEventId}, state={reportBack.ControlState}, "
    + $"reports={reportBack.Reports.Count}, first value={reportBack.Reports[0].Values[0].Name.TrimEnd()}="
    + $"{reportBack.Reports[0].Values[0].Value}");
Console.WriteLine($"timestamp survived: {reportBack.Timestamp:O} ({reportBack.Timestamp == report.Timestamp})");

// The binary leg: the hub written down, registered like any other spoke.
var (binary, binaryReport) = await transcoder.TranscodeTextAsync(SecsFormatIds.Sml, sml, SecsFormatIds.Secs2Binary);
Console.WriteLine($"sml -> secs2: {binaryReport}, {binary.Length} byte(s) on the wire");
foreach (var warning in binaryReport.Warnings)
{
    Console.WriteLine($"    ! {warning}");
}

roundTripped.Dispose();

static string Indent(string text)
    => string.Join(
        Environment.NewLine,
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => "    " + line.TrimEnd('\r')));
