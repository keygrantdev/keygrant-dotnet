using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests;

// The user-lock groups: what an activation sends for the address and code a person typed (or the error that
// refuses them first), and the error each word of a refused activation gives.
public partial class ConformanceTests
{
    public static TheoryData<int, string> TypedForCases => Cases("typedFor");
    public static TheoryData<int, string> UserLockErrorCases => Cases("userLockErrors");

    [Theory]
    [MemberData(nameof(TypedForCases))]
    public void Typed_addresses_and_codes_are_sent_as_the_reference_sends_them_or_refused(int index, string name)
    {
        var v = Item("typedFor", index);
        // An option that was not given is null in the file.
        var typed = Wire.TypedFor(Str(v["email"]), Str(v["code"]));
        var expect = new JsonObject();
        if (typed.Refused is { } refused)
        {
            expect["refused"] = Names.Of(refused);
        }
        else
        {
            var send = new JsonObject();
            if (typed.Email is not null) send["email"] = typed.Email;
            if (typed.Code is not null) send["code"] = typed.Code;
            expect["send"] = send;
        }
        AssertJson.Equal(v["expect"], expect, name);
    }

    [Theory]
    [MemberData(nameof(UserLockErrorCases))]
    public void The_words_of_a_user_lock_are_their_own_errors_and_any_other_is_network(int index, string name)
    {
        var v = Item("userLockErrors", index);
        // Only a string error word can match: null and 42 are not one.
        Assert.True(Str(v["expect"]) == Names.Of(Wire.MapServerError(Str(v["error"]))), name);
    }
}
