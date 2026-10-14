using System.Globalization;

namespace KeyGrant.Licensing;

/// <summary>
/// One thing a licence grants beyond the base product, as its lease signs it (a member of the lease's
/// <c>ent</c> claim): exactly one of a flag, a number or a text. <c>{"edition": "pro", "seats": 5,
/// "beta": true}</c> is three entitlements: a text, a number and a flag.
/// <para>
/// Read one through the accessor of the kind you expect, which is null for the other kinds:
/// <c>status.Entitlements.TryGetValue("edition", out var edition) &amp;&amp; edition.Text == "pro"</c>.
/// Two are equal when they are the same kind holding the same value. Numbers are doubles, as JSON and
/// JavaScript read them, so <c>5</c> and <c>5.0</c> are the same entitlement. Immutable.
/// </para>
/// </summary>
public sealed class Entitlement : IEquatable<Entitlement>
{
    /// <summary>A flag.</summary>
    /// <param name="flag">Its value.</param>
    public Entitlement(bool flag) => Flag = flag;

    /// <summary>A number.</summary>
    /// <param name="number">Its value.</param>
    public Entitlement(double number) => Number = number;

    /// <summary>A text.</summary>
    /// <param name="text">Its value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public Entitlement(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    /// <summary>The flag, when this entitlement is one (<c>"beta": true</c>); otherwise null.</summary>
    public bool? Flag { get; }

    /// <summary>The number, when this entitlement is one (<c>"seats": 5</c>); otherwise null.</summary>
    public double? Number { get; }

    /// <summary>The text, when this entitlement is one (<c>"edition": "pro"</c>); otherwise null.</summary>
    public string? Text { get; }

    /// <summary>Whether <paramref name="other"/> is the same kind holding the same value (texts compared ordinally).</summary>
    /// <param name="other">The entitlement to compare with.</param>
    /// <returns>True when they are equal.</returns>
    public bool Equals(Entitlement? other) =>
        other is not null && Flag == other.Flag && Nullable.Equals(Number, other.Number) && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Entitlement);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Flag, Number, Text);

    /// <summary>The value as text: a flag as <c>true</c> or <c>false</c>, a number in the invariant culture, a text as it is.</summary>
    /// <returns>The value as text.</returns>
    public override string ToString() =>
        Flag is { } flag ? (flag ? "true" : "false")
        : Number is { } number ? number.ToString("R", CultureInfo.InvariantCulture)
        : Text!;

    /// <summary>Whether two entitlements are equal (<see cref="Equals(Entitlement?)"/>).</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they are equal, or both null.</returns>
    public static bool operator ==(Entitlement? left, Entitlement? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Whether two entitlements differ.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they are not equal.</returns>
    public static bool operator !=(Entitlement? left, Entitlement? right) => !(left == right);
}
