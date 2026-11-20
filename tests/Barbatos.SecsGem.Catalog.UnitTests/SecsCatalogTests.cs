// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Gem;

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// The catalogue: that it loads, that it is internally consistent, and that it does not contradict
/// the stack it ships beside.
/// </summary>
/// <remarks>
/// The counts are pinned on purpose. This is third-party reference data, and the point of pinning
/// is that refreshing it has to be a decision somebody makes rather than a diff nobody reads.
/// </remarks>
public class SecsCatalogTests
{
    // ----------------------------------------------------------------- loading

    [Fact]
    public void The_catalogue_loads_everything_the_reference_documents()
    {
        Assert.Equal(20, SecsCatalog.Streams.Count);
        Assert.Equal(524, SecsCatalog.Messages.Count);
        Assert.Equal(375, SecsCatalog.DataItems.Count);
    }

    [Fact]
    public void Stream_eleven_is_absent_because_E5_does_not_define_one()
    {
        Assert.False(SecsCatalog.TryGetStream(11, out _));
        Assert.DoesNotContain(SecsCatalog.Streams, s => s.Number == 11);

        // The ones around it are there, so this is a gap in the standard and not in the parser.
        Assert.True(SecsCatalog.TryGetStream(10, out _));
        Assert.True(SecsCatalog.TryGetStream(12, out _));
    }

    [Fact]
    public void A_stream_carries_its_own_messages_in_function_order()
    {
        Assert.True(SecsCatalog.TryGetStream(6, out var six));

        Assert.Equal("Data Collection", six!.Name);
        Assert.All(six.Messages, m => Assert.Equal(6, m.Stream));

        var functions = six.Messages.Select(m => m.Function).ToArray();
        Assert.Equal(functions.OrderBy(f => f), functions);
    }

    [Fact]
    public void Every_message_belongs_to_a_stream_the_catalogue_knows()
    {
        foreach (var message in SecsCatalog.Messages)
        {
            Assert.True(
                SecsCatalog.TryGetStream(message.Stream, out _),
                $"S{message.Stream} is named by {message} but is not a stream.");
        }
    }

    // ----------------------------------------------------------------- lookup

    [Fact]
    public void A_message_is_found_by_its_stream_and_function()
    {
        Assert.True(SecsCatalog.TryGetMessage(1, 1, out var areYouThere));

        Assert.Equal("Are You There Request", areYouThere!.Name);
        Assert.True(areYouThere.IsPrimary);
        Assert.Equal(SecsMessageDirection.Either, areYouThere.Direction);
        Assert.Equal<byte?>(2, areYouThere.ReplyFunction);
        Assert.Empty(areYouThere.DataItems);
    }

    [Fact]
    public void A_pair_nobody_defined_is_simply_not_there()
    {
        Assert.False(SecsCatalog.TryGetMessage(99, 99, out var missing));
        Assert.Null(missing);
        Assert.Null(SecsCatalog.NameOf(99, 99));

        // Describe still answers, because a log line should not need a null check.
        Assert.Equal("S99F99", SecsCatalog.Describe(99, 99));
    }

    [Fact]
    public void Describe_turns_a_designator_into_something_readable()
    {
        Assert.Equal("S6F11 Event Report Send", SecsCatalog.Describe(6, 11));
        Assert.Equal("Event Report Send", SecsCatalog.NameOf(6, 11));
    }

    [Fact]
    public void A_message_describes_itself_including_the_W_bit()
    {
        using var primary = new SecsMessage(6, 11, replyExpected: true);
        using var secondary = new SecsMessage(6, 12, replyExpected: false);
        using var unknown = new SecsMessage(99, 1, replyExpected: false);

        Assert.Equal("S6F11 W Event Report Send", primary.Describe());
        Assert.Equal("S6F12 Event Report Acknowledge", secondary.Describe());
        Assert.Equal("S99F1", unknown.Describe());

        Assert.Equal("Event Report Send", primary.Catalogued()!.Name);
        Assert.Null(unknown.Catalogued());
    }

    // ----------------------------------------------------------------- data items

