using System.Text.Json;
using GibCord.Core;

namespace GibCord.App;

/// <summary>Wpis w skrzynce: wzmianka, zapisana wiadomość albo przypomnienie (dane tylko na tym komputerze).</summary>
public sealed class InboxItem
{
    public long ChannelId { get; set; }
    public long MessageId { get; set; }
    public long? ThreadId { get; set; }
    public long AuthorId { get; set; }
    public string Text { get; set; } = "";
    public string Where { get; set; } = "";
    public long At { get; set; }      // kiedy napisano wiadomość
    public long Added { get; set; }   // kiedy trafiła do skrzynki
    public long Due { get; set; }     // przypomnienie: kiedy
    public bool Seen { get; set; }
}

public sealed class InboxData
{
    public List<InboxItem> Mentions { get; set; } = [];
    public List<InboxItem> Saved { get; set; } = [];
    public List<InboxItem> Reminders { get; set; } = [];
}

/// <summary>
/// Skrzynka: wzmianki (zbierane na żywo), zapisane wiadomości i przypomnienia „przypomnij mi o tej wiadomości”.
/// Plik %LocalAppData%\GibCord6-dane\skrzynka.json — osobny dla każdego konta.
/// </summary>
public static class Inbox
{
    static InboxData _d = new();
    static string? _path;
    public static event Action? Changed;

    public static IReadOnlyList<InboxItem> Mentions => _d.Mentions;
    public static IReadOnlyList<InboxItem> Saved => _d.Saved;
    public static IReadOnlyList<InboxItem> Reminders => _d.Reminders;
    public static int Unseen => _d.Mentions.Count(m => !m.Seen);

    /// <summary>Czyja skrzynka jest wczytana (0 = żadna).</summary>
    public static long For { get; private set; }

    public static void Unload() { For = 0; _path = null; _d = new(); Changed?.Invoke(); }

    public static void Load(long userId)
    {
        For = userId;
        _path = Path.Combine(App.DataDir, $"skrzynka-{userId}.json");
        _d = SafeFile.Read<InboxData>(_path) ?? new();
        Changed?.Invoke();
    }

    static void Save()
    {
        if (_path is null) return;
        SafeFile.Write(_path, JsonSerializer.Serialize(_d, Json.Options));
        Changed?.Invoke();
    }

    public static InboxItem From(Message m, Store s)
    {
        var where = s.DmById(m.ChannelId) is { } d ? (d.IsGroup ? s.DmTitle(d) : "@" + s.DmTitle(d))
            : "#" + (s.ChannelById(m.ChannelId)?.Name ?? "kanał") + (s.ServerOfChannel(m.ChannelId) is { } srv ? " · " + srv.Name : "") + (m.ThreadId is not null ? " · wątek" : "");
        return new InboxItem { ChannelId = m.ChannelId, MessageId = m.Id, ThreadId = m.ThreadId, AuthorId = m.AuthorId, Text = s.PlainText(m, 300), Where = where, At = m.CreatedAt, Added = Format.NowMs() };
    }

    public static void AddMention(Message m, Store s)
    {
        if (_d.Mentions.Any(x => x.MessageId == m.Id)) return;
        _d.Mentions.Insert(0, From(m, s));
        if (_d.Mentions.Count > 200) _d.Mentions.RemoveRange(200, _d.Mentions.Count - 200);
        Save();
    }

    public static void MarkSeen() { if (Unseen == 0) return; foreach (var m in _d.Mentions) m.Seen = true; Save(); }

    /// <summary>Wejście na kanał „gasi” jego wzmianki w skrzynce.</summary>
    public static void SeenChannel(long cid)
    {
        var any = false;
        foreach (var m in _d.Mentions.Where(x => !x.Seen && x.ChannelId == cid && x.ThreadId is null)) { m.Seen = true; any = true; }
        if (any) Save();
    }

    public static bool IsSaved(long messageId) => _d.Saved.Any(x => x.MessageId == messageId);

    public static void ToggleSaved(Message m, Store s)
    {
        if (_d.Saved.RemoveAll(x => x.MessageId == m.Id) == 0) _d.Saved.Insert(0, From(m, s));
        Save();
    }

    public static void Remind(Message m, Store s, long due)
    {
        var it = From(m, s);
        it.Due = due;
        _d.Reminders.Add(it);
        _d.Reminders.Sort((a, b) => a.Due.CompareTo(b.Due));
        Save();
    }

    public static void Remove(InboxItem it)
    {
        if (_d.Mentions.Remove(it) || _d.Saved.Remove(it) || _d.Reminders.Remove(it)) Save();
    }

    public static void ClearMentions() { _d.Mentions.Clear(); Save(); }

    /// <summary>Przypomnienia, których czas minął (zwracane raz — usuwane z listy).</summary>
    public static List<InboxItem> TakeDue()
    {
        var now = Format.NowMs();
        var due = _d.Reminders.Where(r => r.Due <= now).ToList();
        if (due.Count == 0) return due;
        foreach (var r in due) _d.Reminders.Remove(r);
        Save();
        return due;
    }
}
