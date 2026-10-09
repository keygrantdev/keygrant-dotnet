namespace KeyGrant.Licensing.Internal;

/// <summary>A field of <see cref="StoredState"/>, as a patch names it.</summary>
internal enum StateField
{
    Key,
    ActivationId,
    Lease,
    TrialStart,
    TrialEndsAt,
    TrialLease,
    TrialAt,
    LastSeen,
    CheckedMajor,
    KeptMajor,
    BackoffSince,
    BackoffMs,
    Refused,
    RefusedMajor,
    RefusedFor,
    DeviceKind,
    VerdictAt,
    Rev,
    PurchaseToken,
    PurchaseTokenAt,
    ClaimAskedAt,
    TrialReportedAt,
}

/// <summary>
/// What a save changes: each field it names is SET to a value, or CLEARED (null); a field it does not name
/// is left as it is. Which fields a patch clears matters as much as what it sets.
/// </summary>
internal sealed class StatePatch
{
    private readonly List<KeyValuePair<StateField, object?>> entries = new();

    public StatePatch()
    {
    }

    private StatePatch(IEnumerable<KeyValuePair<StateField, object?>> entries) => this.entries.AddRange(entries);

    /// <summary>Sets (or, with null, clears) a field: for object and collection initialisers.</summary>
    public object? this[StateField field]
    {
        set => Set(field, value);
    }

    public IReadOnlyList<KeyValuePair<StateField, object?>> Entries => entries;

    public bool Has(StateField field) => entries.Exists(e => e.Key == field);

