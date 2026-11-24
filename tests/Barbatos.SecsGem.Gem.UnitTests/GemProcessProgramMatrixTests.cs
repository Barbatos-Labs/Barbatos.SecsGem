// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Stream 7's third capability: the process program matrix, which was refused for a dozen chapters.
/// </summary>
/// <remarks>
/// <para>
/// The refusal had two reasons. The first was that the family has no request and no directory, so
/// "a matrix program could be written and never read" - which looked only for a row whose name
/// contains Request or Directory. <strong>S7F15 is the read-back</strong>, in the only form E5
/// offers one: the host holds the bytes and asks whether the machine holds the same ones. This
/// library already values exactly that at the formatted half, and already serves a capability with
/// no read-back at all - <c>IGemTerminal</c> has one method.
/// </para>
/// <para>
/// The second was that the rows are item for item the plain ones, so a matrix would be stored under
/// a <c>PPID</c> that S7F5 hands back as a plain program. That is true of a shared store and this
/// one is not shared: the matrix store is its own capability with its own opt-in list, and S7F5,
/// S7F19 and S7F31 have never seen it. <c>A_matrix_is_in_no_directory_no_availability_answer_and_no_program_request</c>
/// is the test that makes the objection not happen rather than an argument that it would not.
/// </para>
/// </remarks>
public class GemProcessProgramMatrixTests
{
    private static readonly byte[] Matrix = [1, 2, 3, 4];

    private static void Model(GemEquipmentModelBuilder builder)
        => builder.AddStatusVariable(1, "State", () => SecsItem.U1(5));

    // ----------------------------------------------------------------- opt-in

    [Fact(Timeout = 15_000)]
    public void The_matrix_half_is_declared_on_its_own()
    {
        // Three capabilities in stream 7 now, and no list may leak into another or into the core
        // set: equipment may offer any of them, all of them, or none.
        foreach (var pair in GemEquipment.ProcessProgramMatrixMessages)
        {
            Assert.DoesNotContain(pair, GemEquipment.HandledMessages);
            Assert.DoesNotContain(pair, GemEquipment.ProcessProgramMessages);
            Assert.DoesNotContain(pair, GemEquipment.FormattedProcessProgramMessages);
        }

        // Three: a load inquire, a send and a verify. The smallest of the three lists, because the
        // reference gives the matrix no request, no directory and no delete.
        Assert.Equal<(byte, byte)>(
            [(7, 11), (7, 13), (7, 15)],
            [.. GemEquipment.ProcessProgramMatrixMessages]);

        Assert.All(GemEquipment.ProcessProgramMatrixMessages, p => Assert.True(p.Function % 2 == 1));
    }

