using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrophyShots.Core;

/// <summary>
/// 断点续做的落点：只记"上次处理到哪个文件、停在哪个时间点"。
/// 已完成的判定仍以截图文件是否存在为准，所以状态文件丢了也能自愈。
/// </summary>
public sealed class ProcessingState
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>上次处理到的录像文件名（不含目录）。</summary>
    public string? LastFileName { get; set; }

    /// <summary>上次停留的时间点，单位秒。</summary>
    public double LastPositionSeconds { get; set; }

    /// <summary>读状态。文件缺失、解析失败、字段缺失都返回空状态，不抛异常。</summary>
    public static ProcessingState Load(string statePath)
    {
        try
        {
            if (!File.Exists(statePath))
            {
                return new ProcessingState();
            }

            string json = File.ReadAllText(statePath);
            return JsonSerializer.Deserialize<ProcessingState>(json, SerializerOptions) ?? new ProcessingState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ProcessingState();
        }
    }

    /// <summary>写状态：先写临时文件再替换，避免半截文件让下次启动读到坏状态。</summary>
    public void Save(string statePath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(statePath))!;
        Directory.CreateDirectory(directory);

        string temporary = statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(temporary, statePath, overwrite: true);
    }
}
