using System.Text;

namespace GateACheck;

/// <summary>MP4 の自己完結的な検査結果（外部ツール不要で Gate A の自動判定に使う）。</summary>
public sealed class Mp4Info
{
    public double DurationSeconds;
    public int VideoTrackCount;
    public int AudioTrackCount;
    public long MoovOffset = -1;
    public long MdatOffset = -1;

    /// <summary>faststart（moov が mdat より前）= シークに強い配置。</summary>
    public bool IsFastStart =>
        MoovOffset < 0 || MdatOffset < 0 ? MoovOffset >= 0 : MoovOffset < MdatOffset;

    public bool HasVideo => VideoTrackCount > 0;
    public bool HasAudio => AudioTrackCount > 0;
}

/// <summary>
/// MP4 ボックス構造を直接読む軽量インスペクタ（ffprobe 等の外部依存なし）。
/// moov/mvhd（時間）と trak/hdlr（トラック種別）だけを辿る。
/// </summary>
public static class Mp4Inspector
{
    public static Mp4Info Inspect(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var info = new Mp4Info();
        WalkRange(br, start: 0, end: fs.Length, info, depth: 0);
        return info;
    }

    private static void WalkRange(BinaryReader br, long start, long end, Mp4Info info, int depth)
    {
        long pos = start;
        while (pos + 8 <= end)
        {
            br.BaseStream.Position = pos;
            long size = ReadUInt32BE(br);
            var type = Encoding.ASCII.GetString(br.ReadBytes(4));
            var headerSize = 8L;
            if (size == 1)
            {
                size = ReadInt64BE(br);
                headerSize = 16;
            }
            if (size < headerSize || pos + size > end)
            {
                return; // 破損または解析範囲外
            }

            switch (type)
            {
                case "moov":
                    info.MoovOffset = pos;
                    WalkRange(br, pos + headerSize, pos + size, info, depth + 1);
                    break;
                case "mdat":
                    info.MdatOffset = pos;
                    break;
                case "trak":
                case "mdia":
                    if (depth < 4) WalkRange(br, pos + headerSize, pos + size, info, depth + 1);
                    break;
                case "mvhd":
                    ParseMvhd(br, pos + headerSize, info);
                    break;
                case "hdlr":
                    ParseHdlr(br, pos + headerSize, info);
                    break;
            }
            pos += size;
        }
    }

    private static void ParseMvhd(BinaryReader br, long payloadPos, Mp4Info info)
    {
        br.BaseStream.Position = payloadPos;
        var version = br.ReadByte();
        br.ReadBytes(3); // flags
        long timescale;
        long duration;
        if (version == 1)
        {
            br.ReadBytes(16); // creation_time(8) + modification_time(8)
            timescale = ReadUInt32BE(br);
            duration = ReadInt64BE(br);
        }
        else
        {
            br.ReadBytes(8); // creation_time(4) + modification_time(4)
            timescale = ReadUInt32BE(br);
            duration = ReadUInt32BE(br);
        }
        if (timescale > 0)
        {
            info.DurationSeconds = (double)duration / timescale;
        }
    }

    private static void ParseHdlr(BinaryReader br, long payloadPos, Mp4Info info)
    {
        // payload: version(1) + flags(3) + pre_defined(4) + handler_type(4)
        br.BaseStream.Position = payloadPos + 8;
        var handler = Encoding.ASCII.GetString(br.ReadBytes(4));
        switch (handler)
        {
            case "vide":
                info.VideoTrackCount++;
                break;
            case "soun":
                info.AudioTrackCount++;
                break;
        }
    }

    private static uint ReadUInt32BE(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static long ReadInt64BE(BinaryReader br)
    {
        var b = br.ReadBytes(8);
        long v = 0;
        foreach (var t in b)
        {
            v = (v << 8) | t;
        }
        return v;
    }
}
