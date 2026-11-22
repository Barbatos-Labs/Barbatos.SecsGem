// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Stream 4: the two refusals it overturned, the one it replaced, and the item that ended a
/// four-chapter mistake in another package.
/// </summary>
/// <remarks>
/// <para>
/// Stream 4 is the stream where provenance says least. <strong>Every one of its 27 rows is
/// representative</strong> - the reference illustrates all of them and defines none - and this
/// repository has served nine of its primaries since the stream 4 chapter anyway, because the
/// measure that matters says the opposite: stream 4 is gapless, with twenty names of its own, and
/// is one of only seven gapless streams in the whole reference.
/// </para>
/// <para>
/// So the two halves this chapter looked at were refused on <em>reasons</em> rather than on
/// provenance, and both reasons were about the message rows where the answer was in the item
/// dictionary. That is the same error the stream 15 chapter corrected, made twice more.
/// </para>
/// </remarks>
public class SecsCatalogStream4Tests
{
    private static SecsStreamInfo Stream(byte number)
    {
        Assert.True(SecsCatalog.TryGetStream(number, out var stream), $"S{number}");
        return stream!;
    }

    private static SecsDataItemInfo Item(string name)
    {
        Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
        return item!;
    }

    [Fact]
    public void Every_row_is_representative_and_the_stream_is_still_the_firmest_kind()
    {
        var stream = Stream(4);

        // Nine streams are entirely representative and three of them are implemented here. This is
        // the sharpest of the three, because it is also gapless: provenance and anchoring point in
        // opposite directions on the same stream, and anchoring is the one that decided.
        Assert.True(stream.Provenance.IsEntirelyRepresentative);
        Assert.Equal(27, stream.Provenance.MessageCount);
        Assert.Equal(0, stream.Provenance.DefinedCount);

        Assert.Empty(stream.Anchoring.SharedGaps);
        Assert.Empty(stream.Anchoring.OwnGaps);
        Assert.Equal(20, stream.Anchoring.OwnAnchors.Count);
        Assert.Equal(30, stream.Anchoring.DataItemCount);
        Assert.Empty(stream.RivalSpellings);
    }

    [Fact]
    public void The_handoff_half_was_refused_because_a_link_was_thought_to_need_a_transfer_job()
    {
        var stream = Stream(4);

        // The claim was that every handoff row is addressed by a TRLINK "which is created by
        // S4F19", so the handoff half could not be served before the transfer jobs. Nine rows name
        // TRLINK and S4F19 is one of them - and S4F27 is another.
        var carrying = stream.Messages
            .Where(message => message.DataItems.Contains("TRLINK"))
            .Select(message => message.Function)
            .ToList();

        Assert.Equal<byte[]>([19, 27, 29, 31, 33, 35, 37, 39, 41], [.. carrying]);

        // And S4F27 carries the same nine descriptor items S4F19 does, bar the DATAID a job needs
        // and the EQNAME an announcement adds. So an announcement names a link as completely as a
        // job create does, and nothing in the reference reserves the naming to a job.
        var announcement = Assert.Single(stream.Messages, message => message.Function == 27);
        var create = Assert.Single(stream.Messages, message => message.Function == 19);

        Assert.Equal<string[]>(
            ["TRLINK", "TROBJNAME", "TROBJTYPE", "TRPORT", "TRROLE", "TRPTNR", "TRPTPORT", "TRLOCATION"],
            [.. announcement.DataItems.Where(item => !string.Equals(item, "EQNAME", StringComparison.Ordinal))]);

        Assert.All(
            announcement.DataItems.Where(item => !string.Equals(item, "EQNAME", StringComparison.Ordinal)),
            item => Assert.Contains(item, create.DataItems));

        // The decisive half: S4F27 travels either way, like every row of the interlocked handshake
        // this package already serves. A row only a host may send could not be how two machines
        // agree a handoff; this one can.
        Assert.Equal(SecsMessageDirection.Either, announcement.Direction);
        Assert.Equal(SecsMessageDirection.HostToEquipment, create.Direction);
    }

    [Fact]
    public void The_job_half_was_refused_for_having_no_state_model_and_the_dictionary_has_one()
    {
        // The claim was that "a transfer job has none anywhere in this reference", against E94's
        // and E40's state machines. True of the message rows; false of the dictionary.
        var states = Item("TRJOBMS");

        Assert.Equal(6, states.Codes.Count);
        Assert.Equal("Queued", states.MeaningOf("0"));
        Assert.Equal("Selected", states.MeaningOf("1"));
        Assert.Equal("Executing", states.MeaningOf("2"));
        Assert.Equal("Paused", states.MeaningOf("3"));
        Assert.Equal("Completed", states.MeaningOf("4"));
        Assert.Equal("Cancelled", states.MeaningOf("5"));

        // Carried by S4F23, the alert - so a host is told which state a job reached.
        Assert.Equal<byte[]>([4], [.. states.Streams]);
        Assert.Contains(
            Stream(4).Messages,
            message => message.Function == 23 && message.DataItems.Contains("TRJOBMS"));

        // What the reason was reaching for is still true and is a different thing: six state names
        // are not a transition model, and E94 and E40 define transitions. That is a reason not to
        // *enforce* a sequence, which is a choice this package already made for stream 4 and wrote
        // down - not a reason to leave the messages unserved.
    }

