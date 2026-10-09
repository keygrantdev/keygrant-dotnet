using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// <see cref="License.PurchaseUrlAsync"/>: the link a customer with no key buys through, carrying the
/// reference to this install's one-time purchase secret.
/// </summary>
public partial class PurchaseUrlTests
{
    internal const string Link = "https://buy.stripe.com/test_abc";
    /// <summary>The purchase secret held: it stays on the device.</summary>
    internal const string Secret = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>What a link carries for <see cref="Secret"/>.</summary>
    internal static readonly string Reference = ReferenceOf(Secret);
    internal static readonly TimeSpan Grace = LicenseConfig.DefaultHttpTimeout + TimeSpan.FromSeconds(15);

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex SecretShape();

    /// <summary>What a link carries for a secret: <c>kgp_</c> and the base64url of its SHA-256, derived here on its own.</summary>
    internal static string ReferenceOf(string secret) =>
        "kgp_" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A purchase secret held, its link handed out <paramref name="ago"/> ms before <see cref="Now"/>.</summary>
    internal static StoredState Held(long ago = 0) => new() { PurchaseToken = Secret, PurchaseTokenAt = Now - ago };

    [Fact]
    public async Task Keeps_a_new_secret_of_256_random_bits_and_puts_only_the_reference_to_it_in_the_link()
    {
        var h = Create();
        var url = await h.License.PurchaseUrlAsync(Link);
        var secret = h.Stored!.PurchaseToken!;
        Assert.Matches(SecretShape(), secret);
        Assert.Equal($"{Link}?client_reference_id={ReferenceOf(secret)}", url);
        Assert.DoesNotContain(secret, url);
        Assert.Equal(Now, h.Stored.PurchaseTokenAt);
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public void Draws_every_secret_from_the_platform_CSPRNG()
    {
        // The source each License starts with is RandomNumberGenerator.GetBytes itself, nothing else.
        Assert.Equal((Func<int, byte[]>)RandomNumberGenerator.GetBytes, Pickup.Csprng);
        Assert.Equal(Pickup.Csprng, Create().License.SecretBytes);
    }

    [Fact]
    public async Task Makes_a_new_secret_of_the_32_bytes_it_draws_and_puts_only_their_reference_in_the_link()
    {
        var h = Create();
        var drawn = Enumerable.Range(1, 32).Select(b => (byte)b).ToArray();
        var asked = new List<int>();
        h.License.SecretBytes = count =>
        {
            asked.Add(count);
            return drawn;
        };
        var url = await h.License.PurchaseUrlAsync(Link);
        var secret = Convert.ToBase64String(drawn).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(new[] { 32 }, asked);
        Assert.Equal(secret, h.Stored!.PurchaseToken);
        Assert.Equal($"{Link}?client_reference_id={ReferenceOf(secret)}", url);
    }

    [Fact]
    public async Task Hands_the_held_secret_s_link_out_again_its_bound_counted_from_this_call_and_clears_the_claim_s_wait()
    {
        var h = Create(Held(10 * Day) with { ClaimAskedAt = Now - 1000 });
        h.SetClock(Now + 1000);
        Assert.Equal($"{Link}?client_reference_id={Reference}", await h.License.PurchaseUrlAsync(Link));
        Assert.Equal((Secret, Now + 1000, (long?)null), (h.Stored!.PurchaseToken, h.Stored.PurchaseTokenAt, h.Stored.ClaimAskedAt));
    }

    [Fact]
    public async Task Makes_a_new_secret_once_the_held_one_is_past_its_bound()
    {
        var h = Create(Held(Offline.PurchaseTokenMs));
        var url = await h.License.PurchaseUrlAsync(Link);
        Assert.DoesNotContain(Reference, url);
        Assert.NotEqual(Secret, h.Stored!.PurchaseToken);
    }

    [Fact]
    public async Task Keeps_the_link_s_own_parameters_and_fragment_and_replaces_a_client_reference_id_on_it()
    {
        var h = Create(Held());
        var url = await h.License.PurchaseUrlAsync("https://buy.stripe.com/x?prefilled_email=jo%40example.com&client_reference_id=old#pay");
        Assert.Equal($"https://buy.stripe.com/x?prefilled_email=jo%40example.com&client_reference_id={Reference}#pay", url);
    }

    [Fact]
    public async Task Is_null_while_any_key_is_held_and_saves_nothing()
    {
        var keyed = new[]
        {
            new StoredState { Key = "KEY-1", ActivationId = "act_1", Lease = "header.payload.sig" },
            new StoredState { Key = "KEY-1", Refused = Refusal.Revoked },
            new StoredState { Key = "KEY-1" },
        };
        foreach (var state in keyed)
        {
            var h = Create(state);
            Assert.Null(await h.License.PurchaseUrlAsync(Link));
            Assert.Equal(0, h.Writes);
        }
    }

    [Theory]
    [InlineData("buy.stripe.com/test_abc")]
    [InlineData("/buy")]
    [InlineData("")]
    public async Task Rejects_a_link_that_is_not_an_absolute_URL_before_reading_anything(string link)
    {
        var h = Create();
        h.HangNextRead();
        await Assert.ThrowsAsync<ArgumentException>(() => h.License.PurchaseUrlAsync(link).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Reads);
    }

    [Fact]
    public async Task Rejects_when_the_state_cannot_be_read_or_the_token_cannot_be_saved()
    {
        var unreadable = Create();
        unreadable.FailReadAt(1);
        await Assert.ThrowsAsync<IOException>(() => unreadable.License.PurchaseUrlAsync(Link));
        var unsaved = Create();
        unsaved.FailWrites();
        await Assert.ThrowsAsync<DiskSaidNoException>(() => unsaved.License.PurchaseUrlAsync(Link));
    }

    [Fact]
    public async Task Behind_a_call_that_has_not_finished_answers_the_held_secret_s_link_as_it_is_and_throws_with_none_held()
    {
        var (h, time) = await StuckBehind(Held(Day));
        var later = h.License.PurchaseUrlAsync(Link);
        time.Advance(Grace);
        Assert.Equal($"{Link}?client_reference_id={Reference}", await later.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Writes);

        var (none, noneTime) = await StuckBehind(null);
        var refused = none.License.PurchaseUrlAsync(Link);
        noneTime.Advance(Grace);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("has not finished", error.Message);
    }

    [Fact]
    public async Task Behind_a_call_that_has_not_finished_throws_for_a_held_secret_past_its_bound_writing_nothing()
    {
        // Its link would carry a secret the next status drops: a purchase never picked up.
        var (h, time) = await StuckBehind(Held(Offline.PurchaseTokenMs));
        var refused = h.License.PurchaseUrlAsync(Link);
        time.Advance(Grace);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("has not finished", error.Message);
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public async Task Throws_OperationCanceledException_cancelled_while_it_waits_its_turn_writing_nothing()
    {
        var (h, _) = await StuckBehind(Held());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.License.PurchaseUrlAsync(Link, cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public async Task Throws_ArgumentNullException_for_no_link_before_reading_anything()
    {
        var h = Create();
        h.HangNextRead();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.License.PurchaseUrlAsync(null!).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, h.Reads);
    }

    /// <summary>A harness on manual time whose first call (a status) is stuck on a storage read that never settles.</summary>
    internal static async Task<(Harness H, ManualTime Time)> StuckBehind(StoredState? stored)
    {
        var time = new ManualTime();
        var h = Create(stored, new Options { Time = time });
        h.HangNextRead();
        _ = h.License.StatusAsync();
        await Eventually.Until(() => time.HasTimerDueIn(Grace), "the stuck call's grace");
        return (h, time);
    }
}
