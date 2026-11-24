// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Stream 7's refusals, re-read three times - and the stream where the item list stopped
/// discriminating.
/// </summary>
/// <remarks>
/// <para>
/// Every measure that decided the other nineteen streams asks the same thing: are this row's items
/// defined? In stream 7 that question is silent. <strong>Nine of its rows carry the item list
/// <c>PPID, PPBODY</c></strong> and six of those are served; <strong>seven of its primaries carry
/// <c>PPID</c> alone</strong> and six of those are served. So "every item in this row is defined"
/// is true of all six refusals and tells you nothing at all about any of them.
/// </para>
/// <para>
/// What decides instead is whether the row's <em>name</em> names an action on its fields. S7F43
/// does - cancel the command that <c>PPID</c> and <c>RCMD</c> named - and it went in.
/// </para>
/// <para>
/// <strong>S7F35 went in two chapters later, and on the item list after all.</strong> Not on
/// whether its items are defined - that question is the silent one here - but on <em>which</em>
/// item its row names: the reference records the formatted directory's reply with <c>PPBODY</c>
/// where it records the other two directories with <c>PPID</c> alone, and <c>PPBODY</c> has never
/// once been a name without a body behind it.
/// </para>
/// <para>
/// <strong>And the matrix family went in the chapter after that</strong>, which leaves S7F21 as the
/// stream's one refusal. Both of its reasons were answered by the same thing: the matrix is a
/// capability of its own with a store of its own, so S7F5 never hands one back as a plain program
/// and S7F13 is not S7F1's job under another number. What the refusal had actually measured was
/// whether the family contains a row whose name says Request or Directory - and S7F15, the verify,
/// is the read-back E5 gives a matrix.
/// </para>
/// </remarks>
public class SecsCatalogStream7RefusalTests
{
    private static SecsMessageInfo Row(byte function)
    {
        Assert.True(SecsCatalog.TryGetStream(7, out var stream));
        return Assert.Single(stream!.Messages, message => message.Function == function);
    }

    private static SecsMessageInfo[] Sharing(string items)
    {
        Assert.True(SecsCatalog.TryGetStream(7, out var stream));

        return
        [
            .. stream!.Messages.Where(message =>
                string.Join(',', message.DataItems) == items),
        ];
    }

    [Fact]
    public void Nine_rows_of_stream_7_carry_one_item_list_so_the_list_decides_nothing()
    {
        // The finding this chapter turns on. Before it, every refusal in this stream was argued as
        // though its items were the evidence; they cannot be, because six served rows carry the
        // same ones.
        var shared = Sharing("PPID,PPBODY");

        Assert.Equal<byte[]>([3, 6, 9, 11, 15, 23, 26, 27, 36], [.. shared.Select(m => m.Function)]);

        // All nine are served now, the matrix family's two among them - which is the end of the
        // road for this measure in this stream: no refusal left anywhere in it carries this item
        // list, so there is nothing for the list to discriminate.
        Assert.All(
            shared,
            message => Assert.All(
                message.DataItems,
                name => Assert.True(SecsCatalog.TryGetDataItem(name, out _), name)));
    }

    [Fact]
    public void And_seven_of_its_primaries_carry_PPID_alone()
    {
        var shared = Sharing("PPID").Where(message => message.Function % 2 == 1).ToArray();

        Assert.Equal<byte[]>([5, 17, 21, 25, 29, 31, 39], [.. shared.Select(m => m.Function)]);

        // S7F21 is the refusal among them, and the company it keeps is the argument - but not the
        // way the first version of this test put it. Split the seven by what answers them and six
        // fall into three kinds, none of which S7F21 is in: THREE are requests whose reply carries
        // the answer, TWO are news the machine sends unprompted, and ONE is the delete, whose name
        // says what it does to the store.
        foreach (var function in new byte[] { 5, 25, 31 })
        {
            Assert.Equal(SecsMessageDirection.HostToEquipment, Row(function).Direction);
            Assert.True(Row((byte)(function + 1)).DataItems.Count >= 1);
            Assert.DoesNotContain("ACKC7", Row((byte)(function + 1)).DataItems);
        }

        foreach (var function in new byte[] { 29, 39 })
        {
            Assert.Equal(SecsMessageDirection.EquipmentToHost, Row(function).Direction);
        }

        Assert.Equal("Delete Process Program Send", Row(17).Name);

        // So S7F21 is the only one of the seven that the host sends, that is answered by nothing
        // but an acknowledgement, and whose name's object is not something the stream explains.
        Assert.Equal("Process Program Reference Send", Row(21).Name);
        Assert.Equal(SecsMessageDirection.HostToEquipment, Row(21).Direction);
        Assert.Equal<string[]>(["ACKC7"], [.. Row(22).DataItems]);
    }

