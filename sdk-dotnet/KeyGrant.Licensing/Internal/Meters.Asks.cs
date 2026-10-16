namespace KeyGrant.Licensing.Internal;

// When a spend asks the server, and when the journal makes a status ask.
internal static partial class Meters
{
    /// <summary>
    /// Whether the journal's wait is running at <paramref name="now"/> (the guarded clock): with k <c>Unanswered</c> and
    /// <c>Void</c> entries (k at least 1), until <c>min(AskAgainMs, RetryUnansweredMs x 2^(k-1))</c> after the newest of
    /// them was opened: a minute after one, two after two, four, eight, and an hour from the seventh on. A device out of
    /// reach asks less and less often, so a person is not kept waiting on a timeout at every use. An entry from after
    /// <paramref name="now"/> (a clock that jumped ahead and was corrected) is no wait that can be measured, as a device
    /// clock set back ends the first wait: it is left out, of the count and of the newest.
    /// </summary>
    public static bool JournalWaiting(IReadOnlyList<SpendEntry>? spends, long now)
    {
        if (spends is null) return false;
        long newest = long.MinValue;
        var opened = 0;
        foreach (var e in spends)
        {
            if (e.Kind == SpendKind.Offline || e.At > now) continue;
            opened++;
            if (e.At > newest) newest = e.At;
        }
        if (opened == 0) return false;
        // The cap first: 2^(k-1) of a long journal overflows nothing, and past the seventh entry it is the hour.
        var wait = opened >= 7 ? Offline.AskAgainMs : Math.Min(Offline.AskAgainMs, Offline.RetryUnansweredMs << (opened - 1));
        return now < newest + wait;
    }

    /// <summary>
    /// Whether the wait of an uncovered spend runs at <paramref name="now"/> (the guarded clock): a <c>Void</c>, the
    /// take-back of an uncovered spend that was asked and got no answer, opened less than a minute ago. An outage then costs
    /// such a spend one ask a minute, and the journal one void a minute at most. A void opened after <paramref name="now"/>,
    /// or after the device clock (<paramref name="clockNow"/>), is no wait that can be measured, as a device clock set back
    /// ends the first wait, and is left out.
    /// </summary>
    public static bool VoidWaiting(IReadOnlyList<SpendEntry>? spends, long now, long clockNow) =>
        spends is not null && spends.Any(e => e.Kind == SpendKind.Void && e.At <= now && e.At <= clockNow && now < e.At + Offline.RetryUnansweredMs);

    /// <summary>
    /// Whether <c>UseAsync</c> asks the server. A spend the offline allowance would refuse (<paramref name="covered"/>
    /// false) asks whatever the waits of an outage (an online customer is not refused for an outage that has passed), but
    /// for a wait of its own: not while a void opened less than a minute ago stands (<see cref="VoidWaiting"/>, by both
    /// clocks). A spend the allowance covers asks every time, except while a no-answer wait runs (<c>backoffSince</c> set,
    /// <c>backoffMs</c> equal to a minute, and the device clock at or after it and before its end: a clock set back asks
    /// at once) and while the journal's wait runs (<see cref="JournalWaiting"/>). An answer of any kind ends both waits.
    /// </summary>
    /// <param name="state">The stored wait and journal.</param>
    /// <param name="clockNow">The device clock, which the first wait is judged by.</param>
    /// <param name="now">The guarded clock, which the journal's wait is judged by.</param>
    /// <param name="covered">Whether the offline allowance would cover the spend.</param>
    public static bool UseAsksNow(StoredState state, long clockNow, long now, bool covered)
    {
        if (!covered) return !VoidWaiting(state.Spends, now, clockNow);
        var waiting = state.BackoffSince is { } since
            && state.BackoffMs == Offline.RetryUnansweredMs
            && clockNow >= since
            && clockNow < since + Offline.RetryUnansweredMs;
        return !waiting && !JournalWaiting(state.Spends, now);
    }

    /// <summary>Whether the journal makes a status ask a valid lease's server now: it holds an entry and its wait is not running.</summary>
    public static bool JournalAsks(IReadOnlyList<SpendEntry>? spends, long now) => spends is { Count: > 0 } && !JournalWaiting(spends, now);
}
