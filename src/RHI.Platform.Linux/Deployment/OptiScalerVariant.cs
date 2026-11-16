using System.Text.Json;
using System.Text.Json.Serialization;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #271: an OptiScaler version (upstream's shared <c>OsVariants</c> value). One of the four built-in channels (<c>Stable</c>,
/// <c>Nightly</c>, <c>DlssNr</c>, <c>F5</c>), a recommended fork from the manifest (<c>Fork:&lt;id&gt;</c>) or one of the
/// user's custom builds (<c>Custom:&lt;slug&gt;</c>). The id is what every store writes (saved choices, the Default version,
/// install records, presets), so it never changes meaning; <see cref="TryParse"/> also reads the values older builds wrote.
/// The default value is <see cref="Stable"/>, as the enum it replaces.
/// </summary>
[JsonConverter(typeof(OptiScalerVariantJsonConverter))]
public readonly struct OptiScalerVariant : IEquatable<OptiScalerVariant>
{
    public const string ForkPrefix = "Fork:";
    public const string CustomPrefix = "Custom:";

    private readonly string? _id;

    private OptiScalerVariant(string id) => _id = id;

    public static readonly OptiScalerVariant Stable = new("Stable");
    public static readonly OptiScalerVariant Nightly = new("Nightly");
    public static readonly OptiScalerVariant DlssNr = new("DlssNr");
    /// <summary>janblade's F5 DLSSNR multi-pass fork with its bundled NR forwarder (#157, upstream 2.8.7).</summary>
    public static readonly OptiScalerVariant F5 = new("F5");

    /// <summary>The four built-in channels, in upstream's dropdown order.</summary>
    public static IReadOnlyList<OptiScalerVariant> BuiltIns { get; } = [Stable, Nightly, DlssNr, F5];

    /// <summary>The stored value: <c>Stable</c>, <c>Fork:venom</c>, <c>Custom:my-build</c>.</summary>
    public string Id => _id ?? "Stable";

    public bool IsBuiltIn => !IsFork && !IsCustom;
    public bool IsFork => Id.StartsWith(ForkPrefix, StringComparison.Ordinal);
    public bool IsCustom => Id.StartsWith(CustomPrefix, StringComparison.Ordinal);

    /// <summary>The manifest id of a fork (<c>venom</c>) or the slug of a custom build; null for a built-in.</summary>
    public string? Key => IsFork ? Id[ForkPrefix.Length..] : IsCustom ? Id[CustomPrefix.Length..] : null;

    public static OptiScalerVariant Fork(string id) => new(ForkPrefix + id);
    public static OptiScalerVariant Custom(string slug) => new(CustomPrefix + slug);

    /// <summary>
    /// The folder name this version's downloads are cached under (none for Stable, which has the cache root). Built-ins keep
    /// their old folders; forks use <c>fork-&lt;id&gt;</c> (the id is <c>[a-z0-9-]</c>, checked when the manifest is read).
    /// </summary>
    public string CacheFolder => IsFork ? "fork-" + Key : Id.ToLowerInvariant();

    /// <summary>The enum names (and numbers) earlier builds stored, mapped to today's ids (#271: the forks became manifest ids).</summary>
    private static readonly (string Name, OptiScalerVariant Value)[] Legacy =
    [
        ("Stable", Stable), ("Nightly", Nightly), ("DlssNr", DlssNr), ("F5", F5),
        ("Venom", Fork("venom")), ("NrPreSrMfg", Fork("nr-presr-mfg")), ("FeederNr", Fork("feeder-nr")), ("OpenNr", Fork("opennr")),
    ];

    /// <summary>
    /// Reads a stored value: today's ids, the old enum names in any case (<c>dlssnr</c>, <c>Venom</c>) and their numbers
    /// (<c>0</c>–<c>7</c>, as <c>JsonStringEnumConverter</c> also accepted). A fork id or custom slug must be a plain
    /// lower-case name (<c>[a-z0-9-]</c>, at most 64 characters), so a value can never name a folder outside RHI's own.
    /// </summary>
    public static bool TryParse(string? raw, out OptiScalerVariant variant)
    {
        variant = Stable;
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var (name, legacy) in Legacy)
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) { variant = legacy; return true; }
        if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            if (number < 0 || number >= Legacy.Length) return false;
            variant = Legacy[number].Value;
            return true;
        }
        foreach (var prefix in new[] { ForkPrefix, CustomPrefix })
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var key = value[prefix.Length..];
            if (!IsSafeKey(key)) return false;
            variant = prefix == ForkPrefix ? Fork(key) : Custom(key);
            return true;
        }
        return false;
    }

    /// <summary><see cref="TryParse"/>, or <paramref name="fallback"/> when the value is missing or not a version.</summary>
    public static OptiScalerVariant ParseOr(string? raw, OptiScalerVariant fallback) => TryParse(raw, out var parsed) ? parsed : fallback;

    /// <summary>A fork id or custom-build slug: <c>[a-z0-9]</c> first, then <c>[a-z0-9-]</c>, at most 64 characters.</summary>
    public static bool IsSafeKey(string? key)
        => key is { Length: > 0 and <= 64 } && char.IsAsciiLetterLower(key[0]) | char.IsAsciiDigit(key[0])
           && key.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-');

    public bool Equals(OptiScalerVariant other) => string.Equals(Id, other.Id, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is OptiScalerVariant other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
    public static bool operator ==(OptiScalerVariant left, OptiScalerVariant right) => left.Equals(right);
    public static bool operator !=(OptiScalerVariant left, OptiScalerVariant right) => !left.Equals(right);

    /// <summary>A stored value written as a cast (test data, <c>[InlineData("F5")]</c>); a value that is not a version throws.</summary>
    public static explicit operator OptiScalerVariant(string id)
        => TryParse(id, out var variant) ? variant : throw new FormatException("Not an OptiScaler version: " + id);

    /// <summary>The stored id (what <c>enum.ToString()</c> gave the built-ins, so records keep their meaning).</summary>
    public override string ToString() => Id;
}

/// <summary>Writes a version as its id; reads ids, old enum names and old enum numbers (<see cref="OptiScalerVariant.TryParse"/>).</summary>
public sealed class OptiScalerVariantJsonConverter : JsonConverter<OptiScalerVariant>
{
    public override OptiScalerVariant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt32(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            _ => null
        };
        return OptiScalerVariant.TryParse(raw, out var variant)
            ? variant
            : throw new JsonException("Not an OptiScaler version: " + raw);
    }

    public override void Write(Utf8JsonWriter writer, OptiScalerVariant value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Id);

    public override OptiScalerVariant ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => OptiScalerVariant.ParseOr(reader.GetString(), OptiScalerVariant.Stable);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, OptiScalerVariant value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Id);
}
