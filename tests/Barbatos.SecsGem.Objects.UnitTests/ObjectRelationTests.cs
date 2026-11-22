// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// S14F11 and S14F25 - stream 14's last two pairs, and the comparison this library had to decide.
/// </summary>
/// <remarks>
/// <para>
/// Stream 14 had three rows left and one undefined item between them, which is the smallest gap of
/// any stream with work in it. The gap is <c>ENABLE</c> and it is in S14F23, so these two went in
/// and that one did not - <c>SecsCatalogStream14Tests</c> measures both halves of that.
/// </para>
/// <para>
/// <strong>What let S14F11 in is that <c>ATTRRELN</c> arrives with its table.</strong> Eight
/// relations, quoted: equal, not equal, the four orderings, and present and absent, which compare
/// against nothing. What the reference does <em>not</em> say is how two <c>ATTRDATA</c> items stand
/// in one of them, because that item has no format - so the comparison below is this library's
/// decision, written down and asserted rather than left in a reader's head.
/// </para>
/// <para>
/// <strong>Five controls found nothing here at once, and they shared one cause.</strong> S14F12
/// carries no acknowledgement, so "I could not read your query" is the same zero-length list as
/// "nothing matched" - which means every reader guard in this row is unobservable through the
/// reply alone. The tests assert the log instead, and that is not a workaround: the log is where
/// the distinction lives, because the message does not have anywhere to put it.
/// </para>
/// <para>
/// <strong>One control still finds nothing, and it is right that it does.</strong> The reader
/// drops the value for <c>Present</c> and <c>Absent</c>, and nothing can see that: the comparison
/// answers those two from the held value before it ever looks at the wanted one, so keeping the
/// value would change no answer. It stays because it is defence in depth against a future
/// comparison that forgets - a provider overriding <c>FindAsync</c> gets the request as this
/// reader built it - and it is recorded here rather than chased, the way stream 4's item names
/// were.
/// </para>
/// </remarks>
public class ObjectRelationTests
{
    private static ObjectInMemoryProvider Carriers()
    {
        var provider = new ObjectInMemoryProvider();

        provider.Add("CAR-001", "Carrier");
        provider.Set("CAR-001", "SlotCount", SecsItem.U1(25));
        provider.Set("CAR-001", "LotId", SecsItem.A("LOT-A"));

        provider.Add("CAR-002", "Carrier");
        provider.Set("CAR-002", "SlotCount", SecsItem.U4(13));
        provider.Set("CAR-002", "LotId", SecsItem.A("LOT-B"));

        // No slot count at all, which is what Present and Absent are for.
        provider.Add("CAR-003", "Carrier");
        provider.Set("CAR-003", "LotId", SecsItem.A("LOT-C"));

        // A lower-case lot, so that case decides an ordering rather than being incidental.
        provider.Add("CAR-004", "Carrier");
        provider.Set("CAR-004", "LotId", SecsItem.A("LOT-a"));

        provider.Add("PORT-1", "LoadPort");
        provider.Set("PORT-1", "SlotCount", SecsItem.U1(1));

        return provider;
    }

    // ------------------------------------------------------------------ S14F11, the query

    [Fact]
    public async Task A_query_compares_numbers_as_numbers_whatever_width_they_arrived_in()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        using var twenty = SecsItem.U2(20);

        // CAR-001 holds a U1 of 25 and CAR-002 a U4 of 13, and the query sends a U2. A host asking
        // for "slot count greater than 20" is asking about a number and not about an encoding.
        Assert.Equal<string[]>(
            ["CAR-001"],
            [.. await pair.Asker.FindObjectsAsync(
                "Carrier",
                "SlotCount",
                ObjectAttributeRelation.GreaterThan,
                twenty,
                ObjectPair.Token)]);

