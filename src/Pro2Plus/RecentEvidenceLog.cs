using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Pro2Plus;

/// <summary>
/// 由单个日志工作线程持有的近期证据归档：每类证据有独立预算，
/// 重连或遥测风暴不会挤掉最后一次电量读数或断连记录。
/// 每条保留原始时间戳与原始顺序，且只保存对话摘要而非逐帧报文。
/// </summary>
public sealed class RecentEvidenceLog
{
    public const int MaximumBytes = 128 * 1024;
    private const int MaximumLineBytes = 2048;
    private const string Truncated = " [RECENT_LINE_TRUNCATED]";
    private const string SnapshotNotice = "[RECENT_EVIDENCE_POLICY] archived_subset=true original_timestamps=true original_order=true battery_values=last_recorded_not_current separate_retention_budgets=true";

    private readonly Bucket origin = new(4 * 1024);
    private readonly Bucket identity = new(12 * 1024);
    private readonly Bucket battery = new(16 * 1024);
    private readonly Bucket disconnect = new(32 * 1024);
    private readonly Bucket diagnostics = new(16 * 1024);
    private readonly Bucket measurements = new(32 * 1024);
    private readonly Bucket rumble = new(15 * 1024);
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
            // 源文件行独立固定：即使经过多次启动与适配器扫描也不会被顶掉；
            // 更新的显式来源覆盖旧行，但不改变它的时间戳语义。
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

        // 只收正向断连事件：physical_disconnect=false 与 *_disconnect_count=0 不算断连。
        if (Has(line, "[PRO2_BLE_DISCONNECT_SIGNAL]") ||
            Has(line, "[INPUT_DISCONNECT_NEUTRAL]") ||
            Has(line, "[PRO2_OFFLINE_NOTICE]") && !Has(line, "duplicate_suppressed=1") ||
            Has(line, "windows_connection_status=Disconnected") ||
            Has(line, "gatt_session_status=Closed") ||
            Has(line, "[SHUTDOWN]") || Has(line, "[BLUETOOTH_RESTART")) return disconnect;

        if (Has(line, "[BATTERY_RUNTIME_UPDATED]") ||
            Has(line, "[PRO2_BATTERY]") &&
                (Has(line, "source=") || Has(line, "level=") || Has(line, "raw_power="))) return battery;

        if (Has(line, "[START]") || Has(line, "[STARTUP]") || Has(line, "[FEATURE_POLICY]") || Has(line, "[DATA_PATH_POLICY]") ||
            Has(line, "[BLE_COMPATIBILITY]") || Has(line, "[BLE_ADAPTER]") ||
            Has(line, "[PRO2_BLE_ADDR]") || Has(line, "[PRO2_BLE_LINK] windows_connection_status=Connected"))
            return identity;

        // 只收有界摘要，不收逐帧报文；持续震动播放也不能挤掉断连或电量证据。
        if (Has(line, "[PRO2_RUMBLE_TRANSPORT]") || Has(line, "[PRO2_RUMBLE_WINDOW]") ||
            Has(line, "[PRO2_RUMBLE_NATIVE]") || Has(line, "[PRO2_RUMBLE_PACER]") || Has(line, "[PRO2_RUMBLE_QUEUE]") ||
            Has(line, "[PRO2_RUMBLE_CONVERTED]") || Has(line, "[RUMBLE_CONVERSION]") ||
            Has(line, "[HAPTIC_CONVERSION]") || Has(line, "[HAPTIC_STOP]") || Has(line, "[PS5_FEEDBACK_STATE]") ||
            Has(line, "[RUMBLE_TRANSPORT]") || Has(line, "[PRO2_RUMBLE_HOLD]") ||
            Has(line, "[PRO2_RUMBLE_SHUTDOWN]")) return rumble;

        if (Has(line, "[PRO2_BLE_RATE_WINDOW]") || Has(line, "[BLE_SESSION_EVIDENCE]") ||
            Has(line, "[VIIPER_INPUT_PIPELINE]") || Has(line, "[VIIPER_INPUT_POLICY]") ||
            Has(line, "[VIIPER_INPUT_SAFETY]") || Has(line, "[PS5_MOTION_VALIDITY]") ||
            Has(line, "[BATTERY_RUNTIME_RATE]") || Has(line, "[BLE_LOW_RATE_DIAG]")) return measurements;

        if (Has(line, "[BATTERY_RUNTIME") || Has(line, "[PRO2_BATTERY") ||
            Has(line, "[AUDIO_GUARD]") || Has(line, "[BLE_AUTO_PRESET_") ||
            Has(line, "[WIN11_PARAMETER_TRIAL]") || HasRealFailure(line)) return diagnostics;
        return null;
    }

    private static bool HasRealFailure(string line)
    {
        bool failedField = false;
        // 先剔除已知的「非错误」字段再找 error 字样：正常的 watcher 停止
        // 与 stop_error=Success 都是噪声，不应进诊断证据。
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
