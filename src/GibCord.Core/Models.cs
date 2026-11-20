using System.Text.Json;
using System.Text.Json.Serialization;

namespace GibCord.Core;

// Obiekty API GibCorda (opis: /opt/dc/API.md, sekcja 5). Nazwy pól jak w JSON (camelCase).
// Nieznane pola są pomijane, więc nowsze wersje serwera nie psują aplikacji.

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SafeElementConverter() },
    };

    /// <summary>Puste pole JsonElement (np. brak ankiety) zapisujemy jako null zamiast wyjątku.</summary>
    sealed class SafeElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => JsonElement.ParseValue(ref reader);
        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
        {
            if (value.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue(); else value.WriteTo(writer);
        }
    }

    public static T? To<T>(this JsonElement e) => e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? default : e.Deserialize<T>(Options);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static long? Long(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}

public sealed class User
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Avatar { get; set; }
    public string? Color { get; set; }
    public string Status { get; set; } = "offline";
    public string? CustomStatus { get; set; }
    public bool Online { get; set; }
    public long CreatedAt { get; set; }
    public string? Pronouns { get; set; }
    public string? Bio { get; set; }
    public string? Banner { get; set; }
    public string? BannerImage { get; set; }
    public List<string> Badges { get; set; } = [];
    public bool Staff { get; set; }
    public string? Birthday { get; set; }
    public bool BirthdayToday { get; set; }
    public Activity? Activity { get; set; }
    public JsonElement Game { get; set; }

    // tylko w „me”
    public bool IsAdmin { get; set; }
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    public bool Mfa { get; set; }
    public int MfaBackupLeft { get; set; }
    public string? PendingEmail { get; set; }
    public bool BirthdayShowAge { get; set; }
    public bool ShowActivity { get; set; }
    public bool ShowGames { get; set; }
    public bool SpotifyConnected { get; set; }
    public string? PasswordIssue { get; set; }
    public JsonElement Settings { get; set; }

    [JsonIgnore] public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName!;
    [JsonIgnore] public string? GameName => Game.Str("name");
}

public sealed class Activity
{
    public string? Type { get; set; }
    public string? Title { get; set; }
    public JsonElement Artists { get; set; }
    public string? Album { get; set; }
    public string? Image { get; set; }
    public string? Url { get; set; }
    public long DurationMs { get; set; }
    public long ProgressMs { get; set; }
    public bool IsPlaying { get; set; }
    public long At { get; set; }

    [JsonIgnore]
    public string ArtistText => Artists.ValueKind switch
    {
        JsonValueKind.Array => string.Join(", ", Artists.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : a.Str("name")).Where(s => !string.IsNullOrEmpty(s))),
        JsonValueKind.String => Artists.GetString() ?? "",
        _ => "",
    };
}

public sealed class Role
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public long Permissions { get; set; }
    public int Position { get; set; }
    public bool Hoist { get; set; }
    public bool Mentionable { get; set; }
    public string? Icon { get; set; }
    public bool SelfAssignable { get; set; }
    public string? Group { get; set; }
    public string? Description { get; set; }
}

public sealed class Member
{
    public long UserId { get; set; }
    public long JoinedAt { get; set; }
    public List<long> RoleIds { get; set; } = [];
    public long? TimeoutUntil { get; set; }
    public string? Nick { get; set; }
    public string? Avatar { get; set; }
    public int Level { get; set; }
}

public sealed class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Position { get; set; }
    public List<PermOverride> Overrides { get; set; } = [];
}

/// <summary>Nadpisanie uprawnień kanału / kategorii: type everyone (id 0) | role | member.</summary>
public sealed class PermOverride
{
    public string Type { get; set; } = "everyone";
    public long Id { get; set; }
    public long Allow { get; set; }
    public long Deny { get; set; }
}

