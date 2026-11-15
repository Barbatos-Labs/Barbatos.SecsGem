// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The two state models, over a real link.
/// </summary>
/// <remarks>
/// The distinction these tests exist to protect is that a selected HSMS link and an established
/// GEM conversation are different things. Every one of them drives a real connection and a real
/// equipment actor; the only thing faked is the clock.
/// </remarks>
public class GemCommunicationTests
{
    // ----------------------------------------------------------------- establishing

    [Fact]
    public async Task The_equipment_asks_to_establish_communications_as_soon_as_the_link_is_selected()
    {
        await using var link = GemLink.Start();

        await link.SelectAsync();
        using var request = await link.ReadAsync();
        var body = request.Data!;

        Assert.Equal(1, body.S);
        Assert.Equal(13, body.F);
        Assert.True(body.ReplyExpected);

        // MDLN and SOFTREV, which is also what S1F2 answers with.
        Assert.Equal("BRBTOS", body.Item![0].GetString());
        Assert.Equal("0.1.0", body.Item[1].GetString());
    }

    [Fact]
    public async Task A_selected_link_is_not_yet_a_conversation()
    {
        await using var link = GemLink.Start();

        await link.SelectAsync();

        // The bytes flow and E30 still says nothing may be reported. Equipment that treats Selected
        // as permission to talk is equipment a host is entitled to ignore.
        Assert.True(link.Connection.IsSelected);
        Assert.False(link.Equipment.IsCommunicating);
    }

    [Fact]
    public async Task COMMACK_zero_establishes_communication()
    {
        await using var link = GemLink.Start();

        await link.SelectAsync();
        using var request = await link.EstablishAsync();

        Assert.Equal(GemCommunicationState.Communicating, link.Equipment.CommunicationState);
    }

    [Fact]
    public async Task COMMACK_one_leaves_the_equipment_waiting_and_it_asks_again_after_the_interval()
    {
        await using var link = GemLink.Start(configure: options =>
            options.EstablishCommunicationsRetryInterval = TimeSpan.FromSeconds(30));

        await link.SelectAsync();
        using var denied = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);

        await GemLink.WaitUntilAsync(
            () => link.Equipment.CommunicationState == GemCommunicationState.WaitingToRetry,
            "the equipment to wait out the retry interval");

        // Nothing happens until the interval elapses. That delay is the whole reason E30 has a
        // WAIT DELAY state: without it, two entities that come up together spend the day
        // exchanging denials.
        var second = link.ReadAsync();
        Assert.False(second.IsCompleted);

        link.Clock.Advance(TimeSpan.FromSeconds(30));

