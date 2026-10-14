using System.Text;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.Harness;
using static KeyGrant.Licensing.Tests.Support.LicenseFileSupport;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// Licence files: an offline file imported, stored and answered with no network; every refusal; a stored file
/// that does not license this build; and what this port reads differently from JSON than the reference.
/// </summary>
public class LicenseFileTests
{
    private static ImportLicenseFileResult OfflineTaken(LicensedTo to) => ImportLicenseFileResult.Taken(LicenseFileKind.Offline, to);

    [Fact]
    public async Task An_offline_file_is_taken_from_its_text_with_no_network_stored_and_licenses_as_the_file()
    {
        var h = Create();
        var token = h.Signer.OfflineFile();
        var app = h.LicenseFor(2);
        Assert.Equal(OfflineTaken(Ada), await app.ImportLicenseFileAsync(FileText(token)));
        Assert.Equal(token, Assert.IsType<StoredState>(h.Stored).LicenseFile);
        Assert.Equal(FileLicensed, await app.StatusAsync());
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task An_offline_file_is_taken_as_bytes_a_slice_of_a_larger_buffer_read_alone()
    {
        var h = Create();
        var token = h.Signer.OfflineFile();
        var file = Encoding.UTF8.GetBytes(FileText(token));
        // The file inside a buffer holding more around it: only the slice is the file.
        var buffer = new byte[file.Length + 20];
        Array.Fill(buffer, (byte)'x');
        file.CopyTo(buffer, 10);
        Assert.Equal(OfflineTaken(Ada), await h.LicenseFor(2).ImportLicenseFileAsync(buffer.AsMemory(10, file.Length)));
        Assert.Equal(token, Assert.IsType<StoredState>(h.Stored).LicenseFile);
    }

    [Fact]
    public async Task A_stored_offline_file_never_goes_online_refresh_included_though_the_key_beside_it_has_run_out()
    {
        var h = Create();
        // Without the file, this run-out lease would be asked about, and the wait after the ask saved.
        var expired = h.SignLease(new LeaseInput { Sub = Key, Major = 3, TtlSeconds = 3600 }, Now - 2 * Day);
        h.Stored = new StoredState { Key = Key, ActivationId = "act_1", Lease = expired, LastSeen = Now, LicenseFile = h.Signer.OfflineFile() };
        var app = h.LicenseFor(2);
        Assert.Equal(FileLicensed, await app.StatusAsync());
        Assert.Equal(FileLicensed, await app.RefreshAsync());
        Assert.Empty(h.Http.Calls);
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public async Task A_file_says_when_it_is_a_test_licence_and_carries_its_entitlements_which_HasAsync_reads()
    {
        var h = Create();
        h.Stored = StoredFile(h.Signer.OfflineFile(new JsonObject { ["test"] = true, ["ent"] = new JsonObject { ["edition"] = "pro", ["cloud"] = true } }));
        var app = h.LicenseFor(2);
        var expected = FileLicensed with
        {
            Test = true,
            Entitlements = new Dictionary<string, Entitlement> { ["edition"] = new("pro"), ["cloud"] = new(true) },
        };
        Assert.Equal(expected, await app.StatusAsync());
        Assert.True(await app.HasAsync("cloud"));
        var missing = await Assert.ThrowsAsync<MissingEntitlementException>(() => app.RequireAsync("export"));
        Assert.Equal("export", missing.Entitlement);
    }

    [Fact]
    public async Task Removing_no_file_is_no_error_and_leaves_unlicensed_as_it_was()
    {
        var h = Create();
        var app = h.LicenseFor(2);
        await app.RemoveLicenseFileAsync();
        Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await app.StatusAsync());
    }

    [Fact]
    public async Task A_file_replaces_the_one_stored_before_and_a_file_refused_leaves_it()
    {
        var h = Create();
        var app = h.LicenseFor(2);
        await app.ImportLicenseFileAsync(h.Signer.OfflineFile());
        var second = h.Signer.OfflineFile(new JsonObject { ["fid"] = "lf_3", ["name"] = "Grace Hopper", ["email"] = null });
        Assert.Equal(OfflineTaken(new LicensedTo("Grace Hopper", null)), await app.ImportLicenseFileAsync(second));
        var refused = await app.ImportLicenseFileAsync(h.Signer.OfflineFile(new JsonObject { ["product"] = "maindeck" }));
        Assert.Equal(ImportLicenseFileResult.Refused(LicenseFileError.WrongProduct), refused);
        Assert.Equal(second, Assert.IsType<StoredState>(h.Stored).LicenseFile);
    }

    [Fact]
    public async Task A_file_refused_names_each_reason_and_stores_nothing()
    {
        var h = Create();
        var token = h.Signer.OfflineFile();
        var app = h.LicenseFor(2, revoked: ["lf_9"]);
        (byte[] Content, LicenseFileError Error)[] cases =
        [
            ([], LicenseFileError.Malformed),
            (Encoding.UTF8.GetBytes($"{token}\n{token}"), LicenseFileError.Malformed),
            ([0xff, 0xfe, 0x00], LicenseFileError.Malformed),
            (Encoding.UTF8.GetBytes(LeaseTo3(h)), LicenseFileError.NotALicenseFile),
            (Encoding.UTF8.GetBytes(PayloadSwapped(token, h.Signer.OfflineFile(new JsonObject { ["major"] = 9 }))), LicenseFileError.BadSignature),
            (Encoding.UTF8.GetBytes(new TestSigner().OfflineFile()), LicenseFileError.UnknownKey),
            (Encoding.UTF8.GetBytes(h.Signer.OfflineFile(new JsonObject { ["product"] = "maindeck" })), LicenseFileError.WrongProduct),
            (Encoding.UTF8.GetBytes(h.Signer.OfflineFile(new JsonObject { ["fid"] = "lf_9" })), LicenseFileError.RevokedFile),
            (Encoding.UTF8.GetBytes(h.Signer.OfflineFile(new JsonObject { ["major"] = 1 })), LicenseFileError.Upgrade),
            (Encoding.UTF8.GetBytes(h.Signer.OfflineFile(new JsonObject { ["minMajor"] = 3 })), LicenseFileError.Outdated),
        ];
        foreach (var (content, error) in cases)
        {
            Assert.Equal(ImportLicenseFileResult.Refused(error), await app.ImportLicenseFileAsync(content));
        }
        Assert.Null(h.Stored?.LicenseFile);
        Assert.Empty(h.Http.Calls);
    }

    [Fact]
    public async Task A_text_longer_than_a_file_can_be_is_malformed_unread()
    {
        var h = Create();
        var padded = new string('#', LicenseFileFormat.MaxBytes) + "\n" + h.Signer.OfflineFile();
        Assert.Equal(ImportLicenseFileResult.Refused(LicenseFileError.Malformed), await h.LicenseFor(2).ImportLicenseFileAsync(padded));
        Assert.Null(h.Stored?.LicenseFile);
    }

    [Fact]
    public async Task No_text_at_all_is_an_argument_error()
    {
        var h = Create();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.LicenseFor(2).ImportLicenseFileAsync((string)null!));
    }