    [Fact(Timeout = 15_000)]
    public async Task All_three_capabilities_register_together_and_nothing_is_declared_twice()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddGemEquipment(builder => builder.AddStatusVariable(1, "A", () => SecsItem.U4(1u)), runAsHostedService: false)
            .AddGemProcessPrograms()
            .AddGemFormattedProcessPrograms()
            .AddGemProcessProgramMatrices();

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // The failure this would catch is a shared pair: the routing table refuses a second handler
        // for one stream and function, so an overlap between the three lists throws while the
        // router is being built rather than in a fab.
        foreach (var (stream, function) in GemEquipment.ProcessProgramMessages
            .Concat(GemEquipment.FormattedProcessProgramMessages)
            .Concat(GemEquipment.ProcessProgramMatrixMessages))
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler.");
        }

        Assert.NotNull(provider.GetRequiredService<IGemProcessProgramMatrixStore>());
    }

    [Fact(Timeout = 15_000)]
    public async Task Equipment_with_no_matrix_store_declares_none_of_the_three()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddGemEquipment(builder => builder.AddStatusVariable(1, "A", () => SecsItem.U4(1u)), runAsHostedService: false)
            .AddGemProcessPrograms();

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // S9F5 rather than an ACKC7 refusal, which is the whole reason the list is separate: "I do
        // not do matrices" and "no, not that one" are different answers.
        foreach (var (stream, function) in GemEquipment.ProcessProgramMatrixMessages)
        {
            Assert.False(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have no handler.");
        }

        Assert.Null(provider.GetService<IGemProcessProgramMatrixStore>());
    }

    // ----------------------------------------------------------------- the three rows

    [Fact(Timeout = 15_000)]
    public async Task A_matrix_goes_through_the_inquire_the_send_and_the_verify()
    {
        await using var pair = GemPair.Start(Model, matrices: new GemInMemoryProcessProgramMatrixStore());
        await pair.OpenAsync();

        // S7F13 first, which is S7F1's question asked of this store: nothing is held, so the
        // machine says go ahead.
        Assert.Equal(
            GemProcessProgramGrant.Ok,
            await pair.Host.InquireProcessProgramMatrixAsync("MATRIX-1", Matrix.Length, GemPair.Token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.SendProcessProgramMatrixAsync("MATRIX-1", Matrix, GemPair.Token));

        // And the row that got the family served: the host holds the bytes, so the read it needs is
        // "do you hold these" rather than a copy coming back.
        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.VerifyProcessProgramMatrixAsync("MATRIX-1", Matrix, GemPair.Token));

        // Asking again says the machine already has it. There is no matrix delete in the reference,
        // so this is the one place the answer means something its sibling's does not: the host may
        // simply send the matrix again.
        Assert.Equal(
            GemProcessProgramGrant.AlreadyHave,
            await pair.Host.InquireProcessProgramMatrixAsync("MATRIX-1", Matrix.Length, GemPair.Token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.SendProcessProgramMatrixAsync("MATRIX-1", new byte[] { 9, 9 }, GemPair.Token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.VerifyProcessProgramMatrixAsync("MATRIX-1", new byte[] { 9, 9 }, GemPair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_matrix_the_machine_does_not_hold_is_not_found()
    {
        await using var pair = GemPair.Start(Model, matrices: new GemInMemoryProcessProgramMatrixStore());
        await pair.OpenAsync();

        // NotFound rather than PermissionNotGranted, because "I have no matrix under that name" and
        // "I have one and it is not that" are different facts and ACKC7 can say both.
        Assert.Equal(
            GemProcessProgramAcknowledgement.NotFound,
            await pair.Host.VerifyProcessProgramMatrixAsync("MATRIX-9", Matrix, GemPair.Token));

        // And a length the store will not take is refused before any of it is on the wire, which is
        // what S7F13 exists for.
        var small = new GemInMemoryProcessProgramMatrixStore { MaxBodyLength = 2 };

        await using var tight = GemPair.Start(Model, matrices: small);
        await tight.OpenAsync();

        Assert.Equal(
            GemProcessProgramGrant.NoSpace,
            await tight.Host.InquireProcessProgramMatrixAsync("MATRIX-1", 4, GemPair.Token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.LengthError,
            await tight.Host.SendProcessProgramMatrixAsync("MATRIX-1", Matrix, GemPair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_matrix_of_the_right_length_and_the_wrong_bytes_has_no_word_of_its_own()
    {
        var log = new RecordingLogger<GemEquipment>();
        await using var link = GemLink.Start(Model, logger: log, matrices: new GemInMemoryProcessProgramMatrixStore());
        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 11, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("MATRIX-1"), SecsItem.B(Matrix)),
        });

        Assert.Equal(12, sent.F);
        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        using var mismatch = await link.RequestAsync(new SecsMessage(7, 15, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("MATRIX-1"), SecsItem.B(9, 9, 9, 9)),
        });

        // ACKC7 has no code for "those bytes are not these bytes", so the reply cannot carry the
        // distinction and the log is the only place it lives. Assert the log, or the line can be
        // deleted and nothing notices - which is the same hole S7F9's own answer has.
        Assert.Equal(16, mismatch.F);

        Assert.Equal(
            (byte)GemProcessProgramAcknowledgement.PermissionNotGranted,
            mismatch.Item!.FirstValueOrDefault<byte>());

        Assert.Contains(
            log.From(LogLevel.Information),
            message => message.Contains("S7F15", StringComparison.Ordinal)
                && message.Contains("MATRIX-1", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_body_that_is_not_an_identifier_and_bytes_is_a_length_error()
    {
        await using var link = GemLink.Start(Model, matrices: new GemInMemoryProcessProgramMatrixStore());
        await link.OpenAsync();

        // The guard is part of the read, not a fallback to an empty body: handing the store
        // `default` for a PPBODY that arrived as text would save a matrix of zero bytes and answer
        // ACKC7 0, telling the host its matrix landed when what landed was nothing.
        foreach (var (function, body) in new (byte Function, SecsItem Body)[]
        {
            (11, SecsItem.A("MATRIX-1")),
            (11, SecsItem.L(SecsItem.A("MATRIX-1"), SecsItem.A("not bytes"))),
            (13, SecsItem.L(SecsItem.A("MATRIX-1"))),
            (15, SecsItem.L(SecsItem.A("MATRIX-1"), SecsItem.A("not bytes"))),
        })
        {
            using var reply = await link.RequestAsync(new SecsMessage(7, function, replyExpected: true)
            {
                Item = body,
            });

            var expected = function == 13
                ? (byte)GemProcessProgramGrant.InvalidId
                : (byte)GemProcessProgramAcknowledgement.LengthError;

            Assert.Equal((byte)(function + 1), reply.F);
            Assert.Equal(expected, reply.Item!.FirstValueOrDefault<byte>());
        }
    }

    // ----------------------------------------------------------------- the separation

    [Fact(Timeout = 15_000)]
    public async Task A_matrix_is_in_no_directory_no_availability_answer_and_no_program_request()
    {
        await using var pair = GemPair.Start(
            Model,
            programs: new GemInMemoryProcessProgramStore(),
            formatted: new GemInMemoryFormattedProcessProgramStore(),
            matrices: new GemInMemoryProcessProgramMatrixStore());

        await pair.OpenAsync();

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.SendProcessProgramAsync("PLAIN", new byte[] { 7 }, GemPair.Token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.SendProcessProgramMatrixAsync("MATRIX-1", Matrix, GemPair.Token));

        // The whole of the second refusal, made impossible rather than argued about. A matrix under
        // its own PPID is not in the directory a host reads, is not available as a program, and
        // S7F5 has nothing to hand back under its name.
        Assert.Equal<string[]>(["PLAIN"], [.. await pair.Host.ListProcessProgramsAsync(GemPair.Token)]);

        Assert.Equal(
            GemProcessProgramGrant.AlreadyHave,
            await pair.Host.IsProcessProgramAvailableAsync("PLAIN", GemPair.Token));

        Assert.Equal(
            GemProcessProgramGrant.InvalidId,
            await pair.Host.IsProcessProgramAvailableAsync("MATRIX-1", GemPair.Token));

        Assert.Null(await pair.Host.RequestProcessProgramAsync("MATRIX-1", GemPair.Token));

        // And the matrix is still there afterwards, which is what makes the three answers above a
        // separation rather than a machine that never stored it.
        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.VerifyProcessProgramMatrixAsync("MATRIX-1", Matrix, GemPair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_delete_that_empties_both_program_halves_leaves_the_matrix_alone()
    {
        var matrices = new GemInMemoryProcessProgramMatrixStore();

        await using var pair = GemPair.Start(
            Model,
            programs: new GemInMemoryProcessProgramStore(),
            formatted: new GemInMemoryFormattedProcessProgramStore(),
            matrices: matrices);

        await pair.OpenAsync();

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.SendProcessProgramAsync("PLAIN", new byte[] { 7 }, GemPair.Token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.SendProcessProgramMatrixAsync("MATRIX-1", Matrix, GemPair.Token));

        // An empty list is E5's "all of them", and it means all the process programs - of either
        // format. The reference gives the matrix no delete at all, so a host that wants one gone
        // sends another matrix; letting S7F17 reach this store would be inventing a transaction.
        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.DeleteProcessProgramsAsync([], GemPair.Token));

        Assert.Empty(await pair.Host.ListProcessProgramsAsync(GemPair.Token));
        Assert.Equal(1, matrices.Count);

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.VerifyProcessProgramMatrixAsync("MATRIX-1", Matrix, GemPair.Token));
    }
}