        using var retry = await second;
        Assert.Equal(1, retry.Data!.S);
        Assert.Equal(13, retry.Data.F);
    }

    [Fact]
    public async Task A_host_may_establish_communication_itself()
    {
        await using var link = GemLink.Start();

        await link.SelectAsync();
        using var denied = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);

        using var request = new SecsMessage(1, 13, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("HOST"), SecsItem.A("1.0")),
        };

        using var reply = await link.RequestAsync(request);

        Assert.Equal(14, reply.F);
        Assert.Equal((byte)GemCommunicationAcknowledgement.Accepted, reply.Item![0].FirstValueOrDefault<byte>());
        Assert.Equal("BRBTOS", reply.Item[1][0].GetString());

        // The acknowledgement goes out from the dispatch loop; the transition is applied on the
        // equipment's own loop a moment later, which is what stops it racing with the S1F13 the
        // equipment itself still has in flight. So this is a wait, not a read.
        await GemLink.WaitUntilAsync(
            () => link.Equipment.IsCommunicating,
            "the equipment to accept the host's S1F13");
    }

    [Fact]
    public async Task A_second_S1F13_does_not_throw_away_what_the_host_already_configured()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var defined = await link.RequestAsync(new SecsMessage(2, 33, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.L(SecsItem.U4(7_000u), SecsItem.L(SecsItem.U4(1u))))),
        });

        Assert.Equal((byte)GemDefineReportAcknowledgement.Accepted, defined.Item!.FirstValueOrDefault<byte>());

        List<GemControlState> transitions = [];
        link.Equipment.ControlStateChanged += (_, e) => transitions.Add(e.Current);

        using var again = new SecsMessage(1, 13, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("HOST"), SecsItem.A("1.0")),
        };

        using var reply = await link.RequestAsync(again);

        Assert.Equal((byte)GemCommunicationAcknowledgement.Accepted, reply.Item![0].FirstValueOrDefault<byte>());

        // E30 lets either side ask at any time, and asking again says nothing about report
        // definitions. Re-establishing would reset the model underneath a host that had already
        // configured it - and flap the control state on the way through.
        Assert.Equal<uint[]>([1], [.. link.Equipment.Model.VariablesForReport(7_000)]);
        Assert.Empty(transitions);
        Assert.True(link.Equipment.IsCommunicating);
    }

    [Fact]
    public async Task Communication_disabled_means_nothing_is_offered_and_nothing_is_accepted()
    {
        await using var link = GemLink.Start(configure: options => options.CommunicationEnabled = false);

        await link.SelectAsync();

        Assert.Equal(GemCommunicationState.Disabled, link.Equipment.CommunicationState);

        using var request = new SecsMessage(1, 13, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("HOST"), SecsItem.A("1.0")),
        };

        using var reply = await link.RequestAsync(request);

        Assert.Equal((byte)GemCommunicationAcknowledgement.Denied, reply.Item![0].FirstValueOrDefault<byte>());

        // Denied, and the identity is withheld with it: a zero-length list rather than a model name.
        Assert.Equal(0, reply.Item[1].Count);
        Assert.False(link.Equipment.IsCommunicating);
    }

    [Fact]
    public async Task Losing_the_link_ends_the_conversation_and_takes_the_equipment_offline()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        Assert.True(link.Equipment.IsCommunicating);
        Assert.Equal(GemControlState.OnlineRemote, link.Equipment.ControlState);

        await link.DropAsync();

        await GemLink.WaitUntilAsync(() => !link.Equipment.IsCommunicating, "the conversation to end");

        // The control state is set a line later on the same loop, so waiting for the conversation
        // to end is not waiting for this - poll the thing being asserted. No host can see that gap,
        // because the link it would have to ask over is the one that just went.
        await GemLink.WaitUntilAsync(
            () => link.Equipment.ControlState == GemControlState.EquipmentOffline,
            "the equipment to go off-line");

        // EquipmentOffline, not HostOffline. The host did not ask for this, and conflating the two
        // throws away the only information an operator has about why commands are being refused.
        Assert.Equal(GemControlState.EquipmentOffline, link.Equipment.ControlState);
    }

    // ----------------------------------------------------------------- control state

    [Fact]
    public async Task The_default_control_state_decides_where_it_lands()
    {
        await using var remote = GemLink.Start(configure: o => o.DefaultControlState = GemControlState.OnlineRemote);
        await remote.OpenAsync();
        Assert.Equal(GemControlState.OnlineRemote, remote.Equipment.ControlState);

        await using var local = GemLink.Start(configure: o => o.DefaultControlState = GemControlState.OnlineLocal);
        await local.OpenAsync();
        Assert.Equal(GemControlState.OnlineLocal, local.Equipment.ControlState);

        await using var offline = GemLink.Start(configure: o => o.DefaultControlState = GemControlState.EquipmentOffline);
        await offline.OpenAsync();
        Assert.Equal(GemControlState.EquipmentOffline, offline.Equipment.ControlState);
    }

    [Fact]
    public async Task S1F1_answers_with_the_identity_when_online()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 1, replyExpected: true));

        Assert.Equal(2, reply.F);
        Assert.Equal(2, reply.Item!.Count);
        Assert.Equal("BRBTOS", reply.Item[0].GetString());
    }

    [Fact]
    public async Task S1F1_answers_with_a_zero_length_list_when_offline()
    {
        await using var link = GemLink.Start(configure: o => o.DefaultControlState = GemControlState.EquipmentOffline);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 1, replyExpected: true));

        // E30 is specific about this. It is how a host learns the machine is alive but not
        // available, without having to ask a second question.
        Assert.True(reply.Item!.IsList);
        Assert.Equal(0, reply.Item.Count);
    }

    [Fact]
    public async Task S1F15_puts_the_equipment_in_host_offline()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 15, replyExpected: true));

        Assert.Equal(16, reply.F);
        Assert.Equal((byte)GemOfflineAcknowledgement.Acknowledged, reply.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(GemControlState.HostOffline, link.Equipment.ControlState);
    }

    [Fact]
    public async Task S1F17_brings_it_back_from_host_offline()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var offline = await link.RequestAsync(new SecsMessage(1, 15, replyExpected: true));
        Assert.Equal(GemControlState.HostOffline, link.Equipment.ControlState);

        using var online = await link.RequestAsync(new SecsMessage(1, 17, replyExpected: true));

        Assert.Equal(18, online.F);
        Assert.Equal((byte)GemOnlineAcknowledgement.Accepted, online.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(GemControlState.OnlineRemote, link.Equipment.ControlState);
    }

    [Fact]
    public async Task S1F17_when_already_online_says_so_rather_than_pretending_to_act()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 17, replyExpected: true));

        Assert.Equal((byte)GemOnlineAcknowledgement.AlreadyOnline, reply.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(GemControlState.OnlineRemote, link.Equipment.ControlState);
    }

    [Fact]
    public async Task S1F17_is_refused_while_the_equipment_itself_is_offline()
    {
        await using var link = GemLink.Start(configure: o => o.DefaultControlState = GemControlState.EquipmentOffline);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 17, replyExpected: true));

        // The equipment's own off-line is not the host's to overrule, and answering 0 would promise
        // a transition that is not going to happen.
        Assert.Equal((byte)GemOnlineAcknowledgement.Refused, reply.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(GemControlState.EquipmentOffline, link.Equipment.ControlState);
    }

    [Fact]
    public async Task Going_online_is_refused_until_there_is_a_conversation_to_be_online_to()
    {
        await using var link = GemLink.Start(configure: o => o.DefaultControlState = GemControlState.EquipmentOffline);

        await link.SelectAsync();

        Assert.False(link.Equipment.GoOnline());
        Assert.Equal(GemControlState.EquipmentOffline, link.Equipment.ControlState);

        using var request = await link.EstablishAsync();

        Assert.True(link.Equipment.GoOnline());
        Assert.Equal(GemControlState.OnlineRemote, link.Equipment.ControlState);
    }

    [Fact]
    public async Task A_transition_passes_through_attempt_online_so_an_observer_can_see_it()
    {
        await using var link = GemLink.Start(configure: o => o.DefaultControlState = GemControlState.EquipmentOffline);
        await link.OpenAsync();

        List<GemControlState> seen = [];
        link.Equipment.ControlStateChanged += (_, e) => seen.Add(e.Current);

        Assert.True(link.Equipment.GoOnline());

        // E30 draws ATTEMPT ON-LINE as a state, so it is reported as one rather than being skipped
        // because nothing here needs to pause in it.
        Assert.Equal([GemControlState.AttemptOnline, GemControlState.OnlineRemote], seen);
    }

    [Fact]
    public async Task Every_transition_is_reported_once_with_a_reason()
    {
        await using var link = GemLink.Start();

        List<GemCommunicationStateChangedEventArgs> seen = [];
        link.Equipment.CommunicationStateChanged += (_, e) => seen.Add(e);

        await link.OpenAsync();

        Assert.Contains(seen, e => e.Current == GemCommunicationState.WaitingForAcknowledge);
        Assert.Contains(seen, e => e.Current == GemCommunicationState.Communicating);
        Assert.All(seen, e => Assert.NotNull(e.Reason));
        Assert.All(seen, e => Assert.NotEqual(e.Previous, e.Current));
    }
}
