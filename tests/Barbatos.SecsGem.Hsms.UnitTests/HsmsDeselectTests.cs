// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// Initiating a deselect, which the GEM control-state model is the reason to have.
/// </summary>
/// <remarks>
/// The interesting property is not that the frame goes out. It is that the state change happens on
/// the actor loop rather than on the caller's thread, because <c>_state</c> has exactly one writer
/// and a deselect that assigned it directly would be the re-entrancy this design exists to prevent.
/// </remarks>
public class HsmsDeselectTests
{
    [Fact]
    public async Task A_deselect_the_peer_accepts_takes_the_link_out_of_Selected()
    {
        await using var link = TestLink.Start();
        await link.SelectAsync();

        var deselect = link.Connection.DeselectAsync(Frames.Token).AsTask();

        using var request = await link.ReadAsync();
        Assert.Equal(MessageType.DeselectRequest, request.Header.MessageType);

        await link.SendAsync(HsmsMessage.Control(
            MessageType.DeselectResponse,
            request.Header.SystemBytes,
            status: (byte)DeselectStatus.Success));

        await deselect;
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        Assert.False(link.Connection.IsSelected);
    }

    [Fact]
    public async Task The_transition_is_reported_with_a_reason_that_names_who_asked()
    {
        await using var link = TestLink.Start();
        await link.SelectAsync();

        var deselect = link.Connection.DeselectAsync(Frames.Token).AsTask();

        using var request = await link.ReadAsync();
        await link.SendAsync(HsmsMessage.Control(
            MessageType.DeselectResponse,
            request.Header.SystemBytes,
            status: (byte)DeselectStatus.Success));

        await deselect;
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        // "to the peer", not "from the peer". The two are different events and an operator reading
        // a log deserves to be told which one happened.
        var transition = Assert.Single(
            link.Transitions,
            t => t.Current == HsmsConnectionState.Connected && t.Previous == HsmsConnectionState.Selected);

        Assert.Contains("to the peer", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deselect_the_peer_refuses_leaves_the_link_selected()
    {
        await using var link = TestLink.Start();
        await link.SelectAsync();

        var deselect = link.Connection.DeselectAsync(Frames.Token).AsTask();

        using var request = await link.ReadAsync();
        await link.SendAsync(HsmsMessage.Control(
            MessageType.DeselectResponse,
            request.Header.SystemBytes,
            status: (byte)DeselectStatus.Busy));

        var error = await Assert.ThrowsAsync<SecsException>(() => deselect);

        Assert.Contains("Busy", error.Message, StringComparison.Ordinal);
        Assert.True(link.Connection.IsSelected);
    }

    [Fact]
    public async Task A_deselect_needs_the_Selected_state()
    {
        await using var link = TestLink.Start();

        // No select yet, so there is nothing to deselect and saying so beats sending a frame the
        // peer would answer with NotSelected.
        var error = await Assert.ThrowsAsync<SecsException>(
            async () => await link.Connection.DeselectAsync(Frames.Token));

        Assert.Contains("Selected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T7_runs_again_after_a_deselect_so_a_half_open_link_still_closes()
    {
        await using var link = TestLink.Start(configure: options => options.T7 = TimeSpan.FromSeconds(10));
        await link.SelectAsync();

        var deselect = link.Connection.DeselectAsync(Frames.Token).AsTask();

        using var request = await link.ReadAsync();
        await link.SendAsync(HsmsMessage.Control(
            MessageType.DeselectResponse,
            request.Header.SystemBytes,
            status: (byte)DeselectStatus.Success));

        await deselect;
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        // Back in NOT SELECTED means T7 is the timer in charge again. A peer that deselects and
        // then goes quiet must not be allowed to leave the link half-open for ever.
        await link.AdvanceUntilAsync(
            TimeSpan.FromSeconds(11),
            () => link.Connection.State == HsmsConnectionState.NotConnected,
            "T7 to drop a link that was deselected and then left alone");
    }
}