public sealed class Channel
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public string Type { get; set; } = "text";
    public string Name { get; set; } = "";
    public string? Topic { get; set; }
    public int Position { get; set; }
    public long? LastMessageId { get; set; }
    public long? CategoryId { get; set; }
    public int Slowmode { get; set; }
    public bool Readonly { get; set; }
    public string? Kind { get; set; }
    public int Bitrate { get; set; }
    public int UserLimit { get; set; }
    public string? MaxStream { get; set; }
    public List<PermOverride> Overrides { get; set; } = [];

    [JsonIgnore] public bool IsVoice => Type == "voice";
    [JsonIgnore] public bool IsCardChannel => Kind is "announce" or "changelog";
}

public sealed class Emoji
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Animated { get; set; }
}

public sealed class MyPerms
{
    public long Server { get; set; }
    public Dictionary<long, long> Channels { get; set; } = [];
}

public sealed class Server
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public long OwnerId { get; set; }
    public bool Official { get; set; }
    public string? Description { get; set; }
    public long BasePermissions { get; set; }
    public List<Role> Roles { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public List<Channel> Channels { get; set; } = [];
    public List<Member> Members { get; set; } = [];
    public List<Emoji> Emojis { get; set; } = [];
    public List<Sticker> Stickers { get; set; } = [];
    public List<Sound> Sounds { get; set; } = [];
    public List<ServerEvent> Events { get; set; } = [];
    public MyPerms? MyPerms { get; set; }
    public long CreatedAt { get; set; }
    public bool Levels { get; set; }
    public bool LevelMessages { get; set; }
    public List<long> AutoRoleIds { get; set; } = [];
    public long? LevelChannelId { get; set; }
    public string? Banner { get; set; }
}

/// <summary>Wydarzenie serwera (v3): zaplanowane → trwa → zakończone / odwołane.</summary>
public sealed class ServerEvent
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public long? ChannelId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long StartAt { get; set; }
    public long? EndAt { get; set; }
    public string? Location { get; set; }
    public long CreatorId { get; set; }
    public List<long> Rsvp { get; set; } = [];
    public string Status { get; set; } = "scheduled";

    [JsonIgnore] public bool Active => Status == "active";
    [JsonIgnore] public bool Open => Status is "scheduled" or "active";
}

public sealed class Sound
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public string Name { get; set; } = "";
    public string? Emoji { get; set; }
    public string Url { get; set; } = "";
    public double Volume { get; set; } = 1;
}

/// <summary>Serwer z zaproszenia (GET /api/invites/:code) — do karty „Dołącz”.</summary>
public sealed class InviteInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? Banner { get; set; }
    public string? Description { get; set; }
    public long CreatedAt { get; set; }
    public int MemberCount { get; set; }
    public int OnlineCount { get; set; }
}

/// <summary>Wynik wyszukiwania wiadomości (GET /api/search).</summary>
public sealed class SearchResult
{
    public int Total { get; set; }
    public List<Message> Messages { get; set; } = [];
    public Dictionary<long, SearchChannel> Channels { get; set; } = [];
    public Dictionary<long, User> Users { get; set; } = [];
}

public sealed class SearchChannel
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? ServerId { get; set; }
    public string? Type { get; set; }
}

/// <summary>Zapytanie: tekst + filtry (od:@ktoś, ma:zdjęcie) + zakres (kanał / serwer / rozmowy prywatne / wszędzie).</summary>
public sealed record SearchQuery(string Text, long? AuthorId = null, string? Has = null, long? ChannelId = null, long? ServerId = null, bool Dms = false, int Offset = 0)
{
    public string ToQueryString()
    {
        var q = new List<string> { "limit=25", "offset=" + Offset };
        if (!string.IsNullOrWhiteSpace(Text)) q.Add("q=" + Uri.EscapeDataString(Text.Trim()));
        if (AuthorId is long a) q.Add("authorId=" + a);
        if (!string.IsNullOrEmpty(Has)) q.Add("has=" + Has);
        if (ChannelId is long c) q.Add("channelId=" + c);
        else if (ServerId is long s) q.Add("serverId=" + s);
        else if (Dms) q.Add("scope=dms");
        return string.Join("&", q);
    }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && AuthorId is null && Has is null;
}

public sealed record SoundPlay(long ChannelId, long UserId, string Url, double Volume, string Label);

