// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Stream 7's six refusals, re-read - and the stream where the item list stopped discriminating.
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
/// does - cancel the command that <c>PPID</c> and <c>RCMD</c> named - and it went in. The other
/// five name a kind of program, a word, or a body nobody can pin down.
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

        // Two of the nine are the matrix family's and one is the formatted directory's reply -
        // which is to say three of this stream's six refusals are in a list with six served rows.
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

        // S7F21 is the refusal among them, and the company it keeps is the argument: a request, a
        // delete, two notices and an availability question all carry exactly what it carries.
        Assert.Equal("Process Program Reference Send", Row(21).Name);
        Assert.Equal("Delete Process Program Send", Row(17).Name);
    }

    [Fact]
    public void The_matrix_family_can_be_written_and_never_read()
    {
        // The refusal this chapter replaces. It used to say no item in the reference is named for a
        // matrix, which is true and is not the reason - the rows carry no undefined name at all.
        var matrix = SecsCatalog.Messages
            .Where(message => message.Name.Contains("Matrix", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal<byte[]>([11, 12, 13, 14, 15, 16], [.. matrix.Select(message => message.Function)]);
        Assert.All(matrix, message => Assert.Equal((byte)7, message.Stream));

        // Six rows: a send, a load inquire and a verify, with their three acknowledgements. No
        // request and no directory anywhere in the reference - so a matrix process program can be
        // sent, have its room asked about and be verified, and never once be read back.
        Assert.DoesNotContain(matrix, message => message.Name.Contains("Request", StringComparison.Ordinal));
        Assert.DoesNotContain(matrix, message => message.Name.Contains("Directory", StringComparison.Ordinal));

        // Which is what makes serving them impossible rather than merely unwise: they are
        // item-for-item the plain rows, so a matrix program would be stored under a PPID that
        // S7F5 would then hand back as a plain one. A store nothing can read from is not a store.
        Assert.Equal<string[]>([.. Row(3).DataItems], [.. Row(11).DataItems]);
        Assert.Equal<string[]>([.. Row(1).DataItems], [.. Row(13).DataItems]);
        Assert.Equal<string[]>([.. Row(9).DataItems], [.. Row(15).DataItems]);
    }

    [Fact]
    public void And_the_matrix_load_inquire_loses_to_a_definition_as_well()
    {
        // The second reason, which the stream 17 chapter's tie-break supplies: where two rows do
        // one job, the firmer source wins. S7F1 is one of the reference's definitions and S7F13 is
        // one of its illustrations, with the same two items in the same order.
        Assert.False(Row(1).IsRepresentative);
        Assert.True(Row(13).IsRepresentative);

        Assert.Equal<string[]>(["PPID", "LENGTH"], [.. Row(13).DataItems]);
        Assert.Equal<string[]>(["PPGNT"], [.. Row(14).DataItems]);
    }

    [Fact]
    public void The_reference_send_names_no_action_on_the_one_field_it_carries()
    {
        // S7F21's refusal, sharpened. The shape is six served rows' shape, so the shape says
        // nothing; what is left is the word "Reference", and a handler given a PPID and that word
        // has been told an identifier and no action. The same position S1F9 is in - a pass-through
        // whose meaning nobody states would be an API with nothing behind it.
        Assert.Equal<string[]>(["PPID"], [.. Row(21).DataItems]);
        Assert.Equal<string[]>(["ACKC7"], [.. Row(22).DataItems]);

        Assert.All(
            Row(21).DataItems.Concat(Row(22).DataItems),
            name => Assert.True(SecsCatalog.TryGetDataItem(name, out _), name));
    }

    [Fact]
    public void The_formatted_directory_reply_has_two_readings_that_put_different_bytes_on_the_wire()
    {
        // S7F35's refusal, which predates the rule it belongs to: a body whose readings disagree is
        // unparseable rather than merely uninterpretable, which the stream 6 chapter named when it
        // refused S6F25. The two served directory replies say PPID alone; S7F26, a definition, says
        // a program reply is a PPID and a PPBODY. S7F36 is recorded with both names and no nesting.
        Assert.Empty(Row(35).DataItems);
        Assert.Equal<string[]>(["PPID", "PPBODY"], [.. Row(36).DataItems]);

        Assert.Equal<string[]>(["PPID"], [.. Row(8).DataItems]);
        Assert.Equal<string[]>(["PPID"], [.. Row(20).DataItems]);
        Assert.Equal<string[]>(["PPID", "PPBODY"], [.. Row(26).DataItems]);
        Assert.False(Row(26).IsRepresentative);
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