    [Fact]
    public void The_job_half_stays_out_on_TRACK_which_is_a_text_item_with_no_values()
    {
        var track = Item("TRACK");

        // S4F20 and S4F22 are the only answers to a job create and a job command, and TRACK is the
        // whole of the outcome in both. It is an A with no table: the reference never wrote the
        // values down, so a host cannot tell whether its job was created.
        Assert.Equal("A", track.FormatToken);
        Assert.Empty(track.Codes);
        Assert.False(track.IsCoded);

        var answers = Stream(4).Messages
            .Where(message => message.DataItems.Contains("TRACK"))
            .Select(message => message.Function)
            .ToList();

        Assert.Equal<byte[]>([20, 22], [.. answers]);

        // And the silence is meaningful rather than a habit, which is what makes this a refusal
        // rather than a shrug: TRROLE is also an A in this stream and it arrives *with* its table.
        // The source does write values down for a text item when it knows them.
        var role = Item("TRROLE");

        Assert.Equal("A", role.FormatToken);
        Assert.Equal(2, role.Codes.Count);
        Assert.Equal("Equipment is the material source", role.MeaningOf("SOURCE"));

        // The only other field in those two rows is ERRCODE, which is a U4 with no table either -
        // so there is nothing beside TRACK to read an outcome from.
        Assert.Empty(Item("ERRCODE").Codes);
    }

    [Fact]
    public void The_handoff_acknowledgements_have_no_tables_and_this_streams_siblings_supply_the_convention()
    {
        // Three B[1] items with nothing in them, which is the ACKC8 situation exactly: there is a
        // convention to read off rather than a vocabulary to invent.
        foreach (var name in (string[])["HOACK", "HOCANCELACK", "HOHALTACK"])
        {
            var item = Item(name);

            Assert.Equal("B", item.FormatToken);
            Assert.Empty(item.Codes);
            Assert.Equal<byte[]>([4], [.. item.Streams]);
        }

        // And the siblings that supply it are in the same stream and do have tables, which is a
        // better position than ACKC8 was in - that one had to read its convention off eight ACKCn
        // items in other streams.
        Assert.Equal("OK, will accept", Item("RSACK").MeaningOf("0"));
        Assert.Equal("Acknowledge, request granted", Item("RRACK").MeaningOf("0"));
    }

    [Fact]
    public void TRACK_is_not_the_item_GEMs_trace_acknowledgement_reads()
    {
        // The find that came out of asking which streams name TRACK. GEM's
        // GemTraceAcknowledgement is documented as reading TRACK, and TRACK is named by streams 4
        // and 17 - not stream 2. The item S2F24 carries is TIAACK.
        Assert.Equal<byte[]>([4, 17], [.. Item("TRACK").Streams]);

        var trace = Assert.Single(Stream(2).Messages, message => message.Function == 24);

        Assert.Equal<string[]>(["TIAACK"], [.. trace.DataItems]);
        Assert.DoesNotContain("TRACK", trace.DataItems);

        // And TIAACK has a table where TRACK has none, which turns a claim about a gap into a
        // claim about a disagreement: two codes against that package's six. The assertion on the
        // Gem side says which, because both are in scope there.
        var tiaack = Item("TIAACK");

        Assert.Equal(2, tiaack.Codes.Count);
        Assert.Equal("Accepted", tiaack.MeaningOf("0"));
        Assert.Equal("Denied", tiaack.MeaningOf("1"));

        // Stream 17 is the other TRACK user and is the stream that failed both checks, so nothing
        // here will ever read it. Recorded because it means TRACK has exactly one implementable
        // home in this reference, and it is the half of stream 4 that stays out.
        Assert.True(Stream(17).Anchoring.HasNoOwnVocabulary);
    }

    [Fact]
    public void The_multi_block_inquire_stays_out_and_its_format_is_not_the_reason()
    {
        var stream = Stream(4);
        var inquire = Assert.Single(stream.Messages, message => message.Function == 25);

        // Both items defined, and GRANT is a tabled item three packages here already read - so
        // this pair could be encoded tomorrow. It stays out because of where it sits rather than
        // what it says: it is the handshake in front of a message too large for one block, and the
        // only row in this stream large enough to need one is the job create that stays out.
        Assert.Equal<string[]>(["DATAID", "DATALENGTH"], [.. inquire.DataItems]);
        Assert.False(Item("DATAID").IsNamedByNoMessage);
        Assert.False(Item("DATALENGTH").IsNamedByNoMessage);
        Assert.Equal(5, Item("GRANT").Codes.Count);

        // And it is the host's alone, where this package's stream 4 type is one type for both ends
        // by design. Serving it would mean a handoff end answering a question only an equipment end
        // can answer - so it is reopenable the moment the job half is, and not before.
        Assert.Equal(SecsMessageDirection.HostToEquipment, inquire.Direction);
        Assert.Equal(
            SecsMessageDirection.Either,
            Assert.Single(stream.Messages, message => message.Function == 27).Direction);
    }
}