/// <summary>GIF z wyszukiwarki (GIPHY przez serwer) albo z ulubionych.</summary>
public sealed class GifItem
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string Url { get; set; } = "";
    public string? Mp4 { get; set; }
    public string? Preview { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long CreatedAt { get; set; }

    /// <summary>Treść wiadomości z GIF-em: adres + rozmiar (#SZERxWYS), żeby czat od razu zostawił miejsce.</summary>
    [JsonIgnore] public string MessageText => Url + (Width > 0 && Height > 0 ? $"#{Width}x{Height}" : "");
}

public sealed class GifPage
{
    public bool Enabled { get; set; } = true;
    public string? Provider { get; set; }
    public string? Next { get; set; }
    public List<GifItem> Results { get; set; } = [];
}

public sealed class Dm
{
    public long Id { get; set; }
    public string Type { get; set; } = "dm";
    public List<long> UserIds { get; set; } = [];
    public long? LastMessageId { get; set; }
    public string? Name { get; set; }
    public string? Icon { get; set; }
    public long? OwnerId { get; set; }
    public long? HiddenAt { get; set; }

    [JsonIgnore] public bool IsGroup => Type == "group";
}

public sealed class ReplyRef
{
    public long Id { get; set; }
    public long AuthorId { get; set; }
    public string? Content { get; set; }
}

public sealed class Attachment
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Mime { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }

    [JsonIgnore] public bool IsImage => Mime is "image/png" or "image/jpeg" or "image/jpg" or "image/gif" or "image/webp" or "image/bmp" or "image/avif";
}

public sealed class Reaction
{
    public string Emoji { get; set; } = "";
    public List<long> UserIds { get; set; } = [];
}

public sealed class Sticker
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class Message
{
    public long Id { get; set; }
    public long ChannelId { get; set; }
    public long AuthorId { get; set; }
    public string Content { get; set; } = "";
    public long? ThreadId { get; set; }
    public JsonElement Thread { get; set; }
    public long CreatedAt { get; set; }
    public long? EditedAt { get; set; }
    public ReplyRef? ReplyTo { get; set; }
    public List<Attachment> Attachments { get; set; } = [];
    public List<Reaction> Reactions { get; set; } = [];
    public List<long> Mentions { get; set; } = [];
    public bool MentionEveryone { get; set; }
    public List<long> MentionRoles { get; set; } = [];
    public long? PinnedAt { get; set; }
    public JsonElement RefId { get; set; }
    public JsonElement Poll { get; set; }
    public JsonElement Game { get; set; }
    public Sticker? Sticker { get; set; }
    public JsonElement Card { get; set; }
    public string? Nonce { get; set; }
    public string Type { get; set; } = "default";

    // tylko po stronie aplikacji: wiadomość wysyłana / nieudana
    [JsonIgnore] public SendState Send { get; set; }

    [JsonIgnore] public bool IsSystem => Type != "default";
    /// <summary>Gdzie leży na liście: kanał / rozmowa albo wątek (klucz ujemny).</summary>
    [JsonIgnore] public long Key => ThreadId is long t ? -t : ChannelId;
    [JsonIgnore] public long? RefIdLong => RefId.ValueKind == JsonValueKind.Number && RefId.TryGetInt64(out var l) ? l : RefId.ValueKind == JsonValueKind.String && long.TryParse(RefId.GetString(), out var p) ? p : null;
    [JsonIgnore] public bool HasCard => Card.ValueKind == JsonValueKind.Object && Card.Str("title") is { Length: > 0 };
}

public enum SendState { Sent, Pending, Failed }

/// <summary>Wątek: boczna rozmowa przy jednej wiadomości kanału (serwer 5.10). Wiadomości leżą na kanale-rodzicu z threadId.</summary>
public sealed class ThreadInfo
{
    public long Id { get; set; }
    public long ChannelId { get; set; }
    public long ServerId { get; set; }
    public long MessageId { get; set; }
    public string Name { get; set; } = "Wątek";
    public long CreatorId { get; set; }
    public bool Locked { get; set; }
    public long CreatedAt { get; set; }
    public long LastAt { get; set; }
    public int Count { get; set; }
    public long? LastMessageId { get; set; }
    public List<long> LastAuthors { get; set; } = [];
    public bool Archived { get; set; }
    // tylko dla mnie (threadView); w thread_update ich nie ma — wtedy zostają stare
    public bool? Following { get; set; }
    public long? LastRead { get; set; }

