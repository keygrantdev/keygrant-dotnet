using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;
using static KeyGrant.Licensing.Tests.Support.LicenseFileSupport;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// User-locked keys: what <c>ActivateAsync(key, options)</c> and <c>ImportLicenseFileAsync(content, options)</c>
/// send for the address and code a person typed, how each word the server refuses a new device with comes
/// back, and everything that never carries them. What the typing is prepared as, and the word-to-error
/// mapping, are held to the conformance vectors (<c>typedFor</c>, <c>userLockErrors</c>); these are the calls.
/// </summary>
public class UserLockTests
{
    private static readonly (string Word, LicenseError Error)[] Words =
    [
        ("user-email-required", LicenseError.UserEmailRequired),
        ("user-email-mismatch", LicenseError.UserEmailMismatch),
        ("user-code-required", LicenseError.UserCodeRequired),
        ("user-code-invalid", LicenseError.UserCodeInvalid),
        ("too-many-codes", LicenseError.TooManyCodes),
        ("too-many-attempts", LicenseError.TooManyAttempts),
        ("try-again", LicenseError.TryAgain),
    ];

    private static Harness Activating()
    {
        var h = Create();
        h.Http.Lease("activate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        return h;
    }

    private static Harness Refusing(string word, int status = 403)
    {
        var h = Create();
        h.Http.Error("activate", status, word);
        return h;
    }

    private static ActivateOptions Entry(string? email = null, string? code = null) => new() { Email = email, Code = code };

    // --- ActivateAsync with options -----------------------------------------------------------

    [Fact]
    public async Task Sends_what_the_person_typed_trimmed_beside_the_key_and_ActivateAsync_alone_sends_nothing()
    {
        var h = Activating();
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key));
        var plain = h.Http.BodyOf("activate");
        Assert.False(plain.ContainsKey("email") || plain.ContainsKey("code"));