    [Fact]
    public void The_matrix_familys_read_back_is_its_verify_and_the_refusal_looked_for_a_request()
    {
        // The refusal, and what it actually measured. Six rows: a send, a load inquire and a
        // verify, with their three acknowledgements - and no row whose name says Request or
        // Directory, which is true and is what was taken for "can be written and never read".
        var matrix = SecsCatalog.Messages
            .Where(message => message.Name.Contains("Matrix", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal<byte[]>([11, 12, 13, 14, 15, 16], [.. matrix.Select(message => message.Function)]);
        Assert.All(matrix, message => Assert.Equal((byte)7, message.Stream));

        Assert.DoesNotContain(matrix, message => message.Name.Contains("Request", StringComparison.Ordinal));
        Assert.DoesNotContain(matrix, message => message.Name.Contains("Directory", StringComparison.Ordinal));

        // But one of the six says Verify, and a verify is a read: the host holds the bytes already,
        // so what it needs answered is whether the machine holds the same ones. The reference gives
        // the plain half and the formatted half the same row for the same purpose, and this
        // repository serves both of those - S7F9 and S7F27.
        Assert.Contains(
            matrix,
            message => message.Function == 15 && message.Name.Contains("Verify", StringComparison.Ordinal));

        Assert.Contains("Verify", Row(9).Name, StringComparison.Ordinal);
        Assert.Contains("Verify", Row(27).Name, StringComparison.Ordinal);

        // The rows being item for item the plain ones was the second reason, and it is a fact about
        // a SHARED store rather than about the rows: a matrix kept in the plain store would come
        // back from S7F5 under its own PPID as a plain program. The matrix store is its own
        // capability, and GemProcessProgramMatrixTests drives the three answers that prove S7F5,
        // S7F19 and S7F31 cannot see it.
        Assert.Equal<string[]>([.. Row(3).DataItems], [.. Row(11).DataItems]);
        Assert.Equal<string[]>([.. Row(1).DataItems], [.. Row(13).DataItems]);
        Assert.Equal<string[]>([.. Row(9).DataItems], [.. Row(15).DataItems]);
    }

    [Fact]
    public void And_the_load_inquire_loses_to_a_definition_only_if_they_do_one_job()
    {
        // The other half of the second reason, from the stream 17 chapter's tie-break: where two
        // rows do one job, the firmer source wins, and S7F1 is one of the reference's definitions
        // where S7F13 is one of its illustrations.
        Assert.False(Row(1).IsRepresentative);
        Assert.True(Row(13).IsRepresentative);

        Assert.Equal<string[]>(["PPID", "LENGTH"], [.. Row(13).DataItems]);
        Assert.Equal<string[]>(["PPGNT"], [.. Row(14).DataItems]);

        // They do not do one job. S7F1 asks room for a process program and S7F13 asks room for a
        // matrix, which are two objects in two stores - and the tie-break only ever applied on the
        // reading where they share one. Being an illustration is no veto on its own: S7F9, S7F33
        // and S7F35 all carry that flag and all three are served.
        Assert.True(Row(9).IsRepresentative);
        Assert.True(Row(33).IsRepresentative);
        Assert.True(Row(35).IsRepresentative);
    }

    [Fact]
    public void The_reference_sends_object_is_a_word_the_reference_uses_nowhere_else()
    {
        // S7F21's refusal, re-argued from the dictionary rather than from a reading of its name.
        // Both of its items are defined, so the item measures are silent here as they are
        // everywhere in this stream.
        Assert.Equal<string[]>(["PPID"], [.. Row(21).DataItems]);
        Assert.Equal<string[]>(["ACKC7"], [.. Row(22).DataItems]);

        Assert.All(
            Row(21).DataItems.Concat(Row(22).DataItems),
            name => Assert.True(SecsCatalog.TryGetDataItem(name, out _), name));

        // The name has a verb - Send - so "it names no action" was never quite the fault. What it
        // has no second instance of is its OBJECT: of the reference's 524 rows, exactly two name a
        // reference, and they are this row and its own acknowledgement. There is no sibling
        // anywhere to read the word off, which is what the matrix family had three of.
        var naming = SecsCatalog.Messages
            .Where(message => message.Name.Contains("Reference", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal<byte[]>([21, 22], [.. naming.Select(message => message.Function)]);
        Assert.All(naming, message => Assert.Equal((byte)7, message.Stream));

        // And the dictionary wrote a name for this idea and then never put it on a wire. PDEREF is
        // a reference to a process definition element, defined as text, and named by no message at
        // all - one of the 213. So the vocabulary for a recipe reference exists in the reference
        // and nothing carries it, while the row that says Reference carries a bare PPID.
        Assert.True(SecsCatalog.TryGetDataItem("PDEREF", out var pdeRef));
        Assert.Equal("A", pdeRef!.FormatToken);
        Assert.True(pdeRef.IsNamedByNoMessage);

        // The one item whose name contains REF and IS carried belongs to somewhere else entirely:
        // REFP is a wafer map's reference point, in stream 12.
        Assert.True(SecsCatalog.TryGetDataItem("REFP", out var refP));
        Assert.Equal<byte[]>([12], [.. refP!.Streams]);
    }

    [Fact]
    public void And_the_comparison_with_S1F9_went_stale_because_that_row_answers_with_data()
    {
        // The half of this refusal that had to be replaced. It used to end "the same position S1F9
        // is in - a pass-through whose meaning nobody states would be an API with nothing behind
        // it", and S1F9 has been served since the chapter that read TRACK properly.
        Assert.True(SecsCatalog.TryGetMessage(1, 9, out var transferStatus));
        Assert.Empty(transferStatus!.DataItems);

        // It is not this row's position, and the difference is what the comparison hid: S1F10
        // answers with two items of DATA, so a host that sends S1F9 gets content back and the
        // machine reports rather than acts. S7F22 carries one code.
        Assert.True(SecsCatalog.TryGetMessage(1, 10, out var transferStatusData));
        Assert.Equal<string[]>(["TSIP", "TSOP"], [.. transferStatusData!.DataItems]);

        // So the rule the comparison obscured: **a row answered by nothing but an acknowledgement
        // has to name what it does**, because the acknowledgement is the whole of what the host
        // gets back. Serving S7F21 would mean answering ACKC7 0 - done - to an instruction this
        // library cannot perform, where S9F5 says "I do not do that function" and is true. That is
        // the same distinction the spool draws between S9F5 and an RSDA of 1.
        Assert.Equal<string[]>(["ACKC7"], [.. Row(22).DataItems]);
        Assert.True(SecsCatalog.TryGetDataItem("ACKC7", out var ackc7));
        Assert.NotEmpty(ackc7!.Codes);
    }

    [Fact]
    public void The_formatted_directory_names_PPBODY_where_the_other_two_directories_do_not()
    {
        // S7F35's refusal, overturned. It said the row has two readings that put different bytes
        // out: the two served directory replies say PPID alone, while S7F26 - a definition - says a
        // program reply is a PPID and a PPBODY, and S7F36 is recorded with both names and no
        // nesting. All of that is true and none of it is a tie.
        Assert.Empty(Row(35).DataItems);
        Assert.Equal<string[]>(["PPID", "PPBODY"], [.. Row(36).DataItems]);

        Assert.Equal<string[]>(["PPID"], [.. Row(8).DataItems]);
        Assert.Equal<string[]>(["PPID"], [.. Row(20).DataItems]);
        Assert.Equal<string[]>(["PPID", "PPBODY"], [.. Row(26).DataItems]);
        Assert.False(Row(26).IsRepresentative);

        // What breaks it is that the three directory requests are the same row three times - no
        // items at all, in all three - and only this one's REPLY names a body. The silence of the
        // other two is evidence about them, not about a row that speaks.
        foreach (var function in new byte[] { 7, 19, 35 })
        {
            Assert.Empty(Row(function).DataItems);
            Assert.True(Row(function).IsPrimary);
        }

        Assert.DoesNotContain("PPBODY", Row(8).DataItems);
        Assert.DoesNotContain("PPBODY", Row(20).DataItems);
        Assert.Contains("PPBODY", Row(36).DataItems);
    }

    [Fact]
    public void And_PPBODY_is_a_body_in_every_row_of_this_stream_that_anybody_reads()
    {
        // The measurement that serves the row, and it is about the item rather than the stream.
        // PPBODY is defined, and as a B with no length - which is not the whole story, because the
        // formatted half of this stream carries the same recorded name as the structure E5 gives
        // it rather than as bytes. So what PPBODY is to a row has to be read off the rows that
        // carry it, and nine rows do.
        Assert.True(SecsCatalog.TryGetDataItem("PPBODY", out var body));
        Assert.Equal("B", body!.FormatToken);
        Assert.Equal<byte[]>([3, 6, 9, 11, 15, 23, 26, 27, 36], [.. Sharing("PPID,PPBODY").Select(m => m.Function)]);

        // Seven of the nine are served now: three unformatted rows carrying bytes, and - this is
        // the half that decides S7F36 - four formatted ones, where this library writes PPID, MDLN,
        // SOFTREV and a list of commands under that one recorded name. In every served row of
        // either half the item is the program itself. Not one row anywhere names PPBODY and
        // carries nothing, which is why a directory that names it returns programs rather than
        // names. The two left out are the matrix family's, refused for having no request and no
        // directory rather than for a shape.
        Assert.Equal<string[]>(["PPID", "PPBODY"], [.. Row(6).DataItems]);
        Assert.Equal<string[]>(["PPID", "PPBODY"], [.. Row(23).DataItems]);
        Assert.False(Row(6).IsRepresentative);
        Assert.True(Row(36).IsRepresentative);

        // And being an illustration is no veto, which this stream settled before this chapter:
        // S7F33 was left out for exactly that reason and is served now.
        Assert.True(Row(33).IsRepresentative);
    }

    [Fact]
    public void The_cancel_carries_what_the_command_carries_which_is_why_it_went_in()
    {
        // The one overturned. S7F43 is item for item S7F37, which this repository serves, and the
        // refusal said cancelling "needs a register of commands in flight that neither this
        // library nor the reference has" - an argument about what an application must track, not
        // about what the row carries. The same error TRJOBMS exposed in stream 4.
        Assert.Equal<string[]>([.. Row(37).DataItems], [.. Row(43).DataItems]);
        Assert.Equal<string[]>(["PPID", "RCMD"], [.. Row(43).DataItems]);
        Assert.Equal<string[]>(["CMDA"], [.. Row(44).DataItems]);

        // And there is no command identifier anywhere in stream 7, so the pair that started a
        // command is the only thing that can name it - which is a limit on what a host can say,
        // not a reason the row cannot be carried.
        Assert.True(SecsCatalog.TryGetStream(7, out var seven));
        Assert.DoesNotContain(
            seven!.Messages.SelectMany(message => message.DataItems).Distinct(),
            name => name.Contains("CMDID", StringComparison.Ordinal));

        // Its name does name an action on its fields, which is the measure that separates it from
        // the five that stayed out.
        Assert.Contains("Cancel", Row(43).Name, StringComparison.Ordinal);
    }
}