    [Fact]
    public void A_coded_data_item_carries_its_standard_values()
    {
        Assert.True(SecsCatalog.TryGetDataItem("COMMACK", out var commack));

        Assert.True(commack!.IsCoded);
        Assert.Equal(SecsFormat.Binary, commack.Format);
        Assert.Equal("1", commack.Length);
        Assert.Equal("Accepted", commack.MeaningOf("0"));
        Assert.NotNull(commack.MeaningOf("1"));
        Assert.Null(commack.MeaningOf("2"));
    }

    [Fact]
    public void An_item_without_a_documented_length_says_so_rather_than_guessing()
    {
        Assert.True(SecsCatalog.TryGetDataItem("SVID", out var svid));

        Assert.Equal(SecsFormat.U4, svid!.Format);
        Assert.Equal(string.Empty, svid.Length);
        Assert.False(svid.IsCoded);
    }

    [Fact]
    public void Every_data_item_is_typed_and_the_typing_agrees_with_Core()
    {
        // This replaces a test that was skipped for one release with an open question on it. The
        // question was wrong rather than hard: I had assumed some items would carry a token that
        // is not an E5 format - a choice of several, or a note - and asserted that at least one
        // did. None does. All 375 use one of eleven tokens (A, B, BOOLEAN, L, I2, I4, F4, F8, U1,
        // U2, U4), so the untyped set was empty and the assertion that it was not was the line
        // failing all along.
        Assert.All(SecsCatalog.DataItems, item =>
        {
            Assert.NotEqual(string.Empty, item.FormatToken);
            Assert.True(
                SecsFormatExtensions.TryParse(item.FormatToken, out var format),
                $"{item.Name} carries the token '{item.FormatToken}', which Core cannot parse.");

            Assert.Equal(format, item.Format);
        });

        Assert.DoesNotContain(SecsCatalog.DataItems, i => i.Format is null);

        // Length is the field that really is absent sometimes - 58 of the 375 - which is why that
        // one is an empty string rather than a number and this one never is.
        Assert.Equal(58, SecsCatalog.DataItems.Count(i => i.Length.Length == 0));
    }