        // And the answer arrives on S14F12, which only a hand-read frame can check: the connection
        // matches a reply by its system bytes and not by its function, so a handler answering on
        // the wrong one is invisible to every method on this type. A control earned this line, and
        // the refusal path above it already asserted the function - the success path had not.
        using var primary = new SecsMessage(14, 11, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A("Carrier"),
                SecsItem.A("SlotCount"),
                SecsItem.U1((byte)ObjectAttributeRelation.GreaterThan),
                SecsItem.U2(20)),
        };

        using var reply = await pair.AskerLink
            .SendAsync(primary, ObjectPair.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), ObjectPair.Token);

        Assert.Equal(14, reply.S);
        Assert.Equal(12, reply.F);
        Assert.Equal("CAR-001", reply.Item![0].GetString());
    }

    [Fact]
    public async Task All_six_value_relations_answer_from_the_same_two_objects()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        await CheckAsync(ObjectAttributeRelation.EqualTo, 25, ["CAR-001"]);
        await CheckAsync(ObjectAttributeRelation.NotEqualTo, 25, ["CAR-002"]);
        await CheckAsync(ObjectAttributeRelation.LessThan, 25, ["CAR-002"]);
        await CheckAsync(ObjectAttributeRelation.LessThanOrEqualTo, 25, ["CAR-001", "CAR-002"]);
        await CheckAsync(ObjectAttributeRelation.GreaterThan, 13, ["CAR-001"]);
        await CheckAsync(ObjectAttributeRelation.GreaterThanOrEqualTo, 13, ["CAR-001", "CAR-002"]);

        async Task CheckAsync(ObjectAttributeRelation relation, byte value, string[] expected)
        {
            using var wanted = SecsItem.U1(value);

            var matched = await pair.Asker.FindObjectsAsync(
                "Carrier",
                "SlotCount",
                relation,
                wanted,
                ObjectPair.Token);

            Assert.Equal<string[]>(expected, [.. matched.Order(StringComparer.Ordinal)]);
        }
    }

    [Fact]
    public async Task Present_and_absent_compare_against_nothing_and_find_the_object_with_no_value()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        Assert.Equal<string[]>(
            ["CAR-001", "CAR-002"],
            [.. (await pair.Asker.FindObjectsAsync(
                "Carrier",
                "SlotCount",
                ObjectAttributeRelation.Present,
                cancellationToken: ObjectPair.Token)).Order(StringComparer.Ordinal)]);

        // CAR-003 and CAR-004 have lots but no slot count. These two are the only relations that
        // need no value, and the only way to ask the question at all.
        Assert.Equal<string[]>(
            ["CAR-003", "CAR-004"],
            [.. (await pair.Asker.FindObjectsAsync(
                "Carrier",
                "SlotCount",
                ObjectAttributeRelation.Absent,
                cancellationToken: ObjectPair.Token)).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task An_attribute_the_object_has_not_got_matches_no_value_relation_including_not_equal()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        using var twenty = SecsItem.U1(20);

        // CAR-003 is absent from both, which is the judgement worth asserting: an object that has
        // no slot count is not an object whose slot count differs from 20. "Not equal to" over a
        // missing attribute is false rather than true.
        var equal = await pair.Asker.FindObjectsAsync(
            "Carrier",
            "SlotCount",
            ObjectAttributeRelation.EqualTo,
            twenty,
            ObjectPair.Token);

        var different = await pair.Asker.FindObjectsAsync(
            "Carrier",
            "SlotCount",
            ObjectAttributeRelation.NotEqualTo,
            twenty,
            ObjectPair.Token);

        Assert.DoesNotContain("CAR-003", equal);
        Assert.DoesNotContain("CAR-003", different);
        Assert.Equal<string[]>(["CAR-001", "CAR-002"], [.. different.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task Text_is_compared_ordinally_and_the_query_stays_inside_its_type()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        using var lot = SecsItem.A("LOT-B");

        Assert.Equal<string[]>(
            ["CAR-001"],
            [.. await pair.Asker.FindObjectsAsync(
                "Carrier",
                "LotId",
                ObjectAttributeRelation.LessThan,
                lot,
                ObjectPair.Token)]);

        // And the case decides, which a control earned: every lot above is upper case, so
        // comparing without regard to case gave the same answers and nothing noticed. CAR-004
        // holds "LOT-a", which is *greater* than "LOT-B" ordinally - 'a' is 97 and 'B' is 66 - and
        // less than it to anything folding case. Ordinal is this repository's comparison for
        // everything off a wire, and a lot identifier is as much an identifier as a command name.
        using var upper = SecsItem.A("LOT-C");

        var ordinal = await pair.Asker.FindObjectsAsync(
            "Carrier",
            "LotId",
            ObjectAttributeRelation.GreaterThan,
            upper,
            ObjectPair.Token);

        // Ordinally "LOT-a" is greater than "LOT-C" and nothing else is; folding case it is less,
        // and the answer would be empty. One object either way makes the comparison the only thing
        // the assertion can be measuring.
        Assert.Equal<string[]>(["CAR-004"], [.. ordinal.Order(StringComparer.Ordinal)]);

        // PORT-1 has a SlotCount of 1 and is a LoadPort, so a query over Carriers never sees it -
        // the row carries an OBJTYPE and the search is inside that type.
        using var hundred = SecsItem.U1(100);

        var ports = await pair.Asker.FindObjectsAsync(
            "Carrier",
            "SlotCount",
            ObjectAttributeRelation.LessThan,
            hundred,
            ObjectPair.Token);

        Assert.DoesNotContain("PORT-1", ports);
        Assert.Equal<string[]>(
            ["PORT-1"],
            [.. await pair.Asker.FindObjectsAsync(
                "LoadPort",
                "SlotCount",
                ObjectAttributeRelation.LessThan,
                hundred,
                ObjectPair.Token)]);
    }

    [Fact]
    public async Task A_comparison_this_library_cannot_describe_matches_nothing()
    {
        var provider = new ObjectInMemoryProvider();

        provider.Add("CAR-001", "Carrier");
        provider.Set("CAR-001", "SlotMap", SecsItem.L(SecsItem.A("1"), SecsItem.U1(1, 2, 3)));
        provider.Set("CAR-001", "Signature", SecsItem.B(1, 2, 3));

        await using var pair = ObjectPair.Start(provider);
        await pair.OpenAsync();

        // A binary against the same bytes is equal, and stands in no ordering: "less than" over a
        // byte array is not a thing the reference names or this library will invent.
        using var same = SecsItem.B(1, 2, 3);

        Assert.Single(await pair.Asker.FindObjectsAsync(
            "Carrier",
            "Signature",
            ObjectAttributeRelation.EqualTo,
            same,
            ObjectPair.Token));

        using var also = SecsItem.B(1, 2, 3);

        Assert.Empty(await pair.Asker.FindObjectsAsync(
            "Carrier",
            "Signature",
            ObjectAttributeRelation.LessThan,
            also,
            ObjectPair.Token));

        // And a list matches nothing at all, equality included: this library claims no comparison
        // it cannot describe, and the honest answer to a query it cannot perform is that no object
        // matched rather than a guess about which did.
        using var list = SecsItem.L(SecsItem.A("1"), SecsItem.U1(1, 2, 3));

        Assert.Empty(await pair.Asker.FindObjectsAsync(
            "Carrier",
            "SlotMap",
            ObjectAttributeRelation.EqualTo,
            list,
            ObjectPair.Token));

        // Present still works on it, because that asks about the attribute and not the value.
        Assert.Single(await pair.Asker.FindObjectsAsync(
            "Carrier",
            "SlotMap",
            ObjectAttributeRelation.Present,
            cancellationToken: ObjectPair.Token));
    }

    [Fact]
    public async Task A_relation_the_table_does_not_name_is_refused_rather_than_folded()
    {
        var log = new RecordingLogger<ObjectServices>();
        await using var pair = ObjectPair.Start(Carriers(), answererLogger: log);
        await pair.OpenAsync();

        // 9, which ATTRRELN does not name. Every acknowledgement reader in this stack folds an
        // unnamed code onto a catch-all, because an acknowledgement that cannot be read still has
        // to be reported - but this is the *verb* of the query, and the item names no reserved
        // range to fold onto. A query whose relation this end cannot read is a query it cannot
        // perform, and S14F12 has no acknowledgement to say so with, so the answer is the empty
        // list that row uses as a refusal.
        using var primary = new SecsMessage(14, 11, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A("Carrier"),
                SecsItem.A("SlotCount"),
                SecsItem.U1(9),
                SecsItem.U1(25)),
        };

        using var reply = await pair.AskerLink
            .SendAsync(primary, ObjectPair.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), ObjectPair.Token);

        Assert.Equal(12, reply.F);
        Assert.Equal(0, reply.Item!.Count);

        // **The log is the assertion that matters**, and five controls at once earned it. S14F12
        // carries no acknowledgement, so "I could not read your query" is the same zero-length list
        // as "nothing matched" - which means a reader guard that fails open produces a reply no
        // test can tell from a legitimate empty result. Relation 9 would fall through to a
        // comparison that matches nothing and look exactly like this. The log is the only place
        // the difference exists.
        Assert.Contains(
            log.From(LogLevel.Warning),
            message => message.Contains("S14F11", StringComparison.Ordinal)
                && message.Contains("could not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_body_that_is_not_four_items_is_refused_at_three_as_well_as_at_two()
    {
        var log = new RecordingLogger<ObjectServices>();
        await using var pair = ObjectPair.Start(Carriers(), answererLogger: log);
        await pair.OpenAsync();

        // Two items, and then three - because the row carries four and a guard of "three or more"
        // would let the second through to read a value that is not there. A control found that the
        // two-item case alone did not cover it.
        await RefusedAsync(SecsItem.L(SecsItem.A("Carrier"), SecsItem.A("SlotCount")));
        await RefusedAsync(SecsItem.L(SecsItem.A("Carrier"), SecsItem.A("SlotCount"), SecsItem.U1(6)));

        Assert.Equal(2, log.From(LogLevel.Warning).Count(message => message.Contains("S14F11", StringComparison.Ordinal)));

        async Task RefusedAsync(SecsItem body)
        {
            using var primary = new SecsMessage(14, 11, replyExpected: true) { Item = body };

            using var reply = await pair.AskerLink
                .SendAsync(primary, ObjectPair.Token)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), ObjectPair.Token);

            Assert.Equal(12, reply.F);
            Assert.Equal(0, reply.Item!.Count);
        }
    }

    [Fact]
    public async Task A_query_naming_no_attribute_is_refused_and_says_so()
    {
        var log = new RecordingLogger<ObjectServices>();
        await using var pair = ObjectPair.Start(Carriers(), answererLogger: log);
        await pair.OpenAsync();

        // The attribute is the only field whose absence is fatal: a query over no attribute cannot
        // be performed, where a type nobody named simply matches no instances. Asserted through the
        // log for the reason above - an empty result looks the same from the outside.
        Assert.Empty(await pair.Asker.FindObjectsAsync(
            "Carrier",
            string.Empty,
            ObjectAttributeRelation.Present,
            cancellationToken: ObjectPair.Token));

        Assert.Contains(
            log.From(LogLevel.Warning),
            message => message.Contains("S14F11", StringComparison.Ordinal));

        // And a type nobody named is *not* a refusal - it is a query that matched nothing, which is
        // the distinction the log makes and the reply cannot.
        Assert.Empty(await pair.Asker.FindObjectsAsync(
            "NoSuchType",
            "SlotCount",
            ObjectAttributeRelation.Present,
            cancellationToken: ObjectPair.Token));

        Assert.Single(log.From(LogLevel.Warning).Where(message => message.Contains("S14F11", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_relation_that_compares_against_nothing_sends_nothing()
    {
        SecsItem? attrdata = null;

        await using var pair = ObjectPair.Start(Carriers(), answer: transaction =>
        {
            if (transaction.F == 11 && transaction.Message.Item is { IsList: true, Count: 4 } body)
            {
                attrdata = SecsItem.A($"{body[3].Format}:{body[3].Count}");
            }

            return null;
        });

        await pair.OpenAsync();

        var asking = pair.Asker
            .FindObjectsAsync("Carrier", "SlotCount", ObjectAttributeRelation.Present, cancellationToken: ObjectPair.Token)
            .AsTask();

        await ObjectPair.WaitUntilAsync(() => attrdata is not null, "the query to cross the wire");

        // A zero-length list, which is how every other row in this stream says "there is nothing
        // here" - not a zero, which a receiver would read as a value to compare against. Asserted
        // off the wire because the reader ignores the value for this relation either way, so a
        // round-trip cannot see it: the same shape as TRROLE's unnamed word and ACKA's format.
        Assert.Equal($"{SecsFormat.List}:0", attrdata!.GetString());
        attrdata.Dispose();

        // The scripted answer returns null, so nothing replies and the ask is abandoned - which is
        // what this test wanted the wire for.
        _ = asking;
    }

    // -------------------------------------------------------------- S14F25, the error report

    [Fact]
    public async Task An_object_with_nothing_wrong_reports_nothing()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        // Null is "nothing wrong", which is also what a provider keeping no errors reports for
        // every object it holds - the default on IObjectProvider.GetErrorAsync.
        Assert.Null(await pair.Asker.GetObjectErrorAsync("CAR-001", ObjectPair.Token));
    }

    [Fact]
    public async Task An_object_nobody_has_is_an_error_rather_than_silence()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var error = await pair.Asker.GetObjectErrorAsync("CAR-999", ObjectPair.Token);

        // A host needs to tell "nothing wrong" from "not here", and ObjectServices answers the
        // second itself - so a provider never has to check it and its default can simply report
        // none.
        Assert.NotNull(error);
        Assert.Equal(1u, error!.Code);
        Assert.Equal("no such object", error.Text);
    }

    [Fact]
    public async Task A_request_naming_no_object_says_so()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var error = await pair.Asker.GetObjectErrorAsync(string.Empty, ObjectPair.Token);

        Assert.NotNull(error);
        Assert.Contains("named no object", error!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_type_serves_the_reason_on_both_rows_that_carry_one()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        // S1F20 and S14F26 carry the same ERRCODE and ERRTEXT pair, so the same type comes back
        // from both: one item means one thing, and a second type for the same two fields would be
        // two ways to read one reading.
        var fromStreamOne = await pair.Asker.GetAttributeAsync(
            "Carrier",
            "CAR-999",
            ["LotId"],
            _ => { },
            ObjectPair.Token);

        var fromStreamFourteen = await pair.Asker.GetObjectErrorAsync("CAR-999", ObjectPair.Token);

        Assert.IsType<ObjectAttributeError>(fromStreamOne);
        Assert.IsType<ObjectAttributeError>(fromStreamFourteen);
        Assert.Equal(fromStreamOne!.Code, fromStreamFourteen!.Code);
        Assert.Equal(fromStreamOne.Text, fromStreamFourteen.Text);
    }
}