        await h.License.ActivateAsync(Key, Entry("  Mara@Studio.example\n", " 123 456 "));
        var body = h.Http.LastBodyOf("activate");
        Assert.Equal(Key, body["key"]!.GetValue<string>());
        Assert.Equal("Mara@Studio.example", body["email"]!.GetValue<string>());
        Assert.Equal("123456", body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Takes_null_options_as_an_app_that_passes_none_may_and_sends_nothing_of_them()
    {
        var h = Refusing("user-email-required");
        Assert.Equal(ActivateResult.Failure(LicenseError.UserEmailRequired), await h.License.ActivateAsync(Key, (ActivateOptions?)null));
        var body = h.Http.BodyOf("activate");
        Assert.False(body.ContainsKey("email") || body.ContainsKey("code"));
        Assert.Equal(ImportLicenseFileResult.Refused(LicenseFileError.Malformed), await h.License.ImportLicenseFileAsync("not a file", null));
    }

    [Fact]
    public async Task Treats_a_blank_address_or_code_as_none_and_sends_the_one_there_is_alone()
    {
        var h = Refusing("user-email-required");
        await h.License.ActivateAsync(Key, Entry("   ", ""));
        var blank = h.Http.LastBodyOf("activate");
        Assert.False(blank.ContainsKey("email") || blank.ContainsKey("code"));

        await h.License.ActivateAsync(Key, Entry("mara@studio.example"));
        var address = h.Http.LastBodyOf("activate");
        Assert.Equal("mara@studio.example", address["email"]!.GetValue<string>());
        Assert.False(address.ContainsKey("code"));

        await h.License.ActivateAsync(Key, Entry(code: "123456"));
        var code = h.Http.LastBodyOf("activate");
        Assert.Equal("123456", code["code"]!.GetValue<string>());
        Assert.False(code.ContainsKey("email"));
    }

    [Fact]
    public async Task Answers_each_word_of_the_server_as_its_own_error_whatever_the_status()
    {
        foreach (var (word, error) in Words)
        {
            foreach (var status in new[] { 403, 429, 503 })
            {
                var h = Refusing(word, status);
                Assert.Equal(ActivateResult.Failure(error), await h.License.ActivateAsync(Key, Entry("mara@studio.example")));
            }
            // The word an app sends on to its front end is the server's.
            Assert.Equal(word, Names.Of(error));
        }
    }

    [Fact]
    public async Task Answers_a_locked_keys_first_ask_with_ActivateAsync_alone_as_the_error_that_asks_for_the_address()
    {
        var h = Refusing("user-email-required");
        Assert.Equal(ActivateResult.Failure(LicenseError.UserEmailRequired), await h.License.ActivateAsync(Key));
    }

    [Fact]
    public async Task Answers_a_rate_limit_or_an_outage_that_is_not_a_locks_word_as_network_as_it_always_did()
    {
        foreach (var (status, word) in new[] { (429, "rate-limited"), (503, "unavailable"), (500, "internal"), (403, "user-something-new"), (403, "Try-Again") })
        {
            var h = Refusing(word, status);
            Assert.Equal(ActivateResult.Failure(LicenseError.Network), await h.License.ActivateAsync(Key, Entry("mara@studio.example")));
        }
        var down = Create();
        down.Http.Fail = true;
        Assert.Equal(ActivateResult.Failure(LicenseError.Network), await down.License.ActivateAsync(Key, Entry("mara@studio.example")));
    }

    [Fact]
    public async Task Stores_nothing_about_the_address_the_code_or_a_refusal()
    {
        var h = Refusing("user-code-required");
        Assert.Equal(ActivateResult.Failure(LicenseError.UserCodeRequired), await h.License.ActivateAsync(Key, Entry("mara@studio.example", "123456")));
        Assert.Null(h.Stored);
        Assert.Equal(0, h.Writes);

        // Once the lease comes, the activation is stored as ever and nothing of what was typed.
        var ok = Activating();
        Assert.Equal(ActivateResult.Success, await ok.License.ActivateAsync(Key, Entry("mara@studio.example", "123456")));
        var stored = Assert.IsType<StoredState>(ok.Stored).ToJson();
        Assert.Contains(Key, stored);
        Assert.DoesNotContain("studio", stored);
        Assert.DoesNotContain("123456", stored);
        Assert.Equal(LicenseState.Licensed, (await ok.License.StatusAsync()).State);
    }

    [Fact]
    public async Task Refuses_an_address_longer_than_any_and_a_code_that_is_not_six_digits_before_the_server_is_asked()
    {
        var h = Refusing("user-email-required");
        var tooLong = new string('a', Wire.MaxEmail) + "@b.example";
        Assert.Equal(ActivateResult.Failure(LicenseError.UserEmailMismatch), await h.License.ActivateAsync(Key, Entry(tooLong)));
        Assert.Equal(ActivateResult.Failure(LicenseError.UserCodeInvalid), await h.License.ActivateAsync(Key, Entry("a@b.example", "12345")));
        Assert.False(h.Http.Called("activate"));

        // The address is judged first, and the key before both.
        Assert.Equal(ActivateResult.Failure(LicenseError.UserEmailMismatch), await h.License.ActivateAsync(Key, Entry(new string('x', 300), "nope")));
        Assert.Equal(ActivateResult.Failure(LicenseError.InvalidKey), await h.License.ActivateAsync("", Entry(new string('x', 300))));
        // A blank address beside a code that is no code is still the code's fault.
        Assert.Equal(ActivateResult.Failure(LicenseError.UserCodeInvalid), await h.License.ActivateAsync(Key, Entry("", "12")));
        Assert.False(h.Http.Called("activate"));
    }

    // --- what never carries them ------------------------------------------------------------

    [Fact]
    public async Task Sends_neither_to_validate_deactivate_or_a_trial_even_after_an_activation_that_carried_them()
    {
        var h = Activating();
        h.Http.Lease("validate", h.Sign(Key, "perpetual", 30 * 24 * 3600));
        h.Http.Answer("deactivate", 200, new JsonObject());
        Assert.Equal(ActivateResult.Success, await h.License.ActivateAsync(Key, Entry("mara@studio.example", "123456")));
        await h.License.RefreshAsync();
        await h.License.DeactivateAsync();
        await h.License.StartTrialAsync();
        foreach (var (action, body) in h.Http.Calls.Where(c => c.Action != "activate"))
        {
            Assert.False(body.ContainsKey("email") || body.ContainsKey("code"), action);
        }
        Assert.Equal(1, h.Http.Count("validate"));
        Assert.Equal(1, h.Http.Count("deactivate"));
        Assert.True(h.Http.Count("trial") > 0);
    }

    // --- a key file -------------------------------------------------------------------------

    [Fact]
    public async Task Asks_a_key_files_key_with_the_same_address_and_code_and_its_answers_come_back_as_activates()
    {
        var h = Refusing("user-code-required");
        var app = h.LicenseFor(2);
        var file = FileText(h.Signer.KeyFile());
        var result = await app.ImportLicenseFileAsync(file, Entry(" Ada@Example.com ", "123 456"));
        Assert.Equal(ImportLicenseFileResult.NotActivated(LicenseError.UserCodeRequired), result);
        var body = h.Http.BodyOf("activate");
        Assert.Equal(Key, body["key"]!.GetValue<string>());
        Assert.Equal("Ada@Example.com", body["email"]!.GetValue<string>());
        Assert.Equal("123456", body["code"]!.GetValue<string>());

        // With no options it sends neither, as before, and null is none.
        Assert.Equal(ImportLicenseFileResult.NotActivated(LicenseError.UserCodeRequired), await app.ImportLicenseFileAsync(file));
        Assert.Equal(ImportLicenseFileResult.NotActivated(LicenseError.UserCodeRequired), await app.ImportLicenseFileAsync(file, null));
        foreach (var (_, sent) in h.Http.Calls.Skip(1)) Assert.False(sent.ContainsKey("email") || sent.ContainsKey("code"));
        // Nothing of it is stored.
        Assert.Null(h.Stored);
    }

    [Fact]
    public async Task Takes_a_key_files_options_as_bytes_too()
    {
        var h = Refusing("user-email-required");
        var bytes = System.Text.Encoding.UTF8.GetBytes(FileText(h.Signer.KeyFile()));
        var result = await h.LicenseFor(2).ImportLicenseFileAsync(bytes, Entry("ada@example.com"), CancellationToken.None);
        Assert.Equal(ImportLicenseFileResult.NotActivated(LicenseError.UserEmailRequired), result);
        Assert.Equal("ada@example.com", h.Http.BodyOf("activate")["email"]!.GetValue<string>());
    }

    [Fact]
    public async Task Takes_a_key_file_whose_key_activates_with_the_options_sending_them_normalised_and_storing_neither()
    {
        var h = Create();
        h.Http.Lease("activate", LeaseTo3(h));
        var taken = await h.LicenseFor(2).ImportLicenseFileAsync(FileText(h.Signer.KeyFile()), Entry(" Mara@Studio.example\n", " 123 456 "));
        Assert.True(taken.Ok);
        Assert.Equal(LicenseFileKind.Key, taken.Kind);
        var body = h.Http.BodyOf("activate");
        Assert.Equal(Key, body["key"]!.GetValue<string>());
        Assert.Equal("Mara@Studio.example", body["email"]!.GetValue<string>());
        Assert.Equal("123456", body["code"]!.GetValue<string>());
        // Activated as ever, and nothing of what was typed (nor the file's own address) is stored.
        var stored = Assert.IsType<StoredState>(h.Stored).ToJson();
        Assert.Contains(Key, stored);
        // Whatever the case, the address as typed (its local part and its domain) and the code both as
        // normalised and as typed.
        foreach (var typed in new[] { "mara@", "studio.example", "123456", "123 456", "example.com" })
        {
            Assert.DoesNotContain(typed, stored, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Judges_a_key_files_key_before_the_address_and_the_address_before_the_code()
    {
        var h = Refusing("user-email-required");
        var app = h.LicenseFor(2);
        var options = Entry(new string('x', 300), "nope");
        Assert.Equal(ImportLicenseFileResult.NotActivated(LicenseError.UserEmailMismatch), await app.ImportLicenseFileAsync(FileText(h.Signer.KeyFile()), options));
        Assert.Equal(ImportLicenseFileResult.NotActivated(LicenseError.InvalidKey), await app.ImportLicenseFileAsync(FileText(h.Signer.KeyFile(new JsonObject { ["sub"] = " " })), options));
        Assert.False(h.Http.Called("activate"));
    }

    [Fact]
    public async Task Refuses_a_file_that_is_no_licence_file_before_any_key_is_asked_for_whatever_options_come_with_it()
    {
        var h = Create();
        Assert.Equal(ImportLicenseFileResult.Refused(LicenseFileError.Malformed), await h.License.ImportLicenseFileAsync("not a file", Entry("mara@studio.example")));
        Assert.False(h.Http.Called("activate"));
    }

    [Fact]
    public async Task Ignores_the_options_for_an_offline_file_and_asks_nothing()
    {
        var h = Create();
        var taken = await h.LicenseFor(2).ImportLicenseFileAsync(FileText(h.Signer.OfflineFile()), Entry(new string('x', 300), "nope"));
        Assert.True(taken.Ok);
        Assert.Empty(h.Http.Calls);
    }
}
