using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RHI.Platform.Linux.Launch.LauncherOptions;

/// <summary>
/// #318: a Heroic game's arguments, <c>launcherArgs</c> in <c>GamesConfig/&lt;appName&gt;.json</c> (Heroic's Game Arguments,
/// "appended to game launch command"). Checked against Heroic v2.22.4 (tag commit 5b0c5ac): <c>src/backend/game_config.ts</c>
/// keeps the game's settings under its app name, beside <c>version</c> and <c>explicit</c>, and writes the file with
/// <c>JSON.stringify(config, null, 2)</c>; <c>src/common/types.ts</c> <c>GameSettings.launcherArgs: string</c>; the GOG,
/// sideload and Legendary launches split it with <c>shlex</c> or hand it on as one line. RHI writes the file the way Heroic
/// does (two-space indent, no final line break, the other keys in their order) and changes nothing but that key.
/// </summary>
public static class HeroicGameArgs
{
    public const string Folder = "GamesConfig";
    public const string Key = "launcherArgs";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // JSON.stringify keeps non-ASCII and HTML characters as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The game's arguments (null: no <c>launcherArgs</c>). Throws <see cref="FormatException"/> for a file that is not the game's settings.</summary>
    public static string? Read(byte[] file, string appName)
    {
        var settings = Settings(Parse(file), appName);
        return settings[Key] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => throw new FormatException($"Heroic's {Key} for {appName} is not text.")
        };
    }

    /// <summary>The file's bytes with the arguments set to <paramref name="value"/> (null: the key is taken out).</summary>
    public static byte[] Write(byte[] file, string appName, string? value)
    {
        var root = Parse(file);
        var settings = Settings(root, appName);
        if (value is null)
        {
            if (!settings.ContainsKey(Key)) return file;
            settings.Remove(Key);
        }
        else settings[Key] = value;
        var json = root.ToJsonString(WriteOptions);
        return Encoding.UTF8.GetBytes(json);
    }

    private static JsonObject Parse(byte[] file)
    {
        try
        {
            return JsonNode.Parse(file) as JsonObject ?? throw new FormatException("The Heroic game settings file is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("The Heroic game settings file is not valid JSON: " + ex.Message, ex);
        }
    }

    private static JsonObject Settings(JsonObject root, string appName)
        => root[appName] as JsonObject ?? throw new FormatException($"The Heroic game settings file has no settings for {appName}.");
}
