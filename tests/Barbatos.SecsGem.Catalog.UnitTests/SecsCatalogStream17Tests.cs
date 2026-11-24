// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// What a re-read of stream 17 added, and only that.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stream 17 was already the best-argued refusal in this repository</strong>, which is the
/// first thing the re-read found. <c>SecsCatalogTests</c> holds four of its measurements -
/// <c>Stream_17s_control_job_rows_are_stream_16s_with_the_nouns_swapped</c>,
/// <c>Stream_17s_trace_request_carries_stream_2s_trace_body_unchanged</c>,
/// <c>Nothing_in_stream_17_carries_what_E94_is_about</c> and
/// <c>Stream_17_is_the_one_stream_this_repository_will_not_implement</c> - and
/// <c>SecsCatalogStream15Tests</c> records that its only two readable pairs duplicate GEM's trace.
/// So this file does not re-assert any of that; it holds the three things that were missing.
/// </para>
/// <para>
/// The refusal is confirmed rather than replaced, which makes it the first re-read to come out that
/// way. What was missing was the tie-breaker: "it duplicates GEM's trace" is a statement about two
/// rows, and the rule for two rows that do one job is to serve the one from the firmer source. That
/// comparison had not been made, and it goes the way the refusal needs.
/// </para>
/// </remarks>
public class SecsCatalogStream17Tests
{
    private static SecsMessageInfo Row(byte stream, byte function)
    {
        Assert.True(SecsCatalog.TryGetStream(stream, out var info), $"stream {stream}");
        return Assert.Single(info!.Messages, message => message.Function == function);
    }

    [Fact]
    public void The_trace_request_loses_to_S2F23_because_one_is_a_definition_and_one_an_illustration()
    {
        // The tie-breaker nothing had applied. S17F9 and S2F23 carry the same five items in the
        // same order, so this is the stream 18 situation - one capability, two stream numbers,
        // serve one or the other - and the rule there is that the firmer source wins. It does:
        // S2F23 is one of the reference's definitions and S17F9 is one of its illustrations.
        Assert.Equal<string[]>([.. Row(2, 23).DataItems], [.. Row(17, 9).DataItems]);

        Assert.False(Row(2, 23).IsRepresentative);
        Assert.True(Row(17, 9).IsRepresentative);

        // Their replies differ, so this is not the exact duplication stream 20's two rows were:
        // S2F24 carries a TIAACK with two codes and S17F10 carries a TRACK, which has no table at
        // all. The capability is the same either way; the acknowledgement is weaker here.
        Assert.Equal<string[]>(["TIAACK"], [.. Row(2, 24).DataItems]);
        Assert.Equal<string[]>(["TRID", "TRACK"], [.. Row(17, 10).DataItems]);

        Assert.True(SecsCatalog.TryGetDataItem("TRACK", out var track));
        Assert.Equal("A", track!.FormatToken);
        Assert.Empty(track.Codes);
    }

    [Fact]
    public void TRACK_means_something_different_in_each_of_the_two_streams_that_name_it()
    {
        // The last loose end of the stream 4 chapter's TIAACK correction. That chapter found GEM
        // documenting its trace acknowledgement as TRACK while S2F24 carries TIAACK, and recorded
        // that TRACK belongs to streams 4 and 17. What it did not say is what TRACK is in each: a
        // transfer *job* acknowledgement twice in stream 4 - the half of that stream still refused,
        // and refused on this very item - and a *trace* acknowledgement once in stream 17.
        var carrying = SecsCatalog.Messages
            .Where(message => message.DataItems.Contains("TRACK", StringComparer.Ordinal))
            .ToArray();

        Assert.Equal<string[]>(
            ["S4F20", "S4F22", "S17F10"],
            [.. carrying.Select(message => $"S{message.Stream}F{message.Function}")]);

        Assert.All(
            carrying.Where(message => message.Stream == 4),
            message => Assert.Contains("Transfer Job", message.Name, StringComparison.Ordinal));

        Assert.Contains("Trace", Row(17, 10).Name, StringComparison.Ordinal);

        // So the name GEM reached for is real, and it means two other things in two other streams.
        // Reading one off the other was never going to work, which is the whole point of asking
        // which streams name an item before trusting what a package calls it.
        Assert.True(SecsCatalog.TryGetDataItem("TRACK", out var track));
        Assert.Equal<byte[]>([4, 17], [.. track!.Streams]);

        // And one detail that is the opposite way round from what stream 17 would need: in stream
        // 4 this untabled text sits beside an ERRCODE and an ERRTEXT, so there it can at least say
        // why. In S17F10 it is alone with the trace identifier.
        Assert.All(
            carrying.Where(message => message.Stream == 4),
            message => Assert.Contains("ERRTEXT", message.DataItems));

        Assert.DoesNotContain("ERRTEXT", Row(17, 10).DataItems);
    }

    [Fact]
    public void The_trace_reset_would_add_a_reason_and_nothing_else()
    {
        // The one row in stream 17 that is not a duplicate by its item list, weighed properly.
        // Stopping a trace is S2F23 asking for no samples, which GemHost.StopTraceAsync sends - so
        // the capability is already here, and what S17F13 would add is a reason when the machine
        // will not: ACKA arrives with a table, and ERRCODE and ERRTEXT beside it.
        Assert.Equal<string[]>(["TRID"], [.. Row(17, 13).DataItems]);
        Assert.Equal<string[]>(["ACKA", "TRID", "ERRCODE", "ERRTEXT"], [.. Row(17, 14).DataItems]);

        Assert.True(SecsCatalog.TryGetDataItem("ACKA", out var acka));
        Assert.Equal(2, acka!.Codes.Count);

        // Which S2F24 cannot do: TIAACK names two codes and has nowhere to put a reason.
        Assert.True(SecsCatalog.TryGetDataItem("TIAACK", out var tiaack));
        Assert.Equal(2, tiaack!.Codes.Count);
        Assert.DoesNotContain("ERRTEXT", Row(2, 24).DataItems);

        // That is the S1F19 argument, and it did let that row in - the only attribute get in the
        // reference that can say why it failed. The difference is what declaring the stream costs.
        // S1F19 is one row of a stream this repository serves almost entirely; this would be one
        // pair of seven in a stream whose other four primaries the catalogue's own README argues
        // are somebody else's extrapolation. A routing table is a claim about the stream, so a
        // host reading S9F5 for S17F1 would be told to keep asking for a row nothing here believes
        // in - where silence answers S9F3 and is true.
        Assert.True(SecsCatalog.TryGetStream(17, out var seventeen));
        Assert.Equal(7, seventeen!.Messages.Count(message => message.Function % 2 == 1));

        // S17F7 is the third non-control-job row and it is refused on its address, the way stream
        // 19 is: DCID is its whole body, undefined, and named by stream 17 alone.
        Assert.Equal<string[]>(["DCID"], [.. Row(17, 7).DataItems]);
        Assert.Equal<byte[]>(
            [17],
            [.. SecsCatalog.Messages
                .Where(message => message.DataItems.Contains("DCID", StringComparer.Ordinal))
                .Select(message => message.Stream)
                .Distinct()]);
    }
}
