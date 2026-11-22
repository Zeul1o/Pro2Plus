using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Pro2Plus;

/// <summary>
/// 辅助模块重写的离线差分自检（PRO2_AUX_TEST=1）：
/// 音频端点策略、Steam 日志扫描、输出桥线协议各用旧实现语义做期望值，
/// 对固定边界用例与随机 fuzz 逐例比对。不接触真实设备、进程或网络。
/// </summary>
public static class AuxiliaryRewriteSmoke
{
    private static readonly Regex LegacyIfHidBlock = new(
        @"\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})[^\]]*\]\s+Local Device Found\s*\r?\n" +
        @"\s*type:\s*(?<vid>[0-9a-fA-F]{4})\s+(?<pid>[0-9a-fA-F]{4})" +
        @"(?:(?:\r?\n)[^\r\n]*){0,7}?Product:\s*If_Hid\s*(?:\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Run()
    {
        var log = new StringBuilder();
        log.AppendLine("auxiliary rewrite smoke");
        var random = new Random(20261121);
        int audioCases = 0, audioBad = 0;
        int steamCases = 0, steamBad = 0;
        int bridgeCases = 0, bridgeBad = 0;
        int rearCases = 0, rearBad = 0;
        int editorCases = 0, editorBad = 0;
        int sessionCases = 0, sessionBad = 0;
        int viewModelCases = 0, viewModelBad = 0;
        int formattingCases = 0, formattingBad = 0;
        int policyCases = 0, policyBad = 0;
        int presentationCases = 0, presentationBad = 0;
        int routeCases = 0, routeBad = 0;
        int calibrationCases = 0, calibrationBad = 0;
        int topoCases = 0, topoBad = 0;
        int smallCases = 0, smallBad = 0;
        int bleDiagCases = 0, bleDiagBad = 0;
        int recordCases = 0, recordBad = 0;
        int restartCases = 0, restartBad = 0;
        int rateCases = 0, rateBad = 0;
        int gamepadCases = 0, gamepadBad = 0;
        int researchCases = 0, researchBad = 0;
        int abiCases = 0, abiBad = 0;
        int lowRateCases = 0, lowRateBad = 0;
        int envDiagCases = 0, envDiagBad = 0;
        int targetCases = 0, targetBad = 0;
        int layoutCases = 0, layoutBad = 0;
        int timingCases = 0, timingBad = 0;
        int adaptiveCases = 0, adaptiveBad = 0;
        string first = "";
        var mismatches = new List<string>();

        void Check(string section, string name, bool ok, ref int cases, ref int bad)
        {
            cases++;
            if (ok)
            {
                return;
            }

            bad++;
            if (first.Length == 0)
            {
                first = section + " / " + name;
            }

            if (mismatches.Count < 10) mismatches.Add(section + " / " + name);
        }

        Audio(random, Check, ref audioCases, ref audioBad);
        Steam(random, Check, ref steamCases, ref steamBad);
        Bridge(random, Check, ref bridgeCases, ref bridgeBad);
        RearKey(random, Check, ref rearCases, ref rearBad);
        RearEdit(random, Check, ref editorCases, ref editorBad);
        Session(random, Check, ref sessionCases, ref sessionBad);
        ViewModel(random, Check, ref viewModelCases, ref viewModelBad);
        Formatting(random, Check, ref formattingCases, ref formattingBad);
        Policies(random, Check, ref policyCases, ref policyBad);
        Presentation(random, Check, ref presentationCases, ref presentationBad);
        RoutePolicies(random, Check, ref routeCases, ref routeBad);
        Calibration(random, Check, ref calibrationCases, ref calibrationBad);
        Topology(random, Check, ref topoCases, ref topoBad);
        SmallModules(random, Check, ref smallCases, ref smallBad);
        BleDiagnostics(random, Check, ref bleDiagCases, ref bleDiagBad);
        Recording(Check, ref recordCases, ref recordBad);
        AdapterRestart(Check, ref restartCases, ref restartBad);
        RateMonitor(random, Check, ref rateCases, ref rateBad);
        GamepadModel(Check, ref gamepadCases, ref gamepadBad);
        ResearchCapture(Check, ref researchCases, ref researchBad);
        Win11Abi(Check, ref abiCases, ref abiBad);
        LowRate(Check, ref lowRateCases, ref lowRateBad);
        SmallDiagnostics(Check, ref envDiagCases, ref envDiagBad, ref targetCases, ref targetBad,
            ref layoutCases, ref layoutBad);
        Timing(Check, ref timingCases, ref timingBad);
        AdaptivePolicy(Check, ref adaptiveCases, ref adaptiveBad);

        int total = audioCases + steamCases + bridgeCases + rearCases + editorCases + sessionCases + viewModelCases + formattingCases + policyCases + presentationCases + routeCases + calibrationCases + topoCases + smallCases + bleDiagCases + recordCases + restartCases + rateCases +
            gamepadCases + researchCases + abiCases + lowRateCases +
            envDiagCases + targetCases + layoutCases + timingCases + adaptiveCases;
        int bad = audioBad + steamBad + bridgeBad + rearBad + editorBad + sessionBad + viewModelBad + formattingBad + policyBad + presentationBad + routeBad + calibrationBad + topoBad + smallBad + bleDiagBad + recordBad + restartBad + rateBad +
            gamepadBad + researchBad + abiBad + lowRateBad + envDiagBad + targetBad + layoutBad + timingBad + adaptiveBad;
        log.AppendLine("audio cases=" + audioCases + " bad=" + audioBad);
        log.AppendLine("steam cases=" + steamCases + " bad=" + steamBad);
        log.AppendLine("bridge cases=" + bridgeCases + " bad=" + bridgeBad);
        log.AppendLine("rearkey cases=" + rearCases + " bad=" + rearBad);
        log.AppendLine("rearedit cases=" + editorCases + " bad=" + editorBad);
        log.AppendLine("session cases=" + sessionCases + " bad=" + sessionBad);
        log.AppendLine("viewmodel cases=" + viewModelCases + " bad=" + viewModelBad);
        log.AppendLine("formatting cases=" + formattingCases + " bad=" + formattingBad);
        log.AppendLine("policies cases=" + policyCases + " bad=" + policyBad);
        log.AppendLine("presentation cases=" + presentationCases + " bad=" + presentationBad);
        log.AppendLine("route cases=" + routeCases + " bad=" + routeBad);
        log.AppendLine("calibration cases=" + calibrationCases + " bad=" + calibrationBad);
        log.AppendLine("topology cases=" + topoCases + " bad=" + topoBad);
        log.AppendLine("small cases=" + smallCases + " bad=" + smallBad);
        log.AppendLine("blediag cases=" + bleDiagCases + " bad=" + bleDiagBad);
        log.AppendLine("recording cases=" + recordCases + " bad=" + recordBad);
        log.AppendLine("restart cases=" + restartCases + " bad=" + restartBad);
        log.AppendLine("rate cases=" + rateCases + " bad=" + rateBad);
        log.AppendLine("gamepad cases=" + gamepadCases + " bad=" + gamepadBad);
        log.AppendLine("research cases=" + researchCases + " bad=" + researchBad);
        log.AppendLine("abi cases=" + abiCases + " bad=" + abiBad);
        log.AppendLine("lowrate cases=" + lowRateCases + " bad=" + lowRateBad);
        log.AppendLine("envdiag cases=" + envDiagCases + " bad=" + envDiagBad);
        log.AppendLine("target cases=" + targetCases + " bad=" + targetBad);
        log.AppendLine("layout cases=" + layoutCases + " bad=" + layoutBad);
        log.AppendLine("timing cases=" + timingCases + " bad=" + timingBad);
        log.AppendLine("adaptive cases=" + adaptiveCases + " bad=" + adaptiveBad);
        log.AppendLine("total cases=" + total + " bad=" + bad);
        if (first.Length != 0)
        {
            log.AppendLine("  first mismatch " + first);
            for (int i = 1; i < mismatches.Count; i++) log.AppendLine("  mismatch " + mismatches[i]);
        }

        log.AppendLine("summary=" + (bad == 0 ? "PASS" : "FAIL"));
        return log.ToString();
    }

    private delegate void CheckFn(string section, string name, bool ok, ref int cases, ref int bad);

    // ---------------- 音频端点策略 ----------------

    private static bool LegacyController(string id, string name)
    {
        string joined = (id + " " + name).ToLowerInvariant();
        return joined.Contains("dualsense", StringComparison.Ordinal) ||
               joined.Contains("vid_054c", StringComparison.Ordinal) ||
               joined.Contains("pid_0ce6", StringComparison.Ordinal) ||
               joined.Contains("054c&0ce6", StringComparison.Ordinal) ||
               joined.Contains("054c", StringComparison.Ordinal) && joined.Contains("0ce6", StringComparison.Ordinal) ||
               name.Contains("DualSense Wireless Controller", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("DualSense", StringComparison.OrdinalIgnoreCase);
    }

    private static EndpointSnapshot? LegacyFallback(
        List<EndpointSnapshot> active, EndpointSnapshot?[] defaults)
    {
        foreach (EndpointRole role in new[] { EndpointRole.Multimedia, EndpointRole.Console, EndpointRole.Communications })
        {
            EndpointSnapshot? candidate = defaults[(int)role];
            if (candidate != null && !candidate.IsController)
            {
                return candidate;
            }
        }

        return active.FirstOrDefault(e => !e.IsController);
    }

    private static void Audio(Random random, CheckFn check, ref int cases, ref int bad)
    {
        string[] fragments =
        [
            "", "dualsense", "DUALSENSE", "vid_054c", "pid_0ce6", "054c&0ce6",
            "054c", "0ce6", "Wireless Controller", "DualSense Wireless Controller",
            "DualSense", "speakers", "USB\\VID_054C&PID_0CE6\\1234", "耳机", " "
        ];
        foreach (string id in fragments)
        {
            foreach (string name in fragments)
            {
                check("audio", "classify " + id + "|" + name,
                    AudioEndpointPolicy.IsControllerEndpoint(id, name) == LegacyController(id, name),
                    ref cases, ref bad);
            }
        }

        const string alphabet = "abcdefABCDEF0549_c&PID.-\\中文 ";
        for (int i = 0; i < 900; i++)
        {
            string a = RandomText(random, alphabet, random.Next(0, 40));
            string b = RandomText(random, alphabet, random.Next(0, 30));
            check("audio", "fuzz " + i,
                AudioEndpointPolicy.IsControllerEndpoint(a, b) == LegacyController(a, b),
                ref cases, ref bad);
        }
        for (int i = 0; i < 400; i++)
        {
            var active = new List<EndpointSnapshot>();
            int count = random.Next(0, 7);
            for (int j = 0; j < count; j++)
            {
                active.Add(new EndpointSnapshot("id" + j, "name" + j, AudioFlow.Render, random.Next(2) == 0));
            }

            var defaults = new EndpointSnapshot?[3];
            for (int r = 0; r < 3; r++)
            {
                defaults[r] = random.Next(3) == 0
                    ? null
                    : new EndpointSnapshot("d" + r, "default" + r, AudioFlow.Render, random.Next(2) == 0);
            }

            EndpointSnapshot? expected = LegacyFallback(active, defaults);
            EndpointSnapshot? actual = AudioEndpointPolicy.PickFallback(active, defaults);
            check("audio", "fallback " + i, SameSnapshot(expected, actual), ref cases, ref bad);
        }

        var sample = new EndpointSnapshot("id", "Speakers", AudioFlow.Render, true);
        var fallback = new EndpointSnapshot("fb", "Headset", AudioFlow.Render, false);
        check("audio", "text clean",
            AudioEndpointPolicy.DescribeClean() ==
            "[AUDIO_GUARD] checked defaults; DualSense is not the default playback/communication endpoint.", ref cases, ref bad);
        check("audio", "text noactive",
            AudioEndpointPolicy.DescribeNoActive(AudioFlow.Capture) == "[AUDIO_GUARD] Capture has no active endpoint.", ref cases, ref bad);
        check("audio", "text current",
            AudioEndpointPolicy.DescribeCurrent(AudioFlow.Render, EndpointRole.Multimedia, sample) ==
            "[AUDIO_GUARD] default Render/Multimedia=\"Speakers\" dualsense=" + sample.IsController, ref cases, ref bad);
        check("audio", "text nofallback",
            AudioEndpointPolicy.DescribeNoFallback(AudioFlow.Render, EndpointRole.Console) ==
            "[AUDIO_GUARD] Render/Console is DualSense, but no non-DualSense active fallback endpoint was found.", ref cases, ref bad);
        check("audio", "text moved",
            AudioEndpointPolicy.DescribeMoved(AudioFlow.Render, EndpointRole.Console, fallback) ==
            "[AUDIO_GUARD] moved default Render/Console from DualSense to \"Headset\".", ref cases, ref bad);
        check("audio", "text failed",
            AudioEndpointPolicy.DescribeMoveFailure(AudioFlow.Capture, EndpointRole.Communications, fallback, -2147024891) ==
            "[AUDIO_GUARD] failed to move default Capture/Communications to \"Headset\" hr=0x" +
            (-2147024891).ToString("x8") + ".", ref cases, ref bad);
        var error = new InvalidOperationException("boom");
        check("audio", "text warning",
            AudioEndpointPolicy.DescribeWarning(error) ==
            "[AUDIO_GUARD] warning: unable to inspect or adjust Windows audio defaults: InvalidOperationException: boom", ref cases, ref bad);
    }

    private static bool SameSnapshot(EndpointSnapshot? a, EndpointSnapshot? b) =>
        ReferenceEquals(a, b) || (a != null && b != null && a.Id == b.Id && a.Name == b.Name &&
                                  a.Flow == b.Flow && a.IsController == b.IsController);

    private static string RandomText(Random random, string alphabet, int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = alphabet[random.Next(alphabet.Length)];
        }

        return new string(chars);
    }

    // ---------------- Steam 日志扫描 ----------------

    private static IfHidSighting? LegacyLastSighting(string text)
    {
        Match? latest = LegacyIfHidBlock.Matches(text ?? "").Cast<Match>().LastOrDefault();
        if (latest == null ||
            !DateTime.TryParseExact(
                latest.Groups["time"].Value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime observedAt))
        {
            return null;
        }

        return new IfHidSighting(
            latest.Groups["vid"].Value.ToUpperInvariant(),
            latest.Groups["pid"].Value.ToUpperInvariant(),
            observedAt);
    }

    private static string LegacyHex(string? value)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        return normalized.PadLeft(4, '0').ToUpperInvariant();
    }

    private static void Steam(Random random, CheckFn check, ref int cases, ref int bad)
    {
        string[] fixedLogs =
        [
            "",
            "nothing here",
            "[2026-11-21 10:00:00] Local Device Found\n type: 054C 0CE6\nProduct: If_Hid\n",
            "[2026-11-21 10:00:00] Local Device Found\r\n type: 054c 0ce6\r\nfiller\r\nProduct: If_Hid\r\n",
            "[2026-11-21 10:00:00] Local Device Found\n type: 1234 5678\nProduct: If_Hid\n",
            "[9999-99-99 99:99:99] Local Device Found\n type: 054C 0CE6\nProduct: If_Hid\n",
            "[2026-11-21 10:00:00] Local Device Found\n type: 054C 0CE6\nProduct: Other\n"
        ];
        foreach (string text in fixedLogs)
        {
            check("steam", "fixed log",
                SameSighting(LegacyLastSighting(text), SteamControllerLogScanner.FindLast(text)),
                ref cases, ref bad);
        }

        for (int i = 0; i < 500; i++)
        {
            string text = RandomControllerLog(random);
            check("steam", "fuzz log " + i,
                SameSighting(LegacyLastSighting(text), SteamControllerLogScanner.FindLast(text)),
                ref cases, ref bad);
        }

        string[] ids = ["", "054c", "0x0CE6", " 12ab ", "ABCD", "0x", "12345", "中文", null!];
        foreach (string id in ids)
        {
            check("steam", "hex " + id,
                SteamControllerLogScanner.NormalizeHexId(id) == LegacyHex(id), ref cases, ref bad);
        }

        for (int i = 0; i < 250; i++)
        {
            string raw = RandomText(random, "0xXabcdefABCDEF0129 \r\n", random.Next(0, 20));
            check("steam", "hex fuzz " + i,
                SteamControllerLogScanner.NormalizeHexId(raw) == LegacyHex(raw), ref cases, ref bad);
            check("steam", "oneline " + i,
                SteamControllerLogScanner.OneLine(raw) ==
                (raw ?? "").Replace("\r", " ").Replace("\n", " "), ref cases, ref bad);
        }

        DateTime now = new(2026, 11, 21, 11, 0, 0, DateTimeKind.Local);
        for (int i = 0; i < 300; i++)
        {
            DateTime observed = now.AddSeconds(random.Next(-3600, 600));
            DateTime? started = random.Next(3) == 0 ? null : now.AddMinutes(-random.Next(0, 90));
            TimeSpan window = TimeSpan.FromMinutes(random.Next(0, 45));
            bool legacyRecent = now - observed <= window &&
                                observed <= now.AddMinutes(1) &&
                                (!started.HasValue || observed >= started.Value.AddSeconds(-2));
            check("steam", "recent " + i,
                SteamControllerLogScanner.IsRecent(now, observed, started, window) == legacyRecent,
                ref cases, ref bad);

            string a = RandomText(random, "054C0ce6ab", random.Next(0, 6));
            string b = RandomText(random, "054C0ce6ab", random.Next(0, 6));
            bool legacyDiffers = !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            check("steam", "identity " + i,
                SteamControllerLogScanner.IdentityDiffers(a, "1111", b, "1111") == legacyDiffers,
                ref cases, ref bad);
        }

        string legacyDetail =
            "if_hid=054C:0CE6 current=054C:0CE6 observed=" + now.ToString("yyyy-MM-dd HH:mm:ss") +
            " steam_started=" + now.ToString("yyyy-MM-dd HH:mm:ss") +
            " recent=" + true.ToString().ToLowerInvariant() +
            " identity_mismatch=" + false.ToString().ToLowerInvariant();
        check("steam", "detail",
            SteamControllerLogScanner.ComposeDetail("054C", "0CE6", "054C", "0CE6", now, now, true, false) ==
            legacyDetail, ref cases, ref bad);
    }

    private static bool SameSighting(IfHidSighting? a, IfHidSighting? b) =>
        a == null && b == null ||
        (a != null && b != null && a.Vid == b.Vid && a.Pid == b.Pid && a.ObservedAt == b.ObservedAt);

    private static string RandomControllerLog(Random random)
    {
        var text = new StringBuilder();
        string nl = random.Next(2) == 0 ? "\n" : "\r\n";
        int blocks = random.Next(0, 6);
        for (int i = 0; i < blocks; i++)
        {
            string stamp = random.Next(8) == 0
                ? "9999-99-99 99:99:99"
                : new DateTime(2026, 11, 21, 9, 0, 0).AddSeconds(random.Next(0, 7200))
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            text.Append('[').Append(stamp);
            if (random.Next(2) == 0)
            {
                text.Append(" info");
            }

            text.Append("] Local Device Found").Append(nl);
            text.Append(" type: ")
                .Append(random.Next(0x10000).ToString("x4"))
                .Append(' ')
                .Append(random.Next(0x10000).ToString("X4"))
                .Append(nl);
            int fillers = random.Next(0, 4);
            for (int j = 0; j < fillers; j++)
            {
                text.Append(" extra field ").Append(j).Append(nl);
            }

            text.Append(random.Next(4) == 0 ? "Product: Other" : "Product: If_Hid").Append(nl);
        }

        return text.ToString();
    }

    // ---------------- 输出桥线协议 ----------------

    private static string LegacyLine(string path, string? payload) =>
        string.IsNullOrWhiteSpace(payload) ? path + "\0" : path + " " + payload + "\0";

    private static string LegacyStreamPath(uint busId, string devId) =>
        "bus/" + busId + "/" + devId + "\0";

    private static string LegacyDecode(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).TrimEnd('\0', '\r', '\n', ' ');

    private static void LegacyThrowIfProblem(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("status", out JsonElement status) &&
                status.ValueKind == JsonValueKind.Number)
            {
                string title = root.TryGetProperty("title", out JsonElement titleElement)
                    ? titleElement.GetString() ?? "VIIPER API error"
                    : "VIIPER API error";
                string detail = root.TryGetProperty("detail", out JsonElement detailElement)
                    ? detailElement.GetString() ?? ""
                    : "";
                throw new InvalidOperationException(status.GetInt32() + " " + title + ": " + detail);
            }
        }
        catch (JsonException)
        {
        }
    }

    private static List<uint> LegacyBusIds(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var list = new List<uint>();
        if (doc.RootElement.TryGetProperty("buses", out JsonElement buses))
        {
            foreach (JsonElement bus in buses.EnumerateArray())
            {
                list.Add(bus.GetUInt32());
            }
        }

        return list;
    }

    private static List<BridgeDeviceInfo> LegacyDevices(string json, uint busId)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var list = new List<BridgeDeviceInfo>();
        if (doc.RootElement.TryGetProperty("devices", out JsonElement devices))
        {
            foreach (JsonElement item in devices.EnumerateArray())
            {
                list.Add(new BridgeDeviceInfo(
                    item.TryGetProperty("busId", out JsonElement itemBusId) ? itemBusId.GetUInt32() : busId,
                    item.TryGetProperty("devId", out JsonElement devId) ? devId.GetString() ?? "" : "",
                    item.TryGetProperty("vid", out JsonElement vid) ? vid.GetString() ?? "" : "",
                    item.TryGetProperty("pid", out JsonElement pid) ? pid.GetString() ?? "" : "",
                    item.TryGetProperty("type", out JsonElement type) ? type.GetString() ?? "" : ""));
            }
        }

        return list;
    }

    private static uint LegacyCreatedBus(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("busId").GetUInt32();
    }

    private static BridgeDeviceInfo LegacyAdded(string json, string type)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        return new BridgeDeviceInfo(
            root.GetProperty("busId").GetUInt32(),
            root.GetProperty("devId").GetString() ?? "",
            root.TryGetProperty("vid", out JsonElement vid) ? vid.GetString() ?? "" : "",
            root.TryGetProperty("pid", out JsonElement pid) ? pid.GetString() ?? "" : "",
            root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? type : type);
    }

    private static string Capture(Func<string> action)
    {
        try
        {
            return "ok:" + action();
        }
        catch (Exception ex)
        {
            return "throw:" + ex.GetType().Name + ":" + ex.Message;
        }
    }

    private static string DevicesText(IReadOnlyList<BridgeDeviceInfo> list) =>
        string.Join("|", list.Select(d => d.BusId + "," + d.DevId + "," + d.Vid + "," + d.Pid + "," + d.Type));

    private static void Bridge(Random random, CheckFn check, ref int cases, ref int bad)
    {
        string[] paths = ["ping", "bus/list", "bus/3/list", "bus/3/add", "bus/3/remove"];
        string?[] payloads = [null, "", " ", "0", "{\"type\":\"keyboard\"}", "dev-42"];
        foreach (string path in paths)
        {
            foreach (string? payload in payloads)
            {
                check("bridge", "line " + path + "|" + payload,
                    OutputBridgeWire.BuildRequestLine(path, payload) == LegacyLine(path, payload),
                    ref cases, ref bad);
            }
        }

        for (int i = 0; i < 200; i++)
        {
            uint bus = (uint)random.Next(0, 500);
            string dev = "dev-" + random.Next(0, 100);
            check("bridge", "stream " + i,
                OutputBridgeWire.BuildStreamPath(bus, dev) == LegacyStreamPath(bus, dev),
                ref cases, ref bad);

            var bytes = new List<byte>();
            string body = RandomText(random, "abcXYZ019 {}:\"中文", random.Next(0, 24));
            bytes.AddRange(Encoding.UTF8.GetBytes(body));
            int padding = random.Next(0, 8);
            for (int p = 0; p < padding; p++)
            {
                bytes.Add((byte)"\0\r\n "[random.Next(4)]);
            }

            byte[] raw = bytes.ToArray();
            check("bridge", "decode " + i,
                OutputBridgeWire.DecodeReply(raw) == LegacyDecode(raw), ref cases, ref bad);
        }

        string[] replies =
        [
            "", "ok", "{\"buses\":[1,2,3]}", "{\"status\":404,\"title\":\"Missing\",\"detail\":\"bus gone\"}",
            "{\"status\":500}", "not json", "{\"buses\":[]}", "{\"devices\":[]}",
            "{\"busId\":7}", "{\"busId\":7,\"devId\":\"d1\",\"vid\":\"054c\",\"pid\":\"0ce6\",\"type\":\"keyboard\"}"
        ];
        foreach (string reply in replies)
        {
            check("bridge", "error " + reply,
                Capture(() => { OutputBridgeWire.ThrowIfApiError(reply); return ""; }) ==
                Capture(() => { LegacyThrowIfProblem(reply); return ""; }), ref cases, ref bad);
        }

        for (int i = 0; i < 500; i++)
        {
            string busJson = RandomBusJson(random);
            check("bridge", "busids " + i,
                Capture(() => string.Join(",", OutputBridgeWire.ParseBusIds(busJson))) ==
                Capture(() => string.Join(",", LegacyBusIds(busJson))), ref cases, ref bad);

            uint bus = (uint)random.Next(0, 20);
            string devicesJson = RandomDevicesJson(random);
            check("bridge", "devices " + i,
                Capture(() => DevicesText(OutputBridgeWire.ParseDevices(devicesJson, bus))) ==
                Capture(() => DevicesText(LegacyDevices(devicesJson, bus))), ref cases, ref bad);

            string createdJson = random.Next(4) == 0 ? RandomDevicesJson(random) : "{\"busId\":" + random.Next(0, 50) + "}";
            check("bridge", "created " + i,
                Capture(() => OutputBridgeWire.ParseCreatedBusId(createdJson).ToString()) ==
                Capture(() => LegacyCreatedBus(createdJson).ToString()), ref cases, ref bad);

            string addedJson = RandomAddedJson(random);
            string type = "keyboard";
            check("bridge", "added " + i,
                Capture(() => OutputBridgeWire.ParseAddedDevice(addedJson, type).ToString()) ==
                Capture(() => LegacyAdded(addedJson, type).ToString()), ref cases, ref bad);
        }
    }

    private static string RandomBusJson(Random random)
    {
        int count = random.Next(0, 5);
        var parts = new List<string>();
        for (int i = 0; i < count; i++)
        {
            parts.Add(random.Next(0, 1000).ToString());
        }

        return random.Next(8) == 0
            ? "{\"other\":true}"
            : "{\"buses\":[" + string.Join(",", parts) + "]}";
    }

    private static string RandomDevicesJson(Random random)
    {
        int count = random.Next(0, 4);
        var items = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var fields = new List<string>();
            if (random.Next(2) == 0) fields.Add("\"busId\":" + random.Next(0, 30));
            if (random.Next(2) == 0) fields.Add("\"devId\":\"d" + i + "\"");
            if (random.Next(2) == 0) fields.Add("\"vid\":\"054c\"");
            if (random.Next(2) == 0) fields.Add("\"pid\":\"0ce6\"");
            if (random.Next(2) == 0) fields.Add("\"type\":\"keyboard\"");
            items.Add("{" + string.Join(",", fields) + "}");
        }

        return random.Next(8) == 0
            ? "{\"devices\":\"bad\"}"
            : "{\"devices\":[" + string.Join(",", items) + "]}";
    }

    private static string RandomAddedJson(Random random)
    {
        var fields = new List<string> { "\"busId\":" + random.Next(0, 30) };
        if (random.Next(4) != 0) fields.Add("\"devId\":\"dev" + random.Next(0, 9) + "\"");
        if (random.Next(2) == 0) fields.Add("\"vid\":\"054c\"");
        if (random.Next(2) == 0) fields.Add("\"pid\":\"0ce6\"");
        if (random.Next(2) == 0) fields.Add("\"type\":\"mouse\"");
        return "{" + string.Join(",", fields) + "}";
    }

    // ---------------- 背键配置与时序状态机 ----------------

    private static RearKeyPlan LegacyPlan(RearKeyBinding binding, double elapsed)
    {
        switch (binding.Action)
        {
            case RearKeyAction.Hold:
                return new RearKeyPlan(binding.Buttons, 0,
                    elapsed < binding.PressMs ? binding.TouchGesture : Ps5TouchGesture.None,
                    elapsed / binding.PressMs, false);
            case RearKeyAction.Tap:
                if (elapsed < binding.PressMs)
                    return new RearKeyPlan(binding.Buttons, 0, binding.TouchGesture, elapsed / binding.PressMs, false);
                break;
            case RearKeyAction.Repeat:
                int period = binding.PressMs + binding.GapMs;
                double cycle = Math.Floor(elapsed / period);
                if (binding.RepeatCount == 0 || cycle < binding.RepeatCount)
                    return elapsed % period < binding.PressMs
                        ? new RearKeyPlan(binding.Buttons, (long)cycle, binding.TouchGesture,
                            elapsed % period / binding.PressMs, false)
                        : RearKeyPlan.Neutral;
                break;
            case RearKeyAction.Sequence:
                if (elapsed < binding.PressMs)
                    return new RearKeyPlan(binding.Buttons, 0, binding.TouchGesture, elapsed / binding.PressMs, false);
                elapsed -= binding.PressMs;
                long stepIndex = 1;
                foreach (RearKeyStep step in binding.Steps)
                {
                    if (elapsed < step.DelayMs) return RearKeyPlan.Neutral;
                    elapsed -= step.DelayMs;
                    if (elapsed < step.PressMs)
                        return new RearKeyPlan(step.Buttons, stepIndex, step.TouchGesture, elapsed / step.PressMs, false);
                    elapsed -= step.PressMs;
                    stepIndex++;
                }
                break;
        }

        return RearKeyPlan.Done;
    }

    private static RearKeyStep LegacyNormalizeStep(RearKeyStep s) => s with
    {
        Buttons = RearKeyBinding.SafeButtons(s.Buttons),
        TouchGesture = Enum.IsDefined(s.TouchGesture) ? s.TouchGesture : Ps5TouchGesture.None,
        DelayMs = Math.Clamp(s.DelayMs, 0, 30000),
        PressMs = Math.Clamp(s.PressMs,
            Enum.IsDefined(s.TouchGesture) && s.TouchGesture != Ps5TouchGesture.None ? Ps5TouchGestures.MinDurationMs : 30, 30000)
    };

    private static RearKeyBinding LegacyNormalizeBinding(RearKeyBinding b) => b with
    {
        Action = Enum.IsDefined(b.Action) ? b.Action : RearKeyAction.Hold,
        Buttons = RearKeyBinding.SafeButtons(b.Buttons),
        TouchGesture = Enum.IsDefined(b.TouchGesture) ? b.TouchGesture : Ps5TouchGesture.None,
        DelayMs = Math.Clamp(b.DelayMs, 0, 30000),
        PressMs = Math.Clamp(b.PressMs,
            Enum.IsDefined(b.TouchGesture) && b.TouchGesture != Ps5TouchGesture.None ? Ps5TouchGestures.MinDurationMs : 30, 30000),
        GapMs = Math.Clamp(b.GapMs, 30, 30000),
        RepeatCount = Math.Clamp(b.RepeatCount, 0, 1000),
        Steps = (b.Steps ?? []).Where(s => s != null).Take(16).Select(LegacyNormalizeStep).ToArray()
    };

    private static GamepadButtons LegacySafeButtons(GamepadButtons buttons)
    {
        buttons &= RearKeyBinding.AllowedButtons;
        if ((buttons & (GamepadButtons.DPadUp | GamepadButtons.DPadDown)) == (GamepadButtons.DPadUp | GamepadButtons.DPadDown))
            buttons &= ~(GamepadButtons.DPadUp | GamepadButtons.DPadDown);
        if ((buttons & (GamepadButtons.DPadLeft | GamepadButtons.DPadRight)) == (GamepadButtons.DPadLeft | GamepadButtons.DPadRight))
            buttons &= ~(GamepadButtons.DPadLeft | GamepadButtons.DPadRight);
        return buttons;
    }

    private static bool LegacyOpposite(GamepadButtons buttons) =>
        (buttons & (GamepadButtons.DPadUp | GamepadButtons.DPadDown)) == (GamepadButtons.DPadUp | GamepadButtons.DPadDown) ||
        (buttons & (GamepadButtons.DPadLeft | GamepadButtons.DPadRight)) == (GamepadButtons.DPadLeft | GamepadButtons.DPadRight);

    private static GamepadState? LegacyMerge(GamepadState mapped, GamepadState? professional)
    {
        if (professional == null || (professional.Buttons == mapped.Buttons &&
            professional.L2 == mapped.L2 && professional.R2 == mapped.R2 && professional.Ps5Touch == mapped.Ps5Touch))
            return professional;
        GamepadState result = professional.Clone();
        result.Buttons = mapped.Buttons;
        result.L2 = mapped.L2;
        result.R2 = mapped.R2;
        result.Ps5Touch = mapped.Ps5Touch;
        return result;
    }

    private static bool SameStep(RearKeyStep a, RearKeyStep b) =>
        a.Buttons == b.Buttons && a.TouchGesture == b.TouchGesture && a.DelayMs == b.DelayMs && a.PressMs == b.PressMs;

    private static bool SameBinding(RearKeyBinding a, RearKeyBinding b)
    {
        if (a.Enabled != b.Enabled || a.Action != b.Action || a.Buttons != b.Buttons ||
            a.TouchGesture != b.TouchGesture || a.DelayMs != b.DelayMs || a.PressMs != b.PressMs ||
            a.GapMs != b.GapMs || a.RepeatCount != b.RepeatCount || a.Steps.Length != b.Steps.Length) return false;
        for (int i = 0; i < a.Steps.Length; i++)
            if (!SameStep(a.Steps[i], b.Steps[i])) return false;
        return true;
    }

    private static bool SamePlan(RearKeyPlan a, RearKeyPlan b) =>
        a.Buttons == b.Buttons && a.Pulse == b.Pulse && a.Gesture == b.Gesture &&
        a.Finished == b.Finished && Equals(a.Fraction, b.Fraction);

    private static GamepadButtons RandomButtons(Random random) =>
        (GamepadButtons)(ulong)random.NextInt64(0, 1L << 24);

    private static RearKeyStep RandomStep(Random random, bool raw) => raw
        ? new RearKeyStep
        {
            Buttons = RandomButtons(random),
            TouchGesture = (Ps5TouchGesture)random.Next(-1, 10),
            DelayMs = random.Next(-500, 40000),
            PressMs = random.Next(-10, 40000)
        }
        : new RearKeyStep
        {
            Buttons = RandomButtons(random),
            TouchGesture = (Ps5TouchGesture)random.Next(0, 8),
            DelayMs = random.Next(0, 900),
            PressMs = random.Next(30, 900)
        };

    private static RearKeyBinding RandomBinding(Random random, bool raw)
    {
        int steps = random.Next(0, raw ? 21 : 5);
        var array = new RearKeyStep[steps];
        for (int i = 0; i < steps; i++)
            array[i] = raw && random.Next(12) == 0 ? null! : RandomStep(random, raw);
        return new RearKeyBinding
        {
            Enabled = raw || random.Next(4) != 0,
            Action = raw && random.Next(8) == 0
                ? (RearKeyAction)99
                : (RearKeyAction)random.Next(0, 4),
            Buttons = RandomButtons(random),
            TouchGesture = raw && random.Next(8) == 0
                ? (Ps5TouchGesture)99
                : (Ps5TouchGesture)random.Next(0, 8),
            DelayMs = raw ? random.Next(-500, 40000) : random.Next(0, 600),
            PressMs = raw ? random.Next(-10, 40000) : random.Next(30, 900),
            GapMs = raw ? random.Next(-10, 40000) : random.Next(30, 600),
            RepeatCount = raw ? random.Next(-5, 1200) : random.Next(0, 6),
            Steps = array
        };
    }

    private static void RearKey(Random random, CheckFn check, ref int cases, ref int bad)
    {
        for (int i = 0; i < 1400; i++)
        {
            RearKeyBinding binding = RandomBinding(random, false).Normalize();
            double elapsed = random.Next(-400, 6000) + random.NextDouble();
            check("rearkey", "timing " + i,
                SamePlan(LegacyPlan(binding, elapsed), RearKeyTiming.Plan(binding, elapsed)),
                ref cases, ref bad);
        }

        for (int i = 0; i < 500; i++)
        {
            RearKeyBinding binding = RandomBinding(random, true);
            check("rearkey", "normalize binding " + i,
                SameBinding(LegacyNormalizeBinding(binding), binding.Normalize()), ref cases, ref bad);
            RearKeyStep step = RandomStep(random, true);
            check("rearkey", "normalize step " + i,
                SameStep(LegacyNormalizeStep(step), step.Normalize()), ref cases, ref bad);
        }

        for (int i = 0; i < 180; i++)
        {
            RearKeyBinding binding = RandomBinding(random, false).Normalize();
            bool ps5 = random.Next(2) == 0;
            var legacy = new LegacyChannel();
            var rewritten = new RearKeyChannel();
            long ticks = Stopwatch.Frequency;
            bool pressed = false;
            for (int frame = 0; frame < 70; frame++)
            {
                ticks += Stopwatch.Frequency / 60 + random.Next(0, (int)(Stopwatch.Frequency / 300));
                if (random.Next(6) == 0) pressed = !pressed;
                GamepadButtons expected = legacy.Get(binding, pressed, ticks, ps5);
                GamepadButtons actual = rewritten.Get(binding, pressed, ticks, ps5);
                check("rearkey", "channel " + i + "/" + frame,
                    expected == actual && legacy.Touch == rewritten.Touch && legacy.Active == rewritten.Active,
                    ref cases, ref bad);
            }
        }

        for (int i = 0; i < 400; i++)
        {
            GamepadButtons buttons = RandomButtons(random);
            check("rearkey", "safe " + i,
                RearKeyBinding.SafeButtons(buttons) == LegacySafeButtons(buttons), ref cases, ref bad);
            check("rearkey", "opposite " + i,
                RearKeyBinding.HasOppositeDirections(buttons) == LegacyOpposite(buttons), ref cases, ref bad);
        }

        for (int i = 0; i < 200; i++)
        {
            var a = new RearKeyProfile { Left = RandomBinding(random, false), Right = RandomBinding(random, false), C = RandomBinding(random, false) };
            RearKeyProfile b = random.Next(3) == 0 ? a : new RearKeyProfile
            { Left = RandomBinding(random, false), Right = a.Right, C = a.C };
            check("rearkey", "profiles " + i,
                RearKeyAutomation.ProfilesEquivalent(a, b) == LegacyProfilesEquivalent(a, b), ref cases, ref bad);
        }

        for (int i = 0; i < 200; i++)
        {
            var mapped = new GamepadState
            {
                Buttons = RandomButtons(random), L2 = (ushort)random.Next(0, 4096),
                R2 = (ushort)random.Next(0, 4096),
                Ps5Touch = new Ps5TouchPoint(random.Next(2) == 0, (ushort)random.Next(0, 1920), (ushort)random.Next(0, 1080))
            };
            GamepadState? professional = random.Next(6) == 0 ? null : new GamepadState
            {
                Buttons = random.Next(2) == 0 ? mapped.Buttons : RandomButtons(random),
                L2 = random.Next(2) == 0 ? mapped.L2 : (ushort)random.Next(0, 4096),
                R2 = random.Next(2) == 0 ? mapped.R2 : (ushort)random.Next(0, 4096),
                Ps5Touch = random.Next(2) == 0 ? mapped.Ps5Touch : new Ps5TouchPoint(true, 12, 34)
            };
            GamepadState? expected = LegacyMerge(mapped, professional);
            GamepadState? actual = RearKeyAutomation.MergeProfessionalControls(mapped, professional);
            check("rearkey", "merge " + i, SameMerged(expected, actual), ref cases, ref bad);
        }
    }

    private static bool SameMerged(GamepadState? a, GamepadState? b) =>
        a == null && b == null ||
        (a != null && b != null && a.Buttons == b.Buttons && a.L2 == b.L2 &&
         a.R2 == b.R2 && a.Ps5Touch == b.Ps5Touch);

    private static bool LegacyProfilesEquivalent(RearKeyProfile? a, RearKeyProfile? b) =>
        ReferenceEquals(a, b) || (a != null && b != null &&
            LegacyBindingEquivalent(a.Left, b.Left) &&
            LegacyBindingEquivalent(a.Right, b.Right) &&
            LegacyBindingEquivalent(a.C, b.C));

    private static bool LegacyBindingEquivalent(RearKeyBinding a, RearKeyBinding b) =>
        a.Enabled == b.Enabled && a.Action == b.Action && a.Buttons == b.Buttons &&
        a.TouchGesture == b.TouchGesture && a.DelayMs == b.DelayMs && a.PressMs == b.PressMs &&
        a.GapMs == b.GapMs && a.RepeatCount == b.RepeatCount && a.Steps.SequenceEqual(b.Steps);

    /// <summary>旧通道状态机的逐帧参考实现（差分结束后不参与运行时）。</summary>
    private sealed class LegacyChannel
    {
        private bool initialized, down, active, allowTouch;
        private long start, lastPulse = -1;
        private GamepadButtons lastOutput;
        private readonly Ps5TouchPlayback touch = new();
        public Ps5TouchFrame Touch { get; private set; }
        public bool Active => active || touch.HasPending;

        private GamepadButtons Emit(GamepadButtons buttons, long pulse = 0,
            Ps5TouchGesture gesture = Ps5TouchGesture.None, double fraction = 0, bool cancelTouch = false)
        {
            Touch = cancelTouch || !allowTouch ? touch.Cancel() : touch.Sample(gesture, pulse, fraction);
            if (pulse != lastPulse && (lastOutput & buttons) != 0)
            {
                lastOutput = GamepadButtons.None;
                return GamepadButtons.None;
            }
            lastPulse = pulse;
            lastOutput = buttons;
            return buttons;
        }

        public GamepadButtons Get(RearKeyBinding binding, bool pressed, long ticks, bool ps5)
        {
            allowTouch = ps5;
            if (!initialized) { initialized = true; down = pressed; return GamepadButtons.None; }
            if (!binding.Enabled) { active = false; down = pressed; return Emit(GamepadButtons.None, cancelTouch: true); }
            if (pressed && !down && !active && !touch.HasPending) { active = true; start = ticks; touch.Reset(); }
            down = pressed;
            if (!pressed && binding.Action is RearKeyAction.Hold or RearKeyAction.Repeat)
            { active = false; return Emit(GamepadButtons.None, cancelTouch: true); }
            if (!active) return Emit(GamepadButtons.None);
            double elapsed = (ticks - start) * 1000.0 / Stopwatch.Frequency - binding.DelayMs;
            if (elapsed < 0) return Emit(GamepadButtons.None);
            switch (binding.Action)
            {
                case RearKeyAction.Hold:
                    return Emit(binding.Buttons,
                        gesture: elapsed < binding.PressMs ? binding.TouchGesture : Ps5TouchGesture.None,
                        fraction: elapsed / binding.PressMs);
                case RearKeyAction.Tap:
                    if (elapsed < binding.PressMs)
                        return Emit(binding.Buttons, gesture: binding.TouchGesture, fraction: elapsed / binding.PressMs);
                    break;
                case RearKeyAction.Repeat:
                    int period = binding.PressMs + binding.GapMs;
                    double cycle = Math.Floor(elapsed / period);
                    if (binding.RepeatCount == 0 || cycle < binding.RepeatCount)
                        return elapsed % period < binding.PressMs
                            ? Emit(binding.Buttons, (long)cycle, binding.TouchGesture, elapsed % period / binding.PressMs)
                            : Emit(GamepadButtons.None);
                    break;
                case RearKeyAction.Sequence:
                    if (elapsed < binding.PressMs)
                        return Emit(binding.Buttons, gesture: binding.TouchGesture, fraction: elapsed / binding.PressMs);
                    elapsed -= binding.PressMs;
                    long stepIndex = 1;
                    foreach (RearKeyStep step in binding.Steps)
                    {
                        if (elapsed < step.DelayMs) return Emit(GamepadButtons.None);
                        elapsed -= step.DelayMs;
                        if (elapsed < step.PressMs)
                            return Emit(step.Buttons, stepIndex, step.TouchGesture, elapsed / step.PressMs);
                        elapsed -= step.PressMs;
                        stepIndex++;
                    }
                    break;
            }
            active = false;
            return Emit(GamepadButtons.None);
        }
    }

    // ---------------- 背键编辑表单：校验 / 文案 / 候选表 ----------------

    private static int LegacyActionIndex(string? action) =>
        RearKeyFormValidation.Actions.ToList().IndexOf(action!);

    private static bool LegacyNumber(string? text, int min, int max, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
        value >= min && value <= max;

    private static bool LegacyIsTimed(string? action) => action != RearKeyFormValidation.Actions[0];
    private static bool LegacyIsRepeat(string? action) => action == RearKeyFormValidation.Actions[2];
    private static bool LegacyIsSequence(string? action) => action == RearKeyFormValidation.Actions[3];
    private static bool LegacyIsDurationVisible(string? action, Ps5TouchGesture touch) =>
        LegacyIsTimed(action) || touch != Ps5TouchGesture.None;

    private static string LegacyBehaviorHint(string? action, Ps5TouchGesture touch)
    {
        if (touch != Ps5TouchGesture.None)
            return LegacyIsRepeat(action)
                ? "按住时重复手势，松开停止；同一时刻执行一个触控动作。"
                : action == RearKeyFormValidation.Actions[0]
                    ? "按住完成一次手势，提前松开会停止；游戏需支持触控板。"
                    : "每次按下完成一次手势；再次触发需松开。游戏需支持触控板。";
        return action switch
        {
            "按住连发" => "仅按住时连发；松开停止，0 表示不限次数。",
            "单次按键" => "触发一次；执行中重复按下不排队。",
            "顺序按键" => "触发一次顺序动作；执行中不叠加，可随时停止背键。",
            _ => "按住生效，松开停止。"
        };
    }

    private static string LegacySummary(IEnumerable<string> labels, Ps5TouchGesture touch) =>
        string.Join(" + ", labels.Concat(touch == Ps5TouchGesture.None
            ? []
            : new[] { Ps5TouchGestures.Choices.First(c => c.Gesture == touch).Label })) is { Length: > 0 } text
            ? text
            : "未分配按键";

    private static (GamepadButtons, string)[] LegacyChoices(bool ps5, bool nintendo) =>
    [
        (GamepadButtons.None, "无"),
        (GamepadButtons.South, ps5 ? "×" : nintendo ? "B" : "A"),
        (GamepadButtons.East, ps5 ? "○" : nintendo ? "A" : "B"),
        (GamepadButtons.West, ps5 ? "□" : nintendo ? "Y" : "X"),
        (GamepadButtons.North, ps5 ? "△" : nintendo ? "X" : "Y"),
        (GamepadButtons.L1, ps5 ? "L1" : nintendo ? "L" : "LB"),
        (GamepadButtons.R1, ps5 ? "R1" : nintendo ? "R" : "RB"),
        (GamepadButtons.L2, ps5 ? "L2" : nintendo ? "ZL" : "LT"),
        (GamepadButtons.R2, ps5 ? "R2" : nintendo ? "ZR" : "RT"),
        (GamepadButtons.LeftStick, ps5 ? "L3" : nintendo ? "L3" : "L3"),
        (GamepadButtons.RightStick, ps5 ? "R3" : nintendo ? "R3" : "R3"),
        (GamepadButtons.Back, ps5 ? "Create" : nintendo ? "−" : "View"),
        (GamepadButtons.Start, ps5 ? "Options" : nintendo ? "+" : "Menu"),
        (GamepadButtons.Home, ps5 ? "PS" : nintendo ? "Home" : "Xbox"),
        (GamepadButtons.DPadUp, "↑"), (GamepadButtons.DPadDown, "↓"),
        (GamepadButtons.DPadLeft, "←"), (GamepadButtons.DPadRight, "→"),
        ..(ps5 || nintendo
            ? new[] { (GamepadButtons.Capture, ps5 ? "触摸板点击" : "截图") }
            : Array.Empty<(GamepadButtons, string)>())
    ];

    private static (bool Ok, string Error, RearKeyBinding Binding) LegacyBuild(RearKeyForm f)
    {
        var binding = new RearKeyBinding();
        string error = "";
        int actionIndex = LegacyActionIndex(f.Action);
        if (f.Enabled && f.Keys == GamepadButtons.None && f.TouchAction == Ps5TouchGesture.None)
        { error = f.Title + "：请选择按键或触控板动作。"; return (false, error, binding); }
        if (f.Enabled && RearKeyBinding.HasOppositeDirections(f.Keys))
        { error = f.Title + "：相反方向不能同时选择。"; return (false, error, binding); }
        int minDuration = f.TouchAction != Ps5TouchGesture.None ? Ps5TouchGestures.MinDurationMs : 30;
        bool delayOk = LegacyNumber(f.Delay, 0, 30000, out int delay);
        bool durationOk = LegacyNumber(f.Duration, minDuration, 30000, out int duration);
        bool gapOk = LegacyNumber(f.Gap, 30, 30000, out int gap);
        bool countOk = LegacyNumber(f.Count, 0, 1000, out int count);
        if (f.Enabled && (!delayOk || (LegacyIsDurationVisible(f.Action, f.TouchAction) && !durationOk) ||
            (LegacyIsRepeat(f.Action) && (!gapOk || !countOk))))
        {
            error = f.Title + "：延迟0–30000ms，动作时长" + minDuration +
                    "–30000ms，间隔30–30000ms，次数0–1000。";
            return (false, error, binding);
        }
        if (!delayOk) delay = 0;
        if (!durationOk) duration = 80;
        if (!gapOk) gap = 80;
        if (!countOk) count = 0;
        var steps = new List<RearKeyStep>();
        foreach (var step in f.Enabled && LegacyIsSequence(f.Action) ? f.Steps : Enumerable.Empty<RearKeyStepForm>())
        {
            if ((step.Keys == GamepadButtons.None && step.Touch == Ps5TouchGesture.None) ||
                RearKeyBinding.HasOppositeDirections(step.Keys) ||
                !LegacyNumber(step.Delay, 0, 30000, out int stepDelay) ||
                !LegacyNumber(step.Duration, step.Touch != Ps5TouchGesture.None ? Ps5TouchGestures.MinDurationMs : 30, 30000, out int stepDuration))
            { error = f.Title + "：请检查后续按键、延迟和按下时长。"; return (false, error, binding); }
            steps.Add(new() { Buttons = step.Keys, TouchGesture = step.Touch, DelayMs = stepDelay, PressMs = stepDuration });
        }
        binding = new()
        {
            Enabled = f.Enabled, Action = (RearKeyAction)actionIndex, Buttons = f.Keys,
            TouchGesture = f.TouchAction, DelayMs = delay, PressMs = duration, GapMs = gap,
            RepeatCount = count, Steps = steps.ToArray()
        };
        return (true, "", binding);
    }

    private static RearKeyForm RandomForm(Random random)
    {
        string?[] numbers = ["0", "80", "180", "400", "30000", "30001", "-1", "abc", "", " 12 ", "99999999999999"];
        string?[] actions = [null!, "", "按住映射", "单次按键", "按住连发", "顺序按键", "未知动作"];
        var steps = new List<RearKeyStepForm>();
        int count = random.Next(0, 5);
        for (int i = 0; i < count; i++)
        {
            steps.Add(new RearKeyStepForm(
                RandomButtons(random),
                (Ps5TouchGesture)random.Next(0, 8),
                numbers[random.Next(numbers.Length)]!,
                numbers[random.Next(numbers.Length)]!));
        }
        return new RearKeyForm(
            "左背键 · GL", random.Next(4) != 0,
            actions[random.Next(actions.Length)]!,
            (Ps5TouchGesture)random.Next(0, 8),
            numbers[random.Next(numbers.Length)]!, numbers[random.Next(numbers.Length)]!,
            numbers[random.Next(numbers.Length)]!, numbers[random.Next(numbers.Length)]!,
            RandomButtons(random), steps);
    }

    private static bool SameBuild((bool Ok, string Error, RearKeyBinding Binding) a, RearKeyBuildResult b) =>
        a.Ok == b.Ok && a.Error == b.Error && SameBinding(a.Binding, b.Binding);

    private static void RearEdit(Random random, CheckFn check, ref int cases, ref int bad)
    {
        foreach (bool ps5 in new[] { false, true })
        {
            foreach (bool nintendo in new[] { false, true })
            {
                var expected = LegacyChoices(ps5, nintendo);
                IReadOnlyList<RearKeyChoice> actual = RearKeyChoice.Choices(ps5, nintendo);
                bool ok = expected.Length == actual.Count;
                for (int i = 0; ok && i < expected.Length; i++)
                    ok = expected[i].Item1 == actual[i].Key && expected[i].Item2 == actual[i].Label && !actual[i].Selected;
                check("rearedit", "choices " + ps5 + "/" + nintendo, ok, ref cases, ref bad);
            }
        }

        string?[] numbers = ["0", "80", "180", "-1", "30000", "30001", "abc", "", " 12 ", "99999999999999", null];
        foreach (string? text in numbers)
        {
            foreach (int min in new[] { 0, 30, 180 })
            {
                bool a = LegacyNumber(text, min, 30000, out int av);
                bool b = RearKeyFormValidation.Number(text, min, 30000, out int bv);
                check("rearedit", "number " + text + "/" + min, a == b && av == bv, ref cases, ref bad);
            }
        }

        string?[] actions = [null, "", "按住映射", "单次按键", "按住连发", "顺序按键", "未知动作"];
        foreach (string? action in actions)
        {
            foreach (Ps5TouchGesture touch in new[] { Ps5TouchGesture.None, Ps5TouchGesture.Touch, Ps5TouchGesture.SwipeUp })
            {
                check("rearedit", "action flags " + action + "/" + touch,
                    RearKeyFormValidation.ActionIndex(action) == LegacyActionIndex(action) &&
                    RearKeyFormValidation.IsTimed(action) == LegacyIsTimed(action) &&
                    RearKeyFormValidation.IsRepeat(action) == LegacyIsRepeat(action) &&
                    RearKeyFormValidation.IsSequence(action) == LegacyIsSequence(action) &&
                    RearKeyFormValidation.IsDurationVisible(action, touch) == LegacyIsDurationVisible(action, touch),
                    ref cases, ref bad);
                check("rearedit", "hint " + action + "/" + touch,
                    RearKeyFormValidation.BehaviorHint(action, touch) == LegacyBehaviorHint(action, touch),
                    ref cases, ref bad);
                check("rearedit", "label " + touch,
                    RearKeyFormValidation.DurationLabel(touch) ==
                    (touch != Ps5TouchGesture.None ? "手势时长（ms）" : "按下时长（ms）"), ref cases, ref bad);
                check("rearedit", "caption " + action,
                    RearKeyFormValidation.EnabledCaption(action != null) == (action != null ? "已启用" : "未启用"),
                    ref cases, ref bad);
                string[] labels = ["A", "↑", "LB"];
                check("rearedit", "summary " + action + "/" + touch,
                    RearKeyFormValidation.Summary(labels, touch) == LegacySummary(labels, touch), ref cases, ref bad);
            }
        }

        for (int i = 0; i < 1800; i++)
        {
            RearKeyForm form = RandomForm(random);
            check("rearedit", "build " + i,
                SameBuild(LegacyBuild(form), RearKeyFormValidation.Build(form)), ref cases, ref bad);
        }
    }

    // ---------------- 输出会话抽出的纯函数 ----------------

    private static string LegacyHead(ReadOnlySpan<byte> frame)
    {
        int count = Math.Min(8, frame.Length);
        return count == 0 ? "" : BitConverter.ToString(frame[..count].ToArray()).Replace("-", " ");
    }

    private static double LegacyTicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double LegacyPercentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        double position = (sorted.Length - 1) * Math.Clamp(percentile, 0, 100) / 100.0;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        double weight = position - lower;
        return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
    }

    private static void LegacyCountAge(double ageMs, ref ulong a, ref ulong b, ref ulong c, ref ulong d, ref ulong e)
    {
        switch (ageMs)
        {
            case < 20: a++; break;
            case < 33: b++; break;
            case < 50: c++; break;
            case < 100: d++; break;
            default: e++; break;
        }
    }

    private static bool LegacySkip(long nowTicks, long lastWriteTicks, double effectiveHz, double configuredHz)
    {
        if (lastWriteTicks == 0 || effectiveHz <= 0 || effectiveHz >= configuredHz - 0.1) return false;
        double minimumGapMs = 1000.0 / effectiveHz;
        double elapsedMs = LegacyTicksToMs(nowTicks - lastWriteTicks);
        return elapsedMs < minimumGapMs * 0.92;
    }

    private static string LegacyGyroLabel(ImuResampleMode mode) => mode switch
    {
        ImuResampleMode.Hold250Hz => "hold_latest",
        ImuResampleMode.Source60Hz => "source_60hz_zero",
        ImuResampleMode.Scaled250Hz => "scaled_250hz",
        ImuResampleMode.Filtered250Hz => "filtered_hold",
        _ => mode.ToString()
    };

    private static GamepadState LegacyWithoutMotion(GamepadState state)
    {
        GamepadState result = state.Clone();
        result.GyroValid = false;
        result.AccelValid = false;
        result.GyroX = 0; result.GyroY = 0; result.GyroZ = 0;
        result.AccelX = 0; result.AccelY = 0; result.AccelZ = 0;
        return result;
    }

    private static GamepadState LegacyScaleGyro(GamepadState state, double scale)
    {
        if (!state.GyroValid || Math.Abs(scale - 1.0) < 0.001) return state;
        GamepadState result = state.Clone();
        result.GyroX = LegacyScaleInt16(result.GyroX, scale);
        result.GyroY = LegacyScaleInt16(result.GyroY, scale);
        result.GyroZ = LegacyScaleInt16(result.GyroZ, scale);
        return result;
    }

    private static GamepadState LegacyFilterMotion(GamepadState? previous, GamepadState current)
    {
        if (previous == null) return current.Clone();
        const double alpha = 0.35;
        GamepadState filtered = current.Clone();
        if (current.GyroValid && previous.GyroValid)
        {
            filtered.GyroX = LegacyLerp(previous.GyroX, current.GyroX, alpha);
            filtered.GyroY = LegacyLerp(previous.GyroY, current.GyroY, alpha);
            filtered.GyroZ = LegacyLerp(previous.GyroZ, current.GyroZ, alpha);
        }
        if (current.AccelValid && previous.AccelValid)
        {
            filtered.AccelX = LegacyLerp(previous.AccelX, current.AccelX, alpha);
            filtered.AccelY = LegacyLerp(previous.AccelY, current.AccelY, alpha);
            filtered.AccelZ = LegacyLerp(previous.AccelZ, current.AccelZ, alpha);
        }
        return filtered;
    }

    private static GamepadState LegacyCopyMotion(GamepadState current, GamepadState? motion)
    {
        if (motion == null) return current;
        GamepadState result = current.Clone();
        result.GyroValid = motion.GyroValid;
        result.GyroX = motion.GyroX; result.GyroY = motion.GyroY; result.GyroZ = motion.GyroZ;
        result.AccelValid = motion.AccelValid;
        result.AccelX = motion.AccelX; result.AccelY = motion.AccelY; result.AccelZ = motion.AccelZ;
        result.MotionTimestampUs = motion.MotionTimestampUs;
        return result;
    }

    private static short LegacyScaleInt16(short value, double scale) =>
        LegacyClamp((int)Math.Round(value * scale));

    private static short LegacyLerp(short previous, short current, double alpha) =>
        LegacyClamp((int)Math.Round(previous + (current - previous) * alpha));

    private static short LegacyClamp(int value)
    {
        if (value < short.MinValue) return short.MinValue;
        if (value > short.MaxValue) return short.MaxValue;
        return (short)value;
    }

    private static GamepadState RandomMotionState(Random random) => new()
    {
        Buttons = RandomButtons(random),
        L2 = (ushort)random.Next(0, 4096),
        R2 = (ushort)random.Next(0, 4096),
        Ps5Touch = new Ps5TouchPoint(random.Next(2) == 0, (ushort)random.Next(0, 1920), (ushort)random.Next(0, 1080)),
        GyroValid = random.Next(2) == 0,
        AccelValid = random.Next(2) == 0,
        GyroX = (short)random.Next(short.MinValue, short.MaxValue + 1),
        GyroY = (short)random.Next(short.MinValue, short.MaxValue + 1),
        GyroZ = (short)random.Next(short.MinValue, short.MaxValue + 1),
        AccelX = (short)random.Next(short.MinValue, short.MaxValue + 1),
        AccelY = (short)random.Next(short.MinValue, short.MaxValue + 1),
        AccelZ = (short)random.Next(short.MinValue, short.MaxValue + 1),
        MotionTimestampUs = (uint)random.NextInt64(0, (long)uint.MaxValue + 1)
    };

    private static bool SameMotion(GamepadState a, GamepadState b) =>
        a.Buttons == b.Buttons && a.L2 == b.L2 && a.R2 == b.R2 && a.Ps5Touch == b.Ps5Touch &&
        a.GyroValid == b.GyroValid && a.AccelValid == b.AccelValid &&
        a.GyroX == b.GyroX && a.GyroY == b.GyroY && a.GyroZ == b.GyroZ &&
        a.AccelX == b.AccelX && a.AccelY == b.AccelY && a.AccelZ == b.AccelZ &&
        a.MotionTimestampUs == b.MotionTimestampUs;

    private static void Session(Random random, CheckFn check, ref int cases, ref int bad)
    {
        for (int i = 0; i < 500; i++)
        {
            var bytes = new byte[random.Next(0, 24)];
            random.NextBytes(bytes);
            check("session", "head " + i,
                OutputFrameDiagnostics.FormatHead(bytes) == LegacyHead(bytes), ref cases, ref bad);
            long ticks = random.NextInt64(-10_000_000, 10_000_000);
            check("session", "ticks " + i,
                Equals(OutputFrameDiagnostics.TicksToMilliseconds(ticks), LegacyTicksToMs(ticks)),
                ref cases, ref bad);
        }

        for (int i = 0; i < 600; i++)
        {
            var values = new double[random.Next(0, 12)];
            for (int j = 0; j < values.Length; j++)
                values[j] = (random.NextDouble() - 0.5) * random.Next(1, 10000);
            double percentile = random.Next(-20, 140) + random.NextDouble();
            check("session", "percentile " + i,
                Equals(OutputFrameDiagnostics.Percentile(values, percentile), LegacyPercentile(values, percentile)),
                ref cases, ref bad);

            double age = random.Next(6) == 0 ? double.NaN : (random.NextDouble() - 0.2) * 200;
            ulong[] a = new ulong[5], b = new ulong[5];
            LegacyCountAge(age, ref a[0], ref a[1], ref a[2], ref a[3], ref a[4]);
            OutputFrameDiagnostics.CountAgeBucket(age, ref b[0], ref b[1], ref b[2], ref b[3], ref b[4]);
            check("session", "bucket " + i, a.SequenceEqual(b), ref cases, ref bad);

            long now = random.NextInt64(1, 100_000_000);
            long last = random.Next(4) == 0 ? 0 : random.NextInt64(1, 100_000_000);
            double effective = (random.NextDouble() - 0.2) * 500;
            double configured = (random.NextDouble() - 0.2) * 500;
            check("session", "skip " + i,
                OutputFrameDiagnostics.ShouldSkipVirtualWrite(now, last, effective, configured) ==
                LegacySkip(now, last, effective, configured), ref cases, ref bad);
        }

        foreach (ImuResampleMode mode in Enum.GetValues<ImuResampleMode>())
        {
            check("session", "label " + mode,
                OutputFrameDiagnostics.GyroModeLabel(mode) == LegacyGyroLabel(mode), ref cases, ref bad);
        }
        check("session", "label invalid",
            OutputFrameDiagnostics.GyroModeLabel((ImuResampleMode)99) == LegacyGyroLabel((ImuResampleMode)99),
            ref cases, ref bad);
        foreach (string source in new[] { "", "pro2_ble_age_12", "pro2_ble_age_", "PRO2_BLE_AGE_1", "usb", "pro2_ble_live" })
        {
            check("session", "family " + source,
                OutputFrameDiagnostics.SourceFamily(source) ==
                (source.StartsWith("pro2_ble_age_", StringComparison.Ordinal) ? "pro2_ble_live" : source),
                ref cases, ref bad);
            foreach (bool running in new[] { false, true })
                foreach (bool published in new[] { false, true })
                    check("session", "neutral " + source + running + published,
                        OutputFrameDiagnostics.ShouldPublishDisconnectNeutral(running, published) ==
                        (!running && !published), ref cases, ref bad);
        }

        for (int i = 0; i < 900; i++)
        {
            GamepadState state = RandomMotionState(random);
            GamepadState? previous = random.Next(3) == 0 ? null : RandomMotionState(random);
            double scale = random.Next(8) == 0 ? 1.0 : (random.NextDouble() - 0.3) * 4;
            check("session", "without " + i,
                SameMotion(MotionFrameTransform.WithoutMotion(state), LegacyWithoutMotion(state)), ref cases, ref bad);
            check("session", "scale " + i,
                SameMotion(MotionFrameTransform.ScaleGyro(state, scale), LegacyScaleGyro(state, scale)), ref cases, ref bad);
            check("session", "filter " + i,
                SameMotion(MotionFrameTransform.FilterMotion(previous, state), LegacyFilterMotion(previous, state)), ref cases, ref bad);
            check("session", "copy " + i,
                SameMotion(MotionFrameTransform.CopyMotion(state, previous), LegacyCopyMotion(state, previous)), ref cases, ref bad);

            short x = (short)random.Next(short.MinValue, short.MaxValue + 1);
            short y = (short)random.Next(short.MinValue, short.MaxValue + 1);
            double alpha = random.NextDouble() * 2 - 0.5;
            int raw = random.Next(int.MinValue, int.MaxValue);
            check("session", "int16 " + i,
                MotionFrameTransform.ScaleInt16(x, scale) == LegacyScaleInt16(x, scale) &&
                MotionFrameTransform.LerpInt16(x, y, alpha) == LegacyLerp(x, y, alpha) &&
                MotionFrameTransform.ClampInt16(raw) == LegacyClamp(raw), ref cases, ref bad);
        }
    }

    // ---------------- 主 ViewModel 抽出的纯策略 ----------------

    private static string LegacySanitize(string value) =>
        (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");

    private static string LegacyFirstLine(string text) =>
        (text ?? "").Replace("\r", "").Split('\n')[0];

    private static bool LegacyConnectionLogLine(string line)
    {
        if (line.StartsWith("[LINK_TUNING]", StringComparison.OrdinalIgnoreCase)) return false;
        return line.StartsWith("[BLE", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[USB", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[LINK", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[PRO2_BLE", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[PRO2_FIRMWARE", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[PRO2_RECONNECT_TIMING", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[BLUETOOTH_RESTART", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[BLE_ADAPTER", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LegacyPortConflict(string text)
    {
        string value = text ?? "";
        return value.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("only one usage of each socket address", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("forbidden by its access permissions", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("通常每个套接字地址", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("bind:", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("listen tcp", StringComparison.OrdinalIgnoreCase) &&
               value.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LegacyUsbipIssue(string text)
    {
        string value = text ?? "";
        return value.Contains("usbip", StringComparison.OrdinalIgnoreCase) &&
               (value.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("attach", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("denied", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LegacyLoopbackHost(string value)
    {
        string candidate = (value ?? "").Trim();
        if (candidate.Length == 0 || candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(candidate, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    private static bool LegacyValidPort(int port) => port is >= 1 and <= 65535;

    private static int LegacyUsbPort(int apiPort) => apiPort == 3242 ? 3241 : apiPort == 1 ? 2 : apiPort - 1;

    private static List<BridgePortPair> LegacyPortCandidates(int requested)
    {
        var candidates = new List<BridgePortPair>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(int apiPort, string source)
        {
            int usbPort = LegacyUsbPort(apiPort);
            if (!LegacyValidPort(apiPort) || !LegacyValidPort(usbPort) || apiPort == usbPort) return;
            if (seen.Add(apiPort + "/" + usbPort)) candidates.Add(new BridgePortPair(apiPort, usbPort, source));
        }
        Add(requested, "requested");
        if (requested != 3242)
        {
            Add(requested + 100, "requested_plus_100");
            Add(requested + 200, "requested_plus_200");
            Add(requested + 1000, "requested_plus_1000");
        }
        Add(33242, "fallback_1"); Add(34242, "fallback_2"); Add(35242, "fallback_3");
        Add(36242, "fallback_4"); Add(37242, "fallback_5");
        return candidates;
    }

    private static TimeSpan LegacyReconnectDelay(int streak)
    {
        if (streak <= 3) return TimeSpan.FromMilliseconds(2500);
        if (streak <= 6) return TimeSpan.FromSeconds(5);
        if (streak <= 10) return TimeSpan.FromSeconds(10);
        return TimeSpan.FromSeconds(30);
    }

    private static string LegacyStickFailure(string result)
    {
        if (result.Contains("not_started", StringComparison.Ordinal)) return "采样流程被意外中止，请使用修复后的版本重新尝试。";
        if (result.Contains("insufficient_samples", StringComparison.Ordinal)) return "收到的有效手柄数据不足，请确认手柄保持连接后重试。";
        if (result.Contains("incomplete_directional_travel", StringComparison.Ordinal)) return "至少一个摇杆方向没有推到底。请让左右摇杆都沿外圈完整转动。";
        if (result.Contains("sticks_moved", StringComparison.Ordinal)) return "零位采样期间检测到摇杆移动，请完全松开摇杆后重试。";
        return result;
    }

    private static bool LegacyMultiSlot(ViiperVirtualMode mode) =>
        mode is ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.Pro1 or ViiperVirtualMode.Pro2 or ViiperVirtualMode.Xbox;

    private static ViiperDeviceProfile LegacyProfileFor(ViiperVirtualMode mode) => mode switch
    {
        ViiperVirtualMode.DualSenseLike => ViiperDeviceProfile.DualSenseLike,
        ViiperVirtualMode.Pro1 => ViiperDeviceProfile.Pro1,
        ViiperVirtualMode.Pro2 => ViiperDeviceProfile.Pro2,
        ViiperVirtualMode.Xbox => ViiperDeviceProfile.Xbox,
        ViiperVirtualMode.KeyMouse => ViiperDeviceProfile.KeyMouse,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static ViiperVirtualMode LegacyModeFromKey(string? key) => AppSettings.NormalizeModeKey(key) switch
    {
        "pro1" => ViiperVirtualMode.Pro1,
        "pro2" => ViiperVirtualMode.Pro2,
        "xbox" => ViiperVirtualMode.Xbox,
        "keymouse" => ViiperVirtualMode.KeyMouse,
        _ => ViiperVirtualMode.DualSenseLike
    };

    private static string LegacyModeKey(ViiperVirtualMode mode) => mode switch
    {
        ViiperVirtualMode.Pro1 => "pro1",
        ViiperVirtualMode.Pro2 => "pro2",
        ViiperVirtualMode.Xbox => "xbox",
        ViiperVirtualMode.KeyMouse => "keymouse",
        _ => "dualsense"
    };

    private static string LegacyCadence(OutputRateOption option) =>
        option.SourcePaced
            ? "source_adaptive(host_poll_cap_ms=" + option.Interval.TotalMilliseconds.ToString("F1") + ")"
            : "fixed_" + option.Hz.ToString("F1") + "hz";

    private static bool SamePorts(IReadOnlyList<BridgePortPair> a, IReadOnlyList<BridgePortPair> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void ViewModel(Random random, CheckFn check, ref int cases, ref int bad)
    {
        string[] fixedTexts =
        [
            "", "abc", "[BLE] ok", "[ble_adapter] x", "[LINK_TUNING] skip", "[link_tuning] skip",
            "[PRO2_FIRMWARE] z", "[USB] y", "address already in use", "ONLY ONE USAGE OF EACH SOCKET ADDRESS",
            "bind: fail", "listen tcp ... permission denied", "listen tcp only", "通常每个套接字地址",
            "usbip driver not found", "USBIP attach denied", "usbip alone", "\"server\":\"VIIPER\"",
            "{\"server\":\"viiper\"}", "127.0.0.1", "::1", "localhost", "LOCALHOST", "example.com",
            "a\r\nb\"c", "\r\n", "  \r\n  ", "not_started", "insufficient_samples",
            "incomplete_directional_travel", "sticks_moved", "ok"
        ];
        const string alphabet = "abcABC012[]_: \"'\r\n通常每个套接字地址BLINKUSBPRO2serverVIIPERbindlisten";
        var texts = new List<string>(fixedTexts) { null! };
        for (int i = 0; i < 600; i++) texts.Add(RandomText(random, alphabet, random.Next(0, 48)));

        foreach (string? text in texts)
        {
            check("viewmodel", "sanitize " + (text ?? "null"),
                ViewModelPolicies.SanitizeForLog(text!) == LegacySanitize(text!), ref cases, ref bad);
            check("viewmodel", "firstline " + (text ?? "null"),
                ViewModelPolicies.FirstLine(text!) == LegacyFirstLine(text!), ref cases, ref bad);
            check("viewmodel", "loopback " + (text ?? "null"),
                ViewModelPolicies.IsLoopbackHost(text!) == LegacyLoopbackHost(text!), ref cases, ref bad);
            check("viewmodel", "portconflict " + (text ?? "null"),
                ViewModelPolicies.LooksLikePortConflict(text!) == LegacyPortConflict(text!), ref cases, ref bad);
            check("viewmodel", "usbip " + (text ?? "null"),
                ViewModelPolicies.LooksLikeUsbipDriverIssue(text!) == LegacyUsbipIssue(text!), ref cases, ref bad);
            if (text == null) continue;
            check("viewmodel", "logline " + text,
                ViewModelPolicies.IsConnectionLogLine(text) == LegacyConnectionLogLine(text), ref cases, ref bad);
            check("viewmodel", "ping " + text,
                ViewModelPolicies.LooksLikeViiperPing(text) ==
                text.Contains("\"server\":\"VIIPER\"", StringComparison.OrdinalIgnoreCase), ref cases, ref bad);
            check("viewmodel", "stickfail " + text,
                ViewModelPolicies.StickCalibrationFailureText(text) == LegacyStickFailure(text), ref cases, ref bad);
        }

        int[] ports = [0, 1, 2, 100, 3241, 3242, 3243, 65534, 65535, 65536, -1, int.MaxValue, int.MinValue];
        for (int i = 0; i < 500; i++)
        {
            int port = random.Next(4) == 0 ? ports[random.Next(ports.Length)] : random.Next(-100, 70000);
            check("viewmodel", "validport " + i,
                ViewModelPolicies.IsValidTcpPort(port) == LegacyValidPort(port), ref cases, ref bad);
            check("viewmodel", "usbport " + i,
                ViewModelPolicies.DeriveUsbPort(port) == LegacyUsbPort(port), ref cases, ref bad);
            check("viewmodel", "candidates " + i,
                SamePorts(ViewModelPolicies.BuildBridgePortCandidates(port), LegacyPortCandidates(port)),
                ref cases, ref bad);
            int streak = random.Next(-5, 40);
            check("viewmodel", "reconnect " + i,
                ViewModelPolicies.Pro2ReconnectDelayForAttempt(streak) == LegacyReconnectDelay(streak),
                ref cases, ref bad);
        }

        var modes = new List<ViiperVirtualMode>(Enum.GetValues<ViiperVirtualMode>()) { (ViiperVirtualMode)99 };
        foreach (ViiperVirtualMode mode in modes)
        {
            check("viewmodel", "multislot " + mode,
                ViewModelPolicies.IsMultiSlotMode(mode) == LegacyMultiSlot(mode), ref cases, ref bad);
            check("viewmodel", "modekey " + mode,
                ViewModelPolicies.ModeKey(mode) == LegacyModeKey(mode), ref cases, ref bad);
            check("viewmodel", "profile " + mode,
                Capture(() => ViewModelPolicies.ProfileFor(mode).ToString()!) ==
                Capture(() => LegacyProfileFor(mode).ToString()!), ref cases, ref bad);
        }

        string?[] keys = [null, "", "pro1", "PRO2", "xbox", "keymouse", "dualsense", "unknown", " Pro2 "];
        foreach (string? key in keys)
        {
            check("viewmodel", "modefromkey " + key,
                ViewModelPolicies.ModeFromKey(key) == LegacyModeFromKey(key), ref cases, ref bad);
        }

        foreach (OutputRateOption option in OutputRateOption.All)
        {
            check("viewmodel", "cadence " + option.Label,
                ViewModelPolicies.PushCadenceTelemetry(option) == LegacyCadence(option), ref cases, ref bad);
            OutputRateOption custom = option with
            {
                Hz = option.Hz + 12.34,
                Interval = option.Interval + TimeSpan.FromMilliseconds(3.7)
            };
            check("viewmodel", "cadence custom " + option.Label,
                ViewModelPolicies.PushCadenceTelemetry(custom) == LegacyCadence(custom), ref cases, ref bad);
        }
    }

    // ---------------- partial 里抽出的格式化函数 ----------------

    private static string LegacyRangeHint(double left, double right)
    {
        const double done = 0.98;
        bool l = left >= done, r = right >= done;
        if (l && r) return "已覆盖，保持 2 秒";
        if (l) return "还差右摇杆";
        if (r) return "还差左摇杆";
        return "";
    }

    private static double LegacyClampRange(double value, double min, double max, double fallback) =>
        !double.IsFinite(value) ? fallback : Math.Clamp(Math.Round(value, 3), min, max);

    private static double LegacyClampScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value, 2), 0.25, 4.0) : 1.0;

    private static string LegacyLowRateNumber(double value) => double.IsFinite(value) && value >= 0
        ? value.ToString("F1") : "未获取";

    private static string LegacyPercentText(int raw) =>
        (raw / (GamepadState.AxisCenter / 100.0)).ToString("0.0") + "%";

    private static string LegacyDescribeUsbipSource(string source) => source switch
    {
        "saved_manual_path" => "手动指定",
        "near_application" => "程序附近目录",
        "common_directory" => "常用安装目录",
        _ when source.StartsWith("path_", StringComparison.Ordinal) => "环境变量",
        _ when source.StartsWith("uninstall_", StringComparison.Ordinal) || source.StartsWith("app_paths_", StringComparison.Ordinal) => "安装记录",
        _ => "自动查找"
    };

    private static byte[] LegacyTestRumble()
    {
        byte[] report = new byte[64];
        report[0] = 0x02;
        ulong packed = (0x87UL) | ((ulong)400 << 10) | (0x87UL << 20) | ((ulong)400 << 30);
        for (int side = 1; side <= 17; side += 16)
        {
            report[side] = 0x50;
            for (int frame = 0; frame < 3; frame++)
            {
                int off = side + 1 + frame * 5;
                for (int i = 0; i < 5; i++) report[off + i] = (byte)(packed >> (i * 8));
            }
        }
        return report;
    }

    private static void Formatting(Random random, CheckFn check, ref int cases, ref int bad)
    {
        byte[] expectedReport = LegacyTestRumble();
        for (int i = 0; i < 20; i++)
        {
            check("formatting", "rumble report " + i,
                ViewModelFormatting.BuildTestRumbleReport().SequenceEqual(expectedReport), ref cases, ref bad);
        }

        double[] values = [0, 0.97, 0.98, 0.9800001, 1, -1, 0.5, 1e9, double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        foreach (double left in values)
        {
            foreach (double right in values)
            {
                check("formatting", "range " + left + "/" + right,
                    ViewModelFormatting.RangeHint(left, right) == LegacyRangeHint(left, right), ref cases, ref bad);
            }
        }

        for (int i = 0; i < 800; i++)
        {
            double value = random.Next(8) == 0
                ? values[random.Next(values.Length)]
                : (random.NextDouble() - 0.5) * 20;
            check("formatting", "clampscale " + i,
                Equals(ViewModelFormatting.ClampScale(value), LegacyClampScale(value)), ref cases, ref bad);
            check("formatting", "lowrate " + i,
                ViewModelFormatting.LowRateNumber(value) == LegacyLowRateNumber(value), ref cases, ref bad);
            int raw = random.Next(4) == 0 ? random.Next(-500, 9000) : random.Next(0, 4096);
            check("formatting", "percent " + i,
                ViewModelFormatting.PercentText(raw) == LegacyPercentText(raw), ref cases, ref bad);

            double min = random.Next(4) == 0 ? 10 : -10;
            double max = random.Next(4) == 0 ? -20 : 10;
            double fallback = (random.NextDouble() - 0.5) * 5;
            check("formatting", "clamprange " + i,
                Capture(() => ViewModelFormatting.ClampRange(value, min, max, fallback).ToString()) ==
                Capture(() => LegacyClampRange(value, min, max, fallback).ToString()), ref cases, ref bad);
        }

        string[] sources =
        [
            "", "saved_manual_path", "near_application", "common_directory",
            "path_123", "PATH_123", "uninstall_abc", "app_paths_x", "unknown",
            "path_", "uninstall_", "app_paths_", "saved_manual_path_extra"
        ];
        for (int i = 0; i < 300; i++)
        {
            string source = random.Next(3) == 0
                ? sources[random.Next(sources.Length)]
                : RandomText(random, "path_uninstall_app_saved_ABC09_", random.Next(0, 24));
            check("formatting", "usbipsource " + i,
                ViewModelFormatting.DescribeUsbipSource(source) == LegacyDescribeUsbipSource(source),
                ref cases, ref bad);
        }
    }

    // ---------------- 输入帧策略与设备状态解码 ----------------

    private static void Policies(Random random, CheckFn check, ref int cases, ref int bad)
    {
        FinalVerify(check, ref cases, ref bad);
        double[] ages = [-100, -0.001, 0, 1, 19.999, 20, 20.0001, 32.999, 33, 33.001,
            50, 50.001, 99.999, 100, 100.001, 249.999, 250, 250.001,
            749.999, 750, 750.001, 1000, 5000];
        foreach (double ms in ages)
        {
            GamepadState state = RandomPolicyState(random);
            TimeSpan age = TimeSpan.FromMilliseconds(ms);
            GamepadState expected = LegacyFreshness(state, age, out string expectedSource);
            GamepadState actual = FrameFreshnessPolicy.Resolve(state, age, out string actualSource);
            check("policies", "freshness " + ms,
                expectedSource == actualSource && PolicyStateKey(expected) == PolicyStateKey(actual),
                ref cases, ref bad);
        }

        for (int i = 0; i < 700; i++)
        {
            GamepadState state = RandomPolicyState(random);
            TimeSpan age = TimeSpan.FromMilliseconds((random.NextDouble() - 0.02) * 1200);
            GamepadState expected = LegacyFreshness(state, age, out string es);
            GamepadState actual = FrameFreshnessPolicy.Resolve(state, age, out string ac);
            check("policies", "freshness fuzz " + i,
                es == ac && PolicyStateKey(expected) == PolicyStateKey(actual), ref cases, ref bad);
        }

        for (int round = 0; round < 25; round++)
        {
            var actual = new MotionSampleResolver();
            var legacy = new LegacyMotionResolver();
            for (int frame = 0; frame < 40; frame++)
            {
                GamepadState? input = random.Next(10) == 0 ? null : RandomPolicyState(random);
                GamepadState a = actual.Resolve(input);
                GamepadState b = legacy.Resolve(input);
                check("policies", "motion " + round + "/" + frame,
                    PolicyStateKey(a) == PolicyStateKey(b) && actual.HasAcceleration == legacy.Has,
                    ref cases, ref bad);
                if (random.Next(12) == 0) { actual.Reset(); legacy.Reset(); }
            }
        }

        for (int round = 0; round < 50; round++)
        {
            var a = new GapStatisticsWindow();
            var b = new LegacyGapWindow();
            int n = round == 0 ? 2300 : random.Next(0, 160);
            for (int i = 0; i < n; i++)
            {
                double gap = random.Next(12) switch
                {
                    0 => double.NaN,
                    1 => double.PositiveInfinity,
                    2 => double.NegativeInfinity,
                    3 => -random.NextDouble(),
                    4 => 7.5,
                    5 => 15,
                    6 => 22,
                    _ => random.NextDouble() * 30,
                };
                a.Record(gap);
                b.Record(gap);
            }

            string ta = a.SummarizeAndReset(out GapStatisticsSummary sa);
            string tb = b.SummarizeAndReset(out var sb);
            check("policies", "gap " + round,
                ta == tb && sa.Samples == sb.Samples && sa.P50Ms == sb.P50 && sa.P95Ms == sb.P95 &&
                sa.P99Ms == sb.P99 && sa.MinMs == sb.Min && sa.MaxMs == sb.Max &&
                sa.Near7p5 == sb.Near7p5 && sa.Near15 == sb.Near15 && sa.Over22 == sb.Over22,
                ref cases, ref bad);
            a.Record(7.5);
            b.Record(7.5);
            check("policies", "gap after reset " + round,
                a.SummarizeAndReset() == b.SummarizeAndReset(), ref cases, ref bad);
        }

        foreach (uint seed in new uint[] { 0, 1, 1000, uint.MaxValue - 5 })
        {
            var window = new UpdateRateWindow(seed);
            uint previous = seed;
            for (int i = 0; i < 120; i++)
            {
                uint current = random.Next(4) == 0
                    ? (uint)random.Next(0, 50)
                    : previous + (uint)random.Next(0, 20);
                double seconds = random.Next(6) == 0
                    ? new[] { 0d, -1d, double.NaN, double.PositiveInfinity }[random.Next(4)]
                    : random.NextDouble() * 2 + 0.001;
                double actual = window.Measure(current, seconds);
                uint delta = current >= previous ? current - previous : current;
                previous = current;
                double expected = double.IsFinite(seconds) && seconds > 0 ? delta / seconds : 0;
                check("policies", "rate " + seed + "/" + i, Equals(actual, expected), ref cases, ref bad);
            }
        }

        foreach (double minMs in new double[] { 1, 2, 7, 20, 33, 999, 1000, 1500, 3000 })
        {
            foreach (double stepMs in new double[] { 1, 3, 7, 13, 50, 250, 900 })
            {
                TimeSpan minimum = TimeSpan.FromMilliseconds(minMs);
                int tick = 0;
                var delays = new List<long>();
                var expected = new List<long>();
                double elapsed = 0;
                while (elapsed < minMs)
                {
                    long ms = Math.Clamp(
                        (long)Math.Ceiling((minimum - TimeSpan.FromMilliseconds(elapsed)).TotalMilliseconds), 1, 1000);
                    expected.Add(ms);
                    elapsed += stepMs;
                }

                string result = Capture(() =>
                {
                    TimedMeasurementWindow.WaitUntilElapsedAsync(minimum,
                        () => TimeSpan.FromMilliseconds(stepMs * tick++),
                        (d, t) => { delays.Add((long)d.TotalMilliseconds); return Task.CompletedTask; },
                        CancellationToken.None).GetAwaiter().GetResult();
                    return "ok";
                });
                check("policies", "wait " + minMs + "/" + stepMs,
                    result == "ok:ok" && string.Join(",", delays) == string.Join(",", expected),
                    ref cases, ref bad);
            }
        }

        check("policies", "wait zero duration",
            Capture(() =>
            {
                TimedMeasurementWindow.WaitUntilElapsedAsync(TimeSpan.Zero, () => TimeSpan.Zero,
                    (d, t) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
                return "x";
            }).StartsWith("throw:ArgumentOutOfRangeException"), ref cases, ref bad);
        int regress = 0;
        check("policies", "wait nonmonotonic",
            Capture(() =>
            {
                TimedMeasurementWindow.WaitUntilElapsedAsync(TimeSpan.FromMilliseconds(50),
                    () => TimeSpan.FromMilliseconds(regress++ == 0 ? 10 : 5),
                    (d, t) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
                return "x";
            }).StartsWith("throw:InvalidOperationException"), ref cases, ref bad);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            check("policies", "wait cancelled",
                Capture(() =>
                {
                    TimedMeasurementWindow.WaitUntilElapsedAsync(TimeSpan.FromMilliseconds(50),
                        () => TimeSpan.Zero, (d, t) => Task.CompletedTask, cancelled.Token).GetAwaiter().GetResult();
                    return "x";
                }).StartsWith("throw:OperationCanceledException"), ref cases, ref bad);
        }
        check("policies", "wait null clock",
            Capture(() =>
            {
                TimedMeasurementWindow.WaitUntilElapsedAsync(TimeSpan.FromMilliseconds(5), null!,
                    (d, t) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
                return "x";
            }).StartsWith("throw:ArgumentNullException"), ref cases, ref bad);

        for (int round = 0; round < 30; round++)
        {
            int cap = new[] { 2, 3, 8, 64 }[random.Next(4)];
            var a = new SequentialFrameQueue(cap);
            var b = new LegacyFrameQueue(cap);
            for (int op = 0; op < 120; op++)
            {
                int action = random.Next(10);
                string ra, rb;
                if (action < 4 || action == 8)
                {
                    GamepadState state = RandomPolicyState(random);
                    long ticks = random.NextInt64(0, 1_000_000);
                    if (action == 8) { a.ResetTo(state, ticks); b.ResetTo(state, ticks); }
                    else { a.Enqueue(state, ticks); b.Enqueue(state, ticks); }
                    ra = rb = "push";
                }
                else if (action == 7)
                {
                    a.Reset();
                    b.Reset();
                    ra = rb = "reset";
                }
                else if (action == 4)
                {
                    bool oa = a.TryDequeue(out GamepadState sa, out long ta2);
                    bool ob = b.TryDequeue(out GamepadState sb, out long tb2);
                    ra = oa + "|" + PolicyStateKey(sa) + "|" + ta2;
                    rb = ob + "|" + PolicyStateKey(sb) + "|" + tb2;
                }
                else if (action == 5)
                {
                    bool oa = a.TryDequeueNewest(out GamepadState sa, out long ta2, out int ca);
                    bool ob = b.TryDequeueNewest(out GamepadState sb, out long tb2, out int cb);
                    ra = oa + "|" + PolicyStateKey(sa) + "|" + ta2 + "|" + ca;
                    rb = ob + "|" + PolicyStateKey(sb) + "|" + tb2 + "|" + cb;
                }
                else
                {
                    bool oa = a.TryDequeueRealtimeEdges(out GamepadState sa, out long ta2, out int ca);
                    bool ob = b.TryDequeueRealtimeEdges(out GamepadState sb, out long tb2, out int cb);
                    ra = oa + "|" + PolicyStateKey(sa) + "|" + ta2 + "|" + ca;
                    rb = ob + "|" + PolicyStateKey(sb) + "|" + tb2 + "|" + cb;
                }

                check("policies", "queue " + round + "/" + op,
                    ra == rb && FrameQueueCounters(a) == FrameQueueCounters(b), ref cases, ref bad);
            }
        }

        double[] hzValues = [double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            -100, 0, 0.4, 0.5, 39.5, 40, 511, 511.4, 10000];
        double[] limits = [double.NaN, double.NegativeInfinity, double.PositiveInfinity,
            -5, 0, 1, 40, 70, 511, 512, 10000];
        foreach (double hz in hzValues)
        {
            foreach (double lo in limits)
            {
                foreach (double hi in limits)
                {
                    check("policies", "hz " + hz + "/" + lo + "/" + hi,
                        RumbleFrequencyLimits.FromEstimatedHz(hz, lo, hi) == LegacyFromHz(hz, lo, hi) &&
                        RumbleFrequencyLimits.WouldClamp(hz, lo, hi) == LegacyWouldClamp(hz, lo, hi),
                        ref cases, ref bad);
                }
            }
        }
        check("policies", "hz defaults",
            RumbleFrequencyLimits.FromEstimatedHz(120) == LegacyFromHz(120, 40, 511) &&
            RumbleFrequencyLimits.WouldClamp(120) == LegacyWouldClamp(120, 40, 511), ref cases, ref bad);

        for (int raw = 0; raw <= 255; raw++)
        {
            var reading = new BatteryLevelReading((byte)raw);
            check("policies", "battery raw " + raw,
                ActualBatteryKey(reading) == LegacyBatteryKey((byte)raw), ref cases, ref bad);
            var sa = new GamepadState();
            var sb = new GamepadState();
            reading.ApplyTo(sa);
            LegacyBatteryApply((byte)raw, sb);
            check("policies", "battery apply " + raw,
                PolicyStateKey(sa) == PolicyStateKey(sb), ref cases, ref bad);
        }
        for (int i = 0; i < 900; i++)
        {
            GamepadState state = RandomPolicyState(random);
            bool fresh = random.Next(2) == 0;
            bool atConnection = random.Next(2) == 0;
            check("policies", "battery display " + i,
                BatteryLevelReading.Display(state, fresh, atConnection) ==
                LegacyBatteryDisplay(state, fresh, atConnection), ref cases, ref bad);
        }
        for (int i = 0; i < 500; i++)
        {
            byte[] payload = new byte[random.Next(0, 20)];
            random.NextBytes(payload);
            bool oa = BatteryLevelReading.TryDecodePrimary(payload, out BatteryLevelReading ra);
            bool ob = LegacyTryPrimary(payload, out byte rb);
            check("policies", "battery decode " + i,
                oa == ob && (!oa || ActualBatteryKey(ra) == LegacyBatteryKey(rb)), ref cases, ref bad);
        }

        for (int i = 0; i < 700; i++)
        {
            ushort code = (ushort)random.Next(0, 0x10000);
            var reading = new ChargeStatusReading(code, 0);
            check("policies", "charge flags " + i,
                ActualChargeKey(reading) == LegacyChargeKey(code), ref cases, ref bad);
            var sa = new GamepadState();
            var sb = new GamepadState();
            reading.ApplyTo(sa);
            LegacyChargeApply(code, sb);
            check("policies", "charge apply " + i,
                PolicyStateKey(sa) == PolicyStateKey(sb), ref cases, ref bad);
        }
        for (int i = 0; i < 500; i++)
        {
            byte[] fd2 = new byte[random.Next(0, 70)];
            random.NextBytes(fd2);
            bool oa = ChargeStatusReading.TryDecodeFd2(fd2, out ChargeStatusReading ra);
            bool ob = LegacyTryFd2(fd2, out ushort cb);
            check("policies", "charge fd2 " + i,
                oa == ob && (!oa || (ra.Code == cb && ra.Flags == 0)), ref cases, ref bad);

            byte[] reply = new byte[random.Next(0, 24)];
            random.NextBytes(reply);
            if (reply.Length >= 12 && random.Next(2) == 0)
            {
                reply[0] = 0x0b; reply[1] = 1; reply[2] = 1; reply[3] = 4; reply[5] = 0x78;
            }
            bool pa = ChargeStatusReading.TryDecodeReply(reply, out ChargeStatusReading pra);
            bool pb = LegacyTryReply(reply, out ushort pc, out ushort pf);
            check("policies", "charge reply " + i,
                pa == pb && (!pa || (pra.Code == pc && pra.Flags == pf)), ref cases, ref bad);
        }

        for (int i = 0; i < 1800; i++)
        {
            BatteryRefreshRate before = RandomRate(random);
            BatteryRefreshRate after = RandomRate(random);
            byte[]? payload = null;
            if (random.Next(4) != 0)
            {
                payload = new byte[random.Next(0, 20)];
                random.NextBytes(payload);
                if (payload.Length > 1) payload[1] = (byte)((random.Next(0, 10) << 2) | random.Next(0, 4));
            }
            var report = new InitialBatterySnapshotResult(payload, random.Next(2) == 0,
                "status_" + random.Next(3), "cleanup");
            bool same = random.Next(2) == 0;
            double gap = random.Next(6) == 0
                ? new[] { double.NaN, -1d, 45.001, double.PositiveInfinity }[random.Next(4)]
                : random.NextDouble() * 60;
            BatteryRefreshDecision actual = RuntimeBatteryRefreshPolicy.Evaluate(before, after, report, same, gap);
            var expected = LegacyEvaluate(before, after, report, same, gap);
            check("policies", "runtime evaluate " + i,
                actual.Accepted == expected.Accepted && actual.Reason == expected.Reason &&
                RuntimeBatteryRefreshPolicy.CanProbe(before) == LegacyCanProbe(before) &&
                RuntimeBatteryRefreshPolicy.CanProbe(after) == LegacyCanProbe(after),
                ref cases, ref bad);

            bool runtime = random.Next(2) == 0;
            double age = random.Next(4) == 0
                ? new[] { double.NaN, -1d, double.PositiveInfinity }[random.Next(3)]
                : random.NextDouble() * 900;
            check("policies", "runtime fresh " + i,
                RuntimeBatteryRefreshPolicy.IsFreshRuntimeLevel(runtime, age) == LegacyIsFresh(runtime, age),
                ref cases, ref bad);
            int retries = random.Next(-1, 3);
            bool cleanup = random.Next(2) == 0;
            check("policies", "runtime retry " + i,
                RuntimeBatteryRefreshPolicy.CanCooldownRetry(actual, after, same, cleanup, retries) ==
                LegacyCanRetry(actual, after, same, cleanup, retries), ref cases, ref bad);
            double healthy = random.Next(4) == 0 ? double.NaN : random.NextDouble() * 900;
            bool enabled = random.Next(2) == 0, recent = random.Next(2) == 0;
            check("policies", "runtime healthy " + i,
                RuntimeBatteryRefreshPolicy.HealthyCooldownComplete(healthy, enabled, same, recent) ==
                LegacyHealthy(healthy, enabled, same, recent), ref cases, ref bad);
        }
    }

    private static string PolicyStateKey(GamepadState s) =>
        $"{s.Buttons}|{s.RawButtons}|{s.Lx}|{s.Ly}|{s.Rx}|{s.Ry}|{s.L2}|{s.R2}|" +
        $"{s.AccelValid}|{s.GyroValid}|{s.AccelX}|{s.AccelY}|{s.AccelZ}|" +
        $"{s.GyroX}|{s.GyroY}|{s.GyroZ}|{s.Updates}|{s.BatteryLevel}|{s.BatteryPercent}|" +
        $"{s.BatteryPercentApproximate}|{s.BatteryPowerKnown}|{s.BatteryExternalPower}|{s.BatteryCharging}";

    private static GamepadState RandomPolicyState(Random random) => new()
    {
        Buttons = (GamepadButtons)(ulong)random.Next(0, 4096),
        L2 = (ushort)random.Next(0, 4096),
        R2 = (ushort)random.Next(0, 4096),
        Lx = (ushort)random.Next(0, 4096),
        Ly = (ushort)random.Next(0, 4096),
        Rx = (ushort)random.Next(0, 4096),
        Ry = (ushort)random.Next(0, 4096),
        AccelValid = random.Next(2) == 0,
        GyroValid = random.Next(2) == 0,
        AccelX = (short)random.Next(-32768, 32768),
        AccelY = (short)random.Next(-32768, 32768),
        AccelZ = (short)random.Next(-32768, 32768),
        GyroX = (short)random.Next(-32768, 32768),
        GyroY = (short)random.Next(-32768, 32768),
        GyroZ = (short)random.Next(-32768, 32768),
        Updates = (uint)random.Next(0, 100000),
        BatteryLevel = (byte)random.Next(0, 256),
        BatteryPercent = (byte)random.Next(0, 256),
        BatteryPercentApproximate = random.Next(2) == 0,
        BatteryPowerKnown = random.Next(2) == 0,
        BatteryExternalPower = random.Next(2) == 0,
        BatteryCharging = random.Next(2) == 0,
    };

    private static GamepadState LegacyFreshness(GamepadState latest, TimeSpan age, out string source)
    {
        if (age <= TimeSpan.FromMilliseconds(20)) { source = "pro2_ble_age_0_20"; return latest; }
        if (age <= TimeSpan.FromMilliseconds(33)) { source = "pro2_ble_age_20_33"; return latest; }
        if (age <= TimeSpan.FromMilliseconds(50)) { source = "pro2_ble_age_33_50"; return latest; }
        if (age <= TimeSpan.FromMilliseconds(100)) { source = "pro2_ble_age_50_100"; return latest; }
        if (age <= TimeSpan.FromMilliseconds(250)) { source = "pro2_ble_age_gt100"; return latest; }
        if (age <= TimeSpan.FromMilliseconds(750))
        {
            source = "pro2_ble_danger_safe_hold";
            GamepadState r = latest.Clone();
            r.Buttons = GamepadButtons.None;
            r.L2 = 0;
            r.R2 = 0;
            r.AccelValid = false;
            r.GyroValid = false;
            return r;
        }
        source = "neutral";
        return GamepadState.Neutral();
    }

    private sealed class LegacyMotionResolver
    {
        private bool hasAcceleration;
        private short accelX, accelY, accelZ;
        public bool Has => hasAcceleration;

        public GamepadState Resolve(GamepadState? input)
        {
            input ??= GamepadState.Neutral();
            if (input.AccelValid)
            {
                accelX = input.AccelX; accelY = input.AccelY; accelZ = input.AccelZ;
                hasAcceleration = true;
            }
            if (input.AccelValid && input.GyroValid) return input;

            GamepadState resolved = input.Clone();
            if (!input.AccelValid)
            {
                resolved.AccelX = hasAcceleration ? accelX : (short)0;
                resolved.AccelY = hasAcceleration ? accelY : (short)0;
                resolved.AccelZ = hasAcceleration ? accelZ : (short)4096;
                resolved.AccelValid = true;
            }
            if (!input.GyroValid)
            {
                resolved.GyroX = resolved.GyroY = resolved.GyroZ = 0;
                resolved.GyroValid = false;
            }
            return resolved;
        }

        public void Reset() { hasAcceleration = false; accelX = accelY = accelZ = 0; }
    }

    private sealed class LegacyGapWindow
    {
        private readonly double[] gaps = new double[2048];
        private int count, stored, near75, near15, over22;
        private double min = double.PositiveInfinity, max;

        public void Record(double gapMs)
        {
            if (!double.IsFinite(gapMs) || gapMs < 0) return;
            gaps[stored++ % gaps.Length] = gapMs;
            count++;
            min = Math.Min(min, gapMs); max = Math.Max(max, gapMs);
            if (Math.Abs(gapMs - 7.5) <= 1.5) near75++;
            if (Math.Abs(gapMs - 15) <= 1.5) near15++;
            if (gapMs >= 22) over22++;
        }

        public string SummarizeAndReset() => SummarizeAndReset(out _);

        public string SummarizeAndReset(
            out (int Samples, double P50, double P95, double P99, double Min, double Max,
                int Near7p5, int Near15, int Over22) summary)
        {
            int n = Math.Min(stored, gaps.Length);
            Array.Sort(gaps, 0, n);
            double P(double q) => n == 0 ? 0 : gaps[Math.Clamp((int)Math.Ceiling(n * q) - 1, 0, n - 1)];
            summary = (count, P(.5), P(.95), P(.99), count == 0 ? 0 : min, max, near75, near15, over22);
            string line = "gap_samples=" + count + " gap_p50_ms=" + P(.5).ToString("F2") +
                " gap_p95_ms=" + P(.95).ToString("F2") + " gap_p99_ms=" + P(.99).ToString("F2") +
                " gap_min_ms=" + (count == 0 ? 0 : min).ToString("F2") + " gap_max_ms=" + max.ToString("F2") +
                " gap_near7p5=" + near75 + " gap_near15=" + near15 + " gap_ge22=" + over22;
            Reset();
            return line;
        }

        public void Reset()
        {
            count = stored = near75 = near15 = over22 = 0;
            min = double.PositiveInfinity;
            max = 0;
        }
    }

    private static string FrameQueueCounters(SequentialFrameQueue q) =>
        $"{q.Count}|{q.MaximumDepth}|{q.EnqueuedCount}|{q.DequeuedCount}|{q.OverflowDropCount}|{q.RealtimeSupersededCount}";

    private static string FrameQueueCounters(LegacyFrameQueue q) =>
        $"{q.Count}|{q.MaximumDepth}|{q.EnqueuedCount}|{q.DequeuedCount}|{q.OverflowDropCount}|{q.RealtimeSupersededCount}";

    private sealed class LegacyFrameQueue
    {
        private readonly Queue<(GamepadState State, long Ticks)> frames = new();
        private readonly int capacity;
        public LegacyFrameQueue(int capacity) => this.capacity = capacity;

        public int Count => frames.Count;
        public int MaximumDepth { get; private set; }
        public ulong EnqueuedCount { get; private set; }
        public ulong DequeuedCount { get; private set; }
        public ulong OverflowDropCount { get; private set; }
        public ulong RealtimeSupersededCount { get; private set; }

        public void Enqueue(GamepadState state, long arrivalTicks)
        {
            if (frames.Count >= capacity) { frames.Dequeue(); OverflowDropCount++; }
            frames.Enqueue((state.Clone(), arrivalTicks));
            EnqueuedCount++;
            MaximumDepth = Math.Max(MaximumDepth, frames.Count);
        }

        public bool TryDequeue(out GamepadState state, out long arrivalTicks)
        {
            if (!frames.TryDequeue(out var entry))
            {
                state = GamepadState.Neutral(); arrivalTicks = 0; return false;
            }
            state = entry.State; arrivalTicks = entry.Ticks; DequeuedCount++; return true;
        }

        public bool TryDequeueNewest(out GamepadState state, out long arrivalTicks, out int supersededCount)
        {
            supersededCount = 0;
            if (frames.Count == 0) { state = GamepadState.Neutral(); arrivalTicks = 0; return false; }
            while (frames.Count > 1)
            {
                frames.Dequeue(); DequeuedCount++; RealtimeSupersededCount++; supersededCount++;
            }
            var newest = frames.Dequeue(); DequeuedCount++;
            state = newest.State; arrivalTicks = newest.Ticks; return true;
        }

        public bool TryDequeueRealtimeEdges(out GamepadState state, out long arrivalTicks, out int supersededCount)
        {
            supersededCount = 0;
            if (!frames.TryDequeue(out var entry))
            {
                state = GamepadState.Neutral(); arrivalTicks = 0; return false;
            }
            DequeuedCount++;
            while (frames.TryPeek(out var next) &&
                   next.State.Buttons == entry.State.Buttons &&
                   (next.State.L2 > 0) == (entry.State.L2 > 0) &&
                   (next.State.R2 > 0) == (entry.State.R2 > 0))
            {
                entry = frames.Dequeue(); DequeuedCount++; RealtimeSupersededCount++; supersededCount++;
            }
            state = entry.State; arrivalTicks = entry.Ticks; return true;
        }

        public void Reset()
        {
            frames.Clear(); MaximumDepth = 0; EnqueuedCount = 0; DequeuedCount = 0;
            OverflowDropCount = 0; RealtimeSupersededCount = 0;
        }

        public void ResetTo(GamepadState state, long arrivalTicks) { Reset(); Enqueue(state, arrivalTicks); }
    }

    private static ushort LegacyFromHz(double hz, double minimumHz, double maximumHz)
    {
        double minimum = double.IsFinite(minimumHz) ? Math.Clamp(minimumHz, 1, 511) : 40;
        double maximum = double.IsFinite(maximumHz) ? Math.Clamp(maximumHz, minimum, 511) : 511;
        double finite = double.IsFinite(hz) ? hz : minimum;
        return (ushort)Math.Clamp(Math.Round(finite, MidpointRounding.AwayFromZero), minimum, maximum);
    }

    private static bool LegacyWouldClamp(double hz, double minimumHz, double maximumHz) =>
        !double.IsFinite(hz) || hz < minimumHz || hz > Math.Min(maximumHz, 511);

    private static string ActualBatteryKey(BatteryLevelReading r) =>
        $"{r.Level}|{r.HasLevel}|{r.ExternalPower}|{r.Charging}|{r.ApproximatePercent}|{r.Telemetry}";

    private static string LegacyBatteryKey(byte raw)
    {
        byte level = (byte)((raw >> 2) & 0x0f);
        bool has = level <= 9;
        bool ext = (raw & 1) != 0;
        bool chg = (raw & 2) != 0;
        byte pct = has ? (byte)((level * 100 + 4) / 9) : GamepadState.BatteryUnknown;
        string telemetry = "raw_power=0x" + raw.ToString("X2") + " level=" +
            (has ? level + "/9" : "unknown") + " percent_approx=" + (has ? pct.ToString() : "unknown") +
            " external_power=" + ext.ToString().ToLowerInvariant() +
            " charging=" + chg.ToString().ToLowerInvariant();
        return $"{level}|{has}|{ext}|{chg}|{pct}|{telemetry}";
    }

    private static void LegacyBatteryApply(byte raw, GamepadState state)
    {
        byte level = (byte)((raw >> 2) & 0x0f);
        bool has = level <= 9;
        bool ext = (raw & 1) != 0;
        bool chg = (raw & 2) != 0;
        byte pct = has ? (byte)((level * 100 + 4) / 9) : GamepadState.BatteryUnknown;
        state.BatteryLevel = has ? level : GamepadState.BatteryUnknown;
        state.BatteryPercent = pct;
        state.BatteryPercentApproximate = has;
        state.BatteryPowerKnown = true;
        state.BatteryExternalPower = ext;
        state.BatteryCharging = chg;
    }

    private static string LegacyBatteryDisplay(GamepadState state, bool fresh, bool levelAtConnection)
    {
        if (!fresh) return "电量未知";
        string charge = state.BatteryPowerKnown
            ? state.BatteryCharging ? " · 充电中" : state.BatteryExternalPower ? " · 外接电源" : ""
            : "";
        if (state.BatteryLevel <= 9)
            return state.BatteryLevel + "/9 格" + (levelAtConnection ? "（连接时）" : "") + charge;
        if (state.BatteryPercent <= 100)
            return (state.BatteryPercentApproximate ? "约 " : "") + state.BatteryPercent + "%" + charge;
        return "电量未知" + charge;
    }

    private static bool LegacyTryPrimary(byte[] payload, out byte raw)
    {
        raw = 0;
        if (payload.Length < 11) return false;
        raw = payload[1];
        return true;
    }

    private static string ActualChargeKey(ChargeStatusReading r) =>
        $"{r.Known}|{r.Charging}|{r.ExternalPower}";

    private static string LegacyChargeKey(ushort code)
    {
        bool known = code == 0 || code == 0x20 || code is >= 0x30 and <= 0x3f;
        bool charging = code is >= 0x30 and <= 0x3f;
        bool external = code == 0x20 || charging;
        return $"{known}|{charging}|{external}";
    }

    private static void LegacyChargeApply(ushort code, GamepadState state)
    {
        bool known = code == 0 || code == 0x20 || code is >= 0x30 and <= 0x3f;
        bool charging = code is >= 0x30 and <= 0x3f;
        bool external = code == 0x20 || charging;
        state.BatteryPowerKnown = known;
        state.BatteryCharging = known && charging;
        state.BatteryExternalPower = known && external;
    }

    private static bool LegacyTryFd2(byte[] payload, out ushort code)
    {
        code = 0;
        if (payload.Length < 60) return false;
        code = payload[0x21];
        return true;
    }

    private static bool LegacyTryReply(byte[] reply, out ushort code, out ushort flags)
    {
        code = flags = 0;
        if (reply.Length < 12 || reply[0] != 0x0b || reply[1] != 1 ||
            reply[2] != 1 || reply[3] != 4 || reply[5] != 0x78) return false;
        code = (ushort)(reply[8] | (reply[9] << 8));
        flags = (ushort)(reply[10] | (reply[11] << 8));
        return true;
    }

    private static BatteryRefreshRate RandomRate(Random random)
    {
        double[] pool = [double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            -1, 0, 8.5, 14, 15, 100, 128, 200];
        double Pick() => random.Next(5) == 0 ? pool[random.Next(pool.Length)] : random.NextDouble() * 200;
        return new BatteryRefreshRate(Pick(), Pick(), Pick(), Pick(), Pick());
    }

    private static bool LegacyFinite(BatteryRefreshRate r) =>
        double.IsFinite(r.Fd2Hz) && double.IsFinite(r.ParsedHz) && double.IsFinite(r.P50Ms) &&
        double.IsFinite(r.P95Ms) && double.IsFinite(r.MaxGapMs) && r.Fd2Hz >= 0 &&
        r.ParsedHz >= 0 && r.P50Ms >= 0 && r.P95Ms >= r.P50Ms && r.MaxGapMs >= r.P95Ms;

    private static bool LegacyCanProbe(BatteryRefreshRate r) =>
        LegacyFinite(r) && r.Fd2Hz >= 128 && r.ParsedHz >= 128 && r.P50Ms <= 8.5;

    private static (bool Accepted, string Reason) LegacyEvaluate(
        BatteryRefreshRate before, BatteryRefreshRate after,
        InitialBatterySnapshotResult report, bool sameSession, double operationGapMs)
    {
        if (!sameSession) return (false, "session_changed");
        if (!report.SubscriptionReleased) return (false, "cleanup_unverified");
        if (report.Payload == null || !InitialBatterySnapshot.IsValid(report.Payload))
            return (false, report.Status);
        if (!LegacyCanProbe(before)) return (false, "baseline_not_125");
        if (!LegacyFinite(after) || after.Fd2Hz < Math.Max(128, before.Fd2Hz * 0.94) ||
            after.ParsedHz < Math.Max(128, before.ParsedHz * 0.94) ||
            after.P50Ms > 8.5 || after.P95Ms > Math.Max(15, before.P95Ms + 3))
            return (false, "fd2_rate_or_cadence_changed");
        if (!double.IsFinite(operationGapMs) || operationGapMs < 0 ||
            operationGapMs > 45 || after.MaxGapMs > 120)
            return (false, "notify_gap_exceeded");
        return (true, "updated");
    }

    private static bool LegacyIsFresh(bool runtime, double ageSeconds) =>
        runtime && double.IsFinite(ageSeconds) && ageSeconds >= 0 && ageSeconds <= 720;

    private static bool LegacyCanRetry(BatteryRefreshDecision decision, BatteryRefreshRate after,
        bool sameSession, bool cleanupVerified, int retriesUsed) =>
        !decision.Accepted && decision.Reason == "notify_gap_exceeded" &&
        sameSession && cleanupVerified && retriesUsed >= 0 && retriesUsed < 1 &&
        LegacyCanProbe(after) && after.MaxGapMs <= 120;

    private static bool LegacyHealthy(double continuousHealthySeconds, bool enabled,
        bool sameSession, bool recentHighRate) =>
        enabled && sameSession && recentHighRate && double.IsFinite(continuousHealthySeconds) &&
        continuousHealthySeconds >= 600;

    // ---------------- 展示策略与通用基础设施 ----------------

    // ---------------- BLE 预设：最终复核的保留规则 ----------------

    private static BleAdaptiveMeasurement Window(double fd2Hz, double parsedHz, double durationSeconds = 10,
        double? p95GapMs = null, double maxGapMs = 0, double ageSeconds = 10, double inputAgeMs = 10)
    {
        return new BleAdaptiveMeasurement(
            new MeasuredTrialSample(durationSeconds, (int)Math.Round(fd2Hz * durationSeconds),
                (int)Math.Round(parsedHz * durationSeconds), inputAgeMs, true, true, p95GapMs),
            ageSeconds, maxGapMs);
    }

    private static void FinalVerify(CheckFn check, ref int cases, ref int bad)
    {
        // 复核窗口略降但仍在 60Hz 以上、且明显优于原生：保留预设，不退回原生。
        BleAdaptiveMeasurement native = Window(60, 60), retained = Window(130, 130), final = Window(90, 90);
        BleAdaptivePresetDecision preserved = BleAdaptivePresetPolicy.VerifyFinal(native, retained, final);
        BleAdaptivePresetDecision strict = BleAdaptivePresetPolicy.VerifyFinal(retained, final);
        check("policies", "final verify keeps playable preset",
            preserved.Retain && preserved.Code == "final_playable_preserved" && !strict.Retain,
            ref cases, ref bad);

        // 同一次复核，若原生本来就比最终窗口快，仍应放弃预设。
        check("policies", "final verify prefers faster native",
            !BleAdaptivePresetPolicy.VerifyFinal(Window(100, 100), retained, final).Retain, ref cases, ref bad);

        // 低于可游玩线：不保留。
        check("policies", "final verify rejects unplayable",
            !BleAdaptivePresetPolicy.VerifyFinal(native, retained, Window(55, 55)).Retain, ref cases, ref bad);

        // 已达到 125Hz 标准：走两参版本的原有结论，不需要兜底。
        BleAdaptivePresetDecision acceptable = BleAdaptivePresetPolicy.VerifyFinal(native, retained, Window(125, 125));
        check("policies", "final verify accepts standard tier",
            acceptable.Retain && acceptable.Code == "final_target_verified", ref cases, ref bad);

        // 通知间隙明显恶化：即使频率仍可游玩也不保留。
        check("policies", "final verify rejects gap regression",
            !BleAdaptivePresetPolicy.VerifyFinal(native, Window(130, 130, p95GapMs: 8),
                Window(90, 90, p95GapMs: 40)).Retain, ref cases, ref bad);

        // 窗口时长不匹配、窗口过期、输入过期：都不构成保留理由。
        check("policies", "final verify rejects window mismatch",
            !BleAdaptivePresetPolicy.VerifyFinal(native, retained, Window(90, 90, durationSeconds: 12)).Retain,
            ref cases, ref bad);
        check("policies", "final verify rejects stale window",
            !BleAdaptivePresetPolicy.VerifyFinal(native, retained, Window(90, 90, ageSeconds: 20)).Retain,
            ref cases, ref bad);
        check("policies", "final verify rejects stale input",
            !BleAdaptivePresetPolicy.VerifyFinal(native, retained, Window(90, 90, inputAgeMs: 600)).Retain,
            ref cases, ref bad);

        // 扫描一遍：只有「可游玩 + 优于原生 + 无间隙恶化」才可能由兜底保留。
        int kept = 0;
        for (double finalHz = 30; finalHz <= 200; finalHz += 5)
        {
            BleAdaptivePresetDecision decision = BleAdaptivePresetPolicy.VerifyFinal(native, retained, Window(finalHz, finalHz));
            bool keep = decision.Retain;
            bool expected = finalHz >= BleAdaptivePresetPolicy.PlayableHz &&
                finalHz >= native.EffectiveHz + BleAdaptivePresetPolicy.RateNoiseToleranceHz;
            check("policies", "final verify sweep " + finalHz, keep == expected, ref cases, ref bad);
            if (keep && decision.Code == "final_playable_preserved") kept++;
        }

        check("policies", "final verify fallback used", kept > 0, ref cases, ref bad);
    }

    private static void Presentation(Random random, CheckFn check, ref int cases, ref int bad)
    {
        bool[] flags = [false, true];

        foreach (ConnectionPhase phase in Enum.GetValues<ConnectionPhase>())
        {
            foreach (bool connected in flags)
            foreach (bool running in flags)
            foreach (bool busy in flags)
            foreach (bool reconnecting in flags)
            foreach (bool ready in flags)
            {
                ConnectionPhasePresentation a = ConnectionPhasePresentation.Create(
                    phase, connected, running, busy, reconnecting, ready);
                string actual = a.Badge + "|" + a.Title + "|" + a.Hint;
                check("presentation", "phase " + phase,
                    actual == LegacyPhasePresentation((int)phase, connected, running, busy, reconnecting, ready),
                    ref cases, ref bad);
            }
        }
        foreach (bool strict in flags)
        foreach (bool live in flags)
        foreach (bool accepted in flags)
        foreach (bool ready in flags)
        {
            check("presentation", "can start",
                ConnectionPhasePresentation.CanStartVirtual(strict, live, accepted, ready) ==
                (!strict || live && (accepted || ready)), ref cases, ref bad);
        }

        foreach (ConnectionRoute route in Enum.GetValues<ConnectionRoute>())
        {
            var plan = new ConnectionPlan(route, "标题_" + route, "说明_" + route,
                route == ConnectionRoute.Windows11PublicApi, false,
                route is ConnectionRoute.Win10AuditedStack or ConnectionRoute.Win10ExperimentalStack);
            check("presentation", "turbo experimental " + route,
                TurboConsentText.IsExperimental(plan) ==
                (route == ConnectionRoute.Win10ExperimentalStack), ref cases, ref bad);
            foreach (BlePresetDefinition preset in BlePresetCatalog.Presets)
            {
                check("presentation", "turbo note " + route + preset.Id,
                    TurboConsentText.SafetyNote(plan) == LegacySafetyNote(plan) &&
                    TurboConsentText.Confirmation(plan, preset) == LegacyConfirmation(plan, preset),
                    ref cases, ref bad);
                foreach (bool restore in flags)
                foreach (bool consent in flags)
                {
                    string a = Capture(() => string.Join("|", TurboConsentText.Arguments(restore, plan, preset, consent)));
                    string b = Capture(() => string.Join("|", LegacyTurboArguments(restore, plan, preset, consent)));
                    check("presentation", "turbo args " + route + preset.Id + restore + consent,
                        a == b, ref cases, ref bad);
                }
            }
        }
        foreach (bool windows in flags)
        foreach (int build in new[] { 0, 10239, 10240, 19041, 21999, 22000, 99999 })
        {
            check("presentation", "home action",
                TurboConsentText.ShowHomeAction(windows, build) ==
                (windows && build >= 10240 && build < 22000), ref cases, ref bad);
        }

        for (int i = 0; i < 900; i++)
        {
            GamepadState state = RandomPolicyState(random);
            bool connected = random.Next(2) == 0, hasLevel = random.Next(2) == 0, fresh = random.Next(2) == 0;
            string caption = random.Next(3) == 0 ? "自定义档位" : "连接时档位";
            BatteryLevelVisual a = BatteryLevelVisual.Create(state, connected, hasLevel, fresh, caption);
            check("presentation", "battery visual " + i,
                ActualBatteryVisual(a) == LegacyBatteryVisual(state, connected, hasLevel, fresh, caption),
                ref cases, ref bad);
        }

        for (int i = 0; i < 900; i++)
        {
            int index = random.Next(1, 6);
            var slots = new List<SlotAddressInfo>();
            for (int k = 0; k < random.Next(0, 7); k++)
            {
                slots.Add(new SlotAddressInfo(random.Next(1, 6), random.Next(2) == 0,
                    RandomAddress(random), RandomAddress(random), random.Next(2) == 0));
            }
            string a = string.Join(";", SlotAddressPolicy.Exclusions(index, slots)
                .OrderBy(x => x, StringComparer.Ordinal));
            check("presentation", "exclusions " + i, a == LegacyExclusions(index, slots), ref cases, ref bad);
        }
        for (int i = 0; i < 900; i++)
        {
            PeerRateSample[] before = RandomPeers(random);
            PeerRateSample[] now = RandomPeers(random);
            check("presentation", "peer healthy " + i,
                PeerRateGuard.Healthy(before, now) == LegacyPeerHealthy(before, now), ref cases, ref bad);
        }

        string[] keys = [null!, "", "pro2", "pro1", "xbox", "dualsense", "keymouse",
            "PS5", "Xbox", "unknown", "PRO2", "dual_sense"];
        foreach (string key in keys)
        {
            check("presentation", "mode from key " + key,
                SlotModeCatalog.FromKey(key).Key == LegacyFromModeKey(key), ref cases, ref bad);
        }
        foreach (ViiperVirtualMode mode in Enum.GetValues<ViiperVirtualMode>())
        {
            check("presentation", "mode from enum " + mode,
                SlotModeCatalog.FromMode(mode).Key == LegacyFromModeEnum(mode), ref cases, ref bad);
        }
        check("presentation", "mode from invalid enum",
            SlotModeCatalog.FromMode((ViiperVirtualMode)999).Key == LegacyFromModeEnum((ViiperVirtualMode)999),
            ref cases, ref bad);
        for (int i = 0; i < 700; i++)
        {
            string[]? saved = random.Next(5) == 0 ? null : Enumerable.Range(0, random.Next(0, 6))
                .Select(_ => keys[random.Next(keys.Length)]).ToArray();
            string primary = keys[random.Next(keys.Length)];
            check("presentation", "mode normalize " + i,
                string.Join(",", SlotModeCatalog.Normalize(saved, primary)) ==
                string.Join(",", LegacyNormalize(saved, primary)), ref cases, ref bad);
        }
        foreach (ViiperVirtualMode mode in new[] { ViiperVirtualMode.Pro2, ViiperVirtualMode.Pro1,
            ViiperVirtualMode.Xbox, ViiperVirtualMode.DualSenseLike, ViiperVirtualMode.KeyMouse })
        {
            foreach (int slot in new[] { 1, 2, 3, 4 })
            foreach (OutputRateOption cadence in OutputRateOption.All)
            {
                check("presentation", "mode profile " + mode + slot,
                    ProfileKey(SlotModeCatalog.Profile(mode, slot, cadence)) ==
                    LegacyProfileKey(mode, slot, cadence), ref cases, ref bad);
            }
            foreach (int invalid in new[] { 0, 5, -1 })
            {
                check("presentation", "mode profile invalid " + invalid,
                    Capture(() => ProfileKey(SlotModeCatalog.Profile(mode, invalid, OutputRateOption.Default))) ==
                    Capture(() => LegacyProfileKey(mode, invalid, OutputRateOption.Default)), ref cases, ref bad);
            }
        }
        for (int scenario = 0; scenario < 7; scenario++)
        {
            ViiperVirtualMode? previous = scenario is 2 or 4 ? null : ViiperVirtualMode.Pro1;
            using var cts = new CancellationTokenSource();
            if (scenario == 6) cts.Cancel();
            string a = Capture(() => SlotModeSwitch.ReplaceAsync(previous, ViiperVirtualMode.Xbox,
                () => Task.CompletedTask,
                (m, t) => StartResult(scenario, m), cts.Token).GetAwaiter().GetResult().ToString());
            string b = Capture(() => LegacyReplaceAsync(previous, ViiperVirtualMode.Xbox,
                () => Task.CompletedTask,
                (m, t) => StartResult(scenario, m), cts.Token).GetAwaiter().GetResult().ToString());
            check("presentation", "mode switch " + scenario, a == b, ref cases, ref bad);
        }

        string[] argPool = ["--gyro-quality-test", "--gyro-quality-off", "--haptics-quality-test",
            "--haptics-quality-off", "other", "", "GYRO-QUALITY-TEST", "--SOFTWARE-RENDERING"];
        for (int i = 0; i < 900; i++)
        {
            string[] args = Enumerable.Range(0, random.Next(0, 6))
                .Select(_ => argPool[random.Next(argPool.Length)]).ToArray();
            string enable = argPool[random.Next(argPool.Length)];
            string disable = argPool[random.Next(argPool.Length)];
            bool defaultOn = random.Next(2) == 0;
            check("presentation", "quality flags " + i,
                QualityFeatureFlags.IsEnabled(args, enable) == LegacyIsEnabled(args, enable) &&
                QualityFeatureFlags.ResolveEnabled(args, enable, disable, defaultOn) ==
                (!LegacyIsEnabled(args, disable) && (defaultOn || LegacyIsEnabled(args, enable))),
                ref cases, ref bad);
        }

        for (int i = 0; i < 1400; i++)
        {
            byte[] data = new byte[random.Next(0, 24)];
            random.NextBytes(data);
            if (data.Length >= 16 && random.Next(2) == 0)
            {
                data[0] = 0x10; data[1] = 1; data[2] = 1; data[3] = 1; data[5] = 0x78;
            }
            bool oa = FirmwareInfo.TryDecode(data, out FirmwareInfo? fa);
            bool ob = LegacyTryFirmware(data, out string fb);
            check("presentation", "firmware " + i,
                oa == ob && (!oa || FirmwareKey(fa!) == fb), ref cases, ref bad);
        }
        for (int i = 0; i < 900; i++)
        {
            GamepadState state = RandomPolicyState(random);
            state.HeadsetPresenceKnown = random.Next(2) == 0;
            state.HeadsetPresent = random.Next(2) == 0;
            bool connected = random.Next(2) == 0, fresh = random.Next(2) == 0;
            var info = new FirmwareInfo("1.2.3", 4, "未知", null);
            check("presentation", "device presentation " + i,
                DeviceStatusPresentation.Firmware(info, connected) == LegacyFirmwareText(info, connected) &&
                DeviceStatusPresentation.Firmware(null, connected) == LegacyFirmwareText(null, connected) &&
                DeviceStatusPresentation.Headset(state, fresh, connected) ==
                LegacyHeadsetText(state, fresh, connected), ref cases, ref bad);
        }

        for (int i = 0; i < 1200; i++)
        {
            ulong address = random.Next(2) == 0
                ? (ulong)random.NextInt64(0, 0x1000000000000L)
                : (ulong)random.NextInt64();
            BluetoothAddressType type = (BluetoothAddressType)random.Next(0, 3);
            var candidate = new ManualDeviceCandidate(address, type,
                RandomText(random, "Ab c\t\n中文XYZ0123456789", random.Next(0, 120)),
                (short)random.Next(-100, 0), random.Next(2) == 0, random.Next(-10, 100));
            check("presentation", "manual candidate " + i,
                CandidateKey(candidate) == LegacyCandidateKey(candidate), ref cases, ref bad);
        }
        for (int i = 0; i < 250; i++)
        {
            var list = new List<ManualDeviceCandidate>();
            for (int k = 0; k < random.Next(0, 9); k++)
            {
                list.Add(new ManualDeviceCandidate((ulong)random.NextInt64(0, 0x1000000000000L),
                    (BluetoothAddressType)random.Next(0, 3), "dev" + k, (short)random.Next(-90, -10),
                    random.Next(2) == 0, random.Next(-5, 100)));
            }
            check("presentation", "manual sort " + i,
                string.Join(";", ManualDeviceCandidate.Sort(list).Select(CandidateKey)) ==
                string.Join(";", LegacySort(list).Select(CandidateKey)), ref cases, ref bad);
        }

        string[][] argSets =
        [
            [], ["--software-rendering"], ["--SOFTWARE-RENDERING"], ["other"],
            ["other", "--software-rendering"], ["", "--Software-Rendering"], ["--software-rendering-extra"]
        ];
        foreach (bool compat in flags)
        foreach (string[] args in argSets)
        {
            check("presentation", "rendering select",
                RenderingStartupPolicy.SelectSoftwareRendering(compat, args) ==
                LegacySelectRendering(compat, args), ref cases, ref bad);
        }
        check("presentation", "software constants",
            SoftwareOnlyMode.MinimumAcceptedHz == 125.0 &&
            SoftwareOnlyMode.MaximumRepeatedStateRatio == 0.01 &&
            SoftwareOnlyMode.IsReleaseBuild == LegacyReleaseBuild() &&
            SoftwareOnlyMode.IsEnabled == LegacySoftwareEnabled(), ref cases, ref bad);

        foreach (bool allow in flags)
        foreach (bool refresh in flags)
        {
            var eventsA = new List<string>();
            var eventsB = new List<string>();
            var a = new ActionCommand(_ => { eventsA.Add("run"); return Task.CompletedTask; }, _ => allow);
            var b = new LegacyActionCommand(_ => { eventsB.Add("run"); return Task.CompletedTask; }, _ => allow);
            a.CanExecuteChanged += (_, _) => eventsA.Add("changed");
            b.CanExecuteChanged += (_, _) => eventsB.Add("changed");
            bool beforeA = a.CanExecute(null), beforeB = b.CanExecute(null);
            a.Execute(null);
            b.Execute(null);
            if (refresh) { a.RefreshCanExecute(); b.RefreshCanExecute(); }
            check("presentation", "action command " + allow + refresh,
                beforeA == beforeB && a.CanExecute(null) == b.CanExecute(null) &&
                string.Join(",", eventsA) == string.Join(",", eventsB), ref cases, ref bad);
        }
        var signalA = new FrameSignal();
        var signalB = new LegacyFrameSignal();
        string sa = Capture(() =>
        {
            signalA.Notify(); signalA.Notify(); signalA.Notify();
            return signalA.WaitAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult() + "|" +
                signalA.WaitAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
        });
        string sb = Capture(() =>
        {
            signalB.Notify(); signalB.Notify(); signalB.Notify();
            return signalB.WaitAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult() + "|" +
                signalB.WaitAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
        });
        check("presentation", "frame signal", sa == sb, ref cases, ref bad);
    }

    private static string LegacyPhasePresentation(int phase, bool connected, bool running,
        bool busy, bool reconnecting, bool ready)
    {
        const int Scanning = 1, Connecting = 2, Initializing = 3,
            Measuring = 4, Ready = 5, RateLimited = 6, Disconnected = 7;
        string Text(string b, string t, string h) => b + "|" + t + "|" + h;
        if (!connected && phase is Ready or RateLimited or Measuring or Initializing)
            return Text("连接已断开", "手柄连接已断开", "物理连接已中断，不能将旧状态显示为已连接。");
        if (phase == RateLimited)
            return Text(ready ? "可用 · 建议优化" : "已连接 · 速率偏低",
                running ? "可以游玩，建议优化蓝牙" : ready ? "手柄可用，正在准备游戏" : "输入速率偏低，等待恢复",
                running ? "按真实输入速率转发，不补帧。下方可查看低速原因线索与优化建议。"
                    : ready ? "已达到兼容游玩速率，无需等待 125Hz；仍建议优化蓝牙，降低输入间隔。"
                    : "BLE 已连接，但输入尚未达到 60Hz 兼容门槛，游戏输出暂不启动；恢复后自动继续。");
        if (phase == Ready)
            return running ? Text("游戏就绪", "已连接，开始游戏吧", "切换兼容模式无需重新配对手柄。")
                : Text("手柄已连接", "手柄已连接", "输入验收已通过，正在准备所选游戏模式。");
        if (phase == Measuring)
            return Text("已连接 · 正在测速", "蓝牙已连接，正在验证输入", "初始低速可能是蓝牙参数协商过程，并非最终结果。测速与优化通常需要数十秒，验收通过后自动进入游戏。");
        if (connected || phase == Initializing)
            return Text("蓝牙已连接 · 初始化", "蓝牙已连接", "正在初始化手柄和订阅 FD2 输入，请稍候。");
        if (phase == Connecting)
            return Text("正在建立连接", "已找到 Pro2，正在连接", "发现广播不等于已建链，正在等待 GATT 服务。");
        if (phase == Scanning || reconnecting)
            return Text("正在搜索", "正在寻找你的 Pro2", "请唤醒手柄并开启配对广播。");
        if (phase == Disconnected)
            return Text("连接已断开", "手柄连接已断开", "系统报告连接断开；开启自动重连后会继续寻找。");
        return busy ? Text("处理中", "正在处理，请稍候", "正在处理当前操作。")
            : Text("待连接", "连接 Pro2 手柄", "手柄切到 PRO2 模式并开启配对，然后点击下方连接。");
    }

    private static bool LegacyExperimental(ConnectionPlan plan) =>
        plan.Route == ConnectionRoute.Win10ExperimentalStack;

    private static string LegacySafetyNote(ConnectionPlan plan) => LegacyExperimental(plan)
        ? "此 Win10 系统栈尚无实机生效证据；可备份后授权试验，不因新设备或哈希不同而直接拒绝。全局设置会影响其他 BLE 连接，是否提速以真实输入为准。"
        : plan.Route == ConnectionRoute.Win10AuditedStack
            ? "此系统栈已完成参数读取机制审查；不同蓝牙设备仍须实测。全局设置影响其他 BLE 连接，已配置不等于达到 125Hz。"
            : plan.Guidance;

    private static string LegacyConfirmation(ConnectionPlan plan, BlePresetDefinition preset) =>
        "目标：" + preset.Title + "。" + (LegacyExperimental(plan) ? "当前系统尚未验证，属于参数试验。" : "") +
        "\n\n将备份并修改系统全局 BLE 参数，暂时断开当前手柄，其他蓝牙设备也可能断开；无法安全重启蓝牙时，会提示正常重启 Windows。" +
        "\n\n完成后重新连接实测，不保证 125Hz。可在首页恢复原参数，不更换驱动。继续？";

    private static IReadOnlyList<string> LegacyTurboArguments(bool restore, ConnectionPlan plan,
        BlePresetDefinition preset, bool experimentalConsent)
    {
        if (restore) return new[] { BleStackPresetTool.StackRestoreArgument };
        if (!plan.CanConfigureWin10Stack)
            throw new InvalidOperationException(plan.Title + "：" + plan.Guidance);
        if (LegacyExperimental(plan) && !experimentalConsent)
            throw new InvalidOperationException("此系统栈须在确认框明确授权可恢复试验；当前连接未停止，未修改参数。");
        BlePresetDefinition normalized = BlePresetCatalog.Get(preset.Preset);
        var args = new List<string> { BleStackPresetTool.StackApplyArgument,
            BlePresetCatalog.SelectorPrefix + normalized.Id };
        if (LegacyExperimental(plan)) args.Add(BleStackProfile.ExperimentalArgument);
        return args;
    }

    private static string BrushText(string hex) =>
        ((Color)ColorConverter.ConvertFromString(hex)).ToString();

    private static string ActualBatteryVisual(BatteryLevelVisual v) =>
        v.Level + "|" + v.Caption + "|" + v.Charge + "|" +
        string.Join(",", v.Segments.Select(s =>
            s.Filled + ":" + ((SolidColorBrush)s.Fill).Color.ToString()));

    private static string LegacyBatteryVisual(GamepadState state, bool connected, bool hasLevel,
        bool freshPower, string levelCaption)
    {
        bool known = connected && hasLevel && state.BatteryLevel <= 9;
        int level = known ? state.BatteryLevel : 0;
        string caption = !connected ? "连接后读取" :
            (known ? levelCaption : "档位未知") +
            (state.BatteryVoltageMillivolts > 0
                ? " · " + (state.BatteryVoltageMillivolts / 1000.0).ToString("F2") + " V" : "");
        string charge = connected && freshPower && state.BatteryPowerKnown
            ? state.BatteryCharging ? "充电中" : state.BatteryExternalPower ? "外接电源" : "" : "";
        string segments = string.Join(",", Enumerable.Range(0, 9).Select(i =>
            (known && i < level) + ":" +
            (known && i < level ? level <= 2 ? BrushText("#AD751D") : BrushText("#40866A")
                : BrushText("#E5EBE7"))));
        return (known ? level + " / 9" : "未知") + "|" + caption + "|" + charge + "|" + segments;
    }

    private static string RandomAddress(Random random)
    {
        if (random.Next(6) == 0) return random.Next(2) == 0 ? "" : "   ";
        return string.Join(":", Enumerable.Range(0, 6)
            .Select(_ => random.Next(0, 256).ToString(random.Next(2) == 0 ? "X2" : "x2")));
    }

    private static string LegacyExclusions(int index, IEnumerable<SlotAddressInfo> slots)
    {
        SlotAddressInfo[] all = slots.ToArray();
        string own = all.FirstOrDefault(s => s.Index == index)?.SavedAddress ?? "";
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SlotAddressInfo slot in all.Where(s => s.Index != index))
        {
            if (slot.Occupied && !string.IsNullOrWhiteSpace(slot.ConnectedAddress))
                excluded.Add(slot.ConnectedAddress);
            if (slot.Enabled && !string.IsNullOrWhiteSpace(slot.SavedAddress) &&
                (!slot.SavedAddress.Equals(own, StringComparison.OrdinalIgnoreCase) || slot.Index < index))
                excluded.Add(slot.SavedAddress);
        }
        return string.Join(";", excluded.OrderBy(x => x, StringComparer.Ordinal));
    }

    private static PeerRateSample[] RandomPeers(Random random)
    {
        var peers = new PeerRateSample[random.Next(0, 7)];
        for (int i = 0; i < peers.Length; i++)
        {
            double hz = random.Next(5) == 0
                ? new[] { double.NaN, double.PositiveInfinity, -1d, 0d }[random.Next(4)]
                : random.NextDouble() * 200;
            peers[i] = new PeerRateSample(random.Next(1, 6), "DEV-" + random.Next(0, 4),
                random.Next(2) == 0, random.Next(2) == 0, hz);
        }
        return peers;
    }

    private static bool LegacyPeerHealthy(IReadOnlyList<PeerRateSample> before, IReadOnlyList<PeerRateSample> now)
    {
        foreach (PeerRateSample peer in before.Where(p =>
            p.Connected && p.Fresh && double.IsFinite(p.Hz) && p.Hz > 0))
        {
            PeerRateSample? after = now.FirstOrDefault(p => p.Slot == peer.Slot &&
                p.Address.Equals(peer.Address, StringComparison.OrdinalIgnoreCase));
            if (after == null || !after.Connected || !after.Fresh || !double.IsFinite(after.Hz) ||
                after.Hz < peer.Hz - Math.Max(8, peer.Hz * .2)) return false;
        }
        return true;
    }

    private static string LegacyFromModeKey(string? key) =>
        SlotModeCatalog.Options.FirstOrDefault(o => o.Key == AppSettings.NormalizeModeKey(key))?.Key
            ?? SlotModeCatalog.Options[0].Key;

    private static string LegacyFromModeEnum(ViiperVirtualMode mode) =>
        SlotModeCatalog.Options.FirstOrDefault(o => o.Mode == mode)?.Key ?? SlotModeCatalog.Options[0].Key;

    private static string[] LegacyNormalize(string[]? saved, string primaryKey)
    {
        string fallback = LegacyFromModeKey(primaryKey);
        return Enumerable.Range(0, 4).Select(i => saved != null && i < saved.Length &&
            !string.IsNullOrWhiteSpace(saved[i]) ? LegacyFromModeKey(saved[i]) : fallback).ToArray();
    }

    private static string ProfileKey(ViiperDeviceProfile p) =>
        $"{p.Mode}|{p.Label}|{p.DeviceSpecificSerialNumber}|{p.SendInterval}|{p.SourcePaced}";

    private static string LegacyProfileKey(ViiperVirtualMode mode, int slot, OutputRateOption cadence)
    {
        if (slot is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(slot));
        ViiperDeviceProfile profile = mode switch
        {
            ViiperVirtualMode.Pro2 => ViiperDeviceProfile.Pro2,
            ViiperVirtualMode.Pro1 => ViiperDeviceProfile.Pro1,
            ViiperVirtualMode.Xbox => ViiperDeviceProfile.Xbox,
            ViiperVirtualMode.DualSenseLike => ViiperDeviceProfile.DualSenseLike,
            ViiperVirtualMode.KeyMouse => ViiperDeviceProfile.KeyMouse,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return ProfileKey(profile with
        {
            Label = profile.Label + " Slot " + slot,
            DeviceSpecificSerialNumber = ViiperDeviceProfile.SlotSerialNumber(mode, slot),
            SendInterval = cadence.Interval,
            SourcePaced = cadence.SourcePaced
        });
    }

    private static Task<bool> StartResult(int scenario, ViiperVirtualMode mode)
    {
        if (scenario == 3 && mode == ViiperVirtualMode.Xbox) throw new InvalidOperationException("create failed");
        return Task.FromResult(scenario switch
        {
            0 => true,
            1 or 3 => mode != ViiperVirtualMode.Xbox,
            2 or 4 or 5 => false,
            _ => true,
        });
    }

    private static async Task<SlotModeChangeResult> LegacyReplaceAsync(ViiperVirtualMode? previous,
        ViiperVirtualMode requested, Func<Task> stopSlot,
        Func<ViiperVirtualMode, CancellationToken, Task<bool>> startSlot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await stopSlot();
        try
        {
            if (await startSlot(requested, token)) return SlotModeChangeResult.Changed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }
        token.ThrowIfCancellationRequested();
        if (previous.HasValue && await startSlot(previous.Value, token)) return SlotModeChangeResult.RestoredPrevious;
        return SlotModeChangeResult.OutputStopped;
    }

    private static bool LegacyIsEnabled(string[] arguments, string argument) =>
        arguments.Contains(argument, StringComparer.OrdinalIgnoreCase);

    private static string FirmwareKey(FirmwareInfo info) =>
        $"{info.ControllerVersion}|{info.ControllerType}|{info.BluetoothPatchVersion}|{info.DspVersion}";

    private static bool LegacyTryFirmware(byte[] data, out string key)
    {
        key = "";
        if (data.Length < 16 || data[0] != 0x10 || data[1] != 1 || data[2] != 1 ||
            data[3] != 1 || data[5] != 0x78 || !data.AsSpan(8, 3).ContainsAnyExcept((byte)0xFF)) return false;
        static string Version(ReadOnlySpan<byte> b) => $"{b[0]}.{b[1]}.{b[2]}";
        string patch = data.AsSpan(12, 3).ContainsAnyExcept((byte)0xFF) ? Version(data.AsSpan(12, 3)) : "未知";
        string? dsp = data.Length >= 19 && data.AsSpan(16, 3).ContainsAnyExcept((byte)0xFF)
            ? Version(data.AsSpan(16, 3)) : null;
        key = $"{Version(data.AsSpan(8, 3))}|{data[11]}|{patch}|{dsp}";
        return true;
    }

    private static string LegacyFirmwareText(FirmwareInfo? info, bool connected) =>
        !connected ? "未连接" : info?.ControllerVersion ?? "未知";

    private static string LegacyHeadsetText(GamepadState state, bool fresh, bool connected) =>
        !connected ? "未连接" : !fresh || !state.HeadsetPresenceKnown ? "未知" :
            state.HeadsetPresent ? "已插入" : "未插入";

    private static string CandidateKey(ManualDeviceCandidate c) =>
        c.AddressText + "|" + c.DisplayName + "|" + c.AddressDetail + "|" + c.Confidence + "|" +
        c.Signal + "|" + c.Evidence + "|" + c.IsValid;

    private static string LegacyCandidateKey(ManualDeviceCandidate c)
    {
        string address = string.Join(":", Enumerable.Range(0, 6)
            .Select(i => ((c.Address >> ((5 - i) * 8)) & 255).ToString("X2")));
        string display = string.IsNullOrWhiteSpace(c.Name) ? "未提供设备名称" :
            new string(c.Name.Where(ch => !char.IsControl(ch)).Take(80).ToArray());
        string detail = address + (c.AddressType == BluetoothAddressType.Random ? " · 随机地址" : " · 公共地址");
        string confidence = c.Score >= 60 ? "高匹配" : c.Score >= 30 ? "可能是 Pro2" : "低匹配";
        string signal = c.Rssi + " dBm";
        string evidence = c.NintendoManufacturer ? "Nintendo 广播标识" :
            c.Score >= 30 ? "设备名称匹配" : "未匹配 Pro2 标识";
        bool valid = c.Address > 0 && c.Address <= 0xFFFFFFFFFFFF &&
            c.AddressType is BluetoothAddressType.Public or BluetoothAddressType.Random;
        return address + "|" + display + "|" + detail + "|" + confidence + "|" + signal + "|" + evidence + "|" + valid;
    }

    private static IReadOnlyList<ManualDeviceCandidate> LegacySort(IEnumerable<ManualDeviceCandidate> candidates) =>
        candidates.Where(c => c.IsValid).OrderByDescending(c => c.Score).ThenByDescending(c => c.Rssi)
            .ThenBy(c => c.Address).ThenBy(c => c.AddressType).ToArray();

    private static bool LegacySelectRendering(bool compatibilityBuild, IEnumerable<string> arguments)
    {
        if (compatibilityBuild) return true;
        foreach (string argument in arguments)
        {
            if (string.Equals(argument, "--software-rendering", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool LegacyReleaseBuild()
    {
#if PURE_SOFTWARE_RELEASE
        return true;
#else
        return false;
#endif
    }

    private static bool LegacySoftwareEnabled()
    {
        string? value = Environment.GetEnvironmentVariable("PRO2_PURE_SOFTWARE_133");
        bool truthy = value != null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("on", StringComparison.OrdinalIgnoreCase));
        return LegacyReleaseBuild() || truthy;
    }

    private sealed class LegacyActionCommand
    {
        private readonly Func<object?, Task> execute;
        private readonly Predicate<object?>? canExecute;
        private bool running;
        public LegacyActionCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }
        public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke(parameter) ?? true);
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            try
            {
                running = true;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                await execute(parameter);
            }
            finally
            {
                running = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler? CanExecuteChanged;
        public void RefreshCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class LegacyFrameSignal
    {
        private readonly SemaphoreSlim pending = new(0, 1);
        private readonly object gate = new();
        public void Notify()
        {
            lock (gate) { if (pending.CurrentCount == 0) pending.Release(); }
        }
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token) => pending.WaitAsync(timeout, token);
    }

    // ---------------- 连接策略与参数判定 ----------------

    private static void RoutePolicies(Random random, CheckFn check, ref int cases, ref int bad)
    {
        bool[] flags = [false, true];
        int[] builds = [0, 19040, 19041, 19999, 21999, 22000, 26200, 99999];
        string[] hashes = ["unavailable", BleStackProfile.AuditedStackSha256,
            BleStackProfile.AuditedStackSha256.ToLowerInvariant(), "deadbeef", ""];
        foreach (bool windows in flags)
        foreach (int build in builds)
        foreach (bool requestApi in flags)
        foreach (bool readApi in flags)
        foreach (string hash in hashes)
        foreach (bool configured in flags)
        {
            var facts = new EnvironmentFacts(windows, build, requestApi, readApi, hash, configured);
            check("route", "select",
                RoutePlanKey(ConnectionRoutePolicy.Select(facts)) == LegacySelectRoute(facts), ref cases, ref bad);
        }
        for (int i = 0; i < 3000; i++)
        {
            MeasuredTrialSample before = RandomTrialSample(random);
            MeasuredTrialSample after = RandomTrialSample(random);
            check("route", "trial " + i,
                TrialKey(MeasuredTrialPolicy.Evaluate(before, after)) == LegacyEvaluateTrial(before, after),
                ref cases, ref bad);
        }
        foreach (bool win11 in flags)
        foreach (bool requestApi in flags)
        foreach (bool readApi in flags)
        foreach (bool connected in flags)
        foreach (bool busy in flags)
        foreach (bool measuring in flags)
        foreach (bool tried in flags)
        foreach (double hz in new[] { double.NaN, 0d, -1d, 60d, 124.9, 125d, 127.9, 200d })
        {
            check("route", "can try",
                MeasuredTrialPolicy.CanTryInApp(win11, requestApi, readApi, connected, busy, measuring, tried, hz) ==
                (win11 && requestApi && readApi && connected && !busy && !measuring && !tried &&
                 double.IsFinite(hz) && hz > 0 && hz < MeasuredTrialPolicy.TargetHz), ref cases, ref bad);
        }
        check("route", "trial target reached",
            MeasuredTrialPolicy.Evaluate(new MeasuredTrialSample(20, 2000, 2000, 1, true, true),
                new MeasuredTrialSample(20, 3000, 3000, 1, true, true)).Code == "target_reached", ref cases, ref bad);
        check("route", "trial measured improvement",
            MeasuredTrialPolicy.Evaluate(new MeasuredTrialSample(20, 2000, 2000, 1, true, true),
                new MeasuredTrialSample(20, 2200, 2200, 1, true, true)).Code == "measured_improvement", ref cases, ref bad);
        string validId = @"\\?\USB#VID_8087#PID_0026#92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2";
        string validPnp = @"USB\VID_8087\PID_0026";
        var validTarget = new RadioRestartTarget("Intel", validPnp, validId, "Intel", "22.250.0.2", true);
        check("route", "radio valid target",
            RadioRestartPolicy.IsExactRadioTarget(validTarget) == LegacyIsExactRadioTarget(validTarget) &&
            RadioRestartPolicy.IsExactRadioTarget(validTarget), ref cases, ref bad);
        for (int i = 0; i < 1200; i++)
        {
            string id = random.Next(6) switch
            {
                0 => validId,
                1 => validId.Replace("USB", "BTH"),
                2 => validId.Replace("USB", "SWD"),
                3 => validId.Replace("92383b0e", "aaaaaaaa"),
                4 => RandomText(random, @"\\?\USB#VID#PID#0123456789abcdef-", random.Next(0, 70)),
                _ => validId + "#extra",
            };
            string pnp = random.Next(5) switch
            {
                0 => validPnp,
                1 => validPnp.ToLowerInvariant(),
                2 => @"USB\VID_8087",
                3 => @"USB\VID_8087\PID_0026\EXTRA",
                _ => "USB\\VID*8087\\PID_0026",
            };
            var target = new RadioRestartTarget("name", pnp, id, "provider", "1.0", random.Next(2) == 0);
            check("route", "radio exact " + i,
                RadioRestartPolicy.IsExactRadioTarget(target) == LegacyIsExactRadioTarget(target) &&
                Capture(() => string.Join("|", RadioRestartPolicy.RestartArguments(target))) ==
                Capture(() => string.Join("|", LegacyRestartArguments(target))), ref cases, ref bad);
        }
        foreach (bool isDefault in flags)
        {
            var t = new RadioRestartTarget("R", validPnp, validId, "P", "V", isDefault);
            check("route", "radio display",
                t.DisplayName == "R" + (isDefault ? "（系统默认）" : "") &&
                t.DriverSummary == "P · V" && t.CanRestart == LegacyIsExactRadioTarget(t), ref cases, ref bad);
        }
        for (int i = 0; i < 700; i++)
        {
            byte[] bytes = new byte[random.Next(0, 260)];
            random.NextBytes(bytes);
            if (i % 7 == 0 && bytes.Length > 2) { bytes[0] = 0xFF; bytes[1] = 0xFE; }
            if (i % 11 == 0 && bytes.Length > 2) { bytes[0] = 0xFE; bytes[1] = 0xFF; }
            if (i % 13 == 0 && bytes.Length > 3) { bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF; }
            check("route", "radio decode " + i,
                Capture(() => RadioRestartPolicy.DecodeNativeOutput(bytes)) ==
                Capture(() => LegacyDecodeNativeOutput(bytes)), ref cases, ref bad);
        }
        check("route", "radio decode oversized",
            Capture(() => RadioRestartPolicy.DecodeNativeOutput(new byte[65537])) ==
            Capture(() => LegacyDecodeNativeOutput(new byte[65537])), ref cases, ref bad);
        foreach (bool verified in flags)
        foreach (int? exit in new int?[] { null, 0, 1, 3010 })
        foreach (bool present in flags)
        foreach (bool parameters in flags)
        foreach (bool driver in flags)
        {
            check("route", "radio completion",
                RadioRestartPolicy.CompletionMessage(exit, verified) == LegacyCompletionMessage(exit, verified) &&
                RadioRestartPolicy.IsVerifiedSuccess(exit ?? -1, present, parameters, driver) ==
                LegacyVerifiedSuccess(exit ?? -1, present, parameters, driver), ref cases, ref bad);
        }
        for (int i = 0; i < 200; i++)
        {
            SystemBluetoothSnapshot snapshot = RandomBluetoothSnapshot(random);
            check("route", "radio targets " + i,
                TargetsKey(RadioRestartPolicy.Targets(snapshot)) == LegacyTargetsKey(snapshot), ref cases, ref bad);
        }
        foreach (BlePreset preset in Enum.GetValues<BlePreset>())
        {
            check("route", "preset get enum",
                PresetKey(BlePresetCatalog.Get(preset)) == PresetKey(LegacyGetPreset(preset)), ref cases, ref bad);
        }
        check("route", "preset get invalid enum",
            Capture(() => PresetKey(BlePresetCatalog.Get((BlePreset)99))) ==
            Capture(() => PresetKey(LegacyGetPreset((BlePreset)99))), ref cases, ref bad);
        foreach (string id in new[] { "7p5", "7P5", "flexible", "FLEXIBLE", "15", "unknown", "", " 15 " })
        {
            check("route", "preset get id " + id,
                Capture(() => PresetKey(BlePresetCatalog.Get(id))) == Capture(() => PresetKey(LegacyGetPreset(id))),
                ref cases, ref bad);
        }
        for (int i = 0; i < 900; i++)
        {
            string[] args = Enumerable.Range(0, random.Next(0, 5))
                .Select(_ => CommandPool[random.Next(CommandPool.Length)]).ToArray();
            check("route", "preset parse " + i,
                Capture(() => PresetKey(BlePresetCatalog.ParseCommand(args))) ==
                Capture(() => PresetKey(LegacyParseCommand(args))), ref cases, ref bad);
            check("route", "stack consent " + i,
                Capture(() => BleStackProfile.ParseExperimentalConsent(args).ToString()) ==
                Capture(() => LegacyParseConsent(args).ToString()), ref cases, ref bad);
        }
        foreach (BlePresetDefinition definition in BlePresetCatalog.Presets)
        {
            string values = string.Join(";", definition.Values.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + p.Value));
            string expected = $"LEConnectionIntervalMax={definition.IntervalMaxUnits};" +
                $"LEConnectionIntervalMin={definition.IntervalMinUnits};" +
                $"LEConnectionLSTO={definition.TimeoutUnits};" +
                $"LEConnectionLatency={definition.Latency}";
            check("route", "preset values",
                values == expected &&
                definition.MinimumIntervalMs == definition.IntervalMinUnits * 1.25 &&
                definition.MaximumIntervalMs == definition.IntervalMaxUnits * 1.25, ref cases, ref bad);
        }
        foreach (int build in builds)
        foreach (string hash in hashes)
        {
            check("route", "stack audited",
                BleStackProfile.IsAudited(build, hash) == LegacyIsAudited(build, hash), ref cases, ref bad);
        }
        check("route", "stack constants",
            BleStackProfile.ExperimentalArgument == "--ble-turbo-stack-experimental" &&
            BleStackProfile.RegistrySubKey == @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters" &&
            BleStackProfile.Values.Count == 4, ref cases, ref bad);
        foreach (bool pure in flags)
        foreach (string phase in new[] { "fallback", "initial", "", "FALLBACK" })
        foreach (uint current in new uint[] { 0, 6, 12, 36 })
        foreach (uint min in new uint[] { 0, 6, 7, 12 })
        foreach (uint max in new uint[] { 0, 6, 12, 36 })
        foreach (uint latency in new uint[] { 0, 1 })
        foreach (double hz in new[] { double.NaN, 0d, 30d, 100d, 800d })
        {
            check("route", "preference decide",
                BlePreferencePolicy.Decide(pure, phase, current, min, max, latency, hz) ==
                LegacyDecide(pure, phase, current, min, max, latency, hz), ref cases, ref bad);
        }
        foreach (bool win11 in flags)
        foreach (bool pure in flags)
        {
            check("route", "preference observe",
                BlePreferencePolicy.NativeObservationMilliseconds(win11, pure) ==
                (win11 && pure ? 10000 : 1800), ref cases, ref bad);
        }

        foreach (bool windows in flags)
        foreach (int build in builds)
        foreach (bool requestApi in flags)
        foreach (bool readApi in flags)
        foreach (string hash in new[] { "unavailable", ValidatedConnectionProfile.ValidatedStackSha256, "deadbeef" })
        foreach (bool optIn in flags)
        {
            var facts = new EnvironmentFacts(windows, build, requestApi, readApi, hash, false);
            BluetoothAdapterDiagnosticInfo? adapter = random.Next(3) == 0 ? null : RandomAdapter(random);
            bool matches = ValidatedConnectionProfile.MatchesStack(facts) == LegacyMatchesStack(facts);
            string selected = Capture(() => ValidatedConnectionProfile.Select(facts, adapter, optIn).ToString());
            string expected = Capture(() => LegacySelect(facts, adapter, optIn).ToString());
            check("route", "validated profile", matches && selected == expected, ref cases, ref bad);
        }
        foreach (ParameterRoute route in Enum.GetValues<ParameterRoute>())
        {
            check("route", "allows custom",
                ValidatedConnectionProfile.AllowsCustomRequest(route) == LegacyAllowsCustomRequest(route),
                ref cases, ref bad);
        }
        for (int i = 0; i < 200; i++)
        {
            SystemBluetoothSnapshot snapshot = RandomBluetoothSnapshot(random);
            check("route", "single audited " + i,
                AdapterKey(ValidatedConnectionProfile.SingleAuditedAdapter(snapshot)) ==
                AdapterKey(LegacySingleAuditedAdapter(snapshot)), ref cases, ref bad);
        }
        for (int i = 0; i < 1200; i++)
        {
            string json = RandomConfigJson(random);
            string operation = "apply", preset = "7p5";
            DateTimeOffset requested = DateTimeOffset.UtcNow.AddMinutes(-5);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            check("route", "config evidence " + i,
                ConfigKey(ConfigOperationEvidence.Parse(json, operation, preset, requested, now)) ==
                LegacyParseConfig(json, operation, preset, requested, now), ref cases, ref bad);
        }
        check("route", "config evidence reversed time",
            ConfigKey(ConfigOperationEvidence.Parse("{}", "apply", "7p5",
                DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow)) ==
            "False|False|True|completion_unknown", ref cases, ref bad);
        string missingPath = Path.Combine(Path.GetTempPath(), "pro2-aux-smoke-missing-" + Guid.NewGuid().ToString("N") + ".json");
        string codeA = "", codeB = "";
        var environment = new BleAdaptiveEnvironmentKey(26200, "hash", "pnp", "fingerprint", "Intel", "1.0", "2026");
        object? loadedA = AdaptivePresetCache.Load(environment, out codeA, missingPath);
        check("route", "adaptive cache absent",
            loadedA == null && codeA == "cache_absent" &&
            AdaptivePresetCache.Load(null, out codeB, missingPath) == null &&
            codeB == "cache_environment_unavailable", ref cases, ref bad);
        check("route", "adaptive invalidate null",
            Capture(() => { AdaptivePresetCache.Invalidate(null, missingPath); return "ok"; }) == "ok:ok", ref cases, ref bad);

        for (int i = 0; i < 400; i++)
        {
            var facts = new EnvironmentFacts(random.Next(2) == 0, builds[random.Next(builds.Length)],
                random.Next(2) == 0, random.Next(2) == 0, "hash" + i, random.Next(2) == 0);
            ConnectionPlan plan = ConnectionRoutePolicy.Select(facts);
            string warning = RandomText(random, "warn\r\n abc", random.Next(0, 20));
            StackParameterAssessment? assessment = random.Next(2) == 0 ? null :
                new StackParameterAssessment(facts.Build, "path", "1.0", "hash",
                    default, new[] { "A", "B" }, true, "reason");
            var snapshot = new EnvironmentSnapshot(facts, plan, warning,
                random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0, assessment);
            check("route", "environment telemetry " + i,
                snapshot.Telemetry == LegacyTelemetry(snapshot), ref cases, ref bad);
        }
        using (TimerResolutionScope scope = TimerResolutionScope.Begin(false))
        {
            scope.Dispose();
            scope.Dispose();
            check("route", "timer scope disabled",
                !scope.IsActive && scope.Result == uint.MaxValue, ref cases, ref bad);
        }
        foreach (double due in new[] { double.NaN, double.NegativeInfinity, -1d, 0d, double.PositiveInfinity })
        {
            using var semaphore = new SemaphoreSlim(1, 1);
            string a = Capture(() =>
            {
                RumbleWakeCoordinator.WaitAsync(semaphore, due, null!, CancellationToken.None).GetAwaiter().GetResult();
                return semaphore.CurrentCount.ToString();
            });
            string b = Capture(() =>
            {
                using var legacySemaphore = new SemaphoreSlim(1, 1);
                LegacyWake(legacySemaphore, due);
                return legacySemaphore.CurrentCount.ToString();
            });
            check("route", "wake due " + due, a == b, ref cases, ref bad);
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            using var semaphore = new SemaphoreSlim(1, 1);
        check("route", "wake cancelled",
                Capture(() =>
                {
                    RumbleWakeCoordinator.WaitAsync(semaphore, 10, null!, cancelled.Token).GetAwaiter().GetResult();
                    return "x";
                }).StartsWith("throw:OperationCanceledException"), ref cases, ref bad);
        }
    }

    private static readonly string[] CommandPool =
    [
        "--ble-turbo-stack-apply", "--ble-turbo-stack-restore", "--ble-turbo-stack-experimental",
        "--ble-turbo-stack-preset=7p5", "--ble-turbo-stack-preset=flexible", "--ble-turbo-stack-preset=15",
        "--ble-turbo-stack-preset", "--BLE-TURBO-STACK-PRESET=7P5", "--ble-turbo-stack-experimental-extra",
        "other", "", "--ble-turbo-stack-preset=unknown"
    ];

    private static string RoutePlanKey(ConnectionPlan p) =>
        $"{p.Route}|{p.Title}|{p.Guidance}|{p.CanRequestPreferredParameters}|{p.CanReadConnectionParameters}|{p.CanConfigureWin10Stack}";

    private static string LegacySelectRoute(EnvironmentFacts f)
    {
        string Route(string route, string title, string guidance, bool a, bool b, bool c) =>
            route + "|" + title + "|" + guidance + "|" + a + "|" + b + "|" + c;
        const string Unsupported = "最低要求 Windows 10 2004（19041）x64。本程序不保证旧系统能够启动或达到 125Hz。";
        const string Win11Api = "启用自动优化时，先测原连接；低速则在同一连接内最多试 4 个预设并复核较好结果。自定义 7.5ms 参数是兼容尝试，可能被系统拒绝；无效则继续寻找可玩路线。记忆只调整精确环境的重测顺序，不把 66Hz 算作 125Hz、不写 Win10 全局参数。";
        const string Win11NoApi = "保留原生 BLE 连接并按真实 Notify 验收，不盲改注册表。低速时导出诊断，检查官方蓝牙驱动及系统组件。";
        const string AuditedConfigured = "已检测到固定 7.5ms 配置；普通启动不重复改参数或重启蓝牙。不同适配器仍以真实 Notify 验收。";
        const string AuditedPlain = "先连接并实测；速率不足时可明确授权启用可恢复的 Win10 参数。不会自动修改其他 BLE 设备的全局默认值。";
        const string Experimental = "未知蓝牙栈不等于不支持参数。可明确授权试验 7.5ms / 7.5–15ms / 15ms：先检查本机系统文件、备份四项全局值，再保存并核对重启结果。参数机制尚未审查，写入成功不等于生效；按真实 FD2 比较，支持恢复，不更换驱动。";
        if (!f.IsWindows || f.Build < 19041)
            return Route("Unsupported", "此系统不在支持范围", Unsupported, false, false, false);
        if (f.Build >= 22000)
            return f.RequestParametersApiAvailable
                ? Route("Windows11PublicApi", "Win11 · 分层连接策略", Win11Api, true, f.ReadParametersApiAvailable, false)
                : Route("NativeNotifyOnly", "Win11 · 公开 API 不可用", Win11NoApi, false, f.ReadParametersApiAvailable, false);
        if (LegacyIsAudited(f.Build, f.StackSha256))
            return Route("Win10AuditedStack", "Win10 · 已验证的系统栈",
                f.Win10StackValuesConfigured ? AuditedConfigured : AuditedPlain, false, false, true);
        return Route("Win10ExperimentalStack", "Win10 · 可恢复参数实验", Experimental, false, false, true);
    }

    private static MeasuredTrialSample RandomTrialSample(Random random)
    {
        double duration = random.Next(6) == 0
            ? new[] { double.NaN, 0d, -1d, 9.99, 10d }[random.Next(5)]
            : 10 + random.NextDouble() * 20;
        int fd2 = random.Next(6) == 0 ? random.Next(-5, 20) : random.Next(0, 4000);
        int parsed = random.Next(4) == 0 ? random.Next(-5, 20) : random.Next(0, Math.Max(1, fd2 + 1));
        double age = random.Next(6) == 0
            ? new[] { double.NaN, -1d, 500d, 1000d }[random.Next(4)]
            : random.NextDouble() * 600;
        double? p95 = random.Next(4) == 0 ? null : random.Next(4) == 0 ? double.NaN : random.NextDouble() * 30;
        return new MeasuredTrialSample(duration, fd2, parsed, age,
            random.Next(2) == 0, random.Next(2) == 0, p95);
    }

    private static string TrialKey(MeasuredTrialResult r) => $"{r.RetainRequest}|{r.Code}|{r.Summary}";

    private static string LegacyEvaluateTrial(MeasuredTrialSample before, MeasuredTrialSample after)
    {
        string Rollback(string code, string summary) => "False|" + code + "|" + summary;
        double Fd2(MeasuredTrialSample s) => double.IsFinite(s.DurationSeconds) && s.DurationSeconds > 0
            ? s.Fd2Count / s.DurationSeconds : double.NaN;
        double Parsed(MeasuredTrialSample s) => double.IsFinite(s.DurationSeconds) && s.DurationSeconds > 0
            ? s.ParsedCount / s.DurationSeconds : double.NaN;
        double Effective(MeasuredTrialSample s) => Math.Min(Fd2(s), Parsed(s));
        bool Valid(MeasuredTrialSample s) => double.IsFinite(s.DurationSeconds) && s.DurationSeconds > 0 &&
            s.Fd2Count >= 0 && s.ParsedCount >= 0 && s.ParsedCount <= s.Fd2Count &&
            (!s.P95GapMs.HasValue || double.IsFinite(s.P95GapMs.Value) && s.P95GapMs.Value >= 0);
        bool Fresh(double age) => double.IsFinite(age) && age >= 0 && age < 500;
        if (!after.RequestedSuccessfully)
            return Rollback("request_not_successful", "参数请求未确认成功，释放本次请求。");
        if (!before.Connected || !after.Connected)
            return Rollback("link_not_live", "测量窗口结束时连接未保持在线，释放本次请求。");
        if (!Valid(before) || !Valid(after))
            return Rollback("invalid_measurement", "测量时长、计数或间隔统计无效，不能据此保留请求。");
        if (before.DurationSeconds < 10 || after.DurationSeconds < 10)
            return Rollback("incomplete_window", "前后都需要至少 10 秒完整计数窗口，短时 API 成功不能作为改善证据。");
        double shorter = Math.Min(before.DurationSeconds, after.DurationSeconds);
        if (Math.Abs(before.DurationSeconds - after.DurationSeconds) > shorter * .05)
            return Rollback("unequal_windows", "前后测量窗口时长不匹配，释放请求后按相同窗口重新验收。");
        if (before.Fd2Count < 20 || before.ParsedCount < 20 || after.Fd2Count < 20 || after.ParsedCount < 20)
            return Rollback("insufficient_samples", "前后真实 FD2 与 Parsed 样本不足，不能仅凭请求成功保留参数。");
        if (!Fresh(before.LastInputAgeMs) || !Fresh(after.LastInputAgeMs))
            return Rollback("input_not_fresh", "测量末尾输入已停顿或输入年龄未知，历史频率不能证明当前连接改善。");
        if (Fd2(before) >= 128)
            return Rollback("native_target_already_met", "原生 FD2 已达到 128Hz 门槛，无需保留额外参数请求。");
        if (Parsed(after) < Fd2(after) * .95)
            return Rollback("parsed_integrity_failed", "FD2 通知增加但成功解析比例低于 95%，不算有效改善。");
        if (Fd2(after) < Fd2(before) || Parsed(after) < Parsed(before))
            return Rollback("measured_rate_regressed", "真实 FD2 或 Parsed 比原生窗口下降，释放本次请求。");
        if (Fd2(after) >= 128 && Parsed(after) >= 128)
            return "True|target_reached|真实 FD2 / Parsed 均达到 128Hz 门槛，且两路不低于原生测量；保留请求并继续监测。";
        double increase = Effective(after) - Effective(before);
        if (increase + 1e-9 >= 3 && increase + 1e-9 >= Effective(before) * .05)
            return "True|measured_improvement|真实有效输入提升至少 3Hz 且至少 5%；保留这次改善，但尚未达到 128Hz 门槛。";
        return Rollback("no_measured_improvement", "真实输入没有达到可确认的改善门槛，释放本次请求。");
    }

    private static bool LegacyIsExactRadioTarget(RadioRestartTarget target)
    {
        string id = target.InterfaceId;
        if (id.Length > 4096 || !id.StartsWith(@"\\?\", StringComparison.Ordinal) || target.PnpId.Length > 512 ||
            target.PnpId.Any(c => char.IsControl(c) || c is '"' or '*' or '?' or '/') ||
            target.PnpId.Split('\\').Length != 3) return false;
        string[] parts = id[4..].Split('#');
        if (parts.Length != 4 || parts.Take(3).Any(string.IsNullOrWhiteSpace) ||
            !Guid.TryParse(parts[3], out Guid guid) || guid != new Guid("92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2")) return false;
        string bus = parts[0];
        if (bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ||
            new[] { "SWD", "ROOT", "HTREE", "HID" }.Contains(bus, StringComparer.OrdinalIgnoreCase)) return false;
        return string.Equals(string.Join("\\", parts.Take(3)), target.PnpId, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] LegacyRestartArguments(RadioRestartTarget target)
    {
        if (!LegacyIsExactRadioTarget(target)) throw new ArgumentException("无法核实所选蓝牙适配器，未重启任何设备。");
        return new[] { "/restart-device", target.PnpId };
    }

    private static string LegacyDecodeNativeOutput(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Length > 65536) throw new ArgumentException("设备工具输出过长。");
        bool utf16 = bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE ||
            bytes.Length > 3 && Enumerable.Range(0, Math.Min(bytes.Length / 2, 128)).Count(i => bytes[i * 2 + 1] == 0) > Math.Min(bytes.Length / 2, 128) / 3;
        if (utf16) return Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF');
        if (bytes.Length > 1 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes).TrimStart('\uFEFF');
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes);
        }
    }

    private static string LegacyCompletionMessage(int? exitCode, bool verified) => verified
        ? "蓝牙已重启。请唤醒 Pro2，再点击“连接 Pro2”；输入频率需重新测量。"
        : exitCode == 3010 ? "蓝牙热重启未完成。请保存工作后重启 Windows。"
        : "未确认蓝牙重启成功。请查看诊断记录；程序不会强制禁用设备或重启 Windows。";

    private static bool LegacyVerifiedSuccess(int exitCode, bool presentStarted, bool parametersUnchanged, bool driverUnchanged) =>
        exitCode == 0 && presentStarted && parametersUnchanged && driverUnchanged;

    private static BluetoothAdapterDiagnosticInfo RandomAdapter(Random random) =>
        new("adapter" + random.Next(0, 5), @"USB\VID_8087\PID_0026",
            random.Next(3) == 0 ? "8087" : "1234",
            random.Next(3) == 0 ? "0026" : "9999",
            random.Next(3) == 0 ? "Intel Corporation" : "Other",
            random.Next(3) == 0 ? "22.250.0.2" : "1.0",
            "2026", @"\\?\USB#VID#PID#92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2");

    private static SystemBluetoothSnapshot RandomBluetoothSnapshot(Random random)
    {
        var adapters = new List<RadioDiagnosticInfo>();
        for (int i = 0; i < random.Next(0, 4); i++)
        {
            adapters.Add(new RadioDiagnosticInfo(RandomAdapter(random), random.Next(2) == 0,
                "AA:BB:CC:DD:EE:FF", random.Next(2) == 0, random.Next(2) == 0, false, true));
        }
        var system = new SystemDiagnosticInfo("Windows", "Pro", "11", "1.0", "x64", "x64", "1.0", "1.0");
        return new SystemBluetoothSnapshot(DateTimeOffset.UnixEpoch, system, adapters,
            "telemetry", random.Next(3) == 0 ? "" : "warning");
    }

    private static string TargetsKey(IReadOnlyList<RadioRestartTarget> targets) =>
        string.Join(";", targets.Select(t =>
            $"{t.Name}|{t.PnpId}|{t.InterfaceId}|{t.Provider}|{t.DriverVersion}|{t.IsDefault}"));

    private static string LegacyTargetsKey(SystemBluetoothSnapshot snapshot) =>
        string.Join(";", snapshot.Adapters
            .Select(r => new RadioRestartTarget(r.Driver.Name, r.Driver.PnpDeviceId, r.Driver.WinRtDeviceId,
                r.Driver.DriverProvider, r.Driver.DriverVersion, r.IsDefault))
            .GroupBy(r => r.InterfaceId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(r => r.IsDefault).ThenBy(r => r.Name)
            .Select(t => $"{t.Name}|{t.PnpId}|{t.InterfaceId}|{t.Provider}|{t.DriverVersion}|{t.IsDefault}"));

    private static string PresetKey(BlePresetDefinition d) =>
        $"{d.Preset}|{d.Id}|{d.Title}|{d.IntervalMinUnits}|{d.IntervalMaxUnits}|{d.Latency}|{d.TimeoutUnits}";

    private static BlePresetDefinition LegacyGetPreset(BlePreset preset) =>
        BlePresetCatalog.Presets.FirstOrDefault(item => item.Preset == preset) ??
        throw new ArgumentOutOfRangeException(nameof(preset));

    private static BlePresetDefinition LegacyGetPreset(string id) =>
        BlePresetCatalog.Presets.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ??
        throw new ArgumentException("未知 Win10 BLE 预设；允许 7p5、flexible、15，未修改任何参数。", nameof(id));

    private static BlePresetDefinition LegacyParseCommand(IReadOnlyList<string> args)
    {
        string[] selectors = args.Where(argument => argument.StartsWith("--ble-turbo-stack-preset",
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selectors.Length > 1)
            throw new ArgumentException("同一操作只能指定一个 Win10 BLE 预设；未修改任何参数。", nameof(args));
        if (selectors.Length == 0) return BlePresetCatalog.Default;
        if (!selectors[0].StartsWith("--ble-turbo-stack-preset=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Win10 BLE 预设格式应为 --ble-turbo-stack-preset=7p5|flexible|15；未修改任何参数。", nameof(args));
        return LegacyGetPreset(selectors[0]["--ble-turbo-stack-preset=".Length..]);
    }

    private static bool LegacyIsAudited(int windowsBuild, string sha256) =>
        windowsBuild >= 19041 && windowsBuild < 22000 &&
        string.Equals(sha256, "05A928CA6C072FB324B637A924C7C5F377FD897918D63854C2E646F7D0D62852",
            StringComparison.OrdinalIgnoreCase);

    private static bool LegacyParseConsent(IReadOnlyList<string> args)
    {
        const string flag = "--ble-turbo-stack-experimental";
        string[] flags = args.Where(argument => argument.StartsWith(flag,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (flags.Length == 0) return false;
        if (flags.Length != 1 || !string.Equals(flags[0], flag, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("实验确认参数格式无效或重复；未修改任何参数。", nameof(args));
        if (!args.Any(argument => string.Equals(argument, "--ble-turbo-stack-apply", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(argument => string.Equals(argument, "--ble-turbo-stack-restore", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("实验确认仅能用于 Win10 系统栈应用操作；未修改任何参数。", nameof(args));
        return true;
    }

    private static BlePreferenceDecision LegacyDecide(bool pure133, string phase, uint current,
        uint min, uint max, uint latency, double measuredNativeHz)
    {
        if (pure133 && phase != "fallback") return BlePreferenceDecision.DeferUntilMeasured;
        if (current != 0 && max > current) return BlePreferenceDecision.SkipSlowerThanLive;
        if (!pure133) return BlePreferenceDecision.Request;
        if (min < 6 || max < min || latency != 0) return BlePreferenceDecision.SkipIncompatiblePreset;
        if (current != 0 && max == current) return BlePreferenceDecision.SkipNoMeasuredImprovement;
        if (min == 6 && max == 6) return BlePreferenceDecision.Request;
        if (current > max || (current == 0 && double.IsFinite(measuredNativeHz) &&
            measuredNativeHz > 0 && measuredNativeHz < 800.0 / max))
            return BlePreferenceDecision.RequestPartialImprovement;
        return BlePreferenceDecision.SkipNoMeasuredImprovement;
    }

    private static bool LegacyMatchesStack(EnvironmentFacts facts) =>
        facts.IsWindows && facts.Build == 26200 &&
        facts.RequestParametersApiAvailable && facts.ReadParametersApiAvailable &&
        string.Equals(facts.StackSha256,
            "6750A4EA8ACEC328EC615863CC1A10B5808BE0BEACBF8F9F329FA340DED38D62",
            StringComparison.OrdinalIgnoreCase);

    private static string LegacySelect(EnvironmentFacts facts, BluetoothAdapterDiagnosticInfo? adapter, bool diagnosticOptIn)
    {
        if (!facts.IsWindows || facts.Build < 22000) return "UnsupportedPlatform";
        if (!facts.RequestParametersApiAvailable || !facts.ReadParametersApiAvailable) return "ApiUnavailable";
        if (diagnosticOptIn) return "ExplicitDiagnostic";
        if (LegacyMatchesStack(facts) && adapter != null &&
            string.Equals(adapter.Vid, "8087", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(adapter.Pid, "0026", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(adapter.DriverProvider?.Trim(), "Intel Corporation", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(adapter.DriverVersion?.Trim(), "22.250.0.2", StringComparison.Ordinal))
            return "ValidatedAutomatic";
        return "UnvalidatedEnvironment";
    }

    private static bool LegacyAllowsCustomRequest(ParameterRoute route) =>
        route is ParameterRoute.ExplicitDiagnostic or ParameterRoute.ValidatedAutomatic;

    private static BluetoothAdapterDiagnosticInfo? LegacySingleAuditedAdapter(SystemBluetoothSnapshot environment) =>
        environment.Warning.Length == 0 && environment.Adapters.Count == 1 &&
        environment.Adapters[0].LowEnergy && environment.Adapters[0].Central
            ? environment.Adapters[0].Driver : null;

    private static string AdapterKey(BluetoothAdapterDiagnosticInfo? adapter) =>
        adapter == null ? "null" : $"{adapter.Name}|{adapter.Vid}|{adapter.Pid}|{adapter.DriverProvider}";

    private static string RandomConfigJson(Random random)
    {
        string operation = random.Next(5) == 0 ? "other" : "apply";
        string preset = random.Next(5) == 0 ? "other" : "7p5";
        string stamp = random.Next(5) == 0 ? "not-a-date"
            : DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture);
        bool success = random.Next(2) == 0, required = random.Next(4) == 0 ? success : !success;
        if (random.Next(12) == 0) return "{broken json";
        if (random.Next(12) == 0) return "[]";
        return "{\"operation\":\"" + operation + "\",\"preset\":\"" + preset +
            "\",\"completedAt\":\"" + stamp + "\",\"restartSucceeded\":" + (success ? "true" : "false") +
            ",\"restartRequired\":" + (required ? "true" : "false") + "}";
    }

    private static string ConfigKey(ConfigOperationResult r) =>
        $"{r.Known}|{r.RestartSucceeded}|{r.RestartRequired}|{r.Code}";

    private static string LegacyParseConfig(string json, string operation, string preset,
        DateTimeOffset requestedAt, DateTimeOffset now)
    {
        const string unknown = "False|False|True|completion_unknown";
        if (json.Length > 65536 || requestedAt > now) return unknown;
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("operation", out var action) || action.ValueKind != JsonValueKind.String || action.GetString() != operation ||
                !item.TryGetProperty("preset", out var choice) || choice.ValueKind != JsonValueKind.String || choice.GetString() != preset ||
                !item.TryGetProperty("completedAt", out var stamp) || stamp.ValueKind != JsonValueKind.String ||
                !stamp.TryGetDateTimeOffset(out var completed) || completed < requestedAt || completed > now ||
                !item.TryGetProperty("restartSucceeded", out var restarted) || restarted.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !item.TryGetProperty("restartRequired", out var needed) || needed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unknown;
            bool success = restarted.GetBoolean(), required = needed.GetBoolean();
            if (success == required) return unknown;
            return $"True|{success}|{required}|" + (success ? "adapter_restart_verified" : "system_restart_required");
        }
        catch (JsonException) { return unknown; }
    }

    private static string LegacyTelemetry(EnvironmentSnapshot s) =>
        "route=" + s.Plan.Route + " build=" + s.Facts.Build +
        " request_api=" + s.Facts.RequestParametersApiAvailable + " read_api=" + s.Facts.ReadParametersApiAvailable +
        " win10_configured=" + s.Facts.Win10StackValuesConfigured + " bthport_sha256=" + s.Facts.StackSha256 +
        " api_probe_ok=" + s.ApiProbeSucceeded + " configuration_probe_ok=" + s.ConfigurationProbeSucceeded +
        " stack_probe_ok=" + s.StackProbeSucceeded +
        (s.Win10Assessment == null ? "" : " win10_parameter_evidence=" + s.Win10Assessment.EvidenceLevel +
            " win10_parameter_names=" + s.Win10Assessment.ParameterNamesFound.Count + "/4" +
            " win10_experiment_available=" + s.Win10Assessment.CanExperiment +
            " win10_explicit_experiment_required=" + s.Win10Assessment.RequiresExperimentalConsent) +
        " warning=" + s.ProbeWarning.Replace('\r', ' ').Replace('\n', ' ');

    private static void LegacyWake(SemaphoreSlim sourceSignal, double dueMilliseconds)
    {
        if (double.IsPositiveInfinity(dueMilliseconds))
        {
            sourceSignal.WaitAsync(CancellationToken.None).GetAwaiter().GetResult();
            return;
        }
        if (!double.IsFinite(dueMilliseconds) || dueMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(dueMilliseconds));
        if (dueMilliseconds == 0) return;
    }

    // ---------------- 校准、协议与状态归档 ----------------

    private static void Calibration(Random random, CheckFn check, ref int cases, ref int bad)
    {
        bool[] flags = [false, true];

        for (int i = 0; i < 1200; i++)
        {
            var source = new StickCalibrationProfile
            {
                CenterCalibrated = random.Next(4) != 0,
                RangeCalibrated = random.Next(2) == 0,
                CenterLx = (ushort)random.Next(0, 4096), CenterLy = (ushort)random.Next(0, 4096),
                CenterRx = (ushort)random.Next(0, 4096), CenterRy = (ushort)random.Next(0, 4096),
                MinLx = (ushort)random.Next(0, 4096), MaxLx = (ushort)random.Next(0, 4096),
                MinLy = (ushort)random.Next(0, 4096), MaxLy = (ushort)random.Next(0, 4096),
                MinRx = (ushort)random.Next(0, 4096), MaxRx = (ushort)random.Next(0, 4096),
                MinRy = (ushort)random.Next(0, 4096), MaxRy = (ushort)random.Next(0, 4096),
            };
            check("calibration", "stick normalize " + i,
                ProfileKey(StickCalibrationProfile.Normalize(source)) == ProfileKey(LegacyNormalizeProfile(source)),
                ref cases, ref bad);
            check("calibration", "stick summary " + i,
                source.Summary == LegacyProfileSummary(source), ref cases, ref bad);
        }
        check("calibration", "stick normalize null",
            ProfileKey(StickCalibrationProfile.Normalize(null)) == ProfileKey(LegacyNormalizeProfile(null)),
            ref cases, ref bad);
        for (int i = 0; i < 300; i++)
        {
            var axes = new PhysicalStickAxes((ushort)random.Next(0, 4096), (ushort)random.Next(0, 4096),
                (ushort)random.Next(0, 4096), (ushort)random.Next(0, 4096));
            check("calibration", "axes telemetry " + i,
                axes.TelemetryValue == "lx=" + axes.Lx + " ly=" + axes.Ly + " rx=" + axes.Rx + " ry=" + axes.Ry,
                ref cases, ref bad);
        }

        for (int round = 0; round < 24; round++)
        {
            var actual = new GyroBiasLearner();
            var legacy = new LegacyGyroBiasLearner();
            if (round % 3 == 0)
            {
                check("calibration", "gyro manual start " + round,
                    actual.StartManualCalibration() == legacy.StartManualCalibration(), ref cases, ref bad);
            }
            long ticks = 0;
            for (int frame = 0; frame < 180; frame++)
            {
                GamepadState stateA = RandomGyroState(random);
                GamepadState stateB = stateA.Clone();
                ticks += Stopwatch.Frequency / 50;
                actual.Apply(stateA, ticks);
                legacy.Apply(stateB, ticks);
                check("calibration", "gyro frame " + round + "/" + frame,
                    LearnerKey(actual) == LegacyLearnerKey(legacy) &&
                    PolicyStateKey(stateA) == PolicyStateKey(stateB), ref cases, ref bad);
            }
        }

        for (int i = 0; i < 900; i++)
        {
            var options = new InputStabilityOptions
            {
                AxisNormalDeltaThreshold = random.Next(-100, 6000),
                AxisSpikeDeltaThreshold = random.Next(-100, 6000),
                AxisReturnToGoodThreshold = random.Next(-100, 6000),
                AxisMinConfirmMs = random.Next(5) == 0 ? double.NaN : random.NextDouble() * 400,
                AxisMaxDeltaPerMs = random.Next(5) == 0 ? double.PositiveInfinity : random.NextDouble() * 400,
                AxisRampEnabled = random.Next(2) == 0,
                AxisRampMaxDeltaPerMs = random.NextDouble() * 400,
                AxisIdleMinConfirmMs = random.NextDouble() * 400,
                AxisActiveMinConfirmMs = random.NextDouble() * 200,
                AxisFastReversalConfirmMs = random.NextDouble() * 200,
                AxisIdleRampMaxDeltaPerMs = random.NextDouble() * 400,
                AxisActiveRampMaxDeltaPerMs = random.NextDouble() * 700,
                AxisFastReversalRampMaxDeltaPerMs = random.NextDouble() * 700,
                AxisMotionActiveWindowMs = random.NextDouble() * 700,
                AxisLowLatencyMotionBypassEnabled = random.Next(2) == 0,
                AxisLowLatencyBypassDeltaThreshold = random.Next(-100, 6000),
                AxisLinkRecoveryGapMs = random.NextDouble() * 700,
                AxisMotionDeltaThreshold = random.Next(-100, 6000),
                AxisCenterCrossThreshold = random.Next(-100, 6000),
                AxisInputSwallowDetectMs = random.Next(5) == 0 ? double.NaN : random.NextDouble() * 400,
                RawIntegrityModeEnabled = random.Next(2) == 0,
                AxisSpikeLogEnabled = random.Next(2) == 0,
                AxisSpikeRawDumpEnabled = random.Next(2) == 0,
                AxisSpikeRingBufferFramesBefore = random.Next(-10, 100),
                AxisSpikeRingBufferFramesAfter = random.Next(-10, 100),
                AxisSpikeRingBufferCapacity = random.Next(0, 700),
                AxisSpikeDumpRateLimitPer10Seconds = random.Next(-10, 100),
            };
            check("calibration", "stability normalize " + i,
                StabilityKey(InputStabilityOptions.Normalize(options)) == StabilityKey(LegacyNormalizeStability(options)),
                ref cases, ref bad);
            check("calibration", "stability summary " + i,
                options.Summary == LegacyStabilitySummary(options), ref cases, ref bad);
        }
        check("calibration", "stability default",
            StabilityKey(InputStabilityOptions.Default) == StabilityKey(LegacyNormalizeStability(null)),
            ref cases, ref bad);

        byte[] keyA = new byte[16], keyB = new byte[16], a2 = new byte[16];
        random.NextBytes(keyA); random.NextBytes(keyB); random.NextBytes(a2);
        foreach (ulong host in new ulong[] { 0, 1, 0x001122334455, 0xAABBCCDDEEFF, ulong.MaxValue })
        {
            check("calibration", "sleep address " + host,
                Convert.ToHexString(SleepWakeProtocol.ReversedAddress(host)) ==
                Convert.ToHexString(LegacyReversedAddress(host)) &&
                Convert.ToHexString(SleepWakeProtocol.ExchangeAddressesCommand(host)) ==
                Convert.ToHexString(LegacyExchangeAddresses(host)), ref cases, ref bad);
            for (int i = 0; i < 60; i++)
            {
                byte[] area = new byte[random.Next(0, 30)];
                random.NextBytes(area);
                if (area.Length >= 14 && random.Next(2) == 0) area[0] = (byte)random.Next(1, 3);
                check("calibration", "sleep host stored " + i,
                    SleepWakeProtocol.IsHostStored(area, host) == LegacyIsHostStored(area, host),
                    ref cases, ref bad);
            }
        }
        check("calibration", "sleep commands",
            Convert.ToHexString(SleepWakeProtocol.SleepCommand()) == Convert.ToHexString(LegacySleepCommand()) &&
            Convert.ToHexString(SleepWakeProtocol.FinaliseCommand()) == "159101030001000000" &&
            Convert.ToHexString(SleepWakeProtocol.ExchangeKeysCommand(keyA)) ==
            Convert.ToHexString(LegacyKeyedCommand(0x04, keyA)) &&
            Convert.ToHexString(SleepWakeProtocol.ConfirmCommand(a2)) ==
            Convert.ToHexString(LegacyKeyedCommand(0x02, a2)) &&
            Convert.ToHexString(SleepWakeProtocol.ComputeLtk(keyA, keyB)) ==
            Convert.ToHexString(LegacyComputeLtk(keyA, keyB)) &&
            Convert.ToHexString(SleepWakeProtocol.ExpectedConfirmResponse(keyA, a2)) ==
            Convert.ToHexString(LegacyExpectedConfirm(keyA, a2)), ref cases, ref bad);
        check("calibration", "sleep keyed invalid length",
            Capture(() => Convert.ToHexString(SleepWakeProtocol.ExchangeKeysCommand(new byte[15]))) ==
            Capture(() => Convert.ToHexString(LegacyKeyedCommand(0x04, new byte[15]))), ref cases, ref bad);

        for (int round = 0; round < 30; round++)
        {
            var actual = new UserActivityTracker();
            var legacy = new LegacyUserActivityTracker();
            actual.Reset(100);
            legacy.Reset(100);
            for (int frame = 0; frame < 60; frame++)
            {
                GamepadState state = RandomPolicyState(random);
                long now = 100 + frame * 1000L;
                check("calibration", "activity " + round + "/" + frame,
                    actual.Note(state, now) == legacy.Note(state, now) &&
                    actual.LastActivityTicks == legacy.LastActivityTicks, ref cases, ref bad);
            }
        }

        for (int round = 0; round < 40; round++)
        {
            var actual = new RecentEvidenceLog();
            var legacy = new LegacyRecentEvidenceLog();
            for (int line = 0; line < 30; line++)
            {
                string text = RandomLogText(random);
                actual.Add(text);
                legacy.Add(text);
                check("calibration", "evidence " + round + "/" + line,
                    actual.Snapshot == legacy.Snapshot, ref cases, ref bad);
            }
        }

        string[] argPool = ["--battery-init-snapshot-test", "--battery-snapshot-off-test", "other", "", "--BATTERY-SNAPSHOT-OFF-TEST"];
        for (int i = 0; i < 700; i++)
        {
            string[] args = Enumerable.Range(0, random.Next(0, 5))
                .Select(_ => argPool[random.Next(argPool.Length)]).ToArray();
            bool defaultOn = random.Next(2) == 0;
            check("calibration", "snapshot requested " + i,
                InitialBatterySnapshot.ResolveRequested(args, defaultOn) == LegacySnapshotRequested(args, defaultOn) &&
                InitialBatterySnapshot.IsRequested(args) ==
                LegacySnapshotRequested(args, SoftwareOnlyMode.IsReleaseBuild), ref cases, ref bad);
            byte[] payload = new byte[random.Next(0, 20)];
            random.NextBytes(payload);
            if (payload.Length > 1 && random.Next(2) == 0) payload[1] = (byte)((random.Next(0, 10) << 2) | random.Next(0, 4));
            check("calibration", "snapshot valid " + i,
                InitialBatterySnapshot.IsValid(payload) == LegacySnapshotValid(payload), ref cases, ref bad);
        }
        foreach (int scenario in Enumerable.Range(0, 7))
        {
            string a = Capture(() => SnapshotKey(InitialBatterySnapshot.CaptureAsync(
                t => Task.FromResult(scenario is not 1),
                t => scenario switch
                {
                    2 => throw new OperationCanceledException(),
                    3 => throw new InvalidOperationException(),
                    _ => Task.FromResult(new byte[16] { 1, 0, 0x1C, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
                },
                t => scenario == 4 ? throw new IOException() : Task.FromResult(scenario != 5),
                CancellationToken.None, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult()));
            string b = Capture(() => SnapshotKey(LegacyCaptureAsync(
                t => Task.FromResult(scenario is not 1),
                t => scenario switch
                {
                    2 => throw new OperationCanceledException(),
                    3 => throw new InvalidOperationException(),
                    _ => Task.FromResult(new byte[16] { 1, 0, 0x1C, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
                },
                t => scenario == 4 ? throw new IOException() : Task.FromResult(scenario != 5),
                CancellationToken.None, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult()));
            check("calibration", "snapshot capture " + scenario, a == b, ref cases, ref bad);
        }

        foreach (StackEvidenceLevel level in Enum.GetValues<StackEvidenceLevel>())
        foreach (bool valid in flags)
        foreach (bool consent in flags)
        {
            var assessment = new StackParameterAssessment(20000, "path", "1.0", "hash", level,
                new[] { "A", "B" }, valid, "reason");
            check("calibration", "stack properties",
                assessment.IsAudited == LegacyIsAuditedStack(assessment) &&
                assessment.CanExperiment == LegacyCanExperiment(assessment) &&
                assessment.RequiresExperimentalConsent == LegacyRequiresConsent(assessment) &&
                assessment.CanApply(consent) == LegacyCanApply(assessment, consent) &&
                assessment.EvidenceSummary == LegacyEvidenceSummary(assessment), ref cases, ref bad);
        }
        string stackPath = Path.Combine(Path.GetTempPath(), "bthport.sys");
        foreach (int build in new[] { 0, 19040, 19041, 20000, 21999, 22000 })
        foreach (bool x64 in flags)
        foreach (string path in new[] { stackPath, Path.Combine(Path.GetTempPath(), "other.sys"), "" })
        {
            byte[] image = LegacyValidPe(random, includeNames: false);
            check("calibration", "stack analyze blocked",
                StackKey(StackParameterAssessment.Analyze(build, x64, stackPath, path, image, "1.0")) ==
                LegacyAnalyzeKey(build, x64, stackPath, path, image, "1.0"), ref cases, ref bad);
        }
        foreach (bool includeNames in flags)
        foreach (int nameCount in new[] { 0, 1, 4 })
        {
            byte[] image = LegacyValidPe(random, includeNames, nameCount);
            check("calibration", "stack analyze valid pe",
                StackKey(StackParameterAssessment.Analyze(20000, true, stackPath, stackPath, image, "1.0")) ==
                LegacyAnalyzeKey(20000, true, stackPath, stackPath, image, "1.0"), ref cases, ref bad);
        }

        string archiveRoot = Path.Combine(Path.GetTempPath(), "pro2-aux-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(archiveRoot);
            string warningA = "", warningB = "";
            object? emptyResult = AdaptiveDiagnosticArchive.LoadLatest(out warningA, archiveRoot);
            check("calibration", "archive empty",
                emptyResult == null && warningA == "", ref cases, ref bad);
            File.WriteAllText(Path.Combine(archiveRoot, "presets_bad.json"), "{broken");
            object? brokenResult = AdaptiveDiagnosticArchive.LoadLatest(out warningA, archiveRoot);
            check("calibration", "archive broken json",
                brokenResult == null && warningA == "部分历史记录无法读取，已跳过。" &&
                ArchiveLegacy(archiveRoot, out warningB) == null && warningB == warningA, ref cases, ref bad);
            check("calibration", "archive absent",
                AdaptiveDiagnosticArchive.LoadLatest(out _, Path.Combine(archiveRoot, "missing")) == null,
                ref cases, ref bad);
        }
        finally
        {
            try { Directory.Delete(archiveRoot, recursive: true); } catch (IOException) { }
        }
    }

    private static object? ArchiveLegacy(string directory, out string warning)
    {
        warning = "";
        try
        {
            foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("presets_*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(10))
            {
                try
                {
                    using JsonDocument json = JsonDocument.Parse(File.ReadAllText(file.FullName));
                    if (json.RootElement.ValueKind != JsonValueKind.Object) continue;
                }
                catch (JsonException) { warning = "部分历史记录无法读取，已跳过。"; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { warning = "历史记录未能读取；不影响当前连接。"; }
        return null;
    }

    private static string ProfileKey(StickCalibrationProfile p) =>
        $"{p.CenterCalibrated}|{p.RangeCalibrated}|{p.CenterLx}|{p.CenterLy}|{p.CenterRx}|{p.CenterRy}|" +
        $"{p.MinLx}|{p.MaxLx}|{p.MinLy}|{p.MaxLy}|{p.MinRx}|{p.MaxRx}|{p.MinRy}|{p.MaxRy}|{p.UpdatedAtUtc:O}";

    private static string LegacyProfileSummary(StickCalibrationProfile p) =>
        "center=" + p.CenterLx + "," + p.CenterLy + "," + p.CenterRx + "," + p.CenterRy +
        " range=" + p.MinLx + ".." + p.MaxLx + "," + p.MinLy + ".." + p.MaxLy + "," +
        p.MinRx + ".." + p.MaxRx + "," + p.MinRy + ".." + p.MaxRy +
        " center_calibrated=" + p.CenterCalibrated.ToString().ToLowerInvariant() +
        " range_calibrated=" + p.RangeCalibrated.ToString().ToLowerInvariant();

    private static StickCalibrationProfile LegacyNormalizeProfile(StickCalibrationProfile? source)
    {
        if (source == null || !source.CenterCalibrated) return new StickCalibrationProfile();
        ushort lx = LegacyCenter(source.CenterLx), ly = LegacyCenter(source.CenterLy);
        ushort rx = LegacyCenter(source.CenterRx), ry = LegacyCenter(source.CenterRy);
        bool valid = source.RangeCalibrated &&
            LegacyRange(source.MinLx, lx, source.MaxLx) && LegacyRange(source.MinLy, ly, source.MaxLy) &&
            LegacyRange(source.MinRx, rx, source.MaxRx) && LegacyRange(source.MinRy, ry, source.MaxRy);
        return source with
        {
            CenterCalibrated = true, RangeCalibrated = valid,
            CenterLx = lx, CenterLy = ly, CenterRx = rx, CenterRy = ry,
            MinLx = valid ? source.MinLx : LegacyMin(lx), MaxLx = valid ? source.MaxLx : LegacyMax(lx),
            MinLy = valid ? source.MinLy : LegacyMin(ly), MaxLy = valid ? source.MaxLy : LegacyMax(ly),
            MinRx = valid ? source.MinRx : LegacyMin(rx), MaxRx = valid ? source.MaxRx : LegacyMax(rx),
            MinRy = valid ? source.MinRy : LegacyMin(ry), MaxRy = valid ? source.MaxRy : LegacyMax(ry)
        };
    }

    private static ushort LegacyCenter(ushort value) => value is >= 1024 and <= 3071 ? value : GamepadState.AxisCenter;
    private static bool LegacyRange(ushort minimum, ushort center, ushort maximum) =>
        minimum < center && maximum > center && center - minimum >= 256 && maximum - center >= 256;
    private static ushort LegacyMin(ushort center) => (ushort)Math.Max(0, center - 1600);
    private static ushort LegacyMax(ushort center) => (ushort)Math.Min(GamepadState.AxisMax, center + 1600);

    private static GamepadState RandomGyroState(Random random)
    {
        bool still = random.Next(3) != 0;
        return new GamepadState
        {
            GyroValid = true,
            AccelValid = true,
            Buttons = still ? GamepadButtons.None : (GamepadButtons)(ulong)random.Next(0, 8),
            Lx = still ? GamepadState.AxisCenter : (ushort)random.Next(0, 4096),
            Ly = still ? GamepadState.AxisCenter : (ushort)random.Next(0, 4096),
            Rx = still ? GamepadState.AxisCenter : (ushort)random.Next(0, 4096),
            Ry = still ? GamepadState.AxisCenter : (ushort)random.Next(0, 4096),
            AccelX = still ? (short)random.Next(-5, 6) : (short)random.Next(-2000, 2000),
            AccelY = still ? (short)random.Next(-5, 6) : (short)random.Next(-2000, 2000),
            AccelZ = still ? (short)(4096 + random.Next(-5, 6)) : (short)random.Next(-2000, 2000),
            GyroX = still ? (short)random.Next(-3, 4) : (short)random.Next(-100, 100),
            GyroY = still ? (short)random.Next(-3, 4) : (short)random.Next(-100, 100),
            GyroZ = still ? (short)random.Next(-3, 4) : (short)random.Next(-100, 100),
        };
    }

    private static string LearnerKey(GyroBiasLearner l) =>
        l.Summary + "|" + l.Calibrated + "|" + l.LastResult + "|" +
        l.BiasX.ToString("R") + "|" + l.BiasY.ToString("R") + "|" + l.BiasZ.ToString("R") + "|" +
        l.WindowSeconds.ToString("R");

    private static string LegacyLearnerKey(LegacyGyroBiasLearner l) =>
        l.Summary + "|" + l.Calibrated + "|" + l.LastResult + "|" +
        l.BiasX.ToString("R") + "|" + l.BiasY.ToString("R") + "|" + l.BiasZ.ToString("R") + "|" +
        l.WindowSeconds.ToString("R");

    private sealed class LegacyGyroBiasLearner
    {
        private const double MaximumStationaryRaw = 24;
        private const int MinimumSamples = 150;
        private int count;
        private long startTicks, lastTicks;
        private readonly double[] mean = new double[7], m2 = new double[7], min = new double[7], max = new double[7];
        private bool manual;
        private double biasX, biasY, biasZ;
        public bool Calibrated { get; private set; }
        public string LastResult { get; private set; } = "auto_waiting_stationary";
        public double BiasX => biasX;
        public double BiasY => biasY;
        public double BiasZ => biasZ;
        public double WindowSeconds => count == 0 ? 0 : (lastTicks - startTicks) / (double)Stopwatch.Frequency;
        public string Summary => "mode=quality status=" + (manual ? "manual_collecting" : Calibrated ? "calibrated" : "not_calibrated") +
            " samples=" + count + " elapsed_s=" + WindowSeconds.ToString("F2", CultureInfo.InvariantCulture) +
            " bias_raw=" + biasX.ToString("F3", CultureInfo.InvariantCulture) + "," +
            biasY.ToString("F3", CultureInfo.InvariantCulture) + "," +
            biasZ.ToString("F3", CultureInfo.InvariantCulture) +
            " result=" + LastResult + " automatic_bias_updates_after_calibration=0";

        public string StartManualCalibration()
        {
            manual = true;
            ResetWindow();
            LastResult = "manual_collecting_keep_controller_still";
            return "陀螺仪三秒校准已开始；请将手柄静置在稳定平面。移动会拒绝本次采样，保留旧校准。";
        }

        public void Apply(GamepadState state, long nowTicks)
        {
            if (!state.GyroValid || !state.AccelValid) return;
            double gx = state.GyroX, gy = state.GyroY, gz = state.GyroZ;
            if (manual || !Calibrated)
            {
                double norm = Math.Sqrt((double)state.AccelX * state.AccelX +
                    (double)state.AccelY * state.AccelY + (double)state.AccelZ * state.AccelZ);
                bool idle = state.Buttons == GamepadButtons.None &&
                    Math.Abs(state.Lx - GamepadState.AxisCenter) <= 128 && Math.Abs(state.Ly - GamepadState.AxisCenter) <= 128 &&
                    Math.Abs(state.Rx - GamepadState.AxisCenter) <= 128 && Math.Abs(state.Ry - GamepadState.AxisCenter) <= 128;
                bool still = idle && norm is >= 3686 and <= 4506 &&
                    Math.Abs(gx - biasX) <= MaximumStationaryRaw && Math.Abs(gy - biasY) <= MaximumStationaryRaw &&
                    Math.Abs(gz - biasZ) <= MaximumStationaryRaw;
                if (!still) RejectWindow("motion");
                else if (count > 0 && nowTicks <= lastTicks) { }
                else
                {
                    if (count > 0 && (nowTicks - lastTicks) / (double)Stopwatch.Frequency > 0.1) ResetWindow();
                    if (count == 0) startTicks = nowTicks;
                    lastTicks = nowTicks;
                    count++;
                    Span<double> values = stackalloc double[] { gx, gy, gz, state.AccelX, state.AccelY, state.AccelZ, norm };
                    for (int i = 0; i < values.Length; i++)
                    {
                        double value = values[i];
                        if (count == 1) min[i] = max[i] = value;
                        min[i] = Math.Min(min[i], value); max[i] = Math.Max(max[i], value);
                        double delta = value - mean[i]; mean[i] += delta / count;
                        m2[i] += delta * (value - mean[i]);
                    }
                    bool stable = true;
                    for (int i = 0; i < 3; i++) stable &= max[i] - min[i] <= 12;
                    for (int i = 3; i < 6; i++) stable &= max[i] - min[i] <= 48;
                    if (!stable) RejectWindow("direction_or_noise");
                    else if (count >= MinimumSamples && WindowSeconds >= (manual ? 3 : 2))
                    {
                        bool lowVariance = Math.Sqrt(m2[6] / count) <= 8;
                        for (int i = 0; i < 3; i++) lowVariance &= Math.Sqrt(m2[i] / count) <= 2.5;
                        if (!lowVariance) RejectWindow("variance");
                        else
                        {
                            biasX = mean[0]; biasY = mean[1]; biasZ = mean[2];
                            Calibrated = true;
                            LastResult = manual ? "manual_committed_3s" : "auto_committed_2s";
                            manual = false;
                            ResetWindow();
                        }
                    }
                }
            }
            if (Calibrated)
            {
                state.GyroX = Correct(gx - biasX); state.GyroY = Correct(gy - biasY); state.GyroZ = Correct(gz - biasZ);
            }
        }

        private void RejectWindow(string reason)
        {
            LastResult = (manual ? "manual_rejected_" : "auto_waiting_") + reason;
            manual = false;
            ResetWindow();
        }
        private void ResetWindow()
        {
            count = 0; startTicks = lastTicks = 0;
            Array.Clear(mean); Array.Clear(m2); Array.Clear(min); Array.Clear(max);
        }
        private static short Correct(double value) =>
            (short)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
    }

    private static string StabilityKey(InputStabilityOptions o) =>
        string.Join("|", o.AxisNormalDeltaThreshold, o.AxisSpikeDeltaThreshold, o.AxisReturnToGoodThreshold,
            o.AxisMinConfirmMs.ToString("R"), o.AxisMaxDeltaPerMs.ToString("R"), o.AxisRampEnabled,
            o.AxisRampMaxDeltaPerMs.ToString("R"), o.AxisIdleMinConfirmMs.ToString("R"),
            o.AxisActiveMinConfirmMs.ToString("R"), o.AxisFastReversalConfirmMs.ToString("R"),
            o.AxisIdleRampMaxDeltaPerMs.ToString("R"), o.AxisActiveRampMaxDeltaPerMs.ToString("R"),
            o.AxisFastReversalRampMaxDeltaPerMs.ToString("R"), o.AxisMotionActiveWindowMs.ToString("R"),
            o.AxisLowLatencyMotionBypassEnabled, o.AxisLowLatencyBypassDeltaThreshold,
            o.AxisLinkRecoveryGapMs.ToString("R"), o.AxisMotionDeltaThreshold, o.AxisCenterCrossThreshold,
            o.AxisInputSwallowDetectMs.ToString("R"), o.RawIntegrityModeEnabled, o.AxisSpikeLogEnabled,
            o.AxisSpikeRawDumpEnabled, o.AxisSpikeRingBufferFramesBefore, o.AxisSpikeRingBufferFramesAfter,
            o.AxisSpikeRingBufferCapacity, o.AxisSpikeDumpRateLimitPer10Seconds);

    private static string LegacyStabilitySummary(InputStabilityOptions o) =>
        "AxisNormalDeltaThreshold=" + o.AxisNormalDeltaThreshold +
        " AxisSpikeDeltaThreshold=" + o.AxisSpikeDeltaThreshold +
        " AxisReturnToGoodThreshold=" + o.AxisReturnToGoodThreshold +
        " AxisMinConfirmMs=" + o.AxisMinConfirmMs.ToString("F1") +
        " AxisMaxDeltaPerMs=" + o.AxisMaxDeltaPerMs.ToString("F1") +
        " AxisRampEnabled=" + o.AxisRampEnabled +
        " AxisRampMaxDeltaPerMs=" + o.AxisRampMaxDeltaPerMs.ToString("F1") +
        " AxisIdleMinConfirmMs=" + o.AxisIdleMinConfirmMs.ToString("F1") +
        " AxisActiveMinConfirmMs=" + o.AxisActiveMinConfirmMs.ToString("F1") +
        " AxisFastReversalConfirmMs=" + o.AxisFastReversalConfirmMs.ToString("F1") +
        " AxisIdleRampMaxDeltaPerMs=" + o.AxisIdleRampMaxDeltaPerMs.ToString("F1") +
        " AxisActiveRampMaxDeltaPerMs=" + o.AxisActiveRampMaxDeltaPerMs.ToString("F1") +
        " AxisFastReversalRampMaxDeltaPerMs=" + o.AxisFastReversalRampMaxDeltaPerMs.ToString("F1") +
        " AxisMotionActiveWindowMs=" + o.AxisMotionActiveWindowMs.ToString("F1") +
        " AxisLowLatencyMotionBypassEnabled=" + o.AxisLowLatencyMotionBypassEnabled +
        " AxisLowLatencyBypassDeltaThreshold=" + o.AxisLowLatencyBypassDeltaThreshold +
        " AxisLinkRecoveryGapMs=" + o.AxisLinkRecoveryGapMs.ToString("F1") +
        " AxisMotionDeltaThreshold=" + o.AxisMotionDeltaThreshold +
        " AxisCenterCrossThreshold=" + o.AxisCenterCrossThreshold +
        " AxisInputSwallowDetectMs=" + o.AxisInputSwallowDetectMs.ToString("F1") +
        " RawIntegrityModeEnabled=" + o.RawIntegrityModeEnabled +
        " AxisSpikeLogEnabled=" + o.AxisSpikeLogEnabled +
        " AxisSpikeRawDumpEnabled=" + o.AxisSpikeRawDumpEnabled +
        " AxisSpikeRingBufferFramesBefore=" + o.AxisSpikeRingBufferFramesBefore +
        " AxisSpikeRingBufferFramesAfter=" + o.AxisSpikeRingBufferFramesAfter +
        " AxisSpikeRingBufferCapacity=" + o.AxisSpikeRingBufferCapacity +
        " AxisSpikeDumpRateLimitPer10Seconds=" + o.AxisSpikeDumpRateLimitPer10Seconds;

    private static InputStabilityOptions LegacyNormalizeStability(InputStabilityOptions? options)
    {
        options ??= new InputStabilityOptions();
        return new InputStabilityOptions
        {
            AxisNormalDeltaThreshold = Math.Clamp(options.AxisNormalDeltaThreshold, 1, GamepadState.AxisMax),
            AxisSpikeDeltaThreshold = Math.Clamp(Math.Max(options.AxisSpikeDeltaThreshold,
                options.AxisNormalDeltaThreshold + 1), 2, GamepadState.AxisMax),
            AxisReturnToGoodThreshold = Math.Clamp(options.AxisReturnToGoodThreshold, 1, GamepadState.AxisMax),
            AxisMinConfirmMs = LegacyClamp(options.AxisMinConfirmMs, 30, 250),
            AxisMaxDeltaPerMs = LegacyClamp(options.AxisMaxDeltaPerMs, 5, 300),
            AxisRampEnabled = options.AxisRampEnabled,
            AxisRampMaxDeltaPerMs = LegacyClamp(options.AxisRampMaxDeltaPerMs, 5, 300),
            AxisIdleMinConfirmMs = LegacyClamp(options.AxisIdleMinConfirmMs, 30, 250),
            AxisActiveMinConfirmMs = LegacyClamp(options.AxisActiveMinConfirmMs, 0, 90),
            AxisFastReversalConfirmMs = LegacyClamp(options.AxisFastReversalConfirmMs, 0, 80),
            AxisIdleRampMaxDeltaPerMs = LegacyClamp(options.AxisIdleRampMaxDeltaPerMs, 5, 300),
            AxisActiveRampMaxDeltaPerMs = LegacyClamp(options.AxisActiveRampMaxDeltaPerMs, 20, 500),
            AxisFastReversalRampMaxDeltaPerMs = LegacyClamp(options.AxisFastReversalRampMaxDeltaPerMs, 20, 600),
            AxisMotionActiveWindowMs = LegacyClamp(options.AxisMotionActiveWindowMs, 30, 500),
            AxisLowLatencyMotionBypassEnabled = options.AxisLowLatencyMotionBypassEnabled,
            AxisLowLatencyBypassDeltaThreshold = Math.Clamp(options.AxisLowLatencyBypassDeltaThreshold, 1, GamepadState.AxisMax),
            AxisLinkRecoveryGapMs = LegacyClamp(options.AxisLinkRecoveryGapMs, 45, 500),
            AxisMotionDeltaThreshold = Math.Clamp(options.AxisMotionDeltaThreshold, 1, GamepadState.AxisMax),
            AxisCenterCrossThreshold = Math.Clamp(options.AxisCenterCrossThreshold, 1, GamepadState.AxisMax / 2),
            AxisInputSwallowDetectMs = LegacyClamp(options.AxisInputSwallowDetectMs, 15, 250),
            RawIntegrityModeEnabled = options.RawIntegrityModeEnabled,
            AxisSpikeLogEnabled = options.AxisSpikeLogEnabled,
            AxisSpikeRawDumpEnabled = options.AxisSpikeRawDumpEnabled,
            AxisSpikeRingBufferFramesBefore = Math.Clamp(options.AxisSpikeRingBufferFramesBefore, 0, 64),
            AxisSpikeRingBufferFramesAfter = Math.Clamp(options.AxisSpikeRingBufferFramesAfter, 0, 64),
            AxisSpikeRingBufferCapacity = Math.Clamp(options.AxisSpikeRingBufferCapacity, 32, 512),
            AxisSpikeDumpRateLimitPer10Seconds = Math.Clamp(options.AxisSpikeDumpRateLimitPer10Seconds, 0, 60)
        };
    }

    private static double LegacyClamp(double value, double min, double max) =>
        double.IsNaN(value) || double.IsInfinity(value) ? min : Math.Clamp(value, min, max);

    private static byte[] LegacyReversedAddress(ulong address)
    {
        byte[] bytes = new byte[6];
        for (int i = 0; i < 6; i++) bytes[i] = (byte)(address >> (8 * i));
        return bytes;
    }

    private static byte[] LegacySleepCommand()
    {
        byte[] request = new byte[20];
        new byte[] { 0x06, 0x91, 0x01, 0x02, 0x00, 0x0C, 0x00, 0x00 }.CopyTo(request, 0);
        return request;
    }

    private static byte[] LegacyExchangeAddresses(ulong host)
    {
        byte[] first = LegacyReversedAddress(host);
        byte[] second = (byte[])first.Clone();
        second[0] = (byte)(second[0] - 1);
        byte[] request = new byte[8 + 14];
        new byte[] { 0x15, 0x91, 0x01, 0x01, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x02 }.CopyTo(request, 0);
        first.CopyTo(request, 10);
        second.CopyTo(request, 16);
        return request;
    }

    private static byte[] LegacyKeyedCommand(byte subcommand, byte[] value)
    {
        if (value.Length != 16) throw new ArgumentException("16-byte value required", nameof(value));
        byte[] request = new byte[8 + 17];
        new byte[] { 0x15, 0x91, 0x01, subcommand, 0x00, 0x11, 0x00, 0x00, 0x00 }.CopyTo(request, 0);
        value.CopyTo(request, 9);
        return request;
    }

    private static byte[] LegacyComputeLtk(byte[] a1, byte[] b1)
    {
        byte[] ltk = new byte[16];
        for (int i = 0; i < 16; i++) ltk[i] = (byte)(a1[i] ^ b1[i]);
        return ltk;
    }

    private static byte[] LegacyExpectedConfirm(byte[] ltk, byte[] a2)
    {
        byte[] key = (byte[])ltk.Clone(); Array.Reverse(key);
        byte[] block = (byte[])a2.Clone(); Array.Reverse(block);
        using Aes aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(block, PaddingMode.None);
    }

    private static bool LegacyIsHostStored(ReadOnlySpan<byte> pairingArea, ulong host) =>
        pairingArea.Length >= 14 && pairingArea[0] is >= 1 and <= 2 &&
        Convert.ToHexString(pairingArea.Slice(8, 6)) == host.ToString("X12");

    private sealed class LegacyUserActivityTracker
    {
        private bool initialised;
        private ulong buttons;
        private int lx = GamepadState.AxisCenter, ly = GamepadState.AxisCenter;
        private int rx = GamepadState.AxisCenter, ry = GamepadState.AxisCenter;
        private int l2, r2;
        public long LastActivityTicks { get; private set; }
        public void Reset(long nowTicks) => LastActivityTicks = nowTicks;
        public bool Note(GamepadState s, long nowTicks)
        {
            ulong pressed = (ulong)s.Buttons;
            bool active = !initialised || pressed != 0 || pressed != buttons ||
                Math.Abs(s.Lx - lx) > 320 || Math.Abs(s.Ly - ly) > 320 ||
                Math.Abs(s.Rx - rx) > 320 || Math.Abs(s.Ry - ry) > 320 ||
                Math.Abs(s.Lx - GamepadState.AxisCenter) > 900 || Math.Abs(s.Ly - GamepadState.AxisCenter) > 900 ||
                Math.Abs(s.Rx - GamepadState.AxisCenter) > 900 || Math.Abs(s.Ry - GamepadState.AxisCenter) > 900 ||
                Math.Abs(s.L2 - l2) > 320 || Math.Abs(s.R2 - r2) > 320 || s.L2 > 900 || s.R2 > 900;
            if (!active) return false;
            initialised = true;
            LastActivityTicks = nowTicks;
            buttons = pressed;
            lx = s.Lx; ly = s.Ly; rx = s.Rx; ry = s.Ry; l2 = s.L2; r2 = s.R2;
            return true;
        }
    }

    private static readonly string[] LogPrefixes =
    [
        "[SESSION_LOG]", "[PRO2_BLE_DISCONNECT_SIGNAL]", "[INPUT_DISCONNECT_NEUTRAL]",
        "[PRO2_OFFLINE_NOTICE] duplicate_suppressed=1", "[SHUTDOWN]", "[BLUETOOTH_RESTART]",
        "[BATTERY_RUNTIME_UPDATED]", "[PRO2_BATTERY] level=5", "[START]", "[FEATURE_POLICY]",
        "[PRO2_RUMBLE_TRANSPORT]", "[HAPTIC_CONVERSION]", "[PRO2_BLE_RATE_WINDOW]",
        "[VIIPER_INPUT_PIPELINE]", "[AUDIO_GUARD]", "[WIN11_PARAMETER_TRIAL]",
        "error=Success", "stop_error=Success", "error=0x80070005", "Exception: timeout",
        "windows_connection_status=Disconnected", "gatt_session_status=Closed", "plain line"
    ];

    private static string RandomLogText(Random random)
    {
        int lines = random.Next(1, 4);
        var text = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            text.Append(LogPrefixes[random.Next(LogPrefixes.Length)]);
            int extra = random.Next(0, 20);
            if (extra > 0) text.Append(' ').Append(RandomText(random, "abcXYZ 中文0123456789=;", extra));
            text.Append('\n');
        }
        return text.ToString();
    }

    private sealed class LegacyRecentEvidenceLog
    {
        private const int MaximumLineBytes = 2048;
        private const string Truncated = " [RECENT_LINE_TRUNCATED]";
        private const string SnapshotNotice = "[RECENT_EVIDENCE_POLICY] archived_subset=true original_timestamps=true original_order=true battery_values=last_recorded_not_current separate_retention_budgets=true";
        private readonly Bucket origin = new(4 * 1024), identity = new(12 * 1024), battery = new(16 * 1024);
        private readonly Bucket disconnect = new(32 * 1024), diagnostics = new(16 * 1024);
        private readonly Bucket measurements = new(32 * 1024), rumble = new(15 * 1024);
        private long sequence;
        private static readonly Regex ErrorFields = new(
            @"\b(?:[a-z0-9_]*error[a-z0-9_]*|hresult)\s*=\s*(?<value>""[^""]*""|[^\s;,\]]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FailureWords = new(
            @"\b(?:error|exception|failed|failure|unreachable|denied|fault|aborted|timeout)\b|timed out",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public void Add(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                Bucket? bucket = SelectBucket(line);
                if (bucket == null) continue;
                string value = BoundLine(line.TrimEnd('\r')) + Environment.NewLine;
                if (ReferenceEquals(bucket, origin)) origin.Clear();
                bucket.Add(new(++sequence, value, Encoding.UTF8.GetByteCount(value)));
            }
        }

        public string Snapshot => SnapshotNotice + Environment.NewLine + string.Concat(
            new[] { origin, identity, battery, disconnect, diagnostics, measurements, rumble }
                .SelectMany(bucket => bucket.Entries).OrderBy(entry => entry.Sequence).Select(entry => entry.Text));

        private Bucket? SelectBucket(string line)
        {
            if (Has(line, "[SESSION_LOG]")) return origin;
            if (Has(line, "[PRO2_BLE_DISCONNECT_SIGNAL]") || Has(line, "[INPUT_DISCONNECT_NEUTRAL]") ||
                Has(line, "[PRO2_OFFLINE_NOTICE]") && !Has(line, "duplicate_suppressed=1") ||
                Has(line, "windows_connection_status=Disconnected") || Has(line, "gatt_session_status=Closed") ||
                Has(line, "[SHUTDOWN]") || Has(line, "[BLUETOOTH_RESTART")) return disconnect;
            if (Has(line, "[BATTERY_RUNTIME_UPDATED]") ||
                Has(line, "[PRO2_BATTERY]") && (Has(line, "source=") || Has(line, "level=") || Has(line, "raw_power="))) return battery;
            if (Has(line, "[START]") || Has(line, "[STARTUP]") || Has(line, "[FEATURE_POLICY]") || Has(line, "[DATA_PATH_POLICY]") ||
                Has(line, "[BLE_COMPATIBILITY]") || Has(line, "[BLE_ADAPTER]") || Has(line, "[PRO2_BLE_ADDR]") ||
                Has(line, "[PRO2_BLE_LINK] windows_connection_status=Connected")) return identity;
            if (Has(line, "[PRO2_RUMBLE_TRANSPORT]") || Has(line, "[PRO2_RUMBLE_WINDOW]") || Has(line, "[PRO2_RUMBLE_NATIVE]") ||
                Has(line, "[PRO2_RUMBLE_PACER]") || Has(line, "[PRO2_RUMBLE_QUEUE]") || Has(line, "[PRO2_RUMBLE_CONVERTED]") ||
                Has(line, "[RUMBLE_CONVERSION]") || Has(line, "[HAPTIC_CONVERSION]") || Has(line, "[HAPTIC_STOP]") ||
                Has(line, "[PS5_FEEDBACK_STATE]") || Has(line, "[RUMBLE_TRANSPORT]") || Has(line, "[PRO2_RUMBLE_HOLD]") ||
                Has(line, "[PRO2_RUMBLE_SHUTDOWN]")) return rumble;
            if (Has(line, "[PRO2_BLE_RATE_WINDOW]") || Has(line, "[BLE_SESSION_EVIDENCE]") || Has(line, "[VIIPER_INPUT_PIPELINE]") ||
                Has(line, "[VIIPER_INPUT_POLICY]") || Has(line, "[VIIPER_INPUT_SAFETY]") || Has(line, "[PS5_MOTION_VALIDITY]") ||
                Has(line, "[BATTERY_RUNTIME_RATE]") || Has(line, "[BLE_LOW_RATE_DIAG]")) return measurements;
            if (Has(line, "[BATTERY_RUNTIME") || Has(line, "[PRO2_BATTERY") || Has(line, "[AUDIO_GUARD]") ||
                Has(line, "[BLE_AUTO_PRESET_") || Has(line, "[WIN11_PARAMETER_TRIAL]") || HasRealFailure(line)) return diagnostics;
            return null;
        }

        private static bool HasRealFailure(string line)
        {
            bool failedField = false;
            string remainder = ErrorFields.Replace(line, match =>
            {
                string value = match.Groups["value"].Value.Trim('"');
                bool neutral = value.Equals("Success", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("False", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("not_provided", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                    value is "0" or "0x0" or "0x00000000";
                if (!neutral) failedField = true;
                return "";
            });
            return failedField || FailureWords.IsMatch(remainder);
        }

        private static bool Has(string text, string value) => text.Contains(value, StringComparison.Ordinal);

        private static string BoundLine(string line)
        {
            int budget = MaximumLineBytes - Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (Encoding.UTF8.GetByteCount(line) <= budget) return line;
            budget -= Encoding.UTF8.GetByteCount(Truncated);
            int low = 0, high = Math.Min(line.Length, budget);
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (Encoding.UTF8.GetByteCount(line.AsSpan(0, middle)) <= budget) low = middle;
                else high = middle - 1;
            }
            if (low > 0 && char.IsHighSurrogate(line[low - 1])) low--;
            return line[..low] + Truncated;
        }

        private sealed record Entry(long Sequence, string Text, int Bytes);
        private sealed class Bucket(int maximumBytes)
        {
            private readonly Queue<Entry> entries = new();
            private int bytes;
            public IEnumerable<Entry> Entries => entries;
            public void Clear() { entries.Clear(); bytes = 0; }
            public void Add(Entry entry)
            {
                while (entries.Count > 0 && bytes + entry.Bytes > maximumBytes)
                    bytes -= entries.Dequeue().Bytes;
                entries.Enqueue(entry);
                bytes += entry.Bytes;
            }
        }
    }

    private static bool LegacySnapshotRequested(string[] arguments, bool defaultEnabled) =>
        !arguments.Contains("--battery-snapshot-off-test", StringComparer.OrdinalIgnoreCase) &&
        (defaultEnabled || arguments.Contains("--battery-init-snapshot-test", StringComparer.OrdinalIgnoreCase));

    private static bool LegacySnapshotValid(byte[] payload) =>
        payload.Any(value => value != 0) && BatteryLevelReading.TryDecodePrimary(payload, out var battery) && battery.HasLevel;

    private static string SnapshotKey(InitialBatterySnapshotResult r) =>
        $"{r.Payload?.Length ?? -1}|{r.SubscriptionReleased}|{r.Status}|{r.CleanupStatus}";

    private static async Task<InitialBatterySnapshotResult> LegacyCaptureAsync(
        Func<CancellationToken, Task<bool>> enable, Func<CancellationToken, Task<byte[]>> receive,
        Func<CancellationToken, Task<bool>> disableAndVerify, CancellationToken token, TimeSpan? timeout)
    {
        token.ThrowIfCancellationRequested();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(2);
        byte[]? payload = null;
        string status = "not_started", cleanupStatus = "not_started";
        bool released = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(limit);
        try
        {
            if (await enable(deadline.Token))
            {
                var received = await receive(deadline.Token);
                status = LegacySnapshotValid(received) ? "captured" : "invalid_report";
                if (status == "captured") payload = received;
            }
            else status = "subscribe_rejected";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { status = "timeout"; }
        catch (Exception ex) { status = "error_" + ex.GetType().Name; }
        finally
        {
            using var cleanup = new CancellationTokenSource(limit);
            try
            {
                released = await disableAndVerify(cleanup.Token);
                cleanupStatus = released ? "none_verified" : "none_not_verified";
            }
            catch (Exception ex) { cleanupStatus = "error_" + ex.GetType().Name; }
        }
        return new(payload, released, status, cleanupStatus);
    }

    private static bool LegacyIsAuditedStack(StackParameterAssessment a) =>
        a.EvidenceLevel == StackEvidenceLevel.AuditedHash;
    private static bool LegacyCanExperiment(StackParameterAssessment a) =>
        a.ValidSystemX64Image && a.EvidenceLevel != StackEvidenceLevel.Blocked;
    private static bool LegacyRequiresConsent(StackParameterAssessment a) =>
        LegacyCanExperiment(a) && !LegacyIsAuditedStack(a);
    private static bool LegacyCanApply(StackParameterAssessment a, bool consent) =>
        LegacyCanExperiment(a) && (LegacyIsAuditedStack(a) || consent);
    private static string LegacyEvidenceSummary(StackParameterAssessment a) =>
        LegacyIsAuditedStack(a) ? "已审查系统栈；仍须以真实 FD2 实测确认当前硬件是否采用参数。"
            : LegacyCanExperiment(a)
                ? "未审查系统栈（实验路线）；参数名线索 " + a.ParameterNamesFound.Count + "/4，不证明驱动读取这些值，也不保证 7.5ms。"
                : "不能执行此 Win10 实验：" + a.Reason;

    private static string StackKey(StackParameterAssessment a) =>
        $"{a.WindowsBuild}|{a.StackPath}|{a.StackVersion}|{a.StackSha256}|{a.EvidenceLevel}|" +
        $"{string.Join(",", a.ParameterNamesFound)}|{a.ValidSystemX64Image}|{a.Reason}";

    private static byte[] LegacyValidPe(Random random, bool includeNames, int nameCount = 0)
    {
        byte[] image = new byte[1024];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(image, 0x3C);
        int pe = 0x80;
        BitConverter.GetBytes(0x00004550u).CopyTo(image, pe);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(image, pe + 4);
        BitConverter.GetBytes((ushort)1).CopyTo(image, pe + 6);
        BitConverter.GetBytes((ushort)112).CopyTo(image, pe + 20);
        BitConverter.GetBytes((ushort)0x0002).CopyTo(image, pe + 22);
        int optional = pe + 24;
        BitConverter.GetBytes((ushort)0x20B).CopyTo(image, optional);
        BitConverter.GetBytes((ushort)1).CopyTo(image, optional + 68);
        BitConverter.GetBytes((uint)image.Length).CopyTo(image, optional + 60);
        if (includeNames)
        {
            string[] names = ["LEConnectionIntervalMin", "LEConnectionIntervalMax", "LEConnectionLatency", "LEConnectionLSTO"];
            int offset = 400;
            for (int i = 0; i < Math.Min(nameCount, names.Length); i++)
            {
                byte[] ascii = Encoding.ASCII.GetBytes(names[i]);
                ascii.CopyTo(image, offset);
                offset += ascii.Length + 1;
            }
        }
        return image;
    }

    private static string LegacyAnalyzeKey(int windowsBuild, bool windowsX64,
        string expectedSystemPath, string actualPath, byte[] image, string stackVersion)
    {
        string path = actualPath ?? "";
        if (windowsBuild < 19041 || windowsBuild >= 22000)
            return LegacyBlockedKey(windowsBuild, path, "仅支持 Windows 10 build 19041–21999；Win11 使用连接参数 API。");
        if (!windowsX64)
            return LegacyBlockedKey(windowsBuild, path, "仅支持 Windows x64 系统及 x64 程序。");
        try
        {
            if (!string.Equals(Path.GetFullPath(expectedSystemPath), Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(path), "bthport.sys", StringComparison.OrdinalIgnoreCase))
                return LegacyBlockedKey(windowsBuild, path, "目标不是指定的系统 bthport.sys。");
        }
        catch (Exception ex) { return LegacyBlockedKey(windowsBuild, path, "系统路径无效：" + ex.Message); }
        if (image == null || image.Length < 512 || image.Length > 64 * 1024 * 1024 || !LegacyIsValidX64Driver(image))
            return LegacyBlockedKey(windowsBuild, path, "bthport.sys 不是结构有效的 x64 PE 系统驱动。");
        string hash = Convert.ToHexString(SHA256.HashData(image));
        string[] found = BleStackProfile.Values.Keys.Where(name =>
            image.AsSpan().IndexOf(Encoding.ASCII.GetBytes(name)) >= 0 ||
            image.AsSpan().IndexOf(Encoding.Unicode.GetBytes(name)) >= 0).ToArray();
        bool audited = BleStackProfile.IsAudited(windowsBuild, hash);
        StackEvidenceLevel level = audited ? StackEvidenceLevel.AuditedHash : found.Length switch
        {
            0 => StackEvidenceLevel.ExperimentalNoParameterNames,
            4 => StackEvidenceLevel.ExperimentalAllParameterNames,
            _ => StackEvidenceLevel.ExperimentalPartialParameterNames
        };
        return $"{windowsBuild}|{path}|{stackVersion ?? "unknown"}|{hash}|{level}|" +
            $"{string.Join(",", found)}|True|" + (audited
                ? "精确匹配既有已审查系统栈；不同适配器仍需测量。"
                : "结构有效但此系统栈未审查。字符串只作线索；需明确同意可恢复实验，不作为驱动支持证明。");
    }

    private static string LegacyBlockedKey(int build, string path, string reason) =>
        $"{build}|{path}|unknown||Blocked||False|{reason}";

    private static bool LegacyIsValidX64Driver(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data[0] != 'M' || data[1] != 'Z') return false;
        int pe = BitConverter.ToInt32(data.Slice(0x3C, 4));
        if (pe < 0x40 || pe > data.Length - 24 ||
            BitConverter.ToUInt32(data.Slice(pe, 4)) != 0x00004550 ||
            BitConverter.ToUInt16(data.Slice(pe + 4, 2)) != 0x8664) return false;
        int sections = BitConverter.ToUInt16(data.Slice(pe + 6, 2));
        int optionalSize = BitConverter.ToUInt16(data.Slice(pe + 20, 2));
        int optional = pe + 24;
        if (sections < 1 || sections > 96 || optionalSize < 112 ||
            optional > data.Length - optionalSize ||
            BitConverter.ToUInt16(data.Slice(optional, 2)) != 0x20B ||
            BitConverter.ToUInt16(data.Slice(optional + 68, 2)) != 1 ||
            (BitConverter.ToUInt16(data.Slice(pe + 22, 2)) & 2) == 0) return false;
        int table = optional + optionalSize;
        if (table > data.Length - sections * 40) return false;
        uint headers = BitConverter.ToUInt32(data.Slice(optional + 60, 4));
        if (headers < table + sections * 40 || headers > data.Length) return false;
        for (int i = 0; i < sections; ++i)
        {
            int entry = table + i * 40;
            uint length = BitConverter.ToUInt32(data.Slice(entry + 16, 4));
            uint offset = BitConverter.ToUInt32(data.Slice(entry + 20, 4));
            if (length != 0 && (offset < headers || (ulong)offset + length > (ulong)data.Length)) return false;
        }
        return true;
    }

    // ---------------- 控制器拓扑：usbip 端口解析与设备分类 ----------------

    private static void Topology(Random random, CheckFn check, ref int cases, ref int bad)
    {
        string[] samples =
        [
            "",
            "Port 00: <Port in Use>\n       remote bus/dev 001/002\n",
            "Port 00: <Port in Use>\n       remote bus/dev 001/002\nPort 01: <Port in Use>\n       remote bus/dev 001/002\n",
            "Port 00: <Port in Use>\n       192.168.1.5:3241/1-1\n",
            "Port 00: <Port in Use>\n       remote bus/dev 002/003\nPort 01: <Port in Use>\n       127.0.0.1:3241/2-3\n",
            "Port 00: <Port in Use>\n       vid/pid 054c:0ce6 wireless controller\n",
            "Port 00: <Port in Use>\n       nothing here\n"
        ];
        int[] serverPorts = [3241, 3242];
        uint[] buses = [1, 2, 3];
        foreach (string sample in samples)
        {
            foreach (int serverPort in serverPorts)
            {
                foreach (uint bus in buses)
                {
                    IReadOnlyList<int> expected = LegacyLocate(sample, serverPort, bus, "2");
                    IReadOnlyList<int> actual =
                        ControllerTopologyInspector.LocatePortsForRemote(sample, serverPort, bus, "2");
                    check("topology", "locate fixed " + serverPort + "/" + bus, SamePorts(expected, actual),
                        ref cases, ref bad);
                }
            }
        }

        for (int round = 0; round < 400; round++)
        {
            int blocks = random.Next(0, 6);
            int serverPort = serverPorts[random.Next(serverPorts.Length)];
            uint bus = buses[random.Next(buses.Length)];
            string dev = random.Next(2, 4).ToString(CultureInfo.InvariantCulture);
            var text = new StringBuilder();
            int port = 0;
            for (int i = 0; i < blocks; i++)
            {
                port += random.Next(1, 3);
                text.Append("Port ").Append(port.ToString("00", CultureInfo.InvariantCulture)).AppendLine(": <Port in Use>");
                switch (random.Next(4))
                {
                    case 0:
                        text.Append("       remote bus/dev 00").Append(bus).Append('/').Append(dev).AppendLine();
                        break;
                    case 1:
                        text.Append("       ").Append(serverPort).Append('/').Append(bus).Append('-').Append(dev).AppendLine();
                        break;
                    case 2:
                        text.Append("       remote bus/dev 00").Append((bus % 3) + 1).Append("/000").Append(dev).AppendLine();
                        break;
                    default:
                        text.AppendLine("       unrelated payload");
                        break;
                }
            }

            string value = text.ToString();
            check("topology", "locate random " + round,
                SamePorts(LegacyLocate(value, serverPort, bus, dev),
                    ControllerTopologyInspector.LocatePortsForRemote(value, serverPort, bus, dev)),
                ref cases, ref bad);
        }

        int[][] portSets = [[], [3], [3, 3], [5, 3], [1, 2, 3], [4, 4, 2], [9, 1]];
        foreach (int[] set in portSets)
        {
            check("topology", "choose " + string.Join("-", set),
                SamePorts(LegacyChoose(set), ControllerTopologyInspector.ChoosePortsToDetach(set)),
                ref cases, ref bad);
        }

        string[] types = ["DualSenseEdge", "dualsensehaptic", "NS2Pro", "nspro", "Xbox", "other", "NS2PRO"];
        string[] vids = ["0x054C", "0x057E", "0x045E", "0x0000"];
        string[] pids = ["0x0DF2", "0x0CE6", "0x2069", "0x2009", "0x028E", "0x9999"];
        for (int round = 0; round < 600; round++)
        {
            var device = new BridgeDeviceInfo(
                buses[random.Next(buses.Length)],
                random.Next(1, 4).ToString(CultureInfo.InvariantCulture),
                vids[random.Next(vids.Length)],
                pids[random.Next(pids.Length)],
                types[random.Next(types.Length)]);
            check("topology", "bridge " + round,
                string.Equals(LegacyDescribeBridge(device), ControllerTopologyInspector.DescribeBridgeDevice(device),
                    StringComparison.Ordinal),
                ref cases, ref bad);
        }

        string[] pnpClasses = ["HIDClass", "USB", "HIDClass", ""];
        string[] services = ["HidUsb", "USBHUB3", "HidUsb", ""];
        string[] fragments =
        [
            "If_Hid", "HID_DEVICE_SYSTEM_GAME", "_U:0005", "_UP:0001", "VID_057E&PID_2009",
            "PID_0DF2", "PID_0CE6", "PID_2069", "PID_028E", "Wireless Controller", "Nintendo"
        ];
        for (int round = 0; round < 600; round++)
        {
            string combined = "name_" + round + " " + fragments[random.Next(fragments.Length)] + " " +
                              fragments[random.Next(fragments.Length)];
            string pnpClass = pnpClasses[random.Next(pnpClasses.Length)];
            string service = services[random.Next(services.Length)];
            check("topology", "pnp " + round,
                string.Equals(LegacyDescribePnp(combined, pnpClass, service),
                    ControllerTopologyInspector.DescribePnpEntity(combined, pnpClass, service), StringComparison.Ordinal),
                ref cases, ref bad);
            check("topology", "usage " + round,
                string.Equals(LegacyGuessUsage(combined), ControllerTopologyInspector.GuessHidUsage(combined),
                    StringComparison.Ordinal),
                ref cases, ref bad);
            check("topology", "vidpid " + round,
                string.Equals(LegacyVidPid(combined), ControllerTopologyInspector.FormatVidPid(combined),
                    StringComparison.Ordinal),
                ref cases, ref bad);
        }

        string[] messy = ["", "  a  \r\n\r\n b \n", "x\n\ny\n", "  ", "one"];
        foreach (string value in messy)
        {
            check("topology", "lines",
                string.Join("|", LegacyBreakLines(value)) == string.Join("|", ControllerTopologyInspector.BreakLines(value)),
                ref cases, ref bad);
            check("topology", "collapse",
                string.Equals(LegacyCollapse(value), ControllerTopologyInspector.CollapseLine(value), StringComparison.Ordinal),
                ref cases, ref bad);
        }
    }

    private static bool SamePorts(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    // ---- Legacy 语义（备份里的上游同名实现），只用于差分对照 ----

    private static readonly Regex LegacyPortRegex =
        new(@"^\s*Port\s+(\d+)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegacyVidPidRegex =
        new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record LegacyPortBlock(int Port, string Text);

    private static IReadOnlyList<int> LegacyLocate(string output, int usbServerPort, uint busId, string devId)
    {
        string endpointToken = ":" + usbServerPort + "/" + busId + "-" + devId;
        string remotePattern = @"remote\s+bus/dev\s+0*" + busId + @"/0*" + Regex.Escape(devId) + @"(?:\D|$)";
        var ports = new List<int>();
        foreach (LegacyPortBlock block in LegacyParse(output))
        {
            if (block.Text.Contains(endpointToken, StringComparison.OrdinalIgnoreCase) ||
                (block.Text.Contains(":" + usbServerPort + "/", StringComparison.OrdinalIgnoreCase) &&
                 Regex.IsMatch(block.Text, remotePattern, RegexOptions.IgnoreCase)))
            {
                ports.Add(block.Port);
            }
        }

        return ports;
    }

    private static IReadOnlyList<int> LegacyChoose(IEnumerable<int> ports)
    {
        int[] distinct = ports.Distinct().Order().ToArray();
        return distinct.Length == 1 ? Array.Empty<int>() : distinct;
    }

    private static IReadOnlyList<LegacyPortBlock> LegacyParse(string output)
    {
        var blocks = new List<LegacyPortBlock>();
        int? currentPort = null;
        var current = new StringBuilder();
        foreach (string line in LegacyBreakLines(output))
        {
            Match match = LegacyPortRegex.Match(line);
            if (match.Success)
            {
                if (currentPort.HasValue)
                {
                    blocks.Add(new LegacyPortBlock(currentPort.Value, current.ToString()));
                }

                currentPort = int.Parse(match.Groups[1].Value);
                current.Clear();
            }

            current.AppendLine(line);
        }

        if (currentPort.HasValue)
        {
            blocks.Add(new LegacyPortBlock(currentPort.Value, current.ToString()));
        }

        return blocks;
    }

    private static string[] LegacyBreakLines(string value) =>
        (value ?? "").Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string LegacyCollapse(string value) =>
        (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");

    private static string LegacyDescribeBridge(BridgeDeviceInfo device)
    {
        string type = device.Type.ToLowerInvariant();
        string id = (device.Vid + " " + device.Pid).ToLowerInvariant();
        if (type.Contains("dualsenseedge") || id.Contains("0xdf2"))
        {
            return "ps5_edge_virtual_dualsenseedge_l4_r4";
        }

        if (type.Contains("dualsensehaptic") || id.Contains("0xce6"))
        {
            return "ps5_virtual_dualsensehaptic_hd_audio";
        }

        if (type.Contains("ns2pro") || id.Contains("2069"))
        {
            return "nintendo_pro2_virtual";
        }

        if (type == "nspro" || id.Contains("2009"))
        {
            return "nintendo_pro1_virtual";
        }

        if (type.Contains("xbox") || id.Contains("028e"))
        {
            return "xinput_virtual";
        }

        return "unknown_viiper_device";
    }

    private static string LegacyDescribePnp(string combined, string pnpClass, string service)
    {
        if (combined.Contains("VID_057E", StringComparison.OrdinalIgnoreCase) &&
            combined.Contains("PID_2009", StringComparison.OrdinalIgnoreCase))
        {
            return "nintendo_pro1_virtual_or_physical";
        }

        if (combined.Contains("PID_0DF2", StringComparison.OrdinalIgnoreCase))
        {
            return "ps5_edge_virtual_or_physical";
        }

        if (combined.Contains("PID_0CE6", StringComparison.OrdinalIgnoreCase))
        {
            return "ps5_dualsense_virtual_or_physical";
        }

        if (combined.Contains("PID_2069", StringComparison.OrdinalIgnoreCase))
        {
            return combined.Contains("If_Hid", StringComparison.OrdinalIgnoreCase)
                ? "nintendo_interface_if_hid_descriptor_name"
                : "nintendo_pro_controller_interface";
        }

        if (combined.Contains("PID_028E", StringComparison.OrdinalIgnoreCase))
        {
            return "xinput_compat_interface";
        }

        if (combined.Contains("If_Hid", StringComparison.OrdinalIgnoreCase))
        {
            return "if_hid_needs_descriptor_usage_review";
        }

        if (string.Equals(pnpClass, "HIDClass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service, "HidUsb", StringComparison.OrdinalIgnoreCase))
        {
            return "hid_interface";
        }

        return "matched_non_hid_or_parent";
    }

    private static string LegacyGuessUsage(string text)
    {
        if (text.Contains("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("_U:0005", StringComparison.OrdinalIgnoreCase))
        {
            return "game_controller";
        }

        if (text.Contains("_UP:0001", StringComparison.OrdinalIgnoreCase))
        {
            return "generic_desktop";
        }

        return "unknown_from_pnp";
    }

    private static string LegacyVidPid(string text)
    {
        Match match = LegacyVidPidRegex.Match(text);
        return match.Success
            ? match.Groups[1].Value.ToUpperInvariant() + ":" + match.Groups[2].Value.ToUpperInvariant()
            : "none";
    }

    // ---------------- 小件批次：HD 震动换算 / 触摸手势 / 自启路径 / 配置历史 ----------------

    private static void SmallModules(Random random, CheckFn check, ref int cases, ref int bad)
    {
        ProtocolAuditTrace(check, ref cases, ref bad);
        AdaptiveTrace(check, ref cases, ref bad);
        HdRumble(random, check, ref cases, ref bad);
        TouchGestures(random, check, ref cases, ref bad);
        LaunchQuote(random, check, ref cases, ref bad);
        StackHistory(random, check, ref cases, ref bad);
    }

    private static void HdRumble(Random random, CheckFn check, ref int cases, ref int bad)
    {
        byte[][] fixedFrames =
        [
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x01, 0x03],
            [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x02, 0x01],
            [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x00, 0x00],
            [0x7F, 0x01, 0xFF, 0x40, 0x80, 0x01, 0x00, 0x7F, 0x03, 0x02]
        ];

        foreach (byte[] frame in fixedFrames)
        {
            check("small", "hd fixed " + Convert.ToHexString(frame), HdMap(frame) == LegacyHdMap(frame), ref cases, ref bad);
            check("small", "hd pair " + Convert.ToHexString(frame),
                DecodeHd(frame.AsSpan(0, 4)) == LegacyDecodeHd(frame.AsSpan(0, 4)), ref cases, ref bad);
        }

        check("small", "hd short", DecodeHd([1, 2, 3]) == LegacyDecodeHd([1, 2, 3]), ref cases, ref bad);
        check("small", "hd empty", DecodeHd([]) == LegacyDecodeHd([]), ref cases, ref bad);

        for (int i = 0; i < 1500; i++)
        {
            var frame = new byte[random.Next(2) == 0 ? 10 : random.Next(0, 13)];
            random.NextBytes(frame);
            check("small", "hd map " + i, HdMap(frame) == LegacyHdMap(frame), ref cases, ref bad);

            var quad = new byte[random.Next(4, 9)];
            random.NextBytes(quad);
            check("small", "hd decode " + i, DecodeHd(quad) == LegacyDecodeHd(quad), ref cases, ref bad);
        }
    }

    private static string DecodeHd(ReadOnlySpan<byte> raw)
    {
        try
        {
            SwitchHdRumbleMapper.HdPair pair = SwitchHdRumbleMapper.DecodePair(raw);
            return pair.LowHz + "," + pair.LowAmplitude + "," + pair.HighHz + "," + pair.HighAmplitude;
        }
        catch (ArgumentException ex)
        {
            return "throw:" + ex.ParamName + ":" + ex.Message;
        }
    }

    private static string HdMap(byte[] feedback)
    {
        bool ok = SwitchHdRumbleMapper.TryMap(feedback, out Pro2OutputPacket packet, out string reason);
        return ok ? HdPacketText(packet) : "false|" + reason;
    }

    private static string HdPacketText(Pro2OutputPacket packet) =>
        "true|" + Convert.ToHexString(packet.Report) + "|" + packet.Source + "|" + packet.Active + "|" +
        (packet.PlayerLedMask?.ToString(CultureInfo.InvariantCulture) ?? "-") + "|" + packet.FrequencyClamps + "|" +
        packet.ConversionTelemetry;

    // 旧实现（原 Switch1RumbleConverter）逐字复刻，作为期望值。
    private static readonly ushort[] LegacyHdAmplitudes =
    [
        0, 514, 775, 921, 1096, 1303, 1550, 1843, 2192, 2606, 3100, 3686, 4383, 5213, 6199, 7372,
        7698, 8039, 8395, 8767, 9155, 9560, 9984, 10426, 10887, 11369, 11873, 12398, 12947, 13520,
        14119, 14744, 15067, 15397, 15734, 16079, 16431, 16790, 17158, 17534, 17918, 18310, 18711,
        19121, 19540, 19967, 20405, 20851, 21308, 21775, 22251, 22739, 23236, 23745, 24265, 24797,
        25340, 25894, 26462, 27041, 27633, 28238, 28856, 29488, 30134, 30794, 31468, 32157, 32861,
        33581, 34316, 35068, 35836, 36620, 37422, 38242, 39079, 39935, 40809, 41703, 42616, 43549,
        44503, 45477, 46473, 47491, 48531, 49593, 50679, 51789, 52923, 54082, 55266, 56476, 57713,
        58977, 60268, 61588, 62936, 64315, 65535
    ];

    private static ushort LegacyHdFrequency(int code) =>
        (ushort)Math.Clamp(Math.Round(10 * Math.Pow(2, code / 32.0)), 40, 1253);

    private static ushort LegacyHdAmplitude(int code) =>
        (ushort)((LegacyHdAmplitudes[Math.Clamp(code, 0, LegacyHdAmplitudes.Length - 1)] * 29000u + 32767u) / 65535u);

    private static ushort[] LegacyDecodeQuad(ReadOnlySpan<byte> encoded)
    {
        int highCode = ((encoded[0] | ((encoded[1] & 1) << 8)) >> 2) + 0x60;
        int lowCode = (encoded[2] & 0x7F) + 0x40;
        int highAmplitude = encoded[1] >> 1;
        int lowAmplitude = encoded[3] >= 0x40 ? ((encoded[3] - 0x40) * 2 + (encoded[2] >> 7)) : 0;
        return
        [
            LegacyHdFrequency(lowCode), LegacyHdAmplitude(lowAmplitude),
            LegacyHdFrequency(highCode), LegacyHdAmplitude(highAmplitude)
        ];
    }

    private static string LegacyDecodeHd(ReadOnlySpan<byte> encoded)
    {
        try
        {
            if (encoded.Length < 4)
            {
                throw new ArgumentException("Switch Pro rumble requires four bytes.", nameof(encoded));
            }

            ushort[] quad = LegacyDecodeQuad(encoded);
            return quad[0] + "," + quad[1] + "," + quad[2] + "," + quad[3];
        }
        catch (ArgumentException ex)
        {
            return "throw:" + ex.ParamName + ":" + ex.Message;
        }
    }

    private static string LegacyHdMap(byte[] feedback)
    {
        if (feedback.Length != 10) return "false|Switch Pro feedback must be ten bytes";
        byte? led = (feedback[8] & 2) != 0 ? feedback[9] : null;
        if ((feedback[8] & 1) == 0)
        {
            return "false|" + (led.HasValue
                ? "led-only feedback has no physical Pro2 rumble payload"
                : "feedback has no rumble flag");
        }

        ushort[] left = LegacyDecodeQuad(feedback.AsSpan(0, 4));
        ushort[] right = LegacyDecodeQuad(feedback.AsSpan(4, 4));
        int clamps =
            (left[1] != 0 && RumbleFrequencyLimits.WouldClamp(left[0]) ? 1 : 0) +
            (left[3] != 0 && RumbleFrequencyLimits.WouldClamp(left[2]) ? 1 : 0) +
            (right[1] != 0 && RumbleFrequencyLimits.WouldClamp(right[0]) ? 1 : 0) +
            (right[3] != 0 && RumbleFrequencyLimits.WouldClamp(right[2]) ? 1 : 0);
        Pro2OutputPacket packet = Pro2OutputPacketMapper.BuildRaw02Packet(
            RumbleFrequencyLimits.FromEstimatedHz(left[0]), left[1],
            RumbleFrequencyLimits.FromEstimatedHz(left[2]), left[3],
            RumbleFrequencyLimits.FromEstimatedHz(right[0]), right[1],
            RumbleFrequencyLimits.FromEstimatedHz(right[2]), right[3],
            "switch1-hd-converted") with
        {
            PlayerLedMask = led,
            FrequencyClamps = clamps,
            ConversionTelemetry =
                $"frequency_mapping={RumbleFrequencyLimits.Mapping} frequency_clamps={clamps}" +
                $" source_left_hz={left[0]},{left[2]} source_right_hz={right[0]},{right[2]}"
        };
        return HdPacketText(packet);
    }

    // ---------------- 触摸手势 ----------------

    private static void TouchGestures(Random random, CheckFn check, ref int cases, ref int bad)
    {
        double[] progresses = [0, 0.25, 0.5, 1, -0.4, 1.9, double.NaN, double.PositiveInfinity, double.NegativeInfinity];

        for (int value = 0; value <= 11; value++)
        {
            var gesture = (Ps5TouchGesture)value;
            foreach (double progress in progresses)
            {
                check("small", "touch point " + value + "/" + progress,
                    TouchPoint(gesture, progress) == LegacyTouchPoint(gesture, progress), ref cases, ref bad);
            }
        }

        for (int i = 0; i < 800; i++)
        {
            var gesture = (Ps5TouchGesture)random.Next(0, 13);
            double progress = random.Next(3) == 0
                ? progresses[random.Next(progresses.Length)]
                : (random.NextDouble() * 1.4) - 0.2;
            check("small", "touch fuzz " + i, TouchPoint(gesture, progress) == LegacyTouchPoint(gesture, progress),
                ref cases, ref bad);
        }

        string[] labels = ["无", "中央轻触", "向上滑动", "向下滑动", "向左滑动", "向右滑动", "顺时针画圈", "逆时针画圈"];
        check("small", "touch choices",
            Ps5TouchGestures.Choices.Count == labels.Length &&
            Ps5TouchGestures.Choices.Select(c => c.Label).SequenceEqual(labels) &&
            Ps5TouchGestures.Choices.Select(c => (int)c.Gesture).SequenceEqual(Enumerable.Range(0, 8)),
            ref cases, ref bad);
        check("small", "touch constants",
            Ps5TouchGestures.MinDurationMs == 180 && Ps5TouchGestures.DefaultDurationMs == 400 &&
            Ps5TouchGestures.MaxX == 1919 && Ps5TouchGestures.MaxY == 1079, ref cases, ref bad);

        for (int run = 0; run < 150; run++)
        {
            var player = new Ps5TouchPlayback();
            var legacy = new LegacyTouchPlayback();
            for (int step = 0; step < 24; step++)
            {
                string expected, actual;
                if (random.Next(9) == 0)
                {
                    actual = NormalizeTouchFrame(player.Cancel());
                    expected = legacy.Cancel();
                }
                else
                {
                    var requested = (Ps5TouchGesture)random.Next(0, 10);
                    long token = random.Next(0, 4);
                    double fraction = random.Next(4) == 0
                        ? progresses[random.Next(progresses.Length)]
                        : random.NextDouble();
                    actual = NormalizeTouchFrame(player.Sample(requested, token, fraction));
                    expected = legacy.Sample(requested, token, fraction);
                }

                check("small", "touch replay " + run + "/" + step, expected == actual, ref cases, ref bad);
            }

            check("small", "touch pending " + run, player.HasPending == legacy.HasPending, ref cases, ref bad);
            if (random.Next(3) == 0)
            {
                player.Reset();
                legacy.Reset();
                check("small", "touch reset " + run, player.HasPending == legacy.HasPending, ref cases, ref bad);
            }
        }
    }

    private static string TouchPoint(Ps5TouchGesture gesture, double progress)
    {
        Ps5TouchPoint point = Ps5TouchGestures.PointAt(gesture, progress);
        return (point.Active ? "1" : "0") + "," + point.X + "," + point.Y;
    }

    private static string NormalizeTouchFrame(Ps5TouchFrame frame) =>
        (frame.Point.Active ? "1" : "0") + "," + frame.Point.X + "," + frame.Point.Y + "," + (frame.Started ? "1" : "0");

    private static string LegacyTouchPoint(Ps5TouchGesture gesture, double progress)
    {
        double p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        double x = 960, y = 540;
        switch (gesture)
        {
            case Ps5TouchGesture.SwipeUp: y = 900 - 720 * p; break;
            case Ps5TouchGesture.SwipeDown: y = 180 + 720 * p; break;
            case Ps5TouchGesture.SwipeLeft: x = 1660 - 1400 * p; break;
            case Ps5TouchGesture.SwipeRight: x = 260 + 1400 * p; break;
            case Ps5TouchGesture.CircleClockwise:
            case Ps5TouchGesture.CircleCounterClockwise:
                double angle = -Math.PI / 2 + (gesture == Ps5TouchGesture.CircleClockwise ? 1 : -1) * 2 * Math.PI * p;
                x += 460 * Math.Cos(angle);
                y += 340 * Math.Sin(angle);
                break;
        }

        return (gesture != Ps5TouchGesture.None && Enum.IsDefined(gesture) ? "1" : "0") + "," +
            (ushort)Math.Clamp((int)Math.Round(x), 0, Ps5TouchGestures.MaxX) + "," +
            (ushort)Math.Clamp((int)Math.Round(y), 0, Ps5TouchGestures.MaxY);
    }

    // 旧实现（原 Ps5TouchPlayback）逐字复刻。
    private sealed class LegacyTouchPlayback
    {
        private Ps5TouchGesture gesture;
        private long pulse, completedPulse;
        private bool hasCompleted;
        private double progress;
        private Ps5TouchPoint point;

        public bool HasPending => point.Active;

        public void Reset()
        {
            gesture = Ps5TouchGesture.None;
            pulse = completedPulse = 0;
            hasCompleted = false;
            progress = 0;
            point = default;
        }

        public string Cancel()
        {
            if (point.Active)
            {
                completedPulse = pulse;
                hasCompleted = true;
            }

            point = point with { Active = false };
            return NormalizeTouchFrame(new Ps5TouchFrame(point));
        }

        public string Sample(Ps5TouchGesture requested, long token, double fraction)
        {
            if (point.Active)
            {
                if (requested == gesture && token == pulse)
                {
                    progress = Math.Max(progress, Math.Clamp(fraction, 0, 1));
                    point = Ps5TouchGestures.PointAt(gesture, progress);
                    return NormalizeTouchFrame(new Ps5TouchFrame(point));
                }

                if (progress < 1)
                {
                    progress = 1;
                    point = Ps5TouchGestures.PointAt(gesture, 1);
                    return NormalizeTouchFrame(new Ps5TouchFrame(point));
                }

                return Cancel();
            }

            if (requested == Ps5TouchGesture.None || (hasCompleted && token == completedPulse))
            {
                return NormalizeTouchFrame(new Ps5TouchFrame(point));
            }

            gesture = requested;
            pulse = token;
            progress = 0;
            point = Ps5TouchGestures.PointAt(gesture, 0);
            return NormalizeTouchFrame(new Ps5TouchFrame(point, Started: true));
        }
    }

    // ---------------- 开机自启路径 ----------------

    private static void LaunchQuote(Random random, CheckFn check, ref int cases, ref int bad)
    {
        string[] paths =
        [
            @"C:\Program Files\Pro2+\Pro2+.exe",
            "\"C:\\Tools\\Pro2+.exe\"",
            "  C:\\a b\\x.exe  ",
            "\"  C:\\mixed\\y.exe  \"",
            "", "   ", "\"", "\"\"", "plain.exe", "\"\"\"x\"\"\""
        ];

        foreach (string path in paths)
        {
            check("small", "quote " + path, LaunchAtLoginEntry.Quote(path) == LegacyQuote(path), ref cases, ref bad);
        }

        for (int i = 0; i < 300; i++)
        {
            string path = RandomText(random, "AB\\c d\"' .exe", random.Next(0, 22));
            check("small", "quote fuzz " + i, LaunchAtLoginEntry.Quote(path) == LegacyQuote(path), ref cases, ref bad);
        }

        check("small", "run value name", LaunchAtLoginEntry.Quote(@"C:\x\Pro2+.exe") == "\"C:\\x\\Pro2+.exe\"",
            ref cases, ref bad);

        // 自启必须带上 --tray，否则登录时会弹出主界面。
        check("small", "run value tray arg",
            LaunchAtLoginEntry.BuildRunValue(@"C:\x\Pro2+.exe") == "\"C:\\x\\Pro2+.exe\" --tray",
            ref cases, ref bad);
        check("small", "run value tray arg trim",
            LaunchAtLoginEntry.BuildRunValue("  \"C:\\a b\\Pro2+.exe\"  ") == "\"C:\\a b\\Pro2+.exe\" --tray",
            ref cases, ref bad);

        // 配对监听判据：有保存地址时只认该地址，没有才按名字退化。
        check("pairing", "saved address wins",
            BlePairingWatchPolicy.Accepts(0xA1B2C3, 0xA1B2C3, ""), ref cases, ref bad);
        check("pairing", "other address rejected",
            !BlePairingWatchPolicy.Accepts(0xA1B2C4, 0xA1B2C3, "Pro Controller"), ref cases, ref bad);
        check("pairing", "no saved accepts pro name",
            BlePairingWatchPolicy.Accepts(0x11, 0, "Pro Controller"), ref cases, ref bad);
        check("pairing", "no saved rejects other name",
            !BlePairingWatchPolicy.Accepts(0x11, 0, "Joy-Con (L)"), ref cases, ref bad);
        check("pairing", "no saved rejects empty name",
            !BlePairingWatchPolicy.Accepts(0x11, 0, ""), ref cases, ref bad);
    }

    private static string LegacyQuote(string path) => "\"" + path.Trim().Trim('"') + "\"";

    // ---------------- Win10 BLE 配置历史 ----------------

    private static readonly DateTimeOffset HistoryNow = DateTimeOffset.Parse(
        "2026-11-21T12:00:00+08:00", CultureInfo.InvariantCulture);

    private static void StackHistory(Random random, CheckFn check, ref int cases, ref int bad)
    {
        string[] fixedJson =
        [
            "", "   ", "{}", "[]", "null", "\"text\"", "not json", "{\"operation\":\"apply\"}",
            HistoryJson("apply", "7p5", "2026-11-20T10:00:00+08:00", true, false),
            HistoryJson("apply", "7p5", "2026-11-20T10:00:00+08:00", false, false),
            HistoryJson("apply", "7p5", "2026-11-22T10:00:00+08:00", true, false),
            HistoryJson("restore", "original", "2026-11-20T10:00:00+08:00", false, true),
            HistoryJson("restore", "7p5", "2026-11-20T10:00:00+08:00", false, true),
            HistoryJson("apply", "flexible", "2026-11-20T10:00:00+08:00", false, true),
            HistoryJson("apply", "15", "2026-11-20T10:00:00+08:00", true, false),
            "{\"operation\":\"apply\",\"preset\":\"7p5\",\"completedAt\":\"2026-11-20T10:00:00+08:00\"," +
            "\"restartSucceeded\":true,\"restartRequired\":false,\"preset\":\"15\"}",
            "{\"operation\":\"apply\",\"preset\":\"7p5\",\"completedAt\":123,\"restartSucceeded\":true," +
            "\"restartRequired\":false}",
            "{\"operation\":\"apply\",\"preset\":\"7p5\",\"completedAt\":\"2026-11-20T10:00:00+08:00\"," +
            "\"restartSucceeded\":\"yes\",\"restartRequired\":false}",
            "\uFEFF" + HistoryJson("apply", "7p5", "2026-11-20T10:00:00+08:00", true, false),
            new string('x', 70000)
        ];

        foreach (string json in fixedJson)
        {
            CompareHistory(json, check, ref cases, ref bad);
        }

        check("small", "history format null",
            BleStackOperationLog.Format(null) == LegacyFormatStack(null), ref cases, ref bad);
        check("small", "history format win11",
            BleStackOperationLog.Format(null, false) == LegacyFormatStack(null, false), ref cases, ref bad);

        for (int i = 0; i < 900; i++)
        {
            CompareHistory(BuildHistoryJson(random), check, ref cases, ref bad);
        }
    }

    private static void CompareHistory(string json, CheckFn check, ref int cases, ref int bad)
    {
        StackOperationRecord actual = BleStackOperationLog.Parse(json, HistoryNow);
        StackOperationRecord expected = LegacyParseRecord(json, HistoryNow);
        string name = "history " + json.Length + "/" + json.GetHashCode().ToString(CultureInfo.InvariantCulture);
        check("small", name, NormalizeRecord(actual) == NormalizeRecord(expected), ref cases, ref bad);
        check("small", name + " fmt", BleStackOperationLog.Format(actual) == LegacyFormatStack(expected), ref cases, ref bad);
        check("small", name + " win11",
            BleStackOperationLog.Format(actual, false) == LegacyFormatStack(expected, false), ref cases, ref bad);
    }

    private static string HistoryJson(string operation, string preset, string completedAt, bool succeeded, bool required) =>
        "{\"operation\":\"" + operation + "\",\"preset\":\"" + preset + "\",\"completedAt\":\"" + completedAt +
        "\",\"restartSucceeded\":" + (succeeded ? "true" : "false") +
        ",\"restartRequired\":" + (required ? "true" : "false") + "}";

    private static string BuildHistoryJson(Random random)
    {
        string[] operations = ["\"apply\"", "\"restore\"", "\"Apply\"", "\"\"", "1", "null"];
        string[] presets = ["\"7p5\"", "\"flexible\"", "\"15\"", "\"original\"", "\"7P5\"", "\"\"", "[]"];
        string[] stamps =
        [
            "\"2026-11-20T10:00:00+08:00\"", "\"2026-11-21T12:00:00+08:00\"", "\"2026-11-22T00:00:00+08:00\"",
            "\"not-a-date\"", "\"\"", "\"2026-11-20T10:00:00Z\"", "12345", "null"
        ];
        string[] flags = ["true", "false", "\"true\"", "0", "null"];

        var parts = new List<string>(6);
        if (random.Next(12) != 0) parts.Add("\"operation\":" + operations[random.Next(operations.Length)]);
        if (random.Next(12) != 0) parts.Add("\"preset\":" + presets[random.Next(presets.Length)]);
        if (random.Next(12) != 0) parts.Add("\"completedAt\":" + stamps[random.Next(stamps.Length)]);
        if (random.Next(12) != 0) parts.Add("\"restartSucceeded\":" + flags[random.Next(flags.Length)]);
        if (random.Next(12) != 0) parts.Add("\"restartRequired\":" + flags[random.Next(flags.Length)]);
        if (random.Next(14) == 0) parts.Add("\"preset\":" + presets[random.Next(presets.Length)]);
        if (parts.Count == 0) parts.Add("\"operation\":\"apply\"");

        string body = "{" + string.Join(",", parts) + "}";
        return random.Next(6) == 0 ? " \n" + body + "  " : body;
    }

    private static string NormalizeRecord(StackOperationRecord record) =>
        (record.Valid ? "1" : "0") + "|" + record.Code + "|" + (record.Operation ?? "-") + "|" +
        (record.Preset ?? "-") + "|" +
        (record.CompletedAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-") + "|" +
        (record.RestartSucceeded?.ToString() ?? "-") + "|" + (record.RestartRequired?.ToString() ?? "-");

    private static readonly string[] LegacyHistoryFields =
        ["operation", "preset", "completedAt", "restartSucceeded", "restartRequired"];

    // 旧实现（原 Win10BleDiagnosticHistory）逐字复刻，作为期望值。
    private static StackOperationRecord LegacyParseRecord(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(false, "history_absent");
        if (Encoding.UTF8.GetByteCount(json) > 65536) return new(false, "history_oversize");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object) return new(false, "history_invalid");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (LegacyHistoryFields.Contains(property.Name, StringComparer.Ordinal) && !seen.Add(property.Name))
                {
                    return new(false, "history_duplicate_field");
                }
            }

            if (LegacyHistoryFields.Any(name => !seen.Contains(name)) ||
                !LegacyHistoryString(item, "operation", out string? operation) ||
                !LegacyHistoryString(item, "preset", out string? preset) ||
                !LegacyHistoryString(item, "completedAt", out _) ||
                !item.GetProperty("completedAt").TryGetDateTimeOffset(out DateTimeOffset completedAt) ||
                completedAt > now ||
                item.GetProperty("restartSucceeded").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                item.GetProperty("restartRequired").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return new(false, "history_invalid");
            }

            bool succeeded = item.GetProperty("restartSucceeded").GetBoolean();
            bool required = item.GetProperty("restartRequired").GetBoolean();
            if (succeeded == required) return new(false, "history_inconsistent_restart");
            bool validPreset = operation == "apply" && BlePresetCatalog.Presets.Any(p => p.Id == preset) ||
                operation == "restore" && preset == "original";
            if (!validPreset) return new(false, "history_invalid_operation_or_preset");
            return new(true, "history_valid", operation, preset, completedAt, succeeded, required);
        }
        catch (JsonException)
        {
            return new(false, "history_invalid_json");
        }
    }

    private static bool LegacyHistoryString(JsonElement item, string name, out string? value)
    {
        value = null;
        if (item.GetProperty(name).ValueKind != JsonValueKind.String) return false;
        value = item.GetProperty(name).GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string LegacyFormatStack(StackOperationRecord? history, bool isWindows10 = true)
    {
        var text = new StringBuilder();
        text.AppendLine("Win10 系统 BLE 参数预设与配置记录");
        if (!isWindows10)
        {
            text.AppendLine("当前为 Win11：不套用 Win10 系统配置；请查看本连接的 API 预设结果。");
            return text.ToString().TrimEnd();
        }

        text.AppendLine("可用方法（首页授权后测试；此页只读，不执行修改）：");
        foreach (BlePresetDefinition preset in BlePresetCatalog.Presets)
        {
            text.AppendLine(preset.Id + " · " + preset.Title + "：Min=" + preset.IntervalMinUnits +
                " / Max=" + preset.IntervalMaxUnits + " / Latency=" + preset.Latency +
                " / LSTO=" + preset.TimeoutUnits + "（" + LegacyHistoryNumber(preset.MinimumIntervalMs) +
                "–" + LegacyHistoryNumber(preset.MaximumIntervalMs) + "ms；监督超时 " +
                LegacyHistoryNumber(preset.TimeoutUnits * 10.0) + "ms）");
        }

        text.AppendLine("参数目录不是执行结果；以当前真实 FD2 / Parsed 测量判定速率。");
        if (history is not { Valid: true, CompletedAt: not null, Operation: not null, Preset: not null,
            RestartSucceeded: not null, RestartRequired: not null })
        {
            text.AppendLine("历史配置操作：" + LegacyExplainUnknown(history?.Code ?? "history_absent"));
            text.AppendLine("不能据此认定所有预设均未测试、请求被拒绝或蓝牙硬件不支持。");
            return text.ToString().TrimEnd();
        }

        if (history.RestartSucceeded == history.RestartRequired ||
            !(history.Operation == "apply" && BlePresetCatalog.Presets.Any(p => p.Id == history.Preset) ||
              history.Operation == "restore" && history.Preset == "original"))
        {
            return LegacyFormatStack(new(false, "history_invalid"));
        }

        text.AppendLine("历史配置操作（非本次连接验收）：");
        text.AppendLine("完成时间：" + history.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        string presetTitle = history.Operation == "restore" ? "原始备份" :
            BlePresetCatalog.Presets.Single(p => p.Id == history.Preset).Title;
        text.AppendLine("操作：" + (history.Operation == "restore" ? "恢复" : "应用") +
            "；预设：" + history.Preset + " / " + presetTitle);
        text.AppendLine("该次记录：restartSucceeded=" + (history.RestartSucceeded.Value ? "True" : "False") +
            "；restartRequired=" + (history.RestartRequired.Value ? "True" : "False"));
        text.AppendLine(history.RestartSucceeded.Value ?
            "该次适配器重启已完成；不证明本次连接采用了参数或达到 125Hz。" :
            "该次操作提示需重启 Windows；之后可能已重启，不能据此判定当前仍需重启。");
        text.AppendLine("此文件仅保留最近一次配置操作，不是三组预设的完整测量历史。");
        return text.ToString().TrimEnd();
    }

    private static string LegacyHistoryNumber(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string LegacyExplainUnknown(string code) => code switch
    {
        "history_absent" => "未找到；执行结果未记录。",
        "history_oversize" => "文件超过 64KiB，未读取；结果未知。",
        _ => "记录不可用（" + code.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ') + "）；结果未知。"
    };

    // ---------------- BLE 会话审计：行为指纹 ----------------

    // 同一套脚本事件（固定种子）跑出的全部输出取 SHA256，作为重写前后的行为指纹。
    // 事件含短包/超长包/重复包/时间戳回退/非有限回调年龄/多次 End/重连，覆盖计数与排版两侧。
    private const string ProtocolAuditTraceExpected = "9D2EAA6287033BB9:329170";

    private static string? protocolAuditTrace;

    private static void ProtocolAuditTrace(CheckFn check, ref int cases, ref int bad)
    {
        string trace = protocolAuditTrace ??= ComputeProtocolAuditTrace();
        check("small", "protocol audit trace=" + trace, trace == ProtocolAuditTraceExpected, ref cases, ref bad);
    }

    private static string ComputeProtocolAuditTrace()
    {
        var random = new Random(20261122);
        var audit = new BleProtocolSessionAudit();
        var text = new StringBuilder();
        long ticks = 0;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            audit.Begin(random.Next(2) == 0, ticks += random.Next(1000, 50000));
            for (int step = 0; step < 220; step++)
            {
                switch (random.Next(7))
                {
                    case 0:
                        audit.Connected(random.Next(2) == 0 ? "windows" : "gatt", ticks += random.Next(0, 900));
                        break;
                    case 1:
                        audit.SetPdu((ushort)random.Next(0, 500));
                        break;
                    case 2:
                        audit.Note("note_" + random.Next(100) + (random.Next(4) == 0 ? "\r\nx" : ""),
                            ticks += random.Next(0, 400));
                        break;
                    case 3:
                    case 4:
                    {
                        var payload = new byte[random.Next(0, 60)];
                        random.NextBytes(payload);
                        for (int i = 4; i < Math.Min(10, payload.Length); i++)
                        {
                            if (random.Next(4) == 0) payload[i] = 0;
                        }

                        double age = random.Next(5) == 0 ? double.NaN : (random.NextDouble() * 40) - 5;
                        audit.RecordFd2(payload, ticks += random.Next(0, 12), age);
                        break;
                    }
                    case 5:
                        text.AppendLine(audit.Summary(ticks += random.Next(0, 100)));
                        break;
                    default:
                        text.AppendLine(audit.End("r" + random.Next(5), ticks += random.Next(0, 500)));
                        break;
                }
            }

            text.AppendLine(audit.DiagnosticText(ticks += random.Next(0, 1000)));
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash)[..16] + ":" + text.Length;
    }

    // ---------------- 蓝牙自适应诊断：行为指纹 ----------------

    private const string AdaptiveTraceExpected = "97DF4B162FFD760A:336876";
    // 英文版的诊断文本走另一套译文，指纹必然不同；两套都视为已知正常输出。
    private const string AdaptiveTraceExpectedEnglish = "FFA3AA2C4299D6A7:361872";

    private static string? adaptiveTrace;

    private static void AdaptiveTrace(CheckFn check, ref int cases, ref int bad)
    {
        string trace = adaptiveTrace ??= ComputeAdaptiveTrace();
        check("small", "adaptive trace=" + trace,
            trace == AdaptiveTraceExpected || trace == AdaptiveTraceExpectedEnglish, ref cases, ref bad);
    }

    private static string ComputeAdaptiveTrace()
    {
        var random = new Random(20261123);
        var text = new StringBuilder();
        string[] states =
        [
            "running", "completed", "cancelled", "error", "not_started", "not_recorded", "interrupted",
            "historical", "historical_legacy", "weird_state", ""
        ];
        string[] codes =
        [
            "native_measurement_not_ready", "native_target_preserved", "win11_api_unavailable", "win10_native_only",
            "win10_audited_configuration_requires_opt_in", "win10_experimental_configuration_requires_opt_in",
            "win11_bounded_presets", "request_rejected", "target_reached", "playable_improvement",
            "partial_improvement", "stability_improvement", "incumbent_is_better", "no_meaningful_improvement",
            "measured_rate_regressed", "measured_stability_regressed", "invalid_or_stale_measurement", "unequal_windows",
            "final_target_verified", "final_playable_verified", "final_low_rate_verified", "final_measurement_invalid",
            "final_verification_regressed", "request_restore_failed", "link_disconnected", "connection_changed",
            "bounded_timeout", "trial_error", "cancelled", "running", "measuring_native", "not_started",
            "not_recorded", "unknown_code", ""
        ];
        string[] presets =
        [
            "win11_fixed_7p5", "win11_flexible_7p5_15", "win11_throughput", "win11_fixed_15",
            "other_preset", "restore_original"
        ];
        string[] statuses = ["Success", "AccessDenied", "Unauthorized", "Failed", ""];

        for (int i = 0; i < 220; i++)
        {
            var trials = new List<BleAdaptiveTrialRecord>();
            for (int count = random.Next(0, 4); count > 0; count--)
            {
                string preset = presets[random.Next(presets.Length)] + (random.Next(4) == 0 ? "_VERIFY" : "");
                trials.Add(new BleAdaptiveTrialRecord(preset, random.Next(2) == 0, RandomMeasurement(random),
                    codes[random.Next(codes.Length)]));
            }

            var requests = new List<BleAdaptiveRequestEvidence>();
            for (int count = random.Next(0, 4); count > 0; count--)
            {
                requests.Add(new BleAdaptiveRequestEvidence(
                    presets[random.Next(presets.Length)],
                    random.Next(4) == 0 ? "" : "phase" + random.Next(4),
                    RandomUnits(random), RandomUnits(random), RandomUnits(random), RandomUnits(random),
                    statuses[random.Next(statuses.Length)],
                    RandomFlag(random),
                    random.Next(3) == 0 ? unchecked((int)0x80070005) : random.Next(2) == 0 ? random.Next(0, 5) : null,
                    random.Next(3) == 0 ? "UnauthorizedAccessException" : null,
                    RandomFlag(random),
                    random.Next(4) == 0 ? "caller" : ""));
            }

            var windows = new List<BleTrialMeasurement>();
            for (int count = random.Next(0, 3); count > 0; count--) windows.Add(RandomTrial(random));

            var snapshot = new BleAdaptiveDiagnosticSnapshot(
                random.Next(3) == 0 ? null : DateTimeOffset.UnixEpoch.AddSeconds(random.Next(0, 900000)),
                states[random.Next(states.Length)],
                codes[random.Next(codes.Length)],
                RandomMeasurement(random), RandomMeasurement(random), random.Next(2) == 0,
                random.Next(3) == 0 ? null : presets[random.Next(presets.Length)],
                trials, requests,
                random.Next(4) == 0 ? null : windows,
                random.Next(4) == 0 ? "measuring" : "");

            text.AppendLine(BleAdaptiveDiagnostics.Format(snapshot));
            foreach (BleAdaptiveRequestEvidence request in requests)
            {
                text.AppendLine(BleAdaptiveDiagnostics.Interpret(request, RandomMeasurement(random)));
                text.AppendLine(BleAdaptiveDiagnostics.Measurement(RandomMeasurement(random)));
            }
        }

        foreach (string code in codes) text.AppendLine(code + "=>" + BleAdaptiveDiagnostics.ExplainCode(code));
        text.AppendLine(BleAdaptiveDiagnostics.Format(null));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash)[..16] + ":" + text.Length;
    }

    private static bool? RandomFlag(Random random) => random.Next(4) switch
    {
        0 => true,
        1 => false,
        _ => null
    };

    private static ushort? RandomUnits(Random random) => random.Next(3) == 0 ? null : (ushort)random.Next(0, 400);

    private static BleAdaptiveMeasurement? RandomMeasurement(Random random)
    {
        if (random.Next(4) == 0) return null;
        var sample = new MeasuredTrialSample(
            random.Next(5) == 0 ? double.NaN : (random.NextDouble() * 6) - 0.5,
            random.Next(0, 400),
            random.Next(0, 400),
            random.Next(6) == 0 ? double.PositiveInfinity : random.NextDouble() * 900,
            random.Next(3) != 0,
            random.Next(2) == 0,
            random.Next(3) == 0 ? null : random.NextDouble() * 40,
            random.Next(3) == 0 ? null : (ushort)random.Next(0, 30));
        return new BleAdaptiveMeasurement(sample, random.NextDouble() * 10,
            random.Next(3) == 0 ? null : random.NextDouble() * 80);
    }

    private static BleTrialMeasurement RandomTrial(Random random)
    {
        var distribution = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int count = random.Next(0, 3); count > 0; count--)
        {
            distribution["b" + random.Next(5)] = random.Next(1, 40);
        }

        var gaps = new BleGapStatistics(random.Next(0, 200), random.NextDouble() * 20, random.NextDouble() * 30,
            random.NextDouble() * 40, random.NextDouble() * 50, random.NextDouble(), random.NextDouble() * 80,
            distribution);
        return new BleTrialMeasurement("phase" + random.Next(4), random.NextDouble() * 5,
            random.NextDouble() * 200, random.NextDouble() * 200, random.Next(0, 500), random.Next(0, 500),
            (ushort)random.Next(0, 30), "status" + random.Next(3), gaps, random.NextDouble() * 900,
            random.Next(2) == 0);
    }

    // ---------------- BLE 速率健康监视 ----------------

    private const long RateFrequency = 10_000_000;

    private static GapStatisticsSummary RateGaps(Random random)
    {
        int samples = random.Next(0, 400);
        int near7 = random.Next(0, samples + 1);
        int near15 = random.Next(0, samples - near7 + 1);
        int over22 = random.Next(0, samples - near7 - near15 + 1);
        double p50 = 5 + random.NextDouble() * 15;
        return new GapStatisticsSummary(samples, p50, p50 + random.NextDouble() * 5,
            p50 + random.NextDouble() * 8, random.NextDouble() * 3, p50 + random.NextDouble() * 40,
            near7, near15, over22);
    }

    private static void RateMonitor(Random random, CheckFn check, ref int cases, ref int bad)
    {
        // 策略自洽性：非法组合一律构造失败，合法组合可用。
        check("rate", "options validation",
            !new BleRateStabilityOptions { HighRateThresholdHz = 0 }.IsUsable &&
            !new BleRateStabilityOptions { GoodWindowsRequired = 1 }.IsUsable &&
            !new BleRateStabilityOptions { MaximumWindowSeconds = 1 }.IsUsable &&
            !new BleRateStabilityOptions { RecentWindowSeconds = 5 }.IsUsable &&
            !new BleRateStabilityOptions { MaximumContinuityGapSeconds = -1 }.IsUsable &&
            new BleRateStabilityOptions().IsUsable, ref cases, ref bad);
        check("rate", "monitor rejects invalid options",
            Capture(() => new BleRateStabilityMonitor(0).Snapshot(0).State.ToString())
                .StartsWith("throw:ArgumentOutOfRangeException:", StringComparison.Ordinal) &&
            Capture(() => new BleRateStabilityMonitor(RateFrequency,
                    new BleRateStabilityOptions { GoodWindowsRequired = 1 }).Snapshot(0).State.ToString())
                .StartsWith("throw:ArgumentOutOfRangeException:", StringComparison.Ordinal),
            ref cases, ref bad);

        // 同一串窗口分别喂给新实现与旧实现，每步比对返回值与快照全字段。
        for (int round = 0; round < 200; round++)
        {
            var options = new BleRateStabilityOptions
            {
                HighRateThresholdHz = new[] { 60.0, 125.0, 130.0 }[random.Next(3)],
                GoodWindowsRequired = random.Next(2, 5),
                LowWindowsRequired = random.Next(2, 4),
                MaximumContinuityGapSeconds = random.Next(0, 3)
            };
            var live = new BleRateStabilityMonitor(RateFrequency, options);
            var legacy = new LegacyRateMonitor(RateFrequency, options);
            long cursor = 0;
            for (int step = 0; step < 30; step++)
            {
                long start = cursor + random.Next(-2, 5) * RateFrequency / 10;
                long length = (random.Next(8) == 0 ? random.Next(1, 3) : random.Next(3, 13)) * RateFrequency;
                ulong raw = (ulong)random.Next(0, 1600);
                ulong parsed = (ulong)random.Next(0, (int)Math.Min(raw, 1500) + 1);
                if (random.Next(12) == 0) parsed = raw + 1; // 解析数多于接收数：非法窗口
                var window = new BleRateMeasurementWindow(start, start + length, raw, parsed, RateGaps(random));
                bool accepted = live.ObserveWindow(window);
                bool legacyAccepted = legacy.ObserveWindow(window);
                check("rate", "observe " + round + "/" + step, accepted == legacyAccepted, ref cases, ref bad);
                if (accepted) cursor = start + length;
                long now = cursor + random.Next(0, 20) * RateFrequency;
                check("rate", "snapshot " + round + "/" + step,
                    RateKey(live.Snapshot(now)) == RateKey(legacy.Snapshot(now)), ref cases, ref bad);
            }

            live.Reset();
            legacy.Reset();
            check("rate", "reset " + round,
                RateKey(live.Snapshot(cursor)) == RateKey(legacy.Snapshot(cursor)), ref cases, ref bad);
        }

        // 通知节奏判定：固定样例覆盖四个分支与非法摘要。
        var cadenceCases = new[]
        {
            new GapStatisticsSummary(0, 0, 0, 0, 0, 0, 0, 0, 0),
            new GapStatisticsSummary(100, 7.5, 8, 9, 7, 9, 90, 5, 5),
            new GapStatisticsSummary(100, 15, 16, 17, 14, 19, 5, 90, 5),
            new GapStatisticsSummary(100, 15, 16, 17, 14, 19, 30, 40, 30),
            new GapStatisticsSummary(100, 15, 16, 17, 14, 19, 1, 1, 1),
            new GapStatisticsSummary(100, 16, 15, 17, 14, 19, 5, 90, 5),
            new GapStatisticsSummary(10, 15, 16, 17, 14, 19, 0, 10, 0),
            new GapStatisticsSummary(100, double.NaN, 16, 17, 14, 19, 5, 90, 5),
            new GapStatisticsSummary(100, 13, 16, 17, 14, 19, 5, 90, 5)
        };
        foreach (GapStatisticsSummary gaps in cadenceCases)
        {
            check("rate", "cadence " + gaps.Samples,
                BleRateStabilityMonitor.ClassifyCadence(gaps) == LegacyClassifyCadence(gaps), ref cases, ref bad);
        }
    }

    private static string RateKey(BleRateStabilitySnapshot s)
    {
        static string Window(BleRateStabilityWindow? w) => w == null ? "-" :
            $"{w.WindowStartTicks}|{w.WindowEndTicks}|{w.RawNotifications}|{w.ParsedInputs}|" +
            $"{w.Seconds:R}|{w.RawHz:R}|{w.ParsedHz:R}|{w.CadenceHint}";
        static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";
        return $"{s.State}|{s.IsRecent}|{s.IsStableHighRate}|{s.ShouldWarnLowRate}|{s.ConsecutiveGoodWindows}|" +
               $"{s.ConsecutiveLowWindows}|{s.ValidWindowCount}|{s.LowWindowCount}|{Window(s.LastWindow)}|" +
               $"{Window(s.RecentLowWindow)}|{Number(s.SessionMinimumHz)}|{Number(s.SessionMaximumHz)}|" +
               $"{Number(s.SessionMinimumRawHz)}|{Number(s.SessionMaximumRawHz)}|" +
               $"{Number(s.SessionMinimumParsedHz)}|{Number(s.SessionMaximumParsedHz)}|" +
               $"{s.HighRateThresholdHz:R}|{s.ValidMeasuredSeconds:R}";
    }

    private sealed class LegacyRateMonitor
    {
        private readonly long frequency;
        private readonly BleRateStabilityOptions options;
        private BleRateStabilityWindow? lastWindow, recentLowWindow;
        private int consecutiveGood, consecutiveLow;
        private long validWindows, lowWindows;
        private bool lowRateConfirmed;
        private double? minimumHz, maximumHz, minimumRawHz, maximumRawHz, minimumParsedHz, maximumParsedHz;
        private double measuredSeconds;

        public LegacyRateMonitor(long frequency, BleRateStabilityOptions options)
        {
            this.frequency = frequency;
            this.options = options;
        }

        public bool ObserveWindow(BleRateMeasurementWindow window)
        {
            if (window.WindowStartTicks < 0 || window.WindowEndTicks <= window.WindowStartTicks ||
                window.ParsedInputs > window.RawNotifications ||
                (lastWindow != null && window.WindowStartTicks < lastWindow.WindowEndTicks)) return false;

            double seconds = (window.WindowEndTicks - window.WindowStartTicks) / (double)frequency;
            if (!double.IsFinite(seconds) || seconds < options.MinimumWindowSeconds ||
                seconds > options.MaximumWindowSeconds) return false;
            double rawHz = window.RawNotifications / seconds;
            double parsedHz = window.ParsedInputs / seconds;
            if (!double.IsFinite(rawHz) || !double.IsFinite(parsedHz)) return false;

            if (lastWindow != null && (window.WindowStartTicks - lastWindow.WindowEndTicks) /
                    (double)frequency > options.MaximumContinuityGapSeconds)
                consecutiveGood = consecutiveLow = 0;

            lastWindow = new(window.WindowStartTicks, window.WindowEndTicks, window.RawNotifications,
                window.ParsedInputs, seconds, rawHz, parsedHz, window.Gaps,
                LegacyClassifyCadence(window.Gaps));
            validWindows = Increment(validWindows);
            measuredSeconds += seconds;
            minimumHz = Min(minimumHz, lastWindow.EffectiveHz);
            maximumHz = Max(maximumHz, lastWindow.EffectiveHz);
            minimumRawHz = Min(minimumRawHz, rawHz);
            maximumRawHz = Max(maximumRawHz, rawHz);
            minimumParsedHz = Min(minimumParsedHz, parsedHz);
            maximumParsedHz = Max(maximumParsedHz, parsedHz);

            if (lastWindow.EffectiveHz >= options.HighRateThresholdHz)
            {
                consecutiveLow = 0;
                consecutiveGood = Increment(consecutiveGood);
                if (consecutiveGood >= options.GoodWindowsRequired) lowRateConfirmed = false;
            }
            else
            {
                consecutiveGood = 0;
                consecutiveLow = Increment(consecutiveLow);
                lowWindows = Increment(lowWindows);
                recentLowWindow = lastWindow;
                if (consecutiveLow >= options.LowWindowsRequired) lowRateConfirmed = true;
            }

            return true;
        }

        public BleRateStabilitySnapshot Snapshot(long nowTicks)
        {
            bool recent = lastWindow != null && nowTicks >= lastWindow.WindowEndTicks &&
                (nowTicks - lastWindow.WindowEndTicks) / (double)frequency <= options.RecentWindowSeconds;
            bool stable = recent && !lowRateConfirmed && consecutiveGood >= options.GoodWindowsRequired;
            BleRateStabilityState state = lastWindow == null ? BleRateStabilityState.Observing :
                !recent ? BleRateStabilityState.Stale :
                lowRateConfirmed ? (consecutiveGood > 0 ? BleRateStabilityState.Recovering : BleRateStabilityState.LowRate) :
                stable ? BleRateStabilityState.StableHighRate : BleRateStabilityState.Observing;
            return new(state, recent, stable, recent && lowRateConfirmed, consecutiveGood, consecutiveLow,
                validWindows, lowWindows, lastWindow, recentLowWindow, minimumHz, maximumHz,
                minimumRawHz, maximumRawHz, minimumParsedHz, maximumParsedHz,
                options.HighRateThresholdHz, measuredSeconds);
        }

        public void Reset()
        {
            lastWindow = recentLowWindow = null;
            consecutiveGood = consecutiveLow = 0;
            validWindows = lowWindows = 0;
            lowRateConfirmed = false;
            minimumHz = maximumHz = minimumRawHz = maximumRawHz = minimumParsedHz = maximumParsedHz = null;
            measuredSeconds = 0;
        }

        private static long Increment(long value) => value == long.MaxValue ? value : value + 1;
        private static int Increment(int value) => value == int.MaxValue ? value : value + 1;
        private static double Min(double? previous, double value) => previous.HasValue ? Math.Min(previous.Value, value) : value;
        private static double Max(double? previous, double value) => previous.HasValue ? Math.Max(previous.Value, value) : value;
    }

    private static BleNotifyCadenceHint LegacyClassifyCadence(GapStatisticsSummary gaps)
    {
        if (gaps.Samples < 50 || gaps.Near7p5 < 0 || gaps.Near15 < 0 || gaps.Over22 < 0 ||
            (long)gaps.Near7p5 + gaps.Near15 + gaps.Over22 > gaps.Samples ||
            !double.IsFinite(gaps.P50Ms) || !double.IsFinite(gaps.P95Ms) ||
            !double.IsFinite(gaps.P99Ms) || !double.IsFinite(gaps.MinMs) || !double.IsFinite(gaps.MaxMs) ||
            gaps.MinMs < 0 || gaps.P50Ms < gaps.MinMs || gaps.P95Ms < gaps.P50Ms ||
            gaps.P99Ms < gaps.P95Ms || gaps.MaxMs < gaps.P99Ms) return BleNotifyCadenceHint.Unknown;
        if (gaps.Near7p5 >= gaps.Samples * .2 && gaps.Over22 >= gaps.Samples * .1)
            return BleNotifyCadenceHint.MixedShortAndLong;
        if (gaps.Near15 >= gaps.Samples * .8 && gaps.Near7p5 < gaps.Samples * .1 &&
            gaps.Over22 < gaps.Samples * .1 && gaps.P50Ms is >= 13.5 and <= 16.5 && gaps.P95Ms <= 18)
            return BleNotifyCadenceHint.Predominantly15Ms;
        return BleNotifyCadenceHint.Irregular;
    }

    // ---------------- 适配器热重启命令行 ----------------

    private static void AdapterRestart(CheckFn check, ref int cases, ref int bad)
    {
        const string pnp = @"USB\VID_8087\PID_0026";
        const string interfaceId = @"\\?\USB#VID_8087#PID_0026#92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2";
        var target = new RadioRestartTarget("Intel", pnp, interfaceId, "Intel", "22.250", true);
        string operation = Guid.NewGuid().ToString("N");

        check("restart", "is command",
            AdapterRestartCommand.IsCommand(["--x", "--BLUETOOTH-ADAPTER-RESTART"]) &&
            !AdapterRestartCommand.IsCommand(["--other"]), ref cases, ref bad);

        check("restart", "result path",
            AdapterRestartCommand.ResultPath(operation)
                .EndsWith(@"BluetoothRestart\" + operation + ".json", StringComparison.OrdinalIgnoreCase),
            ref cases, ref bad);
        check("restart", "result path rejects bad id",
            Capture(() => AdapterRestartCommand.ResultPath("nope")).StartsWith("throw:ArgumentException:",
                StringComparison.Ordinal), ref cases, ref bad);

        string[] args = AdapterRestartCommand.HelperArguments(target, operation);
        check("restart", "helper args",
            args.Length == 7 && args[0] == AdapterRestartCommand.Command && args[1] == "--operation-id" &&
            args[2] == operation && args[3] == "--adapter-pnp" && args[4] == pnp &&
            args[5] == "--adapter-interface" && args[6] == interfaceId, ref cases, ref bad);
        check("restart", "helper args rejects bad id",
            Capture(() => AdapterRestartCommand.HelperArguments(target, "bad").Length.ToString())
                .StartsWith("throw:ArgumentException:", StringComparison.Ordinal), ref cases, ref bad);

        // 参数校验在提权与设备访问之前完成，因此这几个分支可以离线跑到。
        check("restart", "exit on short args",
            AdapterRestartCommand.ExecuteAsync([AdapterRestartCommand.Command]).GetAwaiter().GetResult() == 7,
            ref cases, ref bad);
        check("restart", "exit on wrong layout",
            AdapterRestartCommand.ExecuteAsync([AdapterRestartCommand.Command, "--adapter-pnp", operation,
                "--operation-id", pnp, "--adapter-interface", interfaceId]).GetAwaiter().GetResult() == 7,
            ref cases, ref bad);
        check("restart", "exit on bad operation id",
            AdapterRestartCommand.ExecuteAsync([AdapterRestartCommand.Command, "--operation-id", "nope",
                "--adapter-pnp", pnp, "--adapter-interface", interfaceId, "extra"]).GetAwaiter().GetResult() == 7,
            ref cases, ref bad);

        DateTimeOffset started = DateTimeOffset.UnixEpoch;
        var outcome = new AdapterRestartOutcome(operation, pnp, interfaceId, started, started.AddSeconds(1),
            true, 0, true, true, true, "ok", "");
        DateTimeOffset now = started.AddSeconds(10);
        check("restart", "matches request",
            AdapterRestartCommand.MatchesRequest(outcome, operation, target, started, now), ref cases, ref bad);
        check("restart", "matches request rejects operation",
            !AdapterRestartCommand.MatchesRequest(outcome, "other", target, started, now), ref cases, ref bad);
        // 结果声称的开始时刻比请求还早 5 秒：跨越了容差，必须拒绝。
        check("restart", "matches request rejects early start",
            !AdapterRestartCommand.MatchesRequest(outcome, operation, target, started.AddSeconds(5), now),
            ref cases, ref bad);
        check("restart", "matches request rejects future completion",
            !AdapterRestartCommand.MatchesRequest(outcome, operation, target, started, started.AddSeconds(-10)),
            ref cases, ref bad);
        check("restart", "matches request rejects other target",
            !AdapterRestartCommand.MatchesRequest(outcome, operation,
                new RadioRestartTarget("Other", @"USB\VID_8087\PID_9999", interfaceId, "Intel", "22.250", true),
                started, now), ref cases, ref bad);
        check("restart", "matches request pnp is case insensitive",
            AdapterRestartCommand.MatchesRequest(outcome with { PnpId = pnp.ToLowerInvariant() }, operation, target,
                started, now), ref cases, ref bad);

        check("restart", "verified success", outcome.Verified && !outcome.RebootRequired, ref cases, ref bad);
        AdapterRestartOutcome reboot = outcome with { NativeExitCode = 3010 };
        check("restart", "reboot required", !reboot.Verified && reboot.RebootRequired, ref cases, ref bad);
        check("restart", "not attempted is not verified",
            !(outcome with { RestartAttempted = false }).Verified, ref cases, ref bad);
        check("restart", "telemetry line",
            outcome.Telemetry.StartsWith("[BLUETOOTH_RESTART_RESULT] operation=" + operation, StringComparison.Ordinal) &&
            outcome.Telemetry.Contains("verified=True", StringComparison.Ordinal) &&
            outcome.Telemetry.EndsWith("automatic_reboot=false", StringComparison.Ordinal), ref cases, ref bad);
    }

    // ---------------- 轴突跳现场记录 ----------------

    private static Pro2Fd2FrameSnapshot Frame(ulong index) =>
        new(index, DateTimeOffset.UnixEpoch.AddMilliseconds((long)index), 20 + index % 5,
            "FD2", 14, "04" + index.ToString("X4"), new Pro2AxisSnapshot(2048, 2048, 2048, 2048),
            new Pro2AxisSnapshot(2048, 2048, 2048, 2048), 0,
            new Pro2MotionSnapshot(true, true, 0, 0, 0, 0, 0, 0), true, "fd2", "accepted", []);

    private static Pro2AxisSpikeTelemetry SpikeTelemetry(string axis) =>
        new(DateTimeOffset.UnixEpoch, "FD2", 14, axis, 100, 900, 950, 800, 2000, 2000, 2000, 2000,
            2100, 2000, 2000, 2000, 0.5, 12, 7.5, 2, "rejected", "delta", "041234", 4242, 11, 126.5, 120, 130,
            8.2, true, true, "active", true, false, false, false, 850);

    private static void Recording(CheckFn check, ref int cases, ref int bad)
    {
        // 限流窗口：与旧的 Queue 实现逐次比对，覆盖倒退的时钟与恰好 10 秒的边界。
        var random = new Random(20261125);
        for (int limit = 0; limit <= 4; limit++)
        {
            var live = new DumpRateWindow(limit);
            var legacy = new LegacyDumpWindow(limit);
            long at = 1_000_000;
            for (int step = 0; step < 200; step++)
            {
                at += random.Next(0, 4) switch
                {
                    0 => -random.Next(1, 5000),
                    1 => 10_000,
                    2 => 10_001,
                    _ => random.Next(0, 4000)
                };
                var now = DateTimeOffset.UnixEpoch.AddMilliseconds(at);
                check("recording", "rate limit " + limit + "/" + step,
                    live.TryTake(now) == legacy.TryTake(now), ref cases, ref bad);
            }
        }

        // 环形缓冲取段：乱序帧号、空槽、越界范围。
        for (int round = 0; round < 300; round++)
        {
            var ring = new Pro2Fd2FrameSnapshot?[1 + random.Next(0, 20)];
            for (int i = 0; i < ring.Length; i++)
            {
                if (random.Next(4) != 0) ring[i] = Frame((ulong)random.Next(0, 60));
            }

            ulong first = (ulong)random.Next(0, 60);
            ulong last = first + (ulong)random.Next(0, 30);
            check("recording", "select range " + round,
                SameFrames(Pro2Fd2SpikeRecorder.SelectRange(ring, first, last), LegacySelectRange(ring, first, last)),
                ref cases, ref bad);
        }

        // 文件名规则。
        string directory = Path.Combine("C:", "logs", "spikes");
        var localNow = new DateTime(2026, 11, 22, 13, 5, 7, 42);
        foreach (string axis in new[] { "LX", "left Y", "RX outer" })
        {
            string expected = Path.Combine(directory,
                "spike_" + localNow.ToString("yyyyMMdd_HHmmss_fff") + "_4242_" + axis.Replace(" ", "_") + ".jsonl");
            check("recording", "dump path " + axis,
                SpikeDumpPath.Build(SpikeTelemetry(axis), localNow, directory) == expected, ref cases, ref bad);
        }

        // 环境开关：只认四个真值，大小写不敏感，不裁剪空格。
        foreach ((string? value, bool on) in new (string?, bool)[]
                 {
                     ("1", true), ("TRUE", true), ("yes", true), ("On", true), ("true", true),
                     ("0", false), ("no", false), (" on ", false), ("", false), (null, false), ("enable", false)
                 })
        {
            check("recording", "env truthy " + (value ?? "<null>"),
                EnvironmentSwitches.IsTruthy(value) == on, ref cases, ref bad);
        }

        RecordingDumpFile(check, ref cases, ref bad);
    }

    // 端到端：真的写一个 JSONL 到临时目录，读回来核对行数、顺序与计数。
    private static void RecordingDumpFile(CheckFn check, ref int cases, ref int bad)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pro2-spike-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var options = new InputStabilityOptions
            {
                RawIntegrityModeEnabled = true,
                AxisSpikeRingBufferCapacity = 16,
                AxisSpikeRingBufferFramesBefore = 3,
                AxisSpikeRingBufferFramesAfter = 2,
                AxisSpikeDumpRateLimitPer10Seconds = 2
            };
            var recorder = new Pro2Fd2SpikeRecorder(options, directory);
            for (ulong index = 100; index < 110; index++) recorder.AddFrame(Frame(index));
            bool queued = recorder.TryQueueDump(SpikeTelemetry("LX"), 108, out string path);
            bool second = recorder.TryQueueDump(SpikeTelemetry("LY"), 109, out string secondPath);
            bool third = recorder.TryQueueDump(SpikeTelemetry("RX"), 109, out string thirdPath);
            recorder.DisposeAsync().AsTask().GetAwaiter().GetResult();
            check("recording", "queue acceptance", recorder.CaptureEnabled && queued && second && !third,
                ref cases, ref bad);
            check("recording", "rate limited path empty", thirdPath.Length == 0, ref cases, ref bad);
            check("recording", "dump counters", recorder.WrittenDumpCount == 2 && recorder.DroppedDumpCount == 1,
                ref cases, ref bad);

            string[] lines = File.Exists(path) ? File.ReadAllLines(path) : [];
            check("recording", "dump file exists", lines.Length == 6, ref cases, ref bad);
            if (lines.Length > 0)
            {
                using JsonDocument head = JsonDocument.Parse(lines[0]);
                check("recording", "dump header",
                    head.RootElement.GetProperty("type").GetString() == "spike" &&
                    head.RootElement.GetProperty("captured_frames").GetInt32() == 5 &&
                    head.RootElement.GetProperty("TriggerFrameIndex").GetUInt64() == 108,
                    ref cases, ref bad);
                ulong previous = 0;
                bool ascending = true;
                for (int i = 1; i < lines.Length; i++)
                {
                    using JsonDocument frame = JsonDocument.Parse(lines[i]);
                    ulong index = frame.RootElement.GetProperty("frame").GetProperty("FrameIndex").GetUInt64();
                    if (i > 1 && index <= previous) ascending = false;
                    previous = index;
                }

                check("recording", "dump frames ascending", ascending && previous == 109, ref cases, ref bad);
            }

            check("recording", "second dump written", File.Exists(secondPath), ref cases, ref bad);
        }
        catch (Exception ex)
        {
            check("recording", "dump file exception " + ex.GetType().Name, false, ref cases, ref bad);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static bool SameFrames(Pro2Fd2FrameSnapshot[] left, List<Pro2Fd2FrameSnapshot> right) =>
        left.Length == right.Count && left.Zip(right).All(pair => pair.First.FrameIndex == pair.Second.FrameIndex);

    private sealed class LegacyDumpWindow(int limit)
    {
        private readonly Queue<DateTimeOffset> stamps = new();

        public bool TryTake(DateTimeOffset now)
        {
            while (stamps.Count > 0 && now - stamps.Peek() > TimeSpan.FromSeconds(10)) stamps.Dequeue();
            if (stamps.Count >= limit) return false;
            stamps.Enqueue(now);
            return true;
        }
    }

    private static List<Pro2Fd2FrameSnapshot> LegacySelectRange(Pro2Fd2FrameSnapshot?[] ring, ulong first, ulong last)
    {
        var result = new List<Pro2Fd2FrameSnapshot>();
        foreach (Pro2Fd2FrameSnapshot? frame in ring)
        {
            if (frame != null && frame.FrameIndex >= first && frame.FrameIndex <= last) result.Add(frame);
        }

        result.Sort(static (a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        return result;
    }

    // ---------------- 低速诊断决策链：行为指纹 ----------------

    private const string LowRateExpected = "3D93F7C80184CD85:73851";
    // 英文版的诊断文案走另一套译文，指纹必然不同；两套都算已知输出。
    private const string LowRateExpectedEnglish = "7A4C358841461958:75603";

    private static BluetoothAdapterDiagnosticInfo TestAdapter(string name) =>
        new(name, @"USB\VID_8087&PID_0026\5&1", "8087", "0026", "Intel", "22.250.0.2", "2024-01-01",
            @"\\?\USB#VID_8087#PID_0026#5&1");

    private static EnvironmentSnapshot TestEnvironment(int build, ConnectionRoute route, bool canRequest,
        bool canConfigure, bool apiOk = true, bool configProbeOk = true, bool win10Configured = false) =>
        new(new EnvironmentFacts(true, build, canRequest, canRequest, "sha", win10Configured),
            new ConnectionPlan(route, "route", "guidance", canRequest, canRequest, canConfigure), "",
            apiOk, configProbeOk, true);

    private static SystemBluetoothSnapshot? TestSnapshot(int adapters, Win10ConfigurationAudit? audit) =>
        adapters < 0
            ? null
            : new SystemBluetoothSnapshot(DateTimeOffset.UnixEpoch,
                new SystemDiagnosticInfo("Windows", "Pro", "22H2", "10.0.22621", "x64", "x64", "1.0", "22621"),
                Enumerable.Range(0, adapters)
                    .Select(i => new RadioDiagnosticInfo(TestAdapter("AX" + i), i == 0, "AA:BB:CC:DD:EE:0" + i,
                        true, true, false, true)).ToArray(),
                "", "", audit);

    private static BleRateStabilitySnapshot? TestStability(int windows, double hz, bool recent)
    {
        if (windows < 0) return null;
        var monitor = new BleRateStabilityMonitor(RateFrequency);
        long cursor = 0;
        for (int i = 0; i < windows; i++)
        {
            var window = new BleRateMeasurementWindow(cursor, cursor + RateFrequency, (ulong)hz, (ulong)hz,
                new GapStatisticsSummary(60, 7.5, 8, 8, 7, 12, 58, 2, 0));
            if (monitor.ObserveWindow(window)) cursor += RateFrequency;
        }
        return monitor.Snapshot(cursor + (recent ? RateFrequency : 60 * RateFrequency));
    }

    private static Pro2RateEvidence RateEvidence(ConnectionPhase phase = ConnectionPhase.Ready,
        bool running = true, bool connected = true, bool highRate = false, double fd2Hz = 60, double parsedHz = 60,
        double lastInputAgeMs = 100, ushort intervalUnits = 6, string descriptorStatus = "write_success",
        string win11Route = "Windows11PublicApi", GapStatisticsSummary gaps = default, bool recentWindow = true,
        ulong handlerSamples = 0, double handlerAverageUs = 0, uint parseFailures = 0,
        BleRateStabilitySnapshot? stability = null) =>
        new(phase, running, connected, highRate, fd2Hz, parsedHz, (uint)fd2Hz, (uint)parsedHz, lastInputAgeMs,
            intervalUnits, descriptorStatus, "applied", win11Route, gaps, recentWindow, handlerSamples,
            handlerAverageUs, 0, parseFailures, "fresh", "", stability);

    private static BleRateStabilitySnapshot RecoveringStability()
    {
        var monitor = new BleRateStabilityMonitor(RateFrequency);
        long cursor = 0;
        void Feed(double hz)
        {
            monitor.ObserveWindow(new BleRateMeasurementWindow(cursor, cursor + RateFrequency, (ulong)hz, (ulong)hz,
                new GapStatisticsSummary(60, 13, 14, 15, 12, 16, 2, 55, 3)));
            cursor += RateFrequency;
        }

        Feed(60);
        Feed(60);
        Feed(60);
        Feed(130);
        return monitor.Snapshot(cursor + RateFrequency);
    }

    private static void LowRate(CheckFn check, ref int cases, ref int bad)
    {
        var results = new List<BleLowRateDiagnosis>();
        void Evaluate(Pro2RateEvidence rate, EnvironmentSnapshot environment, SystemBluetoothSnapshot? snapshot) =>
            results.Add(BleLowRateDiagnoser.Evaluate(rate, environment, snapshot));

        EnvironmentSnapshot win11 = TestEnvironment(22621, ConnectionRoute.Windows11PublicApi, true, false);
        EnvironmentSnapshot win10 = TestEnvironment(19045, ConnectionRoute.Win10AuditedStack, true, true);
        EnvironmentSnapshot win10NoTrial = TestEnvironment(19045, ConnectionRoute.Win10AuditedStack, false, false);

        // 固定样例：每个分支至少命中一次。
        Evaluate(RateEvidence(connected: false), win11, TestSnapshot(1, null));
        Evaluate(RateEvidence(lastInputAgeMs: double.NaN), win11, TestSnapshot(1, null));
        Evaluate(RateEvidence(fd2Hz: 120, parsedHz: 60), win11, TestSnapshot(1, null));
        Evaluate(RateEvidence(descriptorStatus: "write_failed"), win11, TestSnapshot(1, null));
        Evaluate(RateEvidence(fd2Hz: 120, parsedHz: 120, stability: TestStability(6, 130, recent: false)),
            win11, TestSnapshot(1, null));
        Evaluate(RateEvidence(fd2Hz: 60, parsedHz: 60, stability: RecoveringStability()), win11,
            TestSnapshot(1, null));
        Evaluate(RateEvidence(), win10, TestSnapshot(1, new Win10ConfigurationAudit(false, false, "", false, false, "")));
        Evaluate(RateEvidence(), win10, TestSnapshot(1, new Win10ConfigurationAudit(true, true, "7.5ms", true, true, "")));
        Evaluate(RateEvidence(), win10, TestSnapshot(1, new Win10ConfigurationAudit(true, false, "", false, false, "")));
        Evaluate(RateEvidence(), win10NoTrial, TestSnapshot(-1, null));
        Evaluate(RateEvidence(fd2Hz: 100, parsedHz: 100, intervalUnits: 8), win11, TestSnapshot(1, null));
        Evaluate(RateEvidence(handlerSamples: 10, handlerAverageUs: 5000), win11, TestSnapshot(1, null));
        Evaluate(RateEvidence(gaps: new GapStatisticsSummary(100, 8, 25, 30, 6, 40, 30, 40, 30)), win11,
            TestSnapshot(1, null));
        Evaluate(RateEvidence(win11Route: "UnvalidatedEnvironment", fd2Hz: 60, parsedHz: 60), win11,
            TestSnapshot(1, null));
        Evaluate(RateEvidence(fd2Hz: 66, parsedHz: 66, gaps: new GapStatisticsSummary(100, 15, 16, 17, 14, 18, 2, 95, 3)),
            win11, TestSnapshot(1, null));
        Evaluate(RateEvidence(gaps: new GapStatisticsSummary(100, 8, 40, 45, 6, 80, 5, 5, 90)), win11,
            TestSnapshot(1, null));
        Evaluate(RateEvidence(fd2Hz: 60, parsedHz: 60), win10,
            TestSnapshot(1, new Win10ConfigurationAudit(true, true, "7.5ms", true, false, "admin")));
        Evaluate(RateEvidence(fd2Hz: 40, parsedHz: 40), win11, null);

        // 随机组合：把选项的交叉面全覆盖一遍。
        var random = new Random(20261126);
        double[] rates = [0, 30, 60, 66, 75, 100, 120, 127, 130];
        double[] ages = [0, 120, 480, 500, 900, double.PositiveInfinity, double.NaN];
        ushort[] intervals = [0, 6, 7, 8, 12];
        string[] descriptors = ["write_success", "write_failed", "readback_mismatch"];
        string[] routes = ["Windows11PublicApi", "AdaptiveBoundedPresets", "UnvalidatedEnvironment", "NativeNotifyOnly"];
        for (int i = 0; i < 240; i++)
        {
            int build = random.Next(2) == 0 ? 19045 : 22621;
            ConnectionRoute route = build < 22000
                ? (random.Next(3) == 0 ? ConnectionRoute.Win10ExperimentalStack : ConnectionRoute.Win10AuditedStack)
                : (random.Next(3) == 0 ? ConnectionRoute.NativeNotifyOnly : ConnectionRoute.Windows11PublicApi);
            bool canRequest = random.Next(3) != 0;
            EnvironmentSnapshot environment = TestEnvironment(build, route, canRequest, random.Next(2) == 0,
                random.Next(6) != 0, random.Next(6) != 0, random.Next(2) == 0);

            double fd2 = rates[random.Next(rates.Length)];
            double parsed = random.Next(9) == 0 ? fd2 * .5 : fd2 * (0.9 + random.NextDouble() * .1);
            if (!double.IsFinite(parsed)) parsed = fd2;
            GapStatisticsSummary gaps = random.Next(4) switch
            {
                0 => default,
                1 => new GapStatisticsSummary(20, 15, 16, 17, 14, 18, 1, 18, 1),
                2 => new GapStatisticsSummary(80, 7.5, 15, 22, 6, 30, 40, 30, 10),
                _ => new GapStatisticsSummary(120, 8, 40, 60, 6, 90, 10, 10, 100)
            };
            Win10ConfigurationAudit? audit = random.Next(6) switch
            {
                0 => null,
                1 => new Win10ConfigurationAudit(false, false, "", false, false, ""),
                2 => new Win10ConfigurationAudit(true, true, "7.5ms", true, true, ""),
                3 => new Win10ConfigurationAudit(true, false, "", false, false, "admin"),
                4 => new Win10ConfigurationAudit(true, true, "7.5ms", true, false, "admin"),
                _ => new Win10ConfigurationAudit(true, false, "", false, false, "")
            };
            Evaluate(RateEvidence(
                phase: random.Next(4) == 0 ? ConnectionPhase.Measuring : ConnectionPhase.Ready,
                running: random.Next(8) != 0, connected: random.Next(8) != 0, highRate: fd2 >= 120,
                fd2Hz: fd2, parsedHz: parsed, lastInputAgeMs: ages[random.Next(ages.Length)],
                intervalUnits: intervals[random.Next(intervals.Length)],
                descriptorStatus: descriptors[random.Next(descriptors.Length)],
                win11Route: routes[random.Next(routes.Length)], gaps: gaps,
                recentWindow: random.Next(2) == 0, handlerSamples: random.Next(2) == 0 ? 0UL : 12UL,
                handlerAverageUs: random.Next(4000, 8000), parseFailures: (uint)random.Next(0, 20),
                stability: TestStability(new[] { -1, 0, 2, 3, 5, 9 }[random.Next(6)],
                    new[] { 60.0, 100.0, 130.0 }[random.Next(3)], random.Next(2) == 0)),
                environment, TestSnapshot(new[] { -1, 0, 1, 1, 2 }[random.Next(5)], audit));
        }

        var text = new StringBuilder();
        foreach (BleLowRateDiagnosis diagnosis in results)
            text.Append(diagnosis.Visible).Append('|').Append(diagnosis.Code).Append('|').Append(diagnosis.Title)
                .Append('|').Append(diagnosis.Summary).Append('|').Append(diagnosis.Advice).Append('|')
                .Append(diagnosis.CanOfferWin10Configuration).Append('|').Append(diagnosis.Evidence).Append('\n');
        string digest = CaptureDigest(text.ToString());
        check("lowrate", "decision digest=" + digest,
            digest == LowRateExpected || digest == LowRateExpectedEnglish, ref cases, ref bad);

        // 除了指纹，再按分支单独断言，避免指纹「锁住了一个错误结论」。
        check("lowrate", "hidden when offline",
            !BleLowRateDiagnoser.Evaluate(RateEvidence(connected: false), win11, TestSnapshot(1, null)).Visible,
            ref cases, ref bad);
        check("lowrate", "hidden while measuring",
            !BleLowRateDiagnoser.Evaluate(RateEvidence(phase: ConnectionPhase.Measuring), win11,
                TestSnapshot(1, null)).Visible, ref cases, ref bad);
        check("lowrate", "hidden when healthy and fresh",
            !BleLowRateDiagnoser.Evaluate(RateEvidence(fd2Hz: 130, parsedHz: 130, highRate: true), win11,
                TestSnapshot(1, null)).Visible, ref cases, ref bad);
        // 单点判定的阈值必须跟 125Hz 验收口径一致：125 因为达标而静默，124 仍然提示。
        check("lowrate", "silent at the 125hz standard",
            !BleLowRateDiagnoser.Evaluate(RateEvidence(fd2Hz: 125, parsedHz: 125, highRate: true), win11,
                TestSnapshot(1, null)).Visible, ref cases, ref bad);
        check("lowrate", "warns below the 125hz standard",
            BleLowRateDiagnoser.Evaluate(RateEvidence(fd2Hz: 124, parsedHz: 124, highRate: true), win11,
                TestSnapshot(1, null)).Visible, ref cases, ref bad);
        BleLowRateDiagnosis stalled = BleLowRateDiagnoser.Evaluate(RateEvidence(lastInputAgeMs: 900), win11, null);
        check("lowrate", "stalled code", stalled.Visible && stalled.Code == "input_stalled", ref cases, ref bad);
        check("lowrate", "parse loss code",
            BleLowRateDiagnoser.Evaluate(RateEvidence(fd2Hz: 120, parsedHz: 60), win11, null).Code ==
            "parsed_below_notify", ref cases, ref bad);
        check("lowrate", "missing win10 config offers trial",
            BleLowRateDiagnoser.Evaluate(RateEvidence(), win10, TestSnapshot(1,
                new Win10ConfigurationAudit(true, false, "", false, false, ""))).CanOfferWin10Configuration,
            ref cases, ref bad);
        check("lowrate", "missing win10 config without plan does not offer",
            !BleLowRateDiagnoser.Evaluate(RateEvidence(), win10NoTrial, TestSnapshot(-1, null))
                .CanOfferWin10Configuration, ref cases, ref bad);
        check("lowrate", "single adapter restart advice",
            BleLowRateDiagnoser.Evaluate(RateEvidence(), win10, TestSnapshot(1,
                new Win10ConfigurationAudit(true, false, "", false, false, ""))).Advice.Contains("唯一当前 USB 适配器"),
            ref cases, ref bad);
        check("lowrate", "evidence always describes fd2",
            stalled.Evidence.Contains("FD2 / Parsed：120.0 / 60.0 Hz") == false &&
            stalled.Evidence.Contains("最新输入年龄：900ms"), ref cases, ref bad);
    }

    // ---------------- 预设策略：行为指纹 ----------------

    private const string AdaptiveDecisionExpected = "DD0F76DC039B7703:94293";
    // 英文版的决策说明走另一套译文，指纹必然不同；两套都算已知输出。
    private const string AdaptiveDecisionExpectedEnglish = "923C302CE23D2611:143530";

    private static BleAdaptiveMeasurement Measure(double duration, double fd2, double parsed, double inputAge,
        bool connected = true, bool requested = true, double? p95 = 8, double completedAge = 2, double? maxGap = 20,
        ushort? intervalUnits = null) =>
        new(new MeasuredTrialSample(duration, (int)Math.Round(fd2 * duration), (int)Math.Round(parsed * duration),
            inputAge, connected, requested, p95, intervalUnits), completedAge, maxGap);

    private static BleAdaptiveMeasurement FromHz(double fd2Hz, double parsedHz, double duration = 10,
        double? p95 = 8, double? maxGap = 20, double completedAge = 2, double inputAge = 20,
        bool connected = true, bool requested = true) =>
        Measure(duration, fd2Hz, parsedHz, inputAge, connected, requested, p95, completedAge, maxGap);

    private static BleAdaptiveEnvironmentKey AdaptiveEnv(int build, string sha,
        string adapter = @"USB\VID_8087&PID_0026", string revision = "ble-adaptive-v1") =>
        new(build, sha, adapter, "radio-set-1", "Intel", "22.250.0.2", "2024-01-01", revision);

    private static void AdaptivePolicy(CheckFn check, ref int cases, ref int bad)
    {
        var text = new StringBuilder();
        void Line(string value) => text.Append(value).Append('\n');

        // ---- 计划：能力组合 × 原生测量 × 偏好预设 ----
        BleAdaptivePreset?[] preferred = [null, BleAdaptivePreset.Native, BleAdaptivePreset.Win11Fixed7p5,
            BleAdaptivePreset.Win11Flexible7p5To15, BleAdaptivePreset.Win11ThroughputOptimized,
            BleAdaptivePreset.Win11Fixed15];
        BleAdaptiveMeasurement[] natives =
        [
            FromHz(40, 40), FromHz(60, 59), FromHz(125, 125), FromHz(130, 129), Measure(5, 60, 60, 20)
        ];
        for (int mask = 0; mask < 16; mask++)
        {
            var capabilities = new BleAdaptiveCapabilities(
                Win11: (mask & 1) != 0, RequestApi: (mask & 2) != 0, ReadApi: (mask & 4) != 0,
                Win10AuditedStack: (mask & 8) != 0, Win10ExperimentalStack: (mask & 8) != 0 && (mask & 4) != 0);
            foreach (BleAdaptiveMeasurement native in natives)
            foreach (BleAdaptivePreset? preset in preferred)
            {
                BleAdaptivePresetPlan plan = BleAdaptivePresetPolicy.CreatePlan(capabilities, native, preset);
                Line("plan|" + mask + "|" + native.EffectiveHz + "|" + (preset?.ToString() ?? "-") + "|" +
                    plan.Code + "|" + plan.NativeTargetAlreadyMet + "|" + plan.Win10OptInEligible + "|" +
                    string.Join(",", plan.Candidates.Select(c => c.Id)));
            }
        }

        // ---- 候选评估 ----
        var random = new Random(20261129);
        double[] rates = [0, 30, 59, 60, 62, 100, 120, 124, 125, 127, 130];
        double?[] gaps = [null, 7, 8, 15, 25];
        double?[] maximums = [null, 18, 40, 90];
        double[] ages = [1, 14, 16];
        double[] durations = [10, 10.4, 12, 30, 31];
        BleAdaptiveMeasurement RandomMeasure(bool allowRequestFailure)
        {
            double duration = durations[random.Next(durations.Length)];
            double fd2 = rates[random.Next(rates.Length)];
            double parsed = random.Next(10) == 0 ? fd2 * .8 : fd2 * (0.95 + random.NextDouble() * .05);
            return Measure(duration, fd2, parsed, inputAge: random.Next(3) == 0 ? 600 : 20,
                connected: random.Next(20) != 0, requested: !allowRequestFailure || random.Next(5) != 0,
                p95: gaps[random.Next(gaps.Length)], completedAge: ages[random.Next(ages.Length)],
                maxGap: maximums[random.Next(maximums.Length)]);
        }

        for (int i = 0; i < 400; i++)
        {
            BleAdaptivePresetDecision decision = BleAdaptivePresetPolicy.EvaluateCandidate(
                RandomMeasure(false), RandomMeasure(false), RandomMeasure(true));
            Line("candidate|" + decision.Retain + "|" + decision.Code + "|" + decision.Summary);
        }

        // ---- 最终复核（两参与三参） ----
        for (int i = 0; i < 200; i++)
        {
            BleAdaptivePresetDecision decision = BleAdaptivePresetPolicy.VerifyFinal(
                RandomMeasure(false), RandomMeasure(false));
            Line("final2|" + decision.Retain + "|" + decision.Code + "|" + decision.Summary);
        }

        for (int i = 0; i < 200; i++)
        {
            BleAdaptivePresetDecision decision = BleAdaptivePresetPolicy.VerifyFinal(
                RandomMeasure(false), RandomMeasure(false), RandomMeasure(false));
            Line("final3|" + decision.Retain + "|" + decision.Code + "|" + decision.Summary);
        }

        // ---- 比较符号 ----
        for (int i = 0; i < 300; i++)
            Line("compare|" + Math.Sign(BleAdaptivePresetPolicy.CompareMeasurements(
                RandomMeasure(false), RandomMeasure(false))));

        // ---- 缓存可用性 ----
        string goodSha = new('a', 64);
        for (int i = 0; i < 150; i++)
        {
            BleAdaptiveEnvironmentKey current = AdaptiveEnv(
                random.Next(2) == 0 ? 22621 : 19045,
                random.Next(6) == 0 ? "short" : goodSha,
                random.Next(8) == 0 ? "unknown" : @"USB\VID_8087&PID_0026",
                random.Next(10) == 0 ? "" : "ble-adaptive-v1");
            BleAdaptiveEnvironmentKey stored = random.Next(5) == 0
                ? current with { DriverVersion = "22.250.0.3" } : current;
            var entry = new BleAdaptiveCachedPreset(stored,
                random.Next(6) == 0 ? BleAdaptivePreset.Native
                    : (BleAdaptivePreset)random.Next(1, 5),
                FromHz(60, 60), FromHz(random.Next(2) == 0 ? 127 : 62, 62),
                DateTimeOffset.UnixEpoch.AddDays(random.Next(-1, 9)));
            bool usable = BleAdaptivePresetPolicy.IsCacheUsable(entry, current, DateTimeOffset.UnixEpoch, out string code);
            Line("cache|" + usable + "|" + code);
        }

        string digest = CaptureDigest(text.ToString());
        check("adaptive", "decision digest=" + digest,
            digest == AdaptiveDecisionExpected || digest == AdaptiveDecisionExpectedEnglish, ref cases, ref bad);

        // 指纹之外，关键语义单独断言，避免把错误结论锁死。
        BleAdaptivePresetPlan met = BleAdaptivePresetPolicy.CreatePlan(
            new BleAdaptiveCapabilities(true, true, true), FromHz(130, 129));
        check("adaptive", "plan target preserved",
            met.NativeTargetAlreadyMet && met.Code == "native_target_preserved" && met.Candidates.Count == 0,
            ref cases, ref bad);
        BleAdaptivePresetPlan reordered = BleAdaptivePresetPolicy.CreatePlan(
            new BleAdaptiveCapabilities(true, true, true), FromHz(60, 60), BleAdaptivePreset.Win11Fixed15);
        check("adaptive", "plan prefers cached preset",
            reordered.Candidates.Count == 4 && reordered.Candidates[0].Preset == BleAdaptivePreset.Win11Fixed15 &&
            reordered.Candidates[1].Preset == BleAdaptivePreset.Win11Fixed7p5, ref cases, ref bad);

        BleAdaptiveMeasurement incumbent = FromHz(100, 100);
        check("adaptive", "candidate equal to incumbent rejected",
            BleAdaptivePresetPolicy.EvaluateCandidate(FromHz(60, 60), incumbent, FromHz(100, 100)).Code ==
            "incumbent_is_better", ref cases, ref bad);
        check("adaptive", "candidate target reached",
            BleAdaptivePresetPolicy.EvaluateCandidate(FromHz(60, 60), incumbent, FromHz(127, 127)) is
                { Retain: true, Code: "target_reached" }, ref cases, ref bad);
        // 下降超过 2Hz 属复核退化；只有「仍在原水平附近但低于可游玩线」才走 low_rate_verified。
        check("adaptive", "low final still retains best route",
            BleAdaptivePresetPolicy.VerifyFinal(FromHz(59, 59), FromHz(58, 58)) is
                { Retain: true, Code: "final_low_rate_verified" }, ref cases, ref bad);
        check("adaptive", "regressed final drops the preset",
            BleAdaptivePresetPolicy.VerifyFinal(FromHz(100, 100), FromHz(40, 40)) is
                { Retain: false, Code: "final_verification_regressed" }, ref cases, ref bad);
        check("adaptive", "playable dip preserved against native",
            BleAdaptivePresetPolicy.VerifyFinal(FromHz(60, 60), FromHz(127, 127), FromHz(90, 90)) is
                { Retain: true, Code: "final_playable_preserved" }, ref cases, ref bad);

        var cached = new BleAdaptiveCachedPreset(AdaptiveEnv(22621, goodSha), BleAdaptivePreset.Win11Fixed7p5,
            FromHz(60, 60), FromHz(127, 127), DateTimeOffset.UnixEpoch.AddDays(3));
        check("adaptive", "cache hit",
            BleAdaptivePresetPolicy.IsCacheUsable(cached, AdaptiveEnv(22621, goodSha),
                DateTimeOffset.UnixEpoch.AddDays(3), out string hitCode) &&
            hitCode == "cache_exact_environment_retest_preference", ref cases, ref bad);
        check("adaptive", "cache expired",
            !BleAdaptivePresetPolicy.IsCacheUsable(cached, AdaptiveEnv(22621, goodSha),
                DateTimeOffset.UnixEpoch.AddDays(11), out string expiredCode) && expiredCode == "cache_expired",
            ref cases, ref bad);
    }

    // ---------------- 周期调度与高分辨率定时器 ----------------

    private static void Timing(CheckFn check, ref int cases, ref int bad)
    {
        long oneMs = Math.Max(1, Stopwatch.Frequency / 1000);

        // 截止时刻推进：与旧实现逐拍比对，覆盖同一时刻多次推进、恰好整拍、长时间挂起与时钟回退。
        var random = new Random(20261128);
        for (int round = 0; round < 300; round++)
        {
            long interval = random.Next(1, 50) * oneMs;
            var live = new PeriodicSchedule(interval);
            var legacy = new LegacySchedule(interval);
            long now = 0;
            bool same = true;
            for (int step = 0; step < 25; step++)
            {
                now += random.Next(6) switch
                {
                    0 => 0,
                    1 => interval,
                    2 => interval * random.Next(2, 6),
                    3 => -random.Next(1, (int)interval),
                    _ => random.Next(0, (int)interval)
                };
                if (now < 0) now = 0;
                live.Advance(now);
                legacy.Advance(now);
                same &= live.NextTicks == legacy.NextTicks;
            }

            check("timing", "schedule " + round, same, ref cases, ref bad);
        }

        // 无论落后多少，推进后的截止时刻一定在未来，且跳过的整拍数与差值一致。
        for (int round = 0; round < 200; round++)
        {
            long interval = random.Next(1, 10) * oneMs;
            var schedule = new PeriodicSchedule(interval);
            long now = random.Next(0, 200) * interval;
            // 先前进一拍，再看这一拍是否已经过点：过点就整拍跳过。
            long afterFirstStep = schedule.NextTicks + interval;
            long skipped = schedule.Advance(now);
            long expected = now < afterFirstStep ? 0 : ((now - afterFirstStep) / interval) + 1;
            check("timing", "advance " + round,
                schedule.NextTicks > now && skipped == expected &&
                schedule.NextTicks == afterFirstStep + skipped * interval, ref cases, ref bad);
        }

        // 构造约束。
        check("timing", "rejects zero period",
            Capture(() => new HighResolutionPeriodicTimer(TimeSpan.Zero).Backend)
                .StartsWith("throw:ArgumentOutOfRangeException:", StringComparison.Ordinal), ref cases, ref bad);
        check("timing", "rejects negative period",
            Capture(() => new HighResolutionPeriodicTimer(TimeSpan.FromMilliseconds(-1)).Backend)
                .StartsWith("throw:ArgumentOutOfRangeException:", StringComparison.Ordinal), ref cases, ref bad);
        check("timing", "schedule rejects zero interval",
            Capture(() => new PeriodicSchedule(0).NextTicks.ToString())
                .StartsWith("throw:ArgumentOutOfRangeException:", StringComparison.Ordinal), ref cases, ref bad);
        check("timing", "tiny period still advances",
            new PeriodicSchedule(1).Advance(5) == 4, ref cases, ref bad);

        // 真实定时器：后端名必须是日志里认识的两个之一，且三次唤醒不会卡死。
        using (var timer = new HighResolutionPeriodicTimer(TimeSpan.FromMilliseconds(2)))
        {
            check("timing", "backend name",
                timer.Backend is "high_resolution_waitable_timer_absolute" or "stopwatch_fallback",
                ref cases, ref bad);
            check("timing", "interval ticks",
                timer.IntervalTicks == Math.Max(1,
                    (long)Math.Round(TimeSpan.FromMilliseconds(2).TotalSeconds * Stopwatch.Frequency)),
                ref cases, ref bad);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 3; i++) timer.WaitForNextTick(CancellationToken.None);
            double elapsedMs = watch.Elapsed.TotalMilliseconds;
            // 只要求「没卡死」：标称 6ms，但首次创建定时器的内核调用会吃掉第一拍的一部分，
            // 因此下限放到 3ms，上限 1s，不把调度抖动当失败。
            check("timing", "three ticks " + elapsedMs.ToString("F1"), elapsedMs >= 3 && elapsedMs <= 1000,
                ref cases, ref bad);
        }

        // 取消与释放语义。
        using (var timer = new HighResolutionPeriodicTimer(TimeSpan.FromMilliseconds(50)))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            check("timing", "already cancelled",
                Capture(() => timer.WaitForNextTick(cancelled.Token).ToString())
                    .StartsWith("throw:OperationCanceledException:", StringComparison.Ordinal), ref cases, ref bad);

            using var during = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
            var watch = Stopwatch.StartNew();
            string outcome = Capture(() => timer.WaitForNextTick(during.Token).ToString());
            check("timing", "cancelled while waiting " + watch.ElapsedMilliseconds,
                outcome.StartsWith("throw:OperationCanceledException:", StringComparison.Ordinal) &&
                watch.ElapsedMilliseconds < 1000, ref cases, ref bad);
        }

        var disposed = new HighResolutionPeriodicTimer(TimeSpan.FromMilliseconds(2));
        disposed.Dispose();
        check("timing", "wait after dispose",
            Capture(() => disposed.WaitForNextTick(CancellationToken.None).ToString())
                .StartsWith("throw:ObjectDisposedException:", StringComparison.Ordinal), ref cases, ref bad);
        check("timing", "dispose twice",
            Capture(() => { disposed.Dispose(); return ""; }) == "ok:", ref cases, ref bad);
    }

    /// <summary>旧 AdvanceDeadline 的逐字复刻，只作差分 oracle 用。</summary>
    private sealed class LegacySchedule
    {
        private readonly long interval;

        public LegacySchedule(long interval)
        {
            this.interval = interval;
            NextTicks = interval;
        }

        public long NextTicks { get; private set; }

        public void Advance(long now)
        {
            NextTicks += interval;
            if (NextTicks <= now)
            {
                long missed = ((now - NextTicks) / interval) + 1;
                NextTicks += missed * interval;
            }
        }
    }

    // ---------------- 目标地址、窗口布局、栈审计文本 ----------------

    private const string EnvDiagExpected = "BFE43F5BA5CE6782:7295";

    private static void SmallDiagnostics(CheckFn check, ref int envCases, ref int envBad,
        ref int targetCases, ref int targetBad, ref int layoutCases, ref int layoutBad)
    {
        // 地址解析：合法写法、分隔符、大小写、非公共地址。
        (string? Text, bool Ok, string Hex)[] addresses =
        [
            ("AA:BB:CC:DD:EE:FF", true, "AABBCCDDEEFF"),
            ("aa:bb:cc:dd:ee:ff", true, "AABBCCDDEEFF"),
            ("AA-BB-CC-DD-EE-FF", true, "AABBCCDDEEFF"),
            ("  aa:bb:cc:dd:ee:ff  ", true, "AABBCCDDEEFF"),
            ("00:00:00:00:00:00", false, ""),
            ("FF:FF:FF:FF:FF:FF", false, ""),
            ("AA:BB:CC:DD:EE", false, ""),
            ("AA:BB:CC:DD:EE:FFF", false, ""),
            ("AA:BB:CC:DD:EE:FG", false, ""),
            ("AABBCCDDEEFF", false, ""),
            ("AA/BB/CC/DD/EE/FF", false, ""),
            ("", false, ""),
            (null, false, "")
        ];
        foreach (var item in addresses)
        {
            bool parsed = BleDiagnosticTargetResolver.TryParsePublicAddress(item.Text, out ulong value);
            check("target", "parse " + (item.Text ?? "<null>"),
                parsed == item.Ok && (!parsed || value.ToString("X12") == item.Hex), ref targetCases, ref targetBad);
        }

        // 目标选择：显式地址优先并归一化；非法显式地址直接报错；否则取第一个可用存档地址。
        check("target", "explicit address wins",
            BleDiagnosticTargetResolver.Resolve("aa:bb:cc:dd:ee:ff", ["11:22:33:44:55:66"]) == "AA:BB:CC:DD:EE:FF",
            ref targetCases, ref targetBad);
        check("target", "saved fallback",
            BleDiagnosticTargetResolver.Resolve("  ",
                ["00:00:00:00:00:00", "11-22-33-44-55-66", "AA:BB:CC:DD:EE:FF"]) == "11:22:33:44:55:66",
            ref targetCases, ref targetBad);
        check("target", "nothing saved", BleDiagnosticTargetResolver.Resolve(null, []) == "",
            ref targetCases, ref targetBad);
        check("target", "invalid explicit rejected",
            Capture(() => BleDiagnosticTargetResolver.Resolve("nope", []))
                .StartsWith("throw:ArgumentException:", StringComparison.Ordinal), ref targetCases, ref targetBad);
        check("target", "unadvertised requires exact channels",
            BleDiagnosticTargetResolver.CanInitializeUnadvertisedTarget(true) &&
            !BleDiagnosticTargetResolver.CanInitializeUnadvertisedTarget(false), ref targetCases, ref targetBad);

        // 启动窗口：大屏限宽高、小屏按 4% 让位、极小屏兜底 1 DIP、工作区偏移按中心对齐。
        (System.Windows.Rect Area, double Left, double Top, double Width, double Height)[] layouts =
        [
            (new System.Windows.Rect(0, 0, 1920, 1080), 430, 120, 1060, 840),
            (new System.Windows.Rect(0, 0, 800, 600), 16, 12, 768, 576),
            (new System.Windows.Rect(0, 0, 20, 20), 0.4, 0.4, 19.2, 19.2),
            (new System.Windows.Rect(0, 0, 1, 1), 0, 0, 1, 1),
            (new System.Windows.Rect(100, 50, 1600, 900), 370, 80, 1060, 840)
        ];
        foreach (var item in layouts)
        {
            System.Windows.Rect fit = StartupWindowLayout.Fit(item.Area);
            check("layout", "fit " + item.Area.Width + "x" + item.Area.Height,
                Math.Abs(fit.Left - item.Left) < 1e-9 && Math.Abs(fit.Top - item.Top) < 1e-9 &&
                Math.Abs(fit.Width - item.Width) < 1e-9 && Math.Abs(fit.Height - item.Height) < 1e-9,
                ref layoutCases, ref layoutBad);
        }

        check("layout", "rejects empty", Capture(() => StartupWindowLayout.Fit(System.Windows.Rect.Empty).ToString())
            .StartsWith("throw:ArgumentOutOfRangeException:", StringComparison.Ordinal), ref layoutCases, ref layoutBad);
        check("layout", "rejects nan",
            Capture(() => StartupWindowLayout.Fit(new System.Windows.Rect(0, 0, double.NaN, 600)).ToString())
                .StartsWith("throw:ArgumentOutOfRangeException:", StringComparison.Ordinal),
            ref layoutCases, ref layoutBad);
        check("layout", "rejects zero height",
            Capture(() => StartupWindowLayout.Fit(new System.Windows.Rect(0, 0, 800, 0)).ToString())
                .StartsWith("throw:ArgumentOutOfRangeException:", StringComparison.Ordinal),
            ref layoutCases, ref layoutBad);

        // 栈审计文本：键名是日志分析脚本的契约，逐行核对 + 指纹锁住。
        BluetoothAdapterDiagnosticInfo adapter = TestAdapter("Intel(R) Wireless Bluetooth(R)");
        check("envdiag", "driver line",
            BleEnvironmentDiagnostics.DriverLine(adapter) ==
            "[BLE_DRIVER_AUDIT] name=\"Intel(R) Wireless Bluetooth(R)\" pnp_id=\"USB\\VID_8087&PID_0026\\5&1\" " +
            "vid=8087 pid=0026 provider=\"Intel\" driver_version=22.250.0.2 driver_date=2024-01-01",
            ref envCases, ref envBad);
        check("envdiag", "stack line",
            BleEnvironmentDiagnostics.StackLine("10.0.22621.4", "10.0.22621.0", "sha") ==
            "[BLE_STACK_AUDIT] bthport_version=10.0.22621.4 runtime_os=10.0.22621.0 sha256=sha",
            ref envCases, ref envBad);
        check("envdiag", "os line",
            BleEnvironmentDiagnostics.OsLine("22H2", "22621", 4890) ==
            "[BLE_STACK_AUDIT] display_version=22H2 build=22621 ubr=4890", ref envCases, ref envBad);
        check("envdiag", "value absent",
            BleEnvironmentDiagnostics.ValueLine("X", null) ==
            "[BLE_STACK_VALUE] name=X value=absent read_only=true configured_value_is_not_effectiveness_proof",
            ref envCases, ref envBad);
        check("envdiag", "value dword",
            BleEnvironmentDiagnostics.ValueLine("X", 7).StartsWith("[BLE_STACK_VALUE] name=X value=7 ",
                StringComparison.Ordinal), ref envCases, ref envBad);
        check("envdiag", "value long is not dword",
            BleEnvironmentDiagnostics.ValueLine("X", 7L).Contains("value=non_dword", StringComparison.Ordinal),
            ref envCases, ref envBad);
        check("envdiag", "value string is not dword",
            BleEnvironmentDiagnostics.ValueLine("X", "7").Contains("value=non_dword", StringComparison.Ordinal),
            ref envCases, ref envBad);

        var digestInput = new StringBuilder();
        var random = new Random(20261127);
        string[] names = ["LEConnectionIntervalMin", "LEConnectionIntervalMax", "LEConnectionLatency",
            "LEConnectionLSTO", "ConnectionIntervalMin", "ConnectionIntervalMax", "ConnectionLatency",
            "LinkSupervisionTimeout"];
        for (int i = 0; i < 60; i++)
        {
            object? value = random.Next(4) switch
            {
                0 => null,
                1 => random.Next(0, 700),
                2 => "text" + random.Next(10),
                _ => (long)random.Next(0, 700)
            };
            digestInput.Append(BleEnvironmentDiagnostics.ValueLine(names[random.Next(names.Length)], value));
        }
        digestInput.Append(BleEnvironmentDiagnostics.StackLine("10.0." + random.Next(20000),
            "10.0.22621.0", "d34db33f"));
        digestInput.Append(BleEnvironmentDiagnostics.OsLine("22H2", random.Next(19045, 26100), random.Next(4000)));
        digestInput.Append(BleEnvironmentDiagnostics.DriverLine(adapter));
        string digest = CaptureDigest(digestInput.ToString());
        check("envdiag", "audit digests=" + digest, digest == EnvDiagExpected, ref envCases, ref envBad);
    }

    // ---------------- 输入帧状态 ----------------

    private static void GamepadModel(CheckFn check, ref int cases, ref int bad)
    {
        // 按键位序直接对应输出报告的按键字，逐个锁定。
        (string Name, GamepadButtons Value)[] bits =
        [
            ("South", GamepadButtons.South), ("East", GamepadButtons.East), ("West", GamepadButtons.West),
            ("North", GamepadButtons.North), ("L1", GamepadButtons.L1), ("R1", GamepadButtons.R1),
            ("L2", GamepadButtons.L2), ("R2", GamepadButtons.R2), ("Back", GamepadButtons.Back),
            ("Start", GamepadButtons.Start), ("LeftStick", GamepadButtons.LeftStick),
            ("RightStick", GamepadButtons.RightStick), ("DPadDown", GamepadButtons.DPadDown),
            ("DPadRight", GamepadButtons.DPadRight), ("DPadLeft", GamepadButtons.DPadLeft),
            ("DPadUp", GamepadButtons.DPadUp), ("Home", GamepadButtons.Home), ("Capture", GamepadButtons.Capture),
            ("PaddleRight", GamepadButtons.PaddleRight), ("PaddleLeft", GamepadButtons.PaddleLeft),
            ("Aux", GamepadButtons.Aux)
        ];
        check("gamepad", "none is zero", GamepadButtons.None == 0, ref cases, ref bad);
        for (int i = 0; i < bits.Length; i++)
            check("gamepad", "bit " + bits[i].Name, (ulong)bits[i].Value == 1UL << i, ref cases, ref bad);
        check("gamepad", "bit count", bits.Length == 21, ref cases, ref bad);
        check("gamepad", "no duplicate bits",
            bits.Select(b => (ulong)b.Value).Distinct().Count() == bits.Length, ref cases, ref bad);

        GamepadState neutral = GamepadState.Neutral();
        check("gamepad", "neutral defaults",
            neutral.Buttons == GamepadButtons.None &&
            neutral.Lx == GamepadState.AxisCenter && neutral.Ly == GamepadState.AxisCenter &&
            neutral.Rx == GamepadState.AxisCenter && neutral.Ry == GamepadState.AxisCenter &&
            neutral.L2 == 0 && neutral.R2 == 0 &&
            neutral.BatteryPercent == GamepadState.BatteryUnknown &&
            neutral.BatteryLevel == GamepadState.BatteryUnknown &&
            neutral.SwitchRawImuSamples.Length == 0 && neutral.SwitchRawImuOffset == -1 &&
            neutral.SwitchRawImuBytesHex.Length == 0 && neutral.Updates == 0 &&
            !neutral.AccelValid && !neutral.GyroValid && !neutral.GyroBiasApplied &&
            !neutral.HeadsetPresent && !neutral.HeadsetPresenceKnown, ref cases, ref bad);

        GamepadButtons combo = GamepadButtons.South | GamepadButtons.L1 | GamepadButtons.Aux;
        var pressed = new GamepadState { Buttons = combo };
        check("gamepad", "is pressed",
            bits.Where(b => (combo & b.Value) != 0).All(b => pressed.IsPressed(b.Value)) &&
            bits.Where(b => (combo & b.Value) == 0).All(b => !pressed.IsPressed(b.Value)) &&
            !pressed.IsPressed(GamepadButtons.None), ref cases, ref bad);

        var sampleA = new SwitchImuRawSample(1, 2, 3, 4, 5, 6, 0, 0, 111, 7);
        var sampleB = new SwitchImuRawSample(9, 9, 9, 9, 9, 9, 1, 12, 222, 8);
        var source = new GamepadState
        {
            Buttons = GamepadButtons.North | GamepadButtons.R2, RawButtons = 0xA5A5,
            Lx = 11, Ly = 22, Rx = 33, Ry = 44, L2 = 55, R2 = 66,
            AccelValid = true, GyroValid = true, GyroBiasApplied = true,
            AccelX = -1, AccelY = -2, AccelZ = -3, GyroX = -4, GyroY = -5, GyroZ = -6,
            SwitchRawImuSamples = [sampleA], SwitchRawImuOffset = 0, SwitchRawImuBytesHex = "04AB",
            MotionTimestampUs = 1234, SourceTimestampTicks = 555, RawNotificationSequence = 9,
            BatteryPercent = 80, BatteryLevel = 3, BatteryPercentApproximate = true, BatteryPowerKnown = true,
            BatteryExternalPower = true, BatteryVoltageMillivolts = 4100, BatteryCharging = true,
            HeadsetPresenceKnown = true, HeadsetPresent = true, Updates = 42,
            Ps5Touch = new Ps5TouchPoint(true, 100, 200)
        };
        GamepadState copy = source.Clone();
        check("gamepad", "clone copies every field",
            copy.Buttons == source.Buttons && copy.RawButtons == 0xA5A5 &&
            copy.Lx == 11 && copy.Ly == 22 && copy.Rx == 33 && copy.Ry == 44 && copy.L2 == 55 && copy.R2 == 66 &&
            copy.AccelValid && copy.GyroValid && copy.GyroBiasApplied &&
            copy.AccelX == -1 && copy.AccelZ == -3 && copy.GyroY == -5 &&
            copy.SwitchRawImuOffset == 0 && copy.SwitchRawImuBytesHex == "04AB" &&
            copy.MotionTimestampUs == 1234 && copy.SourceTimestampTicks == 555 && copy.RawNotificationSequence == 9 &&
            copy.BatteryPercent == 80 && copy.BatteryLevel == 3 && copy.BatteryPercentApproximate &&
            copy.BatteryPowerKnown && copy.BatteryExternalPower && copy.BatteryVoltageMillivolts == 4100 &&
            copy.BatteryCharging && copy.HeadsetPresenceKnown && copy.HeadsetPresent && copy.Updates == 42 &&
            copy.Ps5Touch == source.Ps5Touch && !ReferenceEquals(copy, source), ref cases, ref bad);

        // 改原对象的标量与数组元素，克隆不受影响（IMU 数组必须是独立副本）。
        source.Lx = 999;
        source.Buttons = GamepadButtons.None;
        source.SwitchRawImuSamples[0] = sampleB;
        check("gamepad", "clone isolates scalars",
            copy.Lx == 11 && copy.Buttons == (GamepadButtons.North | GamepadButtons.R2), ref cases, ref bad);
        check("gamepad", "clone isolates imu array",
            copy.SwitchRawImuSamples.Length == 1 && copy.SwitchRawImuSamples[0] == sampleA &&
            !ReferenceEquals(copy.SwitchRawImuSamples, source.SwitchRawImuSamples), ref cases, ref bad);
        check("gamepad", "clone empty imu stays empty",
            GamepadState.Neutral().Clone().SwitchRawImuSamples.Length == 0, ref cases, ref bad);

        var snapshot = new OutputPadSnapshot(0x10UL, 1, 2, 3, 4);
        check("gamepad", "pad snapshot defaults",
            snapshot.Buttons == 0x10 && snapshot.Lx == 1 && snapshot.Ry == 4 &&
            snapshot.Lt == 0 && snapshot.Rt == 0, ref cases, ref bad);
        check("gamepad", "pad snapshot equality",
            snapshot == new OutputPadSnapshot(0x10UL, 1, 2, 3, 4) &&
            snapshot != snapshot with { Lt = 9 }, ref cases, ref bad);
    }

    // ---------------- 研究采集器 ----------------

    private const string ResearchFormatExpected = "8A4B8B5A43D87063:5527";

    private static string CaptureDigest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16] + ":" + text.Length;

    private static void ResearchCapture(CheckFn check, ref int cases, ref int bad)
    {
        // 容量 3、不读，写 10 帧：挤掉 7 帧，留下最新的 3 帧且有序。
        var queue = new CaptureFrameQueue(3);
        for (ulong i = 0; i < 10; i++)
            queue.TryWrite(new CaptureFrame(i, DateTimeOffset.UnixEpoch.AddMilliseconds(i), i, [(byte)i]));
        check("research", "queue drops oldest", queue.Dropped == 7 && queue.Reader.Count == 3,
            ref cases, ref bad);
        queue.Complete();
        var kept = new List<ulong>();
        while (queue.Reader.TryRead(out CaptureFrame frame)) kept.Add(frame.Sequence);
        check("research", "queue keeps newest", kept.SequenceEqual([7UL, 8UL, 9UL]), ref cases, ref bad);

        // 路径占用：同路径（大小写不同）只允许一个；释放后可再占用。
        string leasePath = Path.Combine(Path.GetTempPath(), "pro2-lease-" + Guid.NewGuid().ToString("N")[..8]);
        check("research", "lease claims once",
            CapturePathLease.TryAcquire(leasePath) && !CapturePathLease.TryAcquire(leasePath.ToUpperInvariant()),
            ref cases, ref bad);
        CapturePathLease.Release(leasePath);
        check("research", "lease released", CapturePathLease.TryAcquire(leasePath), ref cases, ref bad);
        CapturePathLease.Release(leasePath);

        // 未配置 / 非法路径：不抛异常，关闭采集并留下原因。
        var unset = new Pro2Fd2ResearchRecorder("   ");
        check("research", "unset disabled",
            !unset.Enabled && unset.OutputPath.Length == 0 && unset.DisabledReason == "not_configured",
            ref cases, ref bad);
        var invalid = new Pro2Fd2ResearchRecorder("bad\0path.jsonl");
        check("research", "invalid path disabled",
            !invalid.Enabled && invalid.DisabledReason.StartsWith("invalid_path:", StringComparison.Ordinal),
            ref cases, ref bad);
        invalid.TryRecord(1, DateTimeOffset.UnixEpoch, 1, [1, 2]);
        check("research", "disabled recorder ignores frames", invalid.Dropped == 0, ref cases, ref bad);

        var relative = new Pro2Fd2ResearchRecorder("relative-capture.jsonl");
        check("research", "relative path absolute",
            relative.Enabled && Path.IsPathRooted(relative.OutputPath), ref cases, ref bad);

        // 结构指纹：固定输入下的表头与帧文本，键名是下游脚本的契约。
        var digestInput = new StringBuilder();
        for (int i = 0; i < 40; i++)
            digestInput.Append(ResearchCaptureFormat.Frame((ulong)i,
                DateTimeOffset.UnixEpoch.AddMilliseconds(i * 7.5), i, [(byte)i, 0xAB]));
        digestInput.Append(ResearchCaptureFormat.Header(DateTimeOffset.UnixEpoch, "PRO2_FD2_RESEARCH_CAPTURE"));
        string digest = CaptureDigest(digestInput.ToString());
        check("research", "capture format digest=" + digest, digest == ResearchFormatExpected,
            ref cases, ref bad);

        ResearchCaptureFile(check, ref cases, ref bad);
    }

    private static void ResearchCaptureFile(CheckFn check, ref int cases, ref int bad)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pro2-research-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(directory, "capture.jsonl");
        try
        {
            int claimedBefore = CapturePathLease.ClaimedCount;
            var first = new Pro2Fd2ResearchRecorder(path);
            check("research", "recorder enabled", first.Enabled && first.OutputPath == path, ref cases, ref bad);
            for (ulong i = 0; i < 200; i++)
                first.TryRecord(i, DateTimeOffset.UnixEpoch.AddMilliseconds(i * 7.5), i == 0 ? 0 : 7.5,
                    [0x04, (byte)i]);

            // 同路径第二个实例只丢弃，不碰文件。
            var duplicate = new Pro2Fd2ResearchRecorder(path);
            duplicate.TryRecord(999, DateTimeOffset.UnixEpoch, 0, [0xFF]);
            check("research", "duplicate suppressed", duplicate.Dropped == 1, ref cases, ref bad);
            duplicate.DisposeAsync().AsTask().GetAwaiter().GetResult();

            first.DisposeAsync().AsTask().GetAwaiter().GetResult();
            check("research", "dispose releases lease", CapturePathLease.ClaimedCount == claimedBefore,
                ref cases, ref bad);
            check("research", "no drops on clean capture", first.Dropped == 0 && first.WriteFailure.Length == 0,
                ref cases, ref bad);

            string[] lines = File.ReadAllLines(path);
            check("research", "capture line count", lines.Length == 201, ref cases, ref bad);
            if (lines.Length > 0)
            {
                using JsonDocument header = JsonDocument.Parse(lines[0]);
                JsonElement root = header.RootElement;
                check("research", "capture header",
                    root.GetProperty("type").GetString() == "research_capture" &&
                    root.GetProperty("source").GetString() == "Pro2 BLE FD2" &&
                    root.GetProperty("environment_variable").GetString() == "PRO2_FD2_RESEARCH_CAPTURE" &&
                    root.GetProperty("created").GetDateTimeOffset() != DateTimeOffset.MinValue,
                    ref cases, ref bad);
            }

            int frameProblems = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                using JsonDocument frame = JsonDocument.Parse(lines[i]);
                JsonElement body = frame.RootElement.GetProperty("frame");
                bool ok = frame.RootElement.GetProperty("type").GetString() == "frame" &&
                    body.GetProperty("FrameIndex").GetUInt64() == (ulong)(i - 1) &&
                    body.GetProperty("RawFd2Hex").GetString() == "04" + ((byte)(i - 1)).ToString("X2") &&
                    Math.Abs(body.GetProperty("DeltaMsFromPreviousFrame").GetDouble() - (i == 1 ? 0 : 7.5)) < 1e-9;
                if (!ok) frameProblems++;
            }
            check("research", "capture frames " + frameProblems, frameProblems == 0, ref cases, ref bad);

            // 释放后同路径可以再采一次（覆盖写，不与上一个文件拼接）。
            var second = new Pro2Fd2ResearchRecorder(path);
            second.TryRecord(1, DateTimeOffset.UnixEpoch, 0, [0x01]);
            second.DisposeAsync().AsTask().GetAwaiter().GetResult();
            check("research", "capture reuse after release", File.ReadAllLines(path).Length == 2,
                ref cases, ref bad);
        }
        catch (Exception ex)
        {
            check("research", "capture file exception " + ex.GetType().Name, false, ref cases, ref bad);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // ---------------- Win11 自定义连接参数（ABI 回环） ----------------

#pragma warning disable CA1416
    private static void Win11Abi(CheckFn check, ref int cases, ref int bad)
    {
        (ushort Min, ushort Max, ushort Latency, ushort Timeout, bool Supported)[] presets =
        [
            (6, 6, 0, 600, true), (6, 6, 0, 200, true), (7, 12, 0, 300, true), (6, 12, 0, 600, true),
            (5, 6, 0, 600, false), (6, 5, 0, 600, false), (6, 13, 0, 600, false), (6, 6, 1, 600, false),
            (6, 6, 0, 199, false), (6, 6, 0, 601, false), (0, 0, 0, 0, false), (12, 6, 0, 600, false)
        ];
        foreach (var preset in presets)
            check("abi", "preset " + preset.Min + "/" + preset.Max + "/" + preset.Latency + "/" + preset.Timeout,
                Win11CustomConnectionParameters.IsSupportedPreset(preset.Min, preset.Max, preset.Latency,
                    preset.Timeout) == preset.Supported, ref cases, ref bad);

        (string[] Args, bool Requested)[] cli =
        [
            (["--win11-7p5-abi-test", "--ble-diagnostic"], true),
            (["--BLE-DIAGNOSTIC", "--Win11-7P5-ABI-TEST"], true),
            (["--win11-7p5-abi-test"], false),
            (["--ble-diagnostic"], false),
            ([], false),
            (["--win11-7p5-abi-test", "--other"], false)
        ];
        foreach (var item in cli)
            check("abi", "cli " + string.Join(" ", item.Args),
                Win11CustomConnectionParameters.IsCliRequested(item.Args) == item.Requested, ref cases, ref bad);

        // 真实 ABI 回环：投影自己的 CCW，四个值逐个读回（纯本地投影，不触发系统调用）。
        var standard = new Win11CustomConnectionParameters();
        check("abi", "default round-trip",
            standard.Readback() == ((ushort)6, (ushort)6, (ushort)0, (ushort)600) &&
            standard.Parameters.MinConnectionInterval == 6 &&
            standard.Parameters.MaxConnectionInterval == 6 &&
            standard.Parameters.ConnectionLatency == 0 && standard.Parameters.LinkTimeout == 600,
            ref cases, ref bad);
        var wide = new Win11CustomConnectionParameters(7, 12, 0, 300);
        check("abi", "custom round-trip", wide.Readback() == ((ushort)7, (ushort)12, (ushort)0, (ushort)300),
            ref cases, ref bad);
        check("abi", "rejects unsupported preset",
            Capture(() => new Win11CustomConnectionParameters(1).GetterTelemetry)
                .StartsWith("throw:ArgumentOutOfRangeException:", StringComparison.Ordinal), ref cases, ref bad);
        check("abi", "telemetry shape",
            standard.GetterTelemetry.StartsWith("get_timeout=", StringComparison.Ordinal) &&
            standard.GetterTelemetry.Contains(" get_latency=") &&
            standard.GetterTelemetry.Contains(" get_min=") &&
            standard.GetterTelemetry.Contains(" get_max="), ref cases, ref bad);
    }
#pragma warning restore CA1416

    // ---------------- BLE 诊断（报告 / 系统快照文本）：行为指纹 ----------------

    // 这两块输出的是给人看、也要逐字进日志的文本，结构可以改、文字不能变；
    // 因此不复制旧实现，改取固定种子随机数据的输出指纹。
    private const string BleReportLinesExpected = "3F222F09393B35B9:363309";
    // 报告日志行是纯 ASCII，中英两版一致；系统快照是中文段落，英文版另有指纹。
    private const string BluetoothSnapshotTextExpected = "45E489668A4776CB:320088";
    private const string BluetoothSnapshotTextExpectedEnglish = "D5FE9BD344FA4A1A:354330";

    private static string? bleReportLinesDigest;
    private static string? bluetoothSnapshotTextDigest;

    private static void BleDiagnostics(Random random, CheckFn check, ref int cases, ref int bad)
    {
        BleCaptureWindow(random, check, ref cases, ref bad);
        BleGapBuckets(random, check, ref cases, ref bad);
        BleVerdict(random, check, ref cases, ref bad);
        BleAdapterParsing(random, check, ref cases, ref bad);

        string report = bleReportLinesDigest ??= ComputeBleReportLinesDigest();
        check("blediag", "report lines " + report, report == BleReportLinesExpected, ref cases, ref bad);
        string snapshot = bluetoothSnapshotTextDigest ??= ComputeBluetoothSnapshotTextDigest();
        check("blediag", "snapshot text " + snapshot,
            snapshot == BluetoothSnapshotTextExpected || snapshot == BluetoothSnapshotTextExpectedEnglish, ref cases, ref bad);
    }

    // 采样窗：同一串脚本事件（含采集前回调、乱序到达、重复时间戳、超容量截断）分别喂给
    // 新实现与旧实现，逐字段比对，包括样本数组本身。
    private static void BleCaptureWindow(Random random, CheckFn check, ref int cases, ref int bad)
    {
        for (int round = 0; round < 60; round++)
        {
            long started = random.Next(10_000, 900_000);
            var live = new Win10BleDiagnosticCapture(started, DateTimeOffset.UnixEpoch);
            var legacy = new LegacyBleCaptureWindow(started);
            int events = round < 4 ? 9000 + round * 250 : random.Next(0, 500);
            for (int i = 0; i < events; i++)
            {
                // 5% 落在窗口开始之前，模拟采集启动前就排队的回调。
                long ticks = started + (random.Next(20) == 0 ? -random.Next(1, 2000) : random.Next(0, 90_000));
                bool isFd2 = random.Next(4) != 0;
                switch (random.Next(10))
                {
                    case < 5:
                        live.RecordRaw(ticks, isFd2);
                        legacy.RecordRaw(ticks, isFd2);
                        break;
                    case < 9:
                        live.RecordParsed(ticks, isFd2);
                        legacy.RecordParsed(ticks, isFd2);
                        break;
                    default:
                        string reason = random.Next(4) == 0 ? "" : "code_" + random.Next(3);
                        live.MarkInterrupted(reason);
                        legacy.MarkInterrupted(reason);
                        break;
                }
            }

            long completed = started + random.Next(0, 120_000);
            bool finished = random.Next(2) == 0;
            bool connected = random.Next(2) == 0;
            Win10BleDiagnosticSamples liveSamples = live.Snapshot(completed, finished, connected);
            Win10BleDiagnosticSamples legacySamples = legacy.Snapshot(completed, finished, connected);
            check("blediag", "capture " + round + " " + SampleDiff(liveSamples, legacySamples),
                SameSamples(liveSamples, legacySamples), ref cases, ref bad);
        }
    }

    private static string SampleDiff(Win10BleDiagnosticSamples a, Win10BleDiagnosticSamples b)
    {
        if (a.RawCount != b.RawCount) return "rawCount " + a.RawCount + "/" + b.RawCount;
        if (a.Fd2Count != b.Fd2Count) return "fd2Count " + a.Fd2Count + "/" + b.Fd2Count;
        if (a.ParsedCount != b.ParsedCount) return "parsedCount " + a.ParsedCount + "/" + b.ParsedCount;
        if (a.RawTicks.Length != b.RawTicks.Length) return "rawLen " + a.RawTicks.Length + "/" + b.RawTicks.Length;
        if (a.Fd2Ticks.Length != b.Fd2Ticks.Length) return "fd2Len " + a.Fd2Ticks.Length + "/" + b.Fd2Ticks.Length;
        if (a.ParsedTicks.Length != b.ParsedTicks.Length) return "parsedLen " + a.ParsedTicks.Length + "/" + b.ParsedTicks.Length;
        if (a.Interruption != b.Interruption) return "interruption " + a.Interruption + "/" + b.Interruption;
        if (a.LastParsedInputAgeMs != b.LastParsedInputAgeMs) return "age " + a.LastParsedInputAgeMs + "/" + b.LastParsedInputAgeMs;
        if (a.MaximumFd2SilenceMs != b.MaximumFd2SilenceMs) return "fd2Silence " + a.MaximumFd2SilenceMs + "/" + b.MaximumFd2SilenceMs;
        if (a.MaximumParsedSilenceMs != b.MaximumParsedSilenceMs) return "parsedSilence " + a.MaximumParsedSilenceMs + "/" + b.MaximumParsedSilenceMs;
        if (a.GapSamplesTruncated != b.GapSamplesTruncated) return "truncated";
        return "";
    }

    private static bool SameSamples(Win10BleDiagnosticSamples a, Win10BleDiagnosticSamples b) =>
        a.StartedTicks == b.StartedTicks && a.CompletedTicks == b.CompletedTicks &&
        a.RawCount == b.RawCount && a.Fd2Count == b.Fd2Count && a.ParsedCount == b.ParsedCount &&
        a.MeasurementCompleted == b.MeasurementCompleted && a.ConnectedAtEnd == b.ConnectedAtEnd &&
        a.Interruption == b.Interruption && a.GapSamplesTruncated == b.GapSamplesTruncated &&
        a.LastParsedInputAgeMs == b.LastParsedInputAgeMs &&
        a.MaximumFd2SilenceMs == b.MaximumFd2SilenceMs &&
        a.MaximumParsedSilenceMs == b.MaximumParsedSilenceMs &&
        a.DurationSeconds == b.DurationSeconds &&
        a.RawTicks.SequenceEqual(b.RawTicks) && a.Fd2Ticks.SequenceEqual(b.Fd2Ticks) &&
        a.ParsedTicks.SequenceEqual(b.ParsedTicks);

    // 间隔统计：固定边界（空/单点/重复/负数/跨桶边界）+ 随机序列，比对分位与分桶。
    private static void BleGapBuckets(Random random, CheckFn check, ref int cases, ref int bad)
    {
        long perMs = Stopwatch.Frequency / 1000;
        long[][] fixedCases =
        [
            [],
            [1000],
            [1000, 1000],
            [1000, 1000 + perMs * 5],
            [0, perMs * 11],
            [0, perMs * 11 + perMs / 4],
            [0, perMs * 47 + perMs / 2],
            [0, perMs * 65],
            [0, perMs * 65 + 1],
            [5000, 5000, 5000, 5000],
            [0, perMs], // 1ms：低于 5ms 下界，应落 other
            [0, -perMs * 10] // 负间隔：落 other
        ];
        foreach (long[] ticks in fixedCases)
        {
            check("blediag", "gap fixed " + ticks.Length + ":" + string.Join(",", ticks),
                GapKey(Win10BleDiagnostic.BuildGapStatistics(ticks)) == GapKey(LegacyGapStatistics(ticks)),
                ref cases, ref bad);
        }

        for (int i = 0; i < 500; i++)
        {
            long ticksAt = random.Next(0, 10_000);
            long[] ticks = new long[random.Next(0, 240)];
            for (int j = 0; j < ticks.Length; j++)
            {
                // 以 1.25ms（BLE 最小单位）的倍数为步长，落在真实桶边界附近。
                ticksAt += random.Next(0, 3) == 0
                    ? (long)(random.NextDouble() * perMs * 70)
                    : perMs * 5 + random.Next(0, 4) * (perMs * 125 / 100);
                ticks[j] = ticksAt;
            }

            check("blediag", "gap random " + i,
                GapKey(Win10BleDiagnostic.BuildGapStatistics(ticks)) == GapKey(LegacyGapStatistics(ticks)),
                ref cases, ref bad);
        }
    }

    private static string GapKey(BleGapStatistics gaps)
    {
        var text = new StringBuilder();
        text.Append(gaps.SampleCount).Append('|')
            .Append(gaps.P50Ms.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(gaps.P90Ms.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(gaps.P95Ms.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(gaps.P99Ms.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(gaps.MinMs.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(gaps.MaxMs.ToString("R", CultureInfo.InvariantCulture)).Append('|');
        foreach (KeyValuePair<string, int> pair in gaps.Distribution)
        {
            text.Append(pair.Key).Append('=').Append(pair.Value).Append(';');
        }

        return text.ToString();
    }

    // 判定：失败原因优先级、速率分级、125Hz 接受条件。
    private static void BleVerdict(Random random, CheckFn check, ref int cases, ref int bad)
    {
        foreach (Win10BleDiagnosticSamples samples in SampleEdgeCases())
        {
            check("blediag", "failure edge " + LegacyFailure(samples),
                BleMeasurementVerdict.Failure(samples) == LegacyFailure(samples), ref cases, ref bad);
        }

        for (int i = 0; i < 900; i++)
        {
            Win10BleDiagnosticSamples samples = RandomSamples(random);
            check("blediag", "failure random " + i,
                BleMeasurementVerdict.Failure(samples) == LegacyFailure(samples), ref cases, ref bad);
        }

        double[] rates = [-1, 0, 1, 29.9, 30, 30.0000001, 49.9, 50, 59.9, 60, 99.9, 100,
            119.9, 120, 124.9, 125, 125.1, 133.3, 240, double.NaN, double.PositiveInfinity, -double.PositiveInfinity];
        foreach (double fd2 in rates)
        foreach (double parsed in rates)
        {
            check("blediag", "classify " + fd2 + "/" + parsed,
                BleMeasurementVerdict.ClassifyRates(fd2, parsed) == LegacyClassifyRates(fd2, parsed) &&
                BleMeasurementVerdict.Is125HzInputAccepted(fd2, parsed) == LegacyIs125Accepted(fd2, parsed),
                ref cases, ref bad);
        }

        for (int i = 0; i < 600; i++)
        {
            double fd2 = random.Next(0, 400) + random.NextDouble();
            double parsed = random.Next(0, 400) + random.NextDouble();
            check("blediag", "classify random " + i,
                BleMeasurementVerdict.ClassifyRates(fd2, parsed) == LegacyClassifyRates(fd2, parsed) &&
                BleMeasurementVerdict.Is125HzInputAccepted(fd2, parsed) == LegacyIs125Accepted(fd2, parsed),
                ref cases, ref bad);
        }
    }

    private static IEnumerable<Win10BleDiagnosticSamples> SampleEdgeCases()
    {
        long tenSeconds = 10 * Stopwatch.Frequency;
        yield return Samples(0, tenSeconds, new long[10], new long[10], new long[10]);
        yield return MakeSamples(0, tenSeconds, 10, 10, 10, true, true, 0, 0, 0);
        yield return MakeSamples(0, tenSeconds - 1, 10, 10, 10, true, true, 0, 0, 0);
        yield return MakeSamples(0, tenSeconds, 10, 10, 10, true, true, 0, 0, 0) with { Interruption = "gatt" };
        yield return MakeSamples(0, tenSeconds, 10, 10, 10, true, false, 0, 0, 0);
        yield return MakeSamples(0, tenSeconds, 10, 10, 10, false, true, 0, 0, 0);
        yield return MakeSamples(0, tenSeconds, 0, 0, 0, true, true, 0, 0, 0);
        yield return MakeNulls(null, null, null);
        yield return MakeNulls(1, 2, null);
        yield return MakeNulls(double.NaN, 1, 1);
        yield return MakeNulls(-1, 1, 1);
        yield return MakeNulls(500, 500, 500);
        yield return MakeNulls(500.5, 0, 0);
    }

    private static Win10BleDiagnosticSamples Samples(long started, long completed, long[] raw, long[] fd2, long[] parsed) =>
        new(started, completed, raw, fd2, parsed);

    private static Win10BleDiagnosticSamples MakeSamples(long started, long completed, int raw, int fd2, int parsed,
        bool finished, bool connected, double? age, double? fd2Silence, double? parsedSilence) =>
        new(started, completed, new long[raw], new long[fd2], new long[parsed])
        {
            MeasurementCompleted = finished,
            ConnectedAtEnd = connected,
            LastParsedInputAgeMs = age,
            MaximumFd2SilenceMs = fd2Silence,
            MaximumParsedSilenceMs = parsedSilence
        };

    private static Win10BleDiagnosticSamples MakeNulls(double? age, double? fd2Silence, double? parsedSilence) =>
        MakeSamples(0, 10 * Stopwatch.Frequency, 10, 10, 10, true, true, age, fd2Silence, parsedSilence);

    private static Win10BleDiagnosticSamples RandomSamples(Random random)
    {
        long started = random.Next(0, 100_000);
        long completed = started + (long)(random.NextDouble() * 20 * Stopwatch.Frequency);
        double? Freshness() => random.Next(6) switch
        {
            0 => null,
            1 => double.NaN,
            2 => -random.NextDouble() * 10,
            3 => 500.0,
            4 => 500.0 + random.NextDouble(),
            _ => random.NextDouble() * 900
        };
        return new Win10BleDiagnosticSamples(started, completed,
            new long[random.Next(0, 40)], new long[random.Next(0, 40)], new long[random.Next(0, 40)])
        {
            MeasurementCompleted = random.Next(4) != 0,
            ConnectedAtEnd = random.Next(2) == 0,
            Interruption = random.Next(3) == 0 ? "" : "r" + random.Next(3),
            LastParsedInputAgeMs = Freshness(),
            MaximumFd2SilenceMs = Freshness(),
            MaximumParsedSilenceMs = Freshness()
        };
    }

    // 适配器身份里的字符串解析：WinRT 接口 ID、VID/PID、INF 资源名、地址排版。
    private static void BleAdapterParsing(Random random, CheckFn check, ref int cases, ref int bad)
    {
        string[] pool =
        [
            "", "  ", "unknown", @"\\?\USB#VID_8087&PID_0026#7&1f2e#0#{92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2}",
            @"USB#VID_054C&PID_0CE9#6&2a3b#0", @"usb#vid_8087&pid_0026#x", "VID_8087", "PID_0026",
            "VID_8087&PID_0026", "@bth.inf,%realtek%;Realtek Bluetooth Adapter", "@x;", "abc;def;ghi",
            "\t\\\\?\\USB#VID#PID#", "VID_8087&PID_002", "@", ";"
        ];
        foreach (string id in pool)
        {
            check("blediag", "instance id " + id,
                BleAdapterProbe.InstanceIdFromInterfaceId(id) == LegacyInstanceId(id), ref cases, ref bad);
            check("blediag", "vid pid " + id,
                BleAdapterProbe.SplitVidPid(id) == LegacySplitVidPid(id), ref cases, ref bad);
            check("blediag", "inf name " + id,
                BleAdapterProbe.UnwrapInfName(id) == LegacyUnwrapInfName(id), ref cases, ref bad);
        }

        for (int i = 0; i < 800; i++)
        {
            var text = new StringBuilder();
            for (int length = random.Next(0, 40); length > 0; length--)
            {
                text.Append("\\?#&;@_VIDPID0123456789abcdefABCDEF"[random.Next(35)]);
            }

            string value = text.ToString();
            check("blediag", "adapter parse " + i,
                BleAdapterProbe.InstanceIdFromInterfaceId(value) == LegacyInstanceId(value) &&
                BleAdapterProbe.SplitVidPid(value) == LegacySplitVidPid(value) &&
                BleAdapterProbe.UnwrapInfName(value) == LegacyUnwrapInfName(value),
                ref cases, ref bad);
        }

        foreach (ulong address in new ulong[] { 0, 1, 0xAABBCCDDEEFF, 0x8087, 0xFFFFFFFFFFFF })
        {
            check("blediag", "address " + address,
                BluetoothRadioInventory.FormatAddress(address) == LegacyFormatAddress(address), ref cases, ref bad);
        }

        for (int i = 0; i < 200; i++)
        {
            ulong address = ((ulong)(uint)random.Next() << 16) | (uint)random.Next(0, 0x10000);
            check("blediag", "address random " + i,
                BluetoothRadioInventory.FormatAddress(address) == LegacyFormatAddress(address), ref cases, ref bad);
        }
    }

    private static string LegacyFormatAddress(ulong address)
    {
        string hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static string LegacyInstanceId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        string normalized = id.Trim();
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normalized = normalized[4..];
        }

        int classGuid = normalized.IndexOf("#{", StringComparison.Ordinal);
        if (classGuid >= 0) normalized = normalized[..classGuid];
        return normalized.Replace('#', '\\');
    }

    private static (string Vid, string Pid) LegacySplitVidPid(string pnpId)
    {
        string upper = pnpId.ToUpperInvariant();
        int vidAt = upper.IndexOf("VID_", StringComparison.Ordinal);
        int pidAt = upper.IndexOf("PID_", StringComparison.Ordinal);
        string vid = vidAt >= 0 && upper.Length >= vidAt + 8 ? upper.Substring(vidAt + 4, 4) : "";
        string pid = pidAt >= 0 && upper.Length >= pidAt + 8 ? upper.Substring(pidAt + 4, 4) : "";
        return (vid, pid);
    }

    private static string LegacyUnwrapInfName(string name) =>
        name.StartsWith('@') && name.Contains(';') ? name[(name.LastIndexOf(';') + 1)..] : name;

    private static string LegacyFailure(Win10BleDiagnosticSamples samples)
    {
        const double maximumSilenceMs = 500;
        return !samples.MeasurementCompleted ? "measurement_not_completed" :
            samples.DurationSeconds < 10 ? "measurement_window_too_short" :
            samples.Interruption.Length > 0 ? "connection_interrupted:" + samples.Interruption :
            !samples.ConnectedAtEnd ? "disconnected_at_end" :
            samples.LastParsedInputAgeMs == null || samples.MaximumFd2SilenceMs == null || samples.MaximumParsedSilenceMs == null
                ? "freshness_not_verified" :
            samples.RawCount == 0 || samples.Fd2Count == 0 || samples.ParsedCount == 0 ? "no_live_input" :
            !double.IsFinite(samples.LastParsedInputAgeMs.Value) || samples.LastParsedInputAgeMs.Value < 0 ||
            !double.IsFinite(samples.MaximumFd2SilenceMs.Value) || samples.MaximumFd2SilenceMs.Value < 0 ||
            !double.IsFinite(samples.MaximumParsedSilenceMs.Value) || samples.MaximumParsedSilenceMs.Value < 0
                ? "invalid_freshness" :
            samples.LastParsedInputAgeMs > maximumSilenceMs ||
            samples.MaximumFd2SilenceMs > maximumSilenceMs || samples.MaximumParsedSilenceMs > maximumSilenceMs
                ? "input_stalled" : "";
    }

    private static string LegacyClassifyRates(double fd2Hz, double parsedHz)
    {
        if (!double.IsFinite(fd2Hz) || !double.IsFinite(parsedHz) || fd2Hz < 0 || parsedHz < 0)
            return "invalid_rate_measurement";
        if (fd2Hz >= 30 && parsedHz < 30)
            return "input_parse_failure_fd2_receiving_but_parsed_below_30hz";
        if (fd2Hz >= 30 && parsedHz / fd2Hz < 0.95)
            return "input_parse_loss_not_accepted";
        double effective = Math.Min(fd2Hz, parsedHz);
        return effective switch
        {
            >= 100 => "high_performance_success_ge_100hz",
            >= 60 => "success_ge_60hz",
            >= 50 => "clear_improvement_below_target_50_60hz",
            >= 30 => "partial_improvement_30_50hz",
            _ => "no_clear_improvement_below_30hz"
        };
    }

    private static bool LegacyIs125Accepted(double fd2Hz, double parsedHz) =>
        double.IsFinite(fd2Hz) && double.IsFinite(parsedHz) &&
        fd2Hz >= SoftwareOnlyMode.MinimumAcceptedHz && parsedHz >= SoftwareOnlyMode.MinimumAcceptedHz &&
        parsedHz / fd2Hz >= 0.95;

    private static BleGapStatistics LegacyGapStatistics(long[] ticks)
    {
        double[] gaps = ticks.Length < 2
            ? []
            : ticks.Zip(ticks.Skip(1), (left, right) => (right - left) * 1000.0 / Stopwatch.Frequency)
                .OrderBy(value => value)
                .ToArray();
        var distribution = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["near_7_5ms"] = 0,
            ["near_15ms"] = 0,
            ["near_22_5ms"] = 0,
            ["near_30ms"] = 0,
            ["near_37_5ms"] = 0,
            ["near_45ms"] = 0,
            ["near_50ms"] = 0,
            ["near_60ms"] = 0,
            ["other"] = 0
        };
        foreach (double gap in gaps)
        {
            string bucket = gap switch
            {
                >= 5.0 and < 11.25 => "near_7_5ms",
                >= 11.25 and < 18.75 => "near_15ms",
                >= 18.75 and < 26.25 => "near_22_5ms",
                >= 26.25 and < 33.75 => "near_30ms",
                >= 33.75 and < 41.25 => "near_37_5ms",
                >= 41.25 and < 47.50 => "near_45ms",
                >= 47.50 and < 55.00 => "near_50ms",
                >= 55.00 and < 65.00 => "near_60ms",
                _ => "other"
            };
            distribution[bucket]++;
        }

        return new BleGapStatistics(gaps.Length, LegacyGapPercentile(gaps, 0.50), LegacyGapPercentile(gaps, 0.90),
            LegacyGapPercentile(gaps, 0.95), LegacyGapPercentile(gaps, 0.99),
            gaps.Length == 0 ? 0 : gaps[0], gaps.Length == 0 ? 0 : gaps[^1], distribution);
    }

    private static double LegacyGapPercentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        double weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    private static string ComputeBleReportLinesDigest()
    {
        var random = new Random(20261123);
        var text = new StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            text.AppendLine(string.Join("\n", RandomReport(random).ToLogLines()));
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash)[..16] + ":" + text.Length;
    }

    private static string ComputeBluetoothSnapshotTextDigest()
    {
        var random = new Random(20261124);
        var text = new StringBuilder();
        for (int i = 0; i < 150; i++)
        {
            SystemBluetoothSnapshot snapshot = RichSnapshot(random);
            text.AppendLine(SystemBluetoothDiagnostics.Format(snapshot));
            text.AppendLine(string.Join("\n", SystemBluetoothDiagnostics.LogLines(snapshot)));
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash)[..16] + ":" + text.Length;
    }

    private static Win10BleDiagnosticReport RandomReport(Random random)
    {
        string[] pick = ["WIN11_PUBLIC_OR_NATIVE", "WIN10_STACK_7P5MS", "BASELINE", "", "unknown"];

        string Pick() => pick[random.Next(pick.Length)];
        string Text(string[] pool) => pool[random.Next(pool.Length)];
        double Maybe(double scale) => random.Next(12) switch
        {
            0 => double.NaN,
            1 => double.PositiveInfinity,
            2 => -random.NextDouble() * scale,
            _ => random.NextDouble() * scale
        };

        var distribution = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int count = random.Next(0, 5); count > 0; count--)
        {
            distribution[Text(["near_7_5ms", "near_15ms", "near_50ms", "other"])] = random.Next(1, 900);
        }

        var gaps = new BleGapStatistics(random.Next(0, 900), Maybe(20), Maybe(30), Maybe(40), Maybe(50),
            Maybe(2), Maybe(90), distribution);
        return new Win10BleDiagnosticReport(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(random.Next(0, 60)),
            Maybe(30),
            Pick(),
            Text(["Windows 11 Pro", "Windows 10 家庭中文版", ""]),
            Text(["24H2", "22H2", ""]),
            Text(["26100.2314", "19045.5011", ""]),
            Text([".NET 8.0.11", ""]),
            RandomAdapter(random),
            Text(["AA:BB:CC:DD:EE:FF", "unknown", ""]),
            Text(["0x0021", "none", ""]),
            Text(["00002902-0000-1000-8000-00805f9b34fb", "unknown", ""]),
            (ushort)random.Next(0, 300),
            Text(["01:8B", "unknown"]),
            Text(["ok", "winrt_error_hresult=0x80070005", "unknown"]),
            Text(["01:8B", ""]),
            random.Next(0, 20000),
            random.Next(0, 20000),
            random.Next(0, 20000),
            Maybe(300),
            Maybe(300),
            Maybe(300),
            gaps,
            Pick())
        {
            MeasurementCompleted = random.Next(3) != 0,
            ConnectionContinuous = random.Next(2) == 0,
            ConnectedAtEnd = random.Next(2) == 0,
            MeasurementFailure = Text(["", "measurement_not_completed", "connection_interrupted:gatt_0x05"]),
            LastParsedInputAgeMs = random.Next(4) == 0 ? null : Maybe(700),
            MaximumFd2SilenceMs = random.Next(4) == 0 ? null : Maybe(700),
            MaximumParsedSilenceMs = random.Next(4) == 0 ? null : Maybe(700),
            GapSamplesTruncated = random.Next(4) == 0,
            CompatibilityRoute = Pick(),
            CompatibilityGuidance = Text(["", "需要 24H2 以上", "未审查系统栈"]),
            SystemStackSha256 = Text(["unknown", "1F2E3D4C5B6A"]),
            Win10StackValuesConfigured = random.Next(2) == 0,
            PublicRequestApiAvailable = random.Next(2) == 0,
            PublicReadApiAvailable = random.Next(2) == 0,
            BatteryLevelAtConnection = (byte)random.Next(0, 12),
            BatteryLastReadLevel = (byte)random.Next(0, 12),
            RuntimeBatteryRefreshEnabled = random.Next(2) == 0,
            RuntimeBatteryRefreshStatus = Text(["unknown", "ok", "suspended"]),
            RuntimeBatterySuspensionReason = Text(["", "haptic_busy", "low_rate"]),
            BatteryLevelAgeSeconds = Maybe(900),
            BatteryVoltageMillivolts = (ushort)random.Next(0, 9000),
            BatteryChargeStateKnown = random.Next(2) == 0,
            BatteryCharging = random.Next(2) == 0,
            BatteryExternalPower = random.Next(2) == 0,
            BatteryTransport = Text(["unknown", "ble", "usb"]),
            ActualConnectionIntervalUnits = (ushort)random.Next(0, 40),
            ActualConnectionLatency = (ushort)random.Next(0, 6),
            ActualLinkTimeoutUnits = (ushort)random.Next(0, 800),
            ConnectionPreferenceStatus = Text(["unknown", "preferred_interval_rejected", "applied"]),
            Custom7p5ExperimentEnabled = random.Next(2) == 0,
            ParameterRoute = Text(["not_evaluated", "win11_public_api", "win10_stack"]),
            BeforeParameters = random.Next(3) == 0 ? null : RandomTrial(random),
            AfterCustomParameters = random.Next(3) == 0 ? null : RandomTrial(random)
        };
    }

    private static SystemBluetoothSnapshot RichSnapshot(Random random)
    {
        string[] pool = ["", "unknown", "Intel(R) Wireless Bluetooth(R)", "  ", "quote\"inside", "换行\n第二行"];
        var adapters = new List<RadioDiagnosticInfo>();
        for (int i = random.Next(0, 5); i > 0; i--)
        {
            var driver = new BluetoothAdapterDiagnosticInfo(
                pool[random.Next(pool.Length)],
                pool[random.Next(pool.Length)],
                random.Next(3) == 0 ? "8087" : "",
                random.Next(3) == 0 ? "0026" : "",
                pool[random.Next(pool.Length)],
                pool[random.Next(pool.Length)],
                pool[random.Next(pool.Length)],
                pool[random.Next(pool.Length)]);
            adapters.Add(new RadioDiagnosticInfo(driver, random.Next(2) == 0, "AA:BB:CC:DD:EE:FF",
                random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0));
        }

        var system = new SystemDiagnosticInfo(pool[random.Next(pool.Length)], pool[random.Next(pool.Length)],
            pool[random.Next(pool.Length)], pool[random.Next(pool.Length)], pool[random.Next(pool.Length)],
            pool[random.Next(pool.Length)], pool[random.Next(pool.Length)], pool[random.Next(pool.Length)]);
        Win10ConfigurationAudit? configuration = random.Next(3) == 0 ? null : new Win10ConfigurationAudit(
            random.Next(2) == 0, random.Next(2) == 0,
            random.Next(3) == 0 ? "unavailable" : "EnableTestMode=1 Interval=7.5", random.Next(2) == 0,
            random.Next(2) == 0, random.Next(3) == 0 ? "True" : "False");
        StackParameterAssessment? assessment = random.Next(3) == 0 ? null : new StackParameterAssessment(
            random.Next(0, 30000), @"C:\Windows\System32\drivers\bthport.sys", pool[random.Next(pool.Length)],
            random.Next(3) == 0 ? "unknown" : "9A8B7C6D", (StackEvidenceLevel)random.Next(5),
            Enumerable.Range(0, random.Next(0, 5)).Select(i => "param" + i).ToArray(), random.Next(2) == 0,
            pool[random.Next(pool.Length)]);
        return new SystemBluetoothSnapshot(DateTimeOffset.UnixEpoch, system, adapters,
            pool[random.Next(pool.Length)], string.Join("；", Enumerable.Range(0, random.Next(0, 3))
                .Select(_ => pool[random.Next(pool.Length)])), configuration, assessment);
    }

    /// <summary>旧采样窗实现的逐字复刻，只作差分 oracle 用。</summary>
    private sealed class LegacyBleCaptureWindow
    {
        private const int MaximumSamples = 8192;
        private readonly List<long> rawTicks = new(MaximumSamples);
        private readonly List<long> fd2Ticks = new(MaximumSamples);
        private readonly List<long> parsedTicks = new(MaximumSamples);
        private int rawCount;
        private int fd2Count;
        private int parsedCount;
        private long lastFd2Ticks;
        private long lastParsedTicks;
        private long maximumFd2GapTicks;
        private long maximumParsedGapTicks;
        private string interruption = "";

        public LegacyBleCaptureWindow(long startedTicks)
        {
            StartedTicks = startedTicks;
        }

        public long StartedTicks { get; }

        public void RecordRaw(long ticks, bool isFd2)
        {
            if (ticks < StartedTicks) return;
            rawCount++;
            Add(rawTicks, ticks);
            if (isFd2)
            {
                fd2Count++;
                NoteArrival(ticks, ref lastFd2Ticks, ref maximumFd2GapTicks);
                Add(fd2Ticks, ticks);
            }
        }

        public void RecordParsed(long ticks, bool isFd2)
        {
            if (isFd2 && ticks >= StartedTicks)
            {
                parsedCount++;
                NoteArrival(ticks, ref lastParsedTicks, ref maximumParsedGapTicks);
                Add(parsedTicks, ticks);
            }
        }

        public void MarkInterrupted(string reason)
        {
            if (interruption.Length == 0) interruption = reason.Length == 0 ? "connection_interrupted" : reason;
        }

        private void NoteArrival(long ticks, ref long last, ref long maximumGap)
        {
            maximumGap = Math.Max(maximumGap, ticks - (last == 0 ? StartedTicks : last));
            last = Math.Max(last, ticks);
        }

        public Win10BleDiagnosticSamples Snapshot(long completedTicks, bool measurementCompleted = false,
            bool connectedAtEnd = false)
        {
            return new Win10BleDiagnosticSamples(
                StartedTicks,
                completedTicks,
                rawTicks.ToArray(),
                fd2Ticks.ToArray(),
                parsedTicks.ToArray())
            {
                RawCount = rawCount, Fd2Count = fd2Count, ParsedCount = parsedCount,
                MeasurementCompleted = measurementCompleted, ConnectedAtEnd = connectedAtEnd,
                Interruption = interruption,
                LastParsedInputAgeMs = ToMilliseconds(completedTicks - (lastParsedTicks == 0 ? StartedTicks : lastParsedTicks)),
                MaximumFd2SilenceMs = ToMilliseconds(Math.Max(maximumFd2GapTicks,
                    completedTicks - (lastFd2Ticks == 0 ? StartedTicks : lastFd2Ticks))),
                MaximumParsedSilenceMs = ToMilliseconds(Math.Max(maximumParsedGapTicks,
                    completedTicks - (lastParsedTicks == 0 ? StartedTicks : lastParsedTicks)))
            };
        }

        private static double ToMilliseconds(long ticks) => Math.Max(0, ticks * 1000.0 / Stopwatch.Frequency);

        private static void Add(List<long> samples, long ticks)
        {
            if (samples.Count < MaximumSamples) samples.Add(ticks);
        }
    }
}