    /// <summary>Klucz okna wiadomości wątku w <see cref="Store.Windows"/> (ujemny, żeby nie zderzył się z id kanału).</summary>
    [JsonIgnore] public long Key => -Id;
    [JsonIgnore] public bool Unread => Following == true && (LastMessageId ?? 0) > (LastRead ?? 0);
}

public sealed class VoiceMember
{
    public long UserId { get; set; }
    public bool Muted { get; set; }
    public bool Deafened { get; set; }
    public bool Streaming { get; set; }
    public bool Video { get; set; }
    public bool ServerMuted { get; set; }
    public bool ServerDeafened { get; set; }
    public bool Suppressed { get; set; }
}

public sealed class Friend
{
    public long UserId { get; set; }
    public string Status { get; set; } = "friends"; // friends | incoming | outgoing
    public long Since { get; set; }
}

public sealed class NotifySetting
{
    public string? Level { get; set; }       // all | mentions | none
    public long? MutedUntil { get; set; }
}

public sealed class NotifyState
{
    public Dictionary<long, NotifySetting> Servers { get; set; } = [];
    public Dictionary<long, NotifySetting> Channels { get; set; } = [];
    public Dictionary<long, NotifySetting> Dms { get; set; } = [];
}

public sealed class Announcement
{
    public string? Text { get; set; }
    public string? Level { get; set; }
    public long? By { get; set; }
    public long? At { get; set; }
}

public sealed class StateResponse
{
    public User Me { get; set; } = new();
    public List<Server> Servers { get; set; } = [];
    public Dictionary<long, User> Users { get; set; } = [];
    public List<Dm> Dms { get; set; } = [];
    public Dictionary<long, long> Reads { get; set; } = [];
    public Dictionary<long, List<VoiceMember>> Voice { get; set; } = [];
    public List<Friend> Friends { get; set; } = [];
    public NotifyState? Notify { get; set; }
    public List<long> Blocked { get; set; } = [];
    public Announcement? Announcement { get; set; }
}

public sealed class Limits
{
    public int MessageLength { get; set; } = 4000;
    public long UploadBytes { get; set; } = 25 * 1024 * 1024;
    public int AttachmentsPerMessage { get; set; } = 10;
}

public sealed class Info
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Release { get; set; }
    public int ApiVersion { get; set; }
    public string? Registration { get; set; }
    public Limits Limits { get; set; } = new();
    public Dictionary<string, JsonElement> Features { get; set; } = [];
    public string? GifProvider { get; set; }
    public List<BuiltinSound> BuiltinSounds { get; set; } = [];
    public int[]? PollHours { get; set; }

    public bool Has(string feature) => Features.TryGetValue(feature, out var v) && v.ValueKind == JsonValueKind.True;
}

public sealed class BuiltinSound
{
    public string Key { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class LoginResult
{
    public User? Me { get; set; }
    public string? Token { get; set; }
    public bool Mfa { get; set; }
    public string? Ticket { get; set; }
    public int? BackupCodesLeft { get; set; }
}

/// <summary>Uprawnienia (bity jak w /api/info → permissions).</summary>
public static class Perm
{
    public const long Admin = 1, ManageServer = 2, ManageChannels = 4, ManageRoles = 8, Kick = 16, Ban = 32, ManageMessages = 64,
        Timeout = 128, VoiceModerate = 256, CreateInvite = 512, MentionEveryone = 1024, Stream = 2048;
    public const long ManageNicknames = 4096, ManageEvents = 8192, ManageExpressions = 16384;
    public const long ViewChannel = 32768, SendMessages = 65536, AddReactions = 131072, AttachFiles = 262144,
        Connect = 524288, Speak = 1048576, UseSoundboard = 2097152, ViewAuditLog = 4194304;
    public const long All = 8388607;
}