    [Fact]
    public void Nothing_in_the_catalogue_loses_its_name()
    {
        Assert.All(SecsCatalog.Messages, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
        Assert.All(SecsCatalog.Streams, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        Assert.All(SecsCatalog.DataItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
    }

    [Fact]
    public void The_item_dictionary_is_looked_up_the_way_E5_writes_the_names()
    {
        Assert.True(SecsCatalog.TryGetDataItem("HCACK", out _));

        // Ordinal, case-sensitive. E5 writes data item names in upper case and a lookup that
        // folded case would make ALID and alid the same name.
        Assert.False(SecsCatalog.TryGetDataItem("hcack", out _));
    }

    // ----------------------------------------------------------------- internal consistency

    [Fact]
    public void An_odd_function_is_always_a_primary_and_an_even_one_never_is()
    {
        foreach (var message in SecsCatalog.Messages)
        {
            Assert.Equal(message.Function % 2 == 1, message.IsPrimary);
        }
    }

    [Fact]
    public void A_primary_is_answered_by_the_next_function_up_and_that_message_exists()
    {
        foreach (var message in SecsCatalog.Messages.Where(m => m.IsPrimary && m.ReplyFunction is not null))
        {
            Assert.Equal((byte)(message.Function + 1), message.ReplyFunction);

            Assert.True(
                SecsCatalog.TryGetMessage(message.Stream, message.ReplyFunction!.Value, out var reply),
                $"{message} names a reply that is not in the catalogue.");

            Assert.False(reply!.IsPrimary);
        }
    }

    [Fact]
    public void A_secondary_points_back_at_the_primary_it_answers()
    {
        foreach (var message in SecsCatalog.Messages.Where(m => !m.IsPrimary && m.ReplyFunction is not null))
        {
            Assert.Equal((byte)(message.Function - 1), message.ReplyFunction);
        }
    }

    [Fact]
    public void A_Stream_9_report_has_no_reply_because_nothing_answers_it()
    {
        foreach (var message in SecsCatalog.Messages.Where(m => m.Stream == 9))
        {
            Assert.True(message.IsPrimary);
            Assert.Null(message.ReplyFunction);
        }
    }

    // ----------------------------------------------------------------- what the source does not know

    [Fact]
    public void The_entries_the_reference_calls_representative_are_flagged_as_such()
    {
        var representative = SecsCatalog.Messages.Count(m => m.IsRepresentative);

        // 405 of 524. Carried through rather than dropped: a catalogue that presented every entry
        // with equal confidence would be claiming more than it knows.
        Assert.Equal(405, representative);

        // The streams this repository implements against are the well-covered ones.
        Assert.All(
            SecsCatalog.Messages.Where(m => m.Stream == 9),
            m => Assert.False(m.IsRepresentative));
    }

    [Fact]
    public void Some_names_appear_in_bodies_without_a_dictionary_entry_and_that_is_the_sources_gap()
    {
        var named = SecsCatalog.Messages.SelectMany(m => m.DataItems).ToHashSet(StringComparer.Ordinal);
        var defined = SecsCatalog.DataItems.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var undefined = named.Except(defined, StringComparer.Ordinal).ToArray();

        // 74 of them, ACKC2 and RIACK among them. Pinned so a refresh of the data has to be looked
        // at: the number going up means the reference grew bodies it did not document, and going
        // down means it filled some in.
        Assert.Equal(74, undefined.Length);
    }

    // ----------------------------------------------------------------- agreement with the implementation

    [Fact]
    public void Every_message_the_GEM_layer_answers_is_catalogued_as_a_primary_with_a_reply()
    {
        foreach (var (stream, function) in GemEquipment.HandledMessages)
        {
            Assert.True(
                SecsCatalog.TryGetMessage(stream, function, out var info),
                $"The GEM layer answers S{stream}F{function}, which is not in the catalogue.");

            Assert.True(info!.IsPrimary, $"{info} is handled as a primary but catalogued as a secondary.");
            Assert.Equal((byte)(function + 1), info.ReplyFunction);
        }
    }

    [Theory]
    [InlineData(1, 2, SecsMessageDirection.Either)]
    [InlineData(1, 4, SecsMessageDirection.EquipmentToHost)]
    [InlineData(1, 13, SecsMessageDirection.Either)]
    [InlineData(2, 41, SecsMessageDirection.HostToEquipment)]
    [InlineData(5, 1, SecsMessageDirection.EquipmentToHost)]
    [InlineData(5, 3, SecsMessageDirection.HostToEquipment)]
    [InlineData(6, 11, SecsMessageDirection.EquipmentToHost)]
    [InlineData(6, 12, SecsMessageDirection.HostToEquipment)]
    public void The_messages_the_GEM_layer_sends_travel_the_way_it_assumes(
        byte stream,
        byte function,
        SecsMessageDirection expected)
    {
        Assert.True(SecsCatalog.TryGetMessage(stream, function, out var info));
        Assert.Equal(expected, info!.Direction);
    }

    [Fact]
    public void S6F11_is_the_correction_this_catalogue_carries()
    {
        Assert.True(SecsCatalog.TryGetMessage(6, 11, out var send));
        Assert.True(SecsCatalog.TryGetMessage(6, 12, out var acknowledge));

        // The reference has this pair reversed, naming S6F11 an "Event Report Request" travelling
        // H->E. It is the one place it disagreed with code that is tested and running, and the code
        // is right: GEM equipment sends S6F11 and the host answers S6F12 with ACKC6. This test is
        // what stops a refresh of the data putting the error back.
        Assert.Equal("Event Report Send", send!.Name);
        Assert.Equal(SecsMessageDirection.EquipmentToHost, send.Direction);
        Assert.Contains("CEID", send.DataItems);
        Assert.Contains("DATAID", send.DataItems);

        Assert.Equal("Event Report Acknowledge", acknowledge!.Name);
        Assert.Equal(SecsMessageDirection.HostToEquipment, acknowledge.Direction);
        Assert.Equal(["ACKC6"], acknowledge.DataItems);
    }

    [Fact]
    public void No_two_messages_in_one_stream_share_a_name()
    {
        // 524 rows nobody can read by eye, and an invariant that holds across every one of them:
        // E5 does not give two functions in the same stream the same name. It found a real fault
        // in this data - S6F1 and S6F2 carried S6F11's and S6F12's names and item lists, which
        // also left SMPLN and STIME defined as data items no message referenced. S6F1 is Trace
        // Data Send.
        List<string> duplicates = [];

        foreach (var stream in SecsCatalog.Streams)
        {
            foreach (var group in stream.Messages.GroupBy(message => message.Name, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    var functions = string.Join(", ", group.Select(message => "F" + message.Function));
                    duplicates.Add($"S{stream.Number} \"{group.Key}\" is {functions}");
                }
            }
        }

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Stream_six_opens_with_trace_data_rather_than_with_an_event_report()
    {
        // The two were the same row in this data until a trace needed S6F1, and nothing noticed
        // because a duplicate reads perfectly well on its own.
        Assert.Equal("Trace Data Send", SecsCatalog.NameOf(6, 1));
        Assert.Equal("Trace Data Acknowledge", SecsCatalog.NameOf(6, 2));
        Assert.Equal("Event Report Send", SecsCatalog.NameOf(6, 11));

        Assert.True(SecsCatalog.TryGetMessage(6, 1, out var trace));
        Assert.Equal(new[] { "TRID", "SMPLN", "STIME", "SV" }, trace!.DataItems);
    }

    [Theory]
    [InlineData(1, 12, new[] { "SVID", "SVNAME", "UNITS" })]
    [InlineData(2, 30, new[] { "ECID", "ECNAME", "ECMIN", "ECMAX", "ECDEF", "UNITS" })]
    [InlineData(5, 6, new[] { "ALCD", "ALID", "ALTX" })]
    [InlineData(2, 41, new[] { "RCMD", "CPNAME", "CPVAL" })]
    public void The_bodies_the_GEM_layer_builds_hold_the_items_the_catalogue_lists(
        byte stream,
        byte function,
        string[] expected)
    {
        Assert.True(SecsCatalog.TryGetMessage(stream, function, out var info));
        Assert.Equal(expected, info!.DataItems);
    }

    // ----------------------------------------------------------------- provenance, per stream

    private static SecsStreamProvenance ProvenanceOf(byte stream)
    {
        Assert.True(SecsCatalog.TryGetStream(stream, out var info));

        return info!.Provenance;
    }

    [Fact]
    public void Every_stream_accounts_for_its_own_messages()
    {
        foreach (var stream in SecsCatalog.Streams)
        {
            var provenance = stream.Provenance;

            Assert.Equal(stream.Number, provenance.Stream);
            Assert.Equal(stream.Messages.Count, provenance.MessageCount);
            Assert.Equal(
                stream.Messages.Count(m => m.IsRepresentative),
                provenance.RepresentativeCount);
            Assert.Equal(provenance.MessageCount - provenance.RepresentativeCount, provenance.DefinedCount);
        }

        // And the per-stream counts add up to the number the whole catalogue is pinned at, which
        // is what stops a stream being dropped from the table without anybody noticing.
        Assert.Equal(
            SecsCatalog.Messages.Count,
            SecsCatalog.Streams.Sum(s => s.Provenance.MessageCount));

        Assert.Equal(405, SecsCatalog.Streams.Sum(s => s.Provenance.RepresentativeCount));
    }

    [Fact]
    public void Nine_streams_have_no_definitions_at_all_and_two_of_them_are_implemented_here()
    {
        var bare = SecsCatalog.Streams
            .Where(s => s.Provenance.IsEntirelyRepresentative)
            .Select(s => (int)s.Number)
            .ToArray();

        Assert.Equal([3, 4, 15, 16, 17, 18, 19, 20, 21], bare);

        // The point of the property, and the reason it is not a veto: stream 3 is E87's carrier
        // actions and stream 16 is E40's process jobs, both implemented in this repository against
        // data the source documented none of - because both survived a check against something
        // else. Asserted so that reading IsEntirelyRepresentative as a refusal breaks a test.
        Assert.Contains(3, bare);
        Assert.Contains(16, bare);
    }

    [Fact]
    public void A_well_covered_stream_resolves_every_item_it_names()
    {
        var nine = ProvenanceOf(9);

        Assert.False(nine.IsEntirelyRepresentative);
        Assert.Equal(7, nine.MessageCount);
        Assert.Equal(7, nine.DefinedCount);
        Assert.Empty(nine.UndefinedDataItems);

        // Stream 16 the other way round: no definitions, and yet most of what its bodies name is
        // in the dictionary, which is the check it passed.
        var sixteen = ProvenanceOf(16);

        Assert.True(sixteen.IsEntirelyRepresentative);
        Assert.Equal([ "JOBSTATE", "MTRLID", "PJACK", "PJID", "QPOSN" ], sixteen.UndefinedDataItems);
    }

    // ----------------------------------------------------------------- stream 17

    /// <summary>
    /// Stream 17's rows are stream 16's and stream 2's with the nouns swapped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This matters because the catalogue calls S17F1 a "Control Job Create Request" and SEMI E94
    /// is the control job standard, so the table reads at a glance like E94's missing message set.
    /// It is not one. Every function number, direction, reply partner and item list in the
    /// control job half is stream 16's with <c>PJ</c> rewritten to <c>CJ</c>, and the trace half
    /// carries stream 2's trace body unchanged.
    /// </para>
    /// <para>
    /// These assertions pin a flaw rather than a feature, which is unusual and deliberate: if a
    /// refreshed data file ever makes them fail, stream 17 has changed and somebody has to decide
    /// again whether it can be implemented. Until then this is the test that stops the next
    /// reader - this repository's own author included - from writing a codec for it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(11)]
    [InlineData(12)]
    public void Stream_17s_control_job_rows_are_stream_16s_with_the_nouns_swapped(byte function)
    {
        Assert.True(SecsCatalog.TryGetMessage(16, function, out var job));
        Assert.True(SecsCatalog.TryGetMessage(17, function, out var control));

        Assert.Equal(job!.Name.Replace("Process Job", "Control Job", StringComparison.Ordinal), control!.Name);
        Assert.Equal(job.Direction, control.Direction);
        Assert.Equal(job.IsPrimary, control.IsPrimary);
        Assert.Equal(job.ReplyFunction, control.ReplyFunction);

        // The item lists too, under the same substitution - PJID to CJID, PJACK to CJACK and
        // JOBSTATE to CJSTATE. S16F1 and S16F6 are the two whose bodies are not a pure rename,
        // and they are the two where a real E94 message would have shown its own subject.
        if (function is not (1 or 6))
        {
            Assert.Equal(
                job.DataItems.Select(Swap),
                control.DataItems);
        }

        static string Swap(string item) => item switch
        {
            "PJID" => "CJID",
            "PJACK" => "CJACK",
            "JOBSTATE" => "CJSTATE",
            _ => item,
        };
    }

    [Fact]
    public void Stream_17s_trace_request_carries_stream_2s_trace_body_unchanged()
    {
        Assert.True(SecsCatalog.TryGetMessage(2, 23, out var initialize));
        Assert.True(SecsCatalog.TryGetMessage(17, 9, out var request));

        Assert.Equal(initialize!.DataItems, request!.DataItems);
        Assert.Equal(initialize.Direction, request.Direction);
    }

    [Fact]
    public void Nothing_in_stream_17_carries_what_E94_is_about()
    {
        Assert.True(SecsCatalog.TryGetMessage(17, 1, out var create));

        // E94 creates a control job from its material - a carrier input specification - and this
        // creates one from a process job identifier, which is backwards: process jobs are made
        // from a control job's material, not the other way round. ControlJobManager.Create takes
        // carriers, a process order and a processing specification, and none of those is here.
        Assert.Equal(["CJID", "PJID"], create!.DataItems);

        var bodies = SecsCatalog.Streams
            .First(s => s.Number == 17)
            .Messages
            .SelectMany(m => m.DataItems)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("CARRIERID", bodies);
        Assert.DoesNotContain("CARRIERSPEC", bodies);
        Assert.DoesNotContain("CTLJOBID", bodies);
        Assert.DoesNotContain("CTLJOBCMD", bodies);
    }

    [Fact]
    public void The_only_two_control_job_items_the_dictionary_defines_are_named_by_no_message()
    {
        // The finding that settles it. The item table knows what a control job's identifier and
        // command are called, with CTLJOBCMD carrying E94's six command values - and the message
        // table never uses either, while its stream 17 rows use CJID, which is defined nowhere.
        // Two halves of one reference disagreeing about one standard's own item names is what an
        // extrapolation looks like from the outside.
        Assert.True(SecsCatalog.TryGetDataItem("CTLJOBID", out _));
        Assert.True(SecsCatalog.TryGetDataItem("CTLJOBCMD", out var command));
        Assert.Equal("Start the control job", command!.MeaningOf("START"));

        Assert.DoesNotContain(
            SecsCatalog.Messages.SelectMany(m => m.DataItems),
            item => item.StartsWith("CTLJOB", StringComparison.Ordinal));

        // Hand-rolled when this was found, and now a measurement anybody can ask for: that scan
        // above is what SecsDataItemInfo.IsNamedByNoMessage answers, for every item at once.
        Assert.True(SecsCatalog.TryGetDataItem("CTLJOBID", out var identifier));
        Assert.True(identifier!.IsNamedByNoMessage);
        Assert.True(command.IsNamedByNoMessage);
        Assert.Contains("CTLJOBID", SecsCatalog.Provenance.UnusedDefinitions);
        Assert.Contains("CTLJOBCMD", SecsCatalog.Provenance.UnusedDefinitions);

        Assert.False(SecsCatalog.TryGetDataItem("CJID", out _));
        Assert.False(SecsCatalog.TryGetDataItem("CJACK", out _));
        Assert.False(SecsCatalog.TryGetDataItem("CJSTATE", out _));
    }

    /// <summary>The dictionary's half of the provenance question, which no stream can show.</summary>
    /// <remarks>
    /// <see cref="SecsStreamProvenance.UndefinedDataItems"/> asks whether a stream's bodies can be
    /// read against anything. This asks the other direction - whether a definition is attached to
    /// any message at all - and it is a fact about the whole table, because an item nothing names
    /// is invisible from every stream at once. Both unanchored sets turn out to be larger than the
    /// anchored one.
    /// </remarks>
    [Fact]
    public void Most_of_the_dictionary_is_named_by_no_message()
    {
        var provenance = SecsCatalog.Provenance;

        Assert.Equal(375, provenance.DataItemCount);
        Assert.Equal(213, provenance.UnusedDefinitions.Count);
        Assert.Equal(236, provenance.NamedDataItemCount);
        Assert.Equal(74, provenance.UndefinedDataItems.Count);

        // 162 items are both defined and used, and the two subtractions have to agree on that or
        // one of the two passes over the table is wrong.
        Assert.Equal(
            provenance.DataItemCount - provenance.UnusedDefinitions.Count,
            provenance.NamedDataItemCount - provenance.UndefinedDataItems.Count);
        Assert.Equal(162, provenance.DataItemCount - provenance.UnusedDefinitions.Count);
    }

    [Fact]
    public void The_two_lists_are_the_two_directions_and_never_overlap()
    {
        var provenance = SecsCatalog.Provenance;

        // One is "used with no definition", the other "defined with nothing using it". A name in
        // both would mean the loader disagreed with itself about what the dictionary holds.
        Assert.Empty(provenance.UndefinedDataItems.Intersect(provenance.UnusedDefinitions, StringComparer.Ordinal));

        Assert.Equal([.. provenance.UndefinedDataItems.Order(StringComparer.Ordinal)], provenance.UndefinedDataItems);
        Assert.Equal([.. provenance.UnusedDefinitions.Order(StringComparer.Ordinal)], provenance.UnusedDefinitions);
    }

    [Fact]
    public void The_catalogue_wide_undefined_list_is_exactly_what_the_streams_report()
    {
        string[] fromStreams = [.. SecsCatalog.Streams
            .SelectMany(stream => stream.Provenance.UndefinedDataItems)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        // The same measurement assembled two ways, which is the only check available on either.
        Assert.Equal(fromStreams, SecsCatalog.Provenance.UndefinedDataItems);
    }

    [Fact]
    public void The_flag_and_the_list_say_the_same_thing_about_every_item()
    {
        string[] flagged = [.. SecsCatalog.DataItems
            .Where(item => item.IsNamedByNoMessage)
            .Select(item => item.Name)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(SecsCatalog.Provenance.UnusedDefinitions, flagged);

        // And the flag agrees with the message table item by item, which is what it claims.
        var used = SecsCatalog.Messages
            .SelectMany(message => message.DataItems)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in SecsCatalog.DataItems)
        {
            Assert.Equal(!used.Contains(item.Name), item.IsNamedByNoMessage);
        }
    }

    [Fact]
    public void The_counts_agree_with_the_streams()
    {
        var provenance = SecsCatalog.Provenance;

        Assert.Equal(SecsCatalog.Streams.Count, provenance.StreamCount);
        Assert.Equal(SecsCatalog.Messages.Count, provenance.MessageCount);
        Assert.Equal(SecsCatalog.Streams.Sum(s => s.Provenance.MessageCount), provenance.MessageCount);
        Assert.Equal(SecsCatalog.Streams.Sum(s => s.Provenance.RepresentativeCount), provenance.RepresentativeCount);
        Assert.Equal(provenance.MessageCount - provenance.RepresentativeCount, provenance.DefinedCount);

        Assert.Equal(
            "20 streams, 524 messages (405 representative), 375 items: 213 unused, 74 undefined",
            provenance.ToString());
    }

    /// <summary>Why a storage service standard got no package here.</summary>
    /// <remarks>
    /// The two items such a standard would be built on are both defined and both named by
    /// nothing, and no stream in the catalogue carries a storage service at all. So there is no
    /// message to read either item in - no neighbours, no direction, no reply - which is the one
    /// thing a definition on its own cannot supply. The storage model this stack does have exists
    /// because S3F31 demanded one, and it lives where that transaction is.
    /// </remarks>
    [Fact]
    public void The_two_location_identifier_items_are_defined_and_named_by_nothing()
    {
        Assert.True(SecsCatalog.TryGetDataItem("LOCID", out var locationId));
        Assert.True(SecsCatalog.TryGetDataItem("SLOTID", out var slotId));

        Assert.True(locationId!.IsNamedByNoMessage);
        Assert.True(slotId!.IsNamedByNoMessage);

        // Defined, with a format each - which is exactly what makes them tempting.
        Assert.Equal(SecsFormat.ASCII, locationId.Format);
        Assert.Equal(SecsFormat.U1, slotId.Format);

        // And the one location item that is named by messages is the other one, LOC, which is
        // what stream 3 uses and what this stack's storage locations are addressed by.
        Assert.True(SecsCatalog.TryGetDataItem("LOC", out var location));
        Assert.False(location!.IsNamedByNoMessage);
        Assert.Equal(SecsFormat.U4, location.Format);
    }

    /// <summary>A definition no message names is evidence, not a verdict.</summary>
    /// <remarks>
    /// E87's object items are all in the unused list, and E87 ships here regardless - on stream 3
    /// and on the stream 14 object services, neither of which needs these five names. So the flag
    /// rules out reading an item in context; it does not rule out the standard.
    /// </remarks>
    [Theory]
    [InlineData("CARRIERSPEC")]
    [InlineData("CATTRID")]
    [InlineData("CATTRDATA")]
    [InlineData("PORTACTION")]
    [InlineData("PODID")]
    public void E87s_own_object_items_are_named_by_nothing_either(string name)
    {
        Assert.True(SecsCatalog.TryGetDataItem(name, out var item));
        Assert.True(item!.IsNamedByNoMessage);
        Assert.Contains(name, SecsCatalog.Provenance.UnusedDefinitions);
    }

    [Fact]
    public void An_item_both_defined_and_used_is_not_flagged()
    {
        foreach (var name in new[] { "CARRIERID", "PTN", "LOC", "CAACK", "ACKC3", "MID" })
        {
            Assert.True(SecsCatalog.TryGetDataItem(name, out var item), $"{name} should be defined");
            Assert.False(item!.IsNamedByNoMessage, $"{name} is named by a message");
            Assert.DoesNotContain(name, SecsCatalog.Provenance.UnusedDefinitions);
        }
    }

    [Fact]
    public void Stream_17_is_the_one_stream_this_repository_will_not_implement()
    {
        var seventeen = ProvenanceOf(17);

        Assert.True(seventeen.IsEntirelyRepresentative);
        Assert.Equal(14, seventeen.MessageCount);
        Assert.Equal(0, seventeen.DefinedCount);

        // Its key items resolve against nothing, which is the difference from stream 16: there is
        // no format to encode CJID as and no code table to read CJACK against, so a codec for
        // these would be this library inventing a wire and calling it E94.
        Assert.Equal(["CJACK", "CJID", "CJSTATE", "DCID", "PJID"], seventeen.UndefinedDataItems);

        // And nothing here answers it, which is the claim the rest of this group explains.
        Assert.DoesNotContain(GemEquipment.HandledMessages, m => m.Stream == 17);
    }
}
