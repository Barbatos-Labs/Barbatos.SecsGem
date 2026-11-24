// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Stream 7, and why nine of its fifteen representative primaries were implemented where
/// stream 15's eighteen were not.
/// </summary>
/// <remarks>
/// <para>
/// This is the chapter that stopped treating "representative" as a verdict for the second time.
/// The first was stream 15, where provenance had swept the stream into stream 19's refusal and the
/// item dictionary overturned it. Here provenance says the same thing - fifteen of twenty-two
/// primaries are rows the reference illustrates - and the measure that matters says the opposite.
/// </para>
/// <para>
/// <strong>Stream 7 has no gaps at all.</strong> Every name it uses is defined, and two of them
/// are its own: <c>PPGNT</c> and <c>ACKC7</c> are named by no other stream in the reference. That
/// is the inverse of the four streams this repository has refused, every one of which has gaps
/// <em>and</em> no vocabulary of its own.
/// </para>
/// </remarks>
public class SecsCatalogStream7Tests
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
    public void Provenance_says_what_it_said_about_stream_fifteen()
    {
        var stream = Stream(7);

        // Twenty-two primaries, seven of them documented. On provenance alone this stream looks
        // like the ones that were refused - which is the point of having three other measures.
        var definitions = stream.Messages
            .Where(message => message.IsPrimary && !message.IsRepresentative)
            .Select(message => message.Function)
            .ToList();

        Assert.Equal<byte[]>([1, 3, 5, 17, 19, 23, 25], [.. definitions]);
        Assert.Equal(44, stream.Provenance.MessageCount);
        Assert.Equal(30, stream.Provenance.RepresentativeCount);
        Assert.False(stream.Provenance.IsEntirelyRepresentative);
    }

    [Fact]
    public void And_the_anchoring_says_the_opposite()
    {
        var stream = Stream(7);

        // No gaps of either kind, which is the whole case. Nine names, every one defined, so there
        // is no field to carry untouched and no vocabulary to leave alone.
        Assert.Empty(stream.Anchoring.SharedGaps);
        Assert.Empty(stream.Anchoring.OwnGaps);
        Assert.Empty(stream.Provenance.UndefinedDataItems);
        Assert.Equal(9, stream.Anchoring.DataItemCount);

        // And two of the nine are nobody else's. A stream whose acknowledgement items are its own
        // is a stream whose refusals were written for it rather than borrowed.
        Assert.Equal<string[]>(["ACKC7", "PPGNT"], [.. stream.Anchoring.OwnAnchors]);
        Assert.False(stream.Anchoring.HasNoOwnVocabulary);
        Assert.Equal<byte[]>([7], [.. Item("PPGNT").Streams]);
        Assert.Equal<byte[]>([7], [.. Item("ACKC7").Streams]);

        // No rival spellings either, so reading a format off a sibling here is reading the
        // reference rather than correcting it - which is exactly what stream 15 could not do.
        Assert.Empty(stream.RivalSpellings);
    }

    [Fact]
    public void The_two_measures_never_disagree_about_which_streams_are_firm()
    {
        var gapless = SecsCatalog.Streams
            .Where(stream => stream.Anchoring.SharedGaps.Count == 0 && stream.Anchoring.OwnGaps.Count == 0)
            .Select(stream => stream.Number)
            .ToList();

        var rootless = SecsCatalog.Streams
            .Where(stream => stream.Anchoring.HasNoOwnVocabulary)
            .Select(stream => stream.Number)
            .ToList();

        // Seven streams have no gaps and six have no vocabulary of their own, and the two sets do
        // not overlap: no stream in this reference is both rootless and whole. So the pair of
        // measures sorts twenty streams into firm and not, with nothing ambiguous in between.
        Assert.Equal<byte[]>([1, 4, 5, 7, 9, 10, 12], [.. gapless]);
        Assert.Equal<byte[]>([8, 15, 16, 17, 19, 20], [.. rootless]);
        Assert.Empty(gapless.Intersect(rootless));

        // And the four streams refused here are all in the second list.
        Assert.All<byte>([15, 17, 19, 20], number => Assert.Contains(number, rootless));
    }

    [Fact]
    public void Six_of_the_nine_read_their_body_off_a_sibling_in_their_own_stream()
    {
        var stream = Stream(7);

        var documented = stream.Messages
            .Where(message => !message.IsRepresentative)
            .SelectMany(message => message.DataItems)
            .ToHashSet(StringComparer.Ordinal);

        // Stream 7's own documented vocabulary is five names, and it is smaller than the stream's
        // nine: the other four appear only in rows the reference illustrates. That is the
        // distinction this test exists to keep, and the first version of it got the claim wrong by
        // saying all nine implemented rows stayed inside the five.
        Assert.Equal<string[]>(
            ["ACKC7", "LENGTH", "PPBODY", "PPGNT", "PPID"],
            [.. documented.Order(StringComparer.Ordinal)]);

        // Six pairs stay inside it - the directory, the two verifies, the availability question,
        // the formatted load inquire and the two notices - so their bodies are readable off a
        // documented row in the same stream.
        byte[] fromOwnStream = [7, 8, 9, 10, 27, 28, 29, 30, 31, 32, 33, 34, 39, 40];

        foreach (var row in stream.Messages.Where(message => fromOwnStream.Contains(message.Function)))
        {
            Assert.All(row.DataItems, item => Assert.Contains(item, documented));
        }
    }

    [Fact]
    public void The_other_three_borrow_from_stream_two_which_is_implemented_here()
    {
        var stream = Stream(7);

        // RCMD, CMDA, CPNAME and CPVAL are not stream 7's own words - they are stream 2's, and in
        // stream 7 they appear only in rows the reference illustrates. So the program-related
        // commands read their body off a sibling in *another* stream, which this repository does
        // elsewhere and says so: stream 21's S21F4 follows S14F2's nesting for the same reason.
        byte[] borrowing = [37, 38, 41, 42, 43, 44];
        var borrowed = stream.Messages
            .Where(message => borrowing.Contains(message.Function))
            .SelectMany(message => message.DataItems)
            .Where(item => !string.Equals(item, "PPID", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal<string[]>(["CMDA", "CPNAME", "CPVAL", "RCMD"], [.. borrowed.Order(StringComparer.Ordinal)]);

        // What makes the loan safe is that stream 2 serves all four from rows the reference
        // documents, and this repository implements those rows: S2F41 carries RCMD, CPNAME and
        // CPVAL, and S2F22 carries CMDA. So the format is not inferred - it is the one already
        // written in Barbatos.SecsGem.Gem.
        foreach (var name in borrowed)
        {
            var carriers = Stream(2).Messages
                .Where(message => message.DataItems.Contains(name) && !message.IsRepresentative)
                .Select(message => message.Function)
                .ToList();

            Assert.NotEmpty(carriers);
        }

        Assert.Contains<byte>(2, Item("RCMD").Streams);
        Assert.Contains<byte>(2, Item("CMDA").Streams);
    }

    [Fact]
    public void The_matrix_rows_stayed_out_because_the_reference_names_only_their_failure()
    {
        // Six rows call themselves a matrix, and they add no name of their own: PPID, PPBODY,
        // LENGTH, PPGNT and ACKC7, which is the unformatted transfer's vocabulary exactly.
        var matrix = Stream(7).Messages
            .Where(message => message.Name.Contains("Matrix", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(6, matrix.Count);
        Assert.All(matrix, row => Assert.True(row.IsRepresentative));

        // And nothing in the item dictionary is a matrix. 375 items, not one of them named for the
        // thing these rows transfer - so what a process program matrix contains is a vocabulary the
        // reference never wrote down, which is the firmest kind of gap there is.
        Assert.DoesNotContain(
            SecsCatalog.DataItems,
            item => item.Name.Contains("MATRIX", StringComparison.OrdinalIgnoreCase));

        // The one matrix word in the whole reference is a failure code, and it sits in an item four
        // implemented rows already carry. A capability's failure mode is not its definition: this
        // was the strongest evidence for implementing the matrix family and it is not enough,
        // because it says what goes wrong and never what is sent.
        Assert.Equal("Matrix overflow", Item("ACKC7").MeaningOf("3"));
        Assert.Equal((byte)3, (byte)GemAcknowledgementUnderTest.MatrixOverflow);
    }

    [Fact]
    public void The_streams_one_refusal_is_a_row_the_reference_does_not_settle()
    {
        var stream = Stream(7);

        SecsMessageInfo Row(byte function)
            => Assert.Single(stream.Messages, message => message.Function == function);

        // S7F21 carries one PPID and is answered with an ACKC7, and its name is the whole of what
        // the reference says about it - a verb and an object, where the object appears in two of
        // the reference's 524 row names: this row and its own acknowledgement. The dictionary even
        // has a name for the idea, PDEREF, and no message carries it.
        // SecsCatalogStream7RefusalTests holds that measurement and the rule it stands on: a row
        // answered by nothing but an acknowledgement has to name what it does, because the
        // acknowledgement is the whole of what the host gets back.
        Assert.Equal("Process Program Reference Send", Row(21).Name);
        Assert.Equal<string[]>(["PPID"], [.. Row(21).DataItems]);

        // This test used to hold two more, and the matrix family went in the chapter after that -
        // which is why it is worth keeping the names here rather than only the measurements. S7F43
        // was refused for needing "a register of commands in flight", an argument about what an
        // application must track rather than about what the row carries; it is item for item S7F37.
        Assert.Equal<string[]>(["PPID", "RCMD"], [.. Row(43).DataItems]);
        Assert.Equal<string[]>(["PPID", "RCMD"], [.. Row(37).DataItems]);

        // And S7F36 was refused for a shape: either the reference abbreviated and the formatted
        // directory is PPID alone, like S7F8 and S7F20, or it returns every formatted program
        // whole. It returns them whole, because its record names PPBODY and theirs do not -
        // SecsCatalogStream7RefusalTests holds that measurement.
        Assert.Equal("Formatted Process Program Directory Data", Row(36).Name);
        Assert.Equal<string[]>(["PPID", "PPBODY"], [.. Row(36).DataItems]);
        Assert.Equal<string[]>(["PPID"], [.. Row(8).DataItems]);
        Assert.Equal<string[]>(["PPID"], [.. Row(20).DataItems]);
    }

    /// <summary>A copy of GEM's ACKC7 member this test can name without referencing that package.</summary>
    /// <remarks>
    /// The Catalog test project reads the reference and must not depend on a package that serves
    /// it, which is the direction of the whole repository. So the one value the matrix argument
    /// turns on is restated here, and the agreement with <c>GemProcessProgramAcknowledgement</c> is
    /// asserted on the Gem side where both are in scope.
    /// </remarks>
    private enum GemAcknowledgementUnderTest : byte
    {
        MatrixOverflow = 3,
    }
}