    public bool TryGet(StateField field, out object? value)
    {
        foreach (var entry in entries)
        {
            if (entry.Key != field) continue;
            value = entry.Value;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>The field's value when the patch sets it; null when it clears it or does not name it.</summary>
    public object? ValueOf(StateField field) => TryGet(field, out var value) ? value : null;

    public void Set(StateField field, object? value)
    {
        var index = entries.FindIndex(e => e.Key == field);
        var entry = new KeyValuePair<StateField, object?>(field, Normalise(field, value));
        if (index >= 0) entries[index] = entry;
        else entries.Add(entry);
    }

    public StatePatch Copy() => new(entries);

    /// <summary>This patch, then <paramref name="other"/> over it (<c>{ ...this, ...other }</c>).</summary>
    public StatePatch With(StatePatch other)
    {
        var copy = Copy();
        foreach (var (field, value) in other.entries) copy.Set(field, value);
        return copy;
    }

    /// <summary>This patch without <paramref name="fields"/>.</summary>
    public StatePatch Without(IEnumerable<StateField> fields)
    {
        var drop = new HashSet<StateField>(fields);
        return new StatePatch(entries.Where(e => !drop.Contains(e.Key)));
    }

    /// <summary>Only the <paramref name="fields"/> of this patch that it names.</summary>
    public StatePatch Only(IEnumerable<StateField> fields)
    {
        var keep = new HashSet<StateField>(fields);
        return new StatePatch(entries.Where(e => keep.Contains(e.Key)));
    }

    /// <summary><paramref name="state"/> with this patch applied (<c>{ ...state, ...this }</c>).</summary>
    public StoredState ApplyTo(StoredState state)
    {
        foreach (var (field, value) in entries) state = StateFields.Set(state, field, value);
        return state;
    }

    private static object? Normalise(StateField field, object? value)
    {
        if (value is null) return null;
        return StateFields.TypeOf(field) switch
        {
            var t when t == typeof(long) => value switch
            {
                long l => l,
                int i => (long)i,
                _ => throw new ArgumentException($"{field} takes a number", nameof(value)),
            },
            var t when t == typeof(int) => value switch
            {
                int i => i,
                _ => throw new ArgumentException($"{field} takes a whole number", nameof(value)),
            },
            var t when t.IsInstanceOfType(value) => value,
            _ => throw new ArgumentException($"{field} does not take a {value.GetType().Name}", nameof(value)),
        };
    }
}

/// <summary>The fields of <see cref="StoredState"/> by name, and the groups the merge rules use.</summary>
internal static class StateFields
{
    /// <summary>What the answer to an ask writes: what the server said, and the wait before asking again.</summary>
    public static readonly IReadOnlyList<StateField> Verdict =
    [
        StateField.Lease,
        StateField.Refused,
        StateField.RefusedMajor,
        StateField.RefusedFor,
        StateField.BackoffSince,
        StateField.BackoffMs,
        StateField.CheckedMajor,
        StateField.VerdictAt,
    ];

    /// <summary>A server trial answer, taken whole from the newest one: the end and the lease that says it.</summary>
    public static readonly IReadOnlyList<StateField> TrialAnswer = [StateField.TrialEndsAt, StateField.TrialLease, StateField.TrialAt];

    /// <summary>The trial evidence, which belongs to the device, not to any activation.</summary>
    public static readonly IReadOnlyList<StateField> Trial = [StateField.TrialStart, StateField.TrialEndsAt, StateField.TrialLease, StateField.TrialAt];

    /// <summary>The purchase token and its stamps, which a save about a token writes only while it is stored (<see cref="Basis.Purchase"/>).</summary>
    public static readonly IReadOnlyList<StateField> Purchase = [StateField.PurchaseToken, StateField.PurchaseTokenAt, StateField.ClaimAskedAt];

    public static Type TypeOf(StateField field) => field switch
    {
        StateField.Key or StateField.ActivationId or StateField.Lease or StateField.TrialLease or StateField.PurchaseToken => typeof(string),
        StateField.TrialStart or StateField.TrialEndsAt or StateField.TrialAt or StateField.LastSeen
            or StateField.BackoffSince or StateField.BackoffMs or StateField.VerdictAt or StateField.Rev
            or StateField.PurchaseTokenAt or StateField.ClaimAskedAt or StateField.TrialReportedAt => typeof(long),
        StateField.CheckedMajor or StateField.KeptMajor or StateField.RefusedMajor => typeof(int),
        StateField.Refused => typeof(Refusal),
        StateField.RefusedFor => typeof(IReadOnlyList<string>),
        StateField.DeviceKind => typeof(DeviceKind),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static readonly Dictionary<string, StateField> ByName = Enum.GetValues<StateField>().ToDictionary(JsonName, StringComparer.Ordinal);

    /// <summary>The field named <paramref name="name"/> in the state file, when it is one.</summary>
    public static bool TryByName(string name, out StateField field) => ByName.TryGetValue(name, out field);

    /// <summary>The field's name in the state file.</summary>
    public static string JsonName(StateField field) => field switch
    {
        StateField.Key => "key",
        StateField.ActivationId => "activationId",
        StateField.Lease => "lease",
        StateField.TrialStart => "trialStart",
        StateField.TrialEndsAt => "trialEndsAt",
        StateField.TrialLease => "trialLease",
        StateField.TrialAt => "trialAt",
        StateField.LastSeen => "lastSeen",
        StateField.CheckedMajor => "checkedMajor",
        StateField.KeptMajor => "keptMajor",
        StateField.BackoffSince => "backoffSince",
        StateField.BackoffMs => "backoffMs",
        StateField.Refused => "refused",
        StateField.RefusedMajor => "refusedMajor",
        StateField.RefusedFor => "refusedFor",
        StateField.DeviceKind => "deviceKind",
        StateField.VerdictAt => "verdictAt",
        StateField.Rev => "rev",
        StateField.PurchaseToken => "purchaseToken",
        StateField.PurchaseTokenAt => "purchaseTokenAt",
        StateField.ClaimAskedAt => "claimAskedAt",
        StateField.TrialReportedAt => "trialReportedAt",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public static object? Get(StoredState state, StateField field) => field switch
    {
        StateField.Key => state.Key,
        StateField.ActivationId => state.ActivationId,
        StateField.Lease => state.Lease,
        StateField.TrialStart => state.TrialStart,
        StateField.TrialEndsAt => state.TrialEndsAt,
        StateField.TrialLease => state.TrialLease,
        StateField.TrialAt => state.TrialAt,
        StateField.LastSeen => state.LastSeen,
        StateField.CheckedMajor => state.CheckedMajor,
        StateField.KeptMajor => state.KeptMajor,
        StateField.BackoffSince => state.BackoffSince,
        StateField.BackoffMs => state.BackoffMs,
        StateField.Refused => state.Refused,
        StateField.RefusedMajor => state.RefusedMajor,
        StateField.RefusedFor => state.RefusedFor,
        StateField.DeviceKind => state.DeviceKind,
        StateField.VerdictAt => state.VerdictAt,
        StateField.Rev => state.Rev,
        StateField.PurchaseToken => state.PurchaseToken,
        StateField.PurchaseTokenAt => state.PurchaseTokenAt,
        StateField.ClaimAskedAt => state.ClaimAskedAt,
        StateField.TrialReportedAt => state.TrialReportedAt,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>
    /// <paramref name="state"/> with <paramref name="field"/> set (or, with null, cleared): whatever the
    /// file held for the field that this SDK could not type goes with it (<see cref="StoredState.AdditionalFields"/>),
    /// as the reference's patch replaces the field whole.
    /// </summary>
    public static StoredState Set(StoredState state, StateField field, object? value) => Typed(Untyped(state, JsonName(field)), field, value);

    /// <summary>The state without what the file held under <paramref name="name"/> untyped, however deep.</summary>
    private static StoredState Untyped(StoredState state, string name)
    {
        if (state.AdditionalFields is { } extra && extra.ContainsKey(name))
        {
            var rest = extra.Where(e => e.Key != name).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            state = state with { AdditionalFields = rest.Count > 0 ? rest : null };
        }
        if (state.DeepFields is { } deep && deep.ContainsKey(name))
        {
            var rest = deep.Where(e => e.Key != name).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            state = state with { DeepFields = rest.Count > 0 ? rest : null };
        }
        return state;
    }

    private static StoredState Typed(StoredState state, StateField field, object? value) => field switch
    {
        StateField.Key => state with { Key = (string?)value },
        StateField.ActivationId => state with { ActivationId = (string?)value },
        StateField.Lease => state with { Lease = (string?)value },
        StateField.TrialStart => state with { TrialStart = (long?)value },
        StateField.TrialEndsAt => state with { TrialEndsAt = (long?)value },
        StateField.TrialLease => state with { TrialLease = (string?)value },
        StateField.TrialAt => state with { TrialAt = (long?)value },
        StateField.LastSeen => state with { LastSeen = (long?)value },
        StateField.CheckedMajor => state with { CheckedMajor = (int?)value },
        StateField.KeptMajor => state with { KeptMajor = (int?)value },
        StateField.BackoffSince => state with { BackoffSince = (long?)value },
        StateField.BackoffMs => state with { BackoffMs = (long?)value },
        StateField.Refused => state with { Refused = (Refusal?)value },
        StateField.RefusedMajor => state with { RefusedMajor = (int?)value },
        StateField.RefusedFor => state with { RefusedFor = (IReadOnlyList<string>?)value },
        StateField.DeviceKind => state with { DeviceKind = (DeviceKind?)value },
        StateField.VerdictAt => state with { VerdictAt = (long?)value },
        StateField.Rev => state with { Rev = (long?)value },
        StateField.PurchaseToken => state with { PurchaseToken = (string?)value },
        StateField.PurchaseTokenAt => state with { PurchaseTokenAt = (long?)value },
        StateField.ClaimAskedAt => state with { ClaimAskedAt = (long?)value },
        StateField.TrialReportedAt => state with { TrialReportedAt = (long?)value },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}
