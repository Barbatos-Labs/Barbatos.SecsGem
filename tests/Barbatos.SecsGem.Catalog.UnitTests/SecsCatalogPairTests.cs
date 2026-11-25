// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// The invariants that hold across every pair in the table rather than across the rows somebody
/// thought to name.
/// </summary>
/// <remarks>
/// These came out of a row-by-row audit of all 524 entries against the reference they were taken
/// from. The audit found no error in the data - and found that three of the ten invariants it had
/// to compute by hand were pinned nowhere, which is the part worth keeping. A hand edit to one
/// half of a pair passed the whole suite before this file existed.
/// </remarks>
public class SecsCatalogPairTests
{
    [Fact]
    public void Both_halves_of_a_pair_travel_in_opposite_directions()
    {
        // 250 pairs. Before this test every direction assertion in the suite named its rows - eight
        // by InlineData in SecsCatalogTests, a handful per stream in the stream files - so a reply
        // edited to travel the way its primary does would have passed everything. It matters
        // because Direction is what each package's end type and routing table are built from: a
        // reversed reply is a machine answering into its own inbox.
        List<string> wrong = [];
        var pairs = 0;

        foreach (var primary in SecsCatalog.Messages.Where(m => m.IsPrimary && m.ReplyFunction is not null))
        {
            Assert.True(
                SecsCatalog.TryGetMessage(primary.Stream, primary.ReplyFunction!.Value, out var reply),
                $"{primary} names a reply that is not in the catalogue.");

            pairs++;

            var expected = primary.Direction switch
            {
                SecsMessageDirection.HostToEquipment => SecsMessageDirection.EquipmentToHost,
                SecsMessageDirection.EquipmentToHost => SecsMessageDirection.HostToEquipment,
                _ => SecsMessageDirection.Either,
            };

            if (reply!.Direction != expected)
            {
                wrong.Add($"{primary} travels {primary.Direction} and {reply} answers {reply.Direction}");
            }
        }

        Assert.Empty(wrong);
        Assert.Equal(250, pairs);
    }

    [Fact]
    public void Twenty_four_primaries_are_answered_by_nothing()
    {
        // The generalisation of A_Stream_9_report_has_no_reply_because_nothing_answers_it: a row
        // with no reply is a primary, in every stream and not only in stream 9. Fifteen of stream
        // 4's one-way announcements, seven stream 9 error reports, S10F7 and S12F19 - and 500 rows
        // in 250 pairs beside them, which is the whole 524.
        var lone = SecsCatalog.Messages.Where(m => m.ReplyFunction is null).ToArray();

        Assert.All(lone, message => Assert.True(message.IsPrimary, $"{message} answers nothing and is not a primary."));
        Assert.Equal(24, lone.Length);
        Assert.Equal<byte[]>([4, 9, 10, 12], [.. lone.Select(m => m.Stream).Distinct().Order()]);
        Assert.Equal(524, (250 * 2) + lone.Length);
    }

    [Fact]
    public void No_message_names_one_item_twice()
    {
        // A body is positional and a row's record is the names its table mentions, so one name
        // twice is a transcription slip rather than a repeated field. The rows that really do
        // repeat a field on the wire are the reason to say so: S2F41 carries one CPNAME/CPVAL pair
        // per parameter and its record names each once.
        List<string> repeats = [];

        foreach (var message in SecsCatalog.Messages)
        {
            foreach (var group in message.DataItems
                .GroupBy(name => name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1))
            {
                repeats.Add($"{message} names {group.Key} {group.Count()} times");
            }
        }

        Assert.Empty(repeats);
    }

    [Fact]
    public void The_reversed_reading_of_stream_six_would_leave_two_definitions_named_by_nothing()
    {
        // The second witness for the S6F11 correction, and the one that does not rest on this
        // repository's own code being right.
        //
        // SMPLN and STIME are tabled definitions - U4[1] and A[16] - and S6F1 is the only row in
        // the reference that names either. Under the reversed reading S6F1 is the event report and
        // carries DATAID, CEID, RPTID, V, so both items would be defined and named by nothing at
        // all, and UnusedDefinitions would read 215. A source that defines two items for a row it
        // does not have is a source disagreeing with itself, which is a firmer thing to correct it
        // on than a disagreement with code.
        foreach (var name in new[] { "SMPLN", "STIME" })
        {
            Assert.True(SecsCatalog.TryGetDataItem(name, out var item), $"{name} is not defined.");
            Assert.Equal<byte[]>([6], [.. item!.Streams]);

            var naming = SecsCatalog.Messages
                .Where(message => message.DataItems.Contains(name, StringComparer.Ordinal))
                .ToArray();

            var only = Assert.Single(naming);
            Assert.Equal(6, only.Stream);
            Assert.Equal(1, only.Function);
        }

        Assert.Equal(213, SecsCatalog.Provenance.UnusedDefinitions.Count);
    }
}
