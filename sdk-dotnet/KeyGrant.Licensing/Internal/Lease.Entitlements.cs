using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace KeyGrant.Licensing.Internal;

// The lease claims' entitlements (`ent`), read leniently.
internal static partial class ClaimsParser
{
    /// <summary>
    /// <c>ent</c> read leniently: an object's flag, number and text members, kept as they are, names
    /// included (<c>__proto__</c> is a name like any other); every other member (an object, a list,
    /// null) dropped; and none at all from anything but an object (absent, null, a list, a text, a
    /// number, a flag). Never a refusal: a lease that verifies is the server's word, and one malformed
    /// entitlement must not take away the licence it rides on, nor the entitlements beside it.
    /// <para>
    /// Read in one pass however deep a dropped member nests (<see cref="TopLevelJson"/>), the last of a
    /// repeated name kept, as JSON.parse keeps it. Numbers are doubles, correctly rounded (the nearest
    /// double, as JavaScript reads them, which
    /// <see cref="double.Parse(string, NumberStyles, IFormatProvider)"/> gives), one past a double's range
    /// an infinity of its sign. A name or a text holding an escaped lone surrogate, which System.Text.Json
    /// cannot read, is dropped as a malformed member is; the server never signs one. No cap on how many
    /// there are, or how long: the signature says the server stood behind them, and a later server may
    /// raise its own caps without locking out the apps built before.
    /// </para>
    /// <para>
    /// <paramref name="ent"/> is the claim as the payload's reader holds it (<see cref="TopLevelJson"/>),
    /// null when it is absent; the conformance vectors' <c>entitlementNumbers</c> are read through here.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, Entitlement> EntitlementsOf(RawJson? ent)
    {
        if (ent is not { } raw) return ReadOnlyDictionary<string, Entitlement>.Empty;
        var top = TopLevelJson.Read(raw.Text);
        if (top.Kind != JsonValueKind.Object) return ReadOnlyDictionary<string, Entitlement>.Empty;
        var read = new Dictionary<string, Entitlement>(StringComparer.Ordinal);
        foreach (var (name, value) in top.Members)
        {
            // A list or an object is dropped unread.
            if (value.Depth == 0 && EntitlementOf(value.Element()) is { } entitlement) read[name] = entitlement;
        }
        return new ReadOnlyDictionary<string, Entitlement>(read);
    }

    /// <summary>A flag, a number or a text; null for anything else (null, and a text .NET cannot hold).</summary>
    private static Entitlement? EntitlementOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => new Entitlement(true),
        JsonValueKind.False => new Entitlement(false),
        JsonValueKind.Number => new Entitlement(double.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)),
        JsonValueKind.String => Json.StringOf(element) is { } text ? new Entitlement(text) : null,
        _ => null,
    };
}
