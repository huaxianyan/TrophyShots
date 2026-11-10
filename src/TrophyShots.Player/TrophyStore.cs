using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrophyShots.Player;

/// <summary>
/// 奖杯检测结果的本机缓存。键含文件大小与修改时间，录像被换掉或重新录制会重新检测。
/// 放在 %LOCALAPPDATA%，不往录像目录里写（那里只允许 screenshots/ 与 .state.json）。
/// </summary>
public sealed class TrophyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>检测算法版本：逻辑变了就升号，旧的"未检出"自动失效重算。</summary>
    private const int DetectorVersion = 14;

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;

    public TrophyStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrophyShots",
            "trophies.json"))
    {
    }

    public TrophyStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public IReadOnlyList<TrophySegment>? TryGet(string videoPath)
    {
        string key = KeyFor(videoPath);
        return _entries.TryGetValue(key, out Entry? entry) && entry.Version == DetectorVersion
            ? entry.Segments.Select(s => new TrophySegment(s.Start, s.End, s.Text ?? "")).ToList()
            : null;
    }

    public void Save(string videoPath, IReadOnlyList<TrophySegment> segments)
    {
        _entries[KeyFor(videoPath)] = new Entry
        {
            Segments = segments.Select(s => new Segment { Start = s.StartSeconds, End = s.EndSeconds, Text = s.Text }).ToList(),
            Version = DetectorVersion,
            CheckedAt = DateTimeOffset.Now,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document { Files = _entries }, SerializerOptions));
        File.Move(temporary, _path, overwrite: true);
    }

    private static string KeyFor(string videoPath)
    {
        FileInfo info = new(videoPath);
        return $"{Path.GetFullPath(videoPath).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, Entry>(StringComparer.Ordinal);
            }

            Document? document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), SerializerOptions);
            return document?.Files is null
                ? new Dictionary<string, Entry>(StringComparer.Ordinal)
                : new Dictionary<string, Entry>(document.Files, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    private sealed class Document
    {
        public Dictionary<string, Entry>? Files { get; set; }
    }

    private sealed class Entry
    {
        public List<Segment> Segments { get; set; } = new();
        public int Version { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }

    private sealed class Segment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
    }
}