    [Fact]
    public async Task An_import_or_a_removal_that_cannot_be_saved_throws_one_attempt_each()
    {
        var h = Create();
        h.Stored = StoredFile(h.Signer.OfflineFile());
        var app = h.LicenseFor(2);
        h.FailWrites();
        await Assert.ThrowsAsync<DiskSaidNoException>(() => app.ImportLicenseFileAsync(h.Signer.OfflineFile(new JsonObject { ["fid"] = "lf_3" })));
        await Assert.ThrowsAsync<DiskSaidNoException>(() => app.RemoveLicenseFileAsync());
        Assert.Equal(2, h.Writes);
    }

    [Fact]
    public async Task An_import_or_a_removal_whose_state_cannot_be_read_throws_writing_nothing()
    {
        var h = Create();
        var app = h.LicenseFor(2);
        h.FailReadAt(1);
        await Assert.ThrowsAsync<IOException>(() => app.ImportLicenseFileAsync(h.Signer.OfflineFile()));
        h.FailReadAt(1);
        await Assert.ThrowsAsync<IOException>(() => app.RemoveLicenseFileAsync());
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public void A_configuration_whose_denylist_holds_null_is_refused_naming_it()
    {
        var h = Create();
        var error = Assert.Throws<ArgumentException>(() => h.LicenseFor(2, revoked: ["lf_1", null!]));
        Assert.Contains("RevokedLicenseFiles", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_denylist_is_the_one_the_License_was_made_with_whatever_the_app_does_to_its_list_after()
    {
        var h = Create();
        h.Stored = StoredFile(h.Signer.OfflineFile());
        var revoked = new List<string> { "lf_9" };
        var app = h.LicenseFor(2, revoked: revoked);
        revoked.Add("lf_1");
        Assert.Equal(FileLicensed, await app.StatusAsync());
    }

    [Fact]
    public async Task A_stored_file_is_revoked_file_once_an_update_lists_it_with_whose_it_is_and_no_entitlements()
    {
        var h = Create();
        h.Stored = StoredFile(h.Signer.OfflineFile(new JsonObject { ["ent"] = new JsonObject { ["cloud"] = true } }));
        var app = h.LicenseFor(2, revoked: ["lf_0", "lf_1"]);
        Assert.Equal(FileInvalid(LicenseReason.RevokedFile), await app.StatusAsync());
        Assert.False(await app.HasAsync("cloud"));
    }

    [Fact]
    public async Task A_stored_file_is_upgrade_on_a_build_newer_than_it_covers_and_outdated_on_one_older()
    {
        var h = Create();
        h.Stored = StoredFile(h.Signer.OfflineFile(new JsonObject { ["minMajor"] = 2, ["major"] = 3 }));
        Assert.Equal(FileInvalid(LicenseReason.Upgrade), await h.LicenseFor(4).StatusAsync());
        Assert.Equal(FileInvalid(LicenseReason.Outdated), await h.LicenseFor(1).StatusAsync());
    }

    [Fact]
    public async Task A_major_past_a_long_is_held_at_its_end_and_one_not_finite_or_negative_is_malformed()
    {
        // The reference compares majors as doubles; this port holds a whole number past a long at its end,
        // which judges every build alike. Infinity is no whole number there either.
        var h = Create();
        var app = h.LicenseFor(2);
        h.Stored = StoredFile(h.Signer.OfflineFile(new JsonObject { ["major"] = 1e20 }));
        Assert.Equal(FileLicensed, await app.StatusAsync());
        h.Stored = StoredFile(h.Signer.OfflineFile(new JsonObject { ["major"] = 1e20, ["minMajor"] = 1e19 }));
        Assert.Equal(FileInvalid(LicenseReason.Outdated), await app.StatusAsync());
        var claims = """{"iss":"keygrant","kind":"offline","fid":"lf_1","sub":"K","product":"sluice","name":"Ada","ent":{},"iat":1,"major":""";
        // The control: the same claims with a real major are a file.
        Assert.True((await app.ImportLicenseFileAsync(h.Signer.RawFile($"{claims}3}}"))).Ok);
        foreach (var major in new[] { "1e400", "-1e20" })
        {
            var result = await app.ImportLicenseFileAsync(h.Signer.RawFile($"{claims}{major}}}"));
            Assert.Equal(ImportLicenseFileResult.Refused(LicenseFileError.Malformed), result);
        }
    }

    [Fact]
    public async Task Claims_nested_deeper_than_this_port_holds_are_read_as_the_reference_reads_them()
    {
        // An unknown claim is never read however deep, nor a dropped ent member; a claim the schema checks
        // cannot nest, so one that deep is malformed, as the reference finds it.
        var h = Create();
        var app = h.LicenseFor(2);
        var deep = new string('[', 100) + new string(']', 100);
        // Every claim but the licensee: each case names its own (a name nested deep, or the email).
        var claims = """{"iss":"keygrant","kind":"offline","fid":"lf_1","sub":"SLUICE-AAAA-BBBB-CCCC-DDDD","product":"sluice","iat":1,"major":3""";
        const string Licensee = "\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\"";
        Assert.Equal(OfflineTaken(Ada), await app.ImportLicenseFileAsync(h.Signer.RawFile($"{claims},{Licensee},\"x\":{deep},\"ent\":{{\"d\":{deep}}}}}")));
        Assert.Equal(FileLicensed, await app.StatusAsync());
        foreach (var claim in new[] { $"\"email\":\"ada@example.com\",\"name\":{deep}", $"{Licensee},\"test\":{deep}" })
        {
            var result = await app.ImportLicenseFileAsync(h.Signer.RawFile($"{claims},{claim}}}"));
            Assert.Equal(ImportLicenseFileResult.Refused(LicenseFileError.Malformed), result);
        }
    }

    [Fact]
    public async Task A_stored_value_that_is_not_text_is_no_file_as_every_field_of_the_wrong_type_is()
    {
        // The reference reads one as tampered; this port reads a stored field of the wrong type as absent, every
        // field alike. Either way it licenses nothing.
        var h = Create();
        var app = h.LicenseFor(2);
        foreach (var value in new JsonNode?[] { 42, null, new JsonObject { ["token"] = "x" } })
        {
            h.Stored = StoredState.FromJson(new JsonObject { ["licenseFile"] = value?.DeepClone() }.ToJsonString());
            Assert.Equal(new LicenseStatus { State = LicenseState.Unlicensed }, await app.StatusAsync());
        }
        // The control: the same field holding text is the stored file.
        h.Stored = StoredState.FromJson(new JsonObject { ["licenseFile"] = h.Signer.OfflineFile() }.ToJsonString());
        Assert.Equal(FileLicensed, await app.StatusAsync());
    }

    [Fact]
    public async Task A_stored_token_edited_or_a_stored_key_file_or_none_at_all_is_tampered()
    {
        var h = Create();
        var edited = PayloadSwapped(h.Signer.OfflineFile(), h.Signer.OfflineFile(new JsonObject { ["major"] = 9 }));
        var app = h.LicenseFor(2);
        foreach (var stored in new[] { edited, h.Signer.KeyFile(), "" })
        {
            h.Stored = StoredFile(stored);
            Assert.Equal(FileUnread(LicenseReason.Tampered), await app.StatusAsync());
        }
    }

    [Fact]
    public async Task A_stored_file_survives_a_rotation_while_the_build_carries_its_key_and_is_unknown_key_after()
    {
        var h = Create();
        var next = new TestSigner();
        h.Stored = StoredFile(h.Signer.OfflineFile());
        // A build after the switch: the new key signs, the old one is kept because it signed files.
        Assert.Equal(FileLicensed, await h.LicenseFor(2, keys: [next.Jwk, h.Signer.Jwk]).StatusAsync());
        // A build that dropped it (a leaked key): the file must be signed again.
        Assert.Equal(FileUnread(LicenseReason.UnknownKey), await h.LicenseFor(2, keys: [next.Jwk]).StatusAsync());
    }
}
