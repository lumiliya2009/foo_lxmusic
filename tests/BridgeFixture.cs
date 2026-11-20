// Deterministic bridge for isolated host UI regression checks only.
// Never include this executable in a component package.
using System;
using System.IO;
using System.Text;
using System.Threading;

static class BridgeFixture
{
    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    static string Encode(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    static string Decode(string value) { value = value.Replace('-', '+').Replace('_', '/'); return Encoding.UTF8.GetString(Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='))); }
    static void Emit(string record, string text) { Console.WriteLine(record + "\t" + Encode(text)); }
    static string Item(string kind, string id, string title)
    {
        string[] fields = { "1", kind, "qq", "tx", "测试音源", id, title, "测试歌手", "测试专辑", "20000", "", "flac,320k", "{}", "flac", kind == "track" ? "" : kind };
        var raw = new StringBuilder();
        foreach (var field in fields) raw.Append(Encoding.UTF8.GetByteCount(field)).Append(':').Append(field);
        return Encode(raw.ToString());
    }
    static string Field(string encoded, int index)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Decode(encoded));
        int pos = 0;
        for (int i = 0; ; ++i)
        {
            int size = 0;
            while (bytes[pos] != ':') size = size * 10 + bytes[pos++] - '0';
            ++pos;
            if (i == index) return Encoding.UTF8.GetString(bytes, pos, size);
            pos += size;
        }
    }
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string command = args[0];
        string id = command == "resolve" || command == "expand" ? Field(args[2], 5) : "";
        File.AppendAllText(Path.Combine(Root, "requests.log"), command + "\t" + id + Environment.NewLine);
        if (command == "source-options")
        {
            Console.WriteLine("SOURCE\t11111111111111111111111111111111\t" + Encode("测试音源一") + "\t1");
            Console.WriteLine("SITE\t11111111111111111111111111111111\tqq\t" + Encode("QQ音乐") + "\ttrack,album,artist,playlist");
            Console.WriteLine("SITE\t11111111111111111111111111111111\tqishui\t" + Encode("汽水音乐") + "\tunavailable");
            Console.WriteLine("SOURCE\t22222222222222222222222222222222\t" + Encode("测试音源二") + "\t0");
            Console.WriteLine("SITE\t22222222222222222222222222222222\tmigu\t" + Encode("咪咕音乐") + "\ttrack");
        }
        else if (command == "check-update") Emit("OK", "当前已是最新版本（测试）");
        else if (command == "search")
        {
            int page = int.Parse(args[5]);
            if (page <= 3) {
                Console.WriteLine("ITEM\t" + Item(args[3], page + "-first", "第" + page + "页结果一"));
                Console.WriteLine("ITEM\t" + Item(args[3], page + "-second", "第" + page + "页结果二"));
            }
            Console.WriteLine("PAGE\t" + page + "\t30\t3\t62\t" + (page < 3 ? "1" : "0"));
        }
        else if (command == "expand")
        {
            Console.WriteLine("ITEM\t" + Item("track", id + "-fail", "播放失败测试"));
            Console.WriteLine("ITEM\t" + Item("track", id + "-sentinel", "不应自动播放的下一首"));
        }
        else if (command == "resolve")
        {
            string modeFile = Path.Combine(Root, "mode.txt");
            string mode = File.Exists(modeFile) ? File.ReadAllText(modeFile).Trim() : "fail";
            if (mode == "slow") Thread.Sleep(10000);
            if (mode == "empty") return 0;
            if (mode == "ok" || mode == "decoder")
            {
                Emit("URL", Path.Combine(Root, mode == "ok" ? "fixture.wav" : "invalid.wav"));
                return 0;
            }
            Emit("ERROR", "测试音源暂时不可用（预期失败）");
            return 1;
        }
        return 0;
    }
}
