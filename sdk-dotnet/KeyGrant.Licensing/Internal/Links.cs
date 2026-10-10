using System.Globalization;
using System.Text;

namespace KeyGrant.Licensing.Internal;

/// <summary>
/// A payment link with a reference of this install's on it: <see cref="License.UpgradeUrlAsync"/>'s, which
/// names the activation an upgrade sale finds the key by.
/// </summary>
internal static class Links
{
    /// <summary>The query parameter a payment link names its checkout's buyer by.</summary>
    private const string Reference = "client_reference_id";

    /// <summary>
    /// A payment link read as an absolute URL; an <see cref="ArgumentException"/> for anything else (a
    /// path such as <c>/buy</c>, a bare host such as <c>buy.stripe.com/x</c>).
    /// </summary>
    public static Uri AbsoluteLink(string link)
    {
        // A bare path is a file URI to .NET on macOS and Linux, and no URL to the WHATWG parser.
        var parsed = Uri.TryCreate(link, UriKind.Absolute, out var uri);
        var implicitFile = parsed && uri!.IsFile && !link.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!parsed || implicitFile) throw new ArgumentException("not an absolute URL", nameof(link));
        return uri!;
    }

    /// <summary>
    /// <paramref name="link"/> with <paramref name="reference"/> as its one <c>client_reference_id</c>. The
    /// link is read as an absolute URL (<see cref="AbsoluteLink"/>), which writes its scheme, host and path
    /// in their normal form, and its query is then edited as text: every parameter named exactly
    /// <c>client_reference_id</c> (its text before the first <c>=</c>, or all of it) and every empty one is
    /// left out, the others are kept as written and in their order, and
    /// <c>client_reference_id=&lt;reference&gt;</c> (percent-encoded as a URI component) is added last. The
    /// fragment is kept.
    /// </summary>
    public static string ReferencedLink(string link, string reference)
    {
        var uri = AbsoluteLink(link);
        var query = uri.Query.Length > 0 ? uri.Query[1..] : "";
        var kept = query.Split('&').Where(part => part.Length > 0 && part.Split('=')[0] != Reference);
        var edited = string.Join("&", kept.Append($"{Reference}={EncodeUriComponent(reference)}"));
        return $"{uri.GetLeftPart(UriPartial.Path)}?{edited}{uri.Fragment}";
    }

    /// <summary>JavaScript's <c>encodeURIComponent</c>: every UTF-8 byte but the letters, digits and <c>-_.!~*'()</c> percent-encoded.</summary>
    private static string EncodeUriComponent(string text)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')') builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
