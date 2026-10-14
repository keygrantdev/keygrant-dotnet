using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using KeyGrant.Licensing.Tests.Support;
using static KeyGrant.Licensing.Tests.Support.LicenseFileSupport;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// The licence-file types an app handles: the import's result, a status from a file printed by content, the
/// errors' words, the format's constants, and the stored field's name in the state file every SDK shares.
/// </summary>
public class LicenseFileTypesTests
{
    [Fact]
    public void An_import_result_says_what_was_taken_or_why_not_and_nothing_else()
    {
        var taken = ImportLicenseFileResult.Taken(LicenseFileKind.Key, Ada);
        Assert.Equal((true, LicenseFileKind.Key, Ada, null, null), (taken.Ok, taken.Kind, taken.LicensedTo, taken.Error, taken.ActivateError));
        var refused = ImportLicenseFileResult.Refused(LicenseFileError.RevokedFile);
        Assert.Equal((false, null, null, LicenseFileError.RevokedFile, null), (refused.Ok, refused.Kind, refused.LicensedTo, refused.Error, refused.ActivateError));
        var notActivated = ImportLicenseFileResult.NotActivated(LicenseError.Limit);
        Assert.Equal((false, null, null, null, LicenseError.Limit), (notActivated.Ok, notActivated.Kind, notActivated.LicensedTo, notActivated.Error, notActivated.ActivateError));
        Assert.Throws<ArgumentNullException>(() => ImportLicenseFileResult.Taken(LicenseFileKind.Offline, null!));
    }

    [Fact]
    public void A_status_from_a_file_prints_its_source_and_who_it_is_licensed_to()
    {
        Assert.Contains("Source = File, LicensedTo = LicensedTo { Name = Ada Lovelace, Email = ada@example.com }", FileLicensed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_file_error_has_the_references_word()
    {
        (LicenseFileError Error, string Word)[] words =
        [
            (LicenseFileError.Malformed, "malformed"), (LicenseFileError.NotALicenseFile, "not-a-license-file"),
            (LicenseFileError.BadSignature, "bad-signature"), (LicenseFileError.UnknownKey, "unknown-key"),
            (LicenseFileError.WrongProduct, "wrong-product"), (LicenseFileError.RevokedFile, "revoked-file"),
            (LicenseFileError.Upgrade, "upgrade"), (LicenseFileError.Outdated, "outdated"),
        ];
        Assert.Equal(Enum.GetValues<LicenseFileError>().Length, words.Length);
        foreach (var (error, word) in words) Assert.Equal(word, Names.Of(error));
    }

    [Fact]
    public void The_extension_and_media_type_are_the_contracts()
    {
        Assert.Equal(".kglicense", LicenseFileFormat.Extension);
        Assert.Equal("application/vnd.keygrant.license", LicenseFileFormat.MediaType);
    }

    [Fact]
    public void The_stored_file_is_licenseFile_in_the_state_file_read_and_written_by_that_name()
    {
        var token = new TestSigner().OfflineFile();
        var read = Assert.IsType<StoredState>(StoredState.FromJson(new JsonObject { ["key"] = Key, ["licenseFile"] = token }.ToJsonString()));
        Assert.Equal(token, read.LicenseFile);
        Assert.Null(read.AdditionalFields);
        AssertJson.Equal(new JsonObject { ["key"] = Key, ["licenseFile"] = token }, JsonNode.Parse(read.ToJson()));
        Assert.NotEqual(read, read with { LicenseFile = null });
    }
}
