using System;
using System.Diagnostics;
using System.Text;

namespace Pro2Plus;

/// <summary>
/// 震动保持节奏的离线自检：不接手柄，用合成时间轴验证自驱动补包、重复状态
/// 不打断节奏、静音立即停止、波形流不被拖长。PRO2_RUMBLE_HOLD_TEST=1 触发。
/// </summary>
public static class RumbleHoldContinuitySmoke
{
    public static string Run()
    {
        var log = new StringBuilder();
        log.AppendLine("rumble hold continuity smoke");
        long ticksPerMs = Stopwatch.Frequency / 1000;
        long now = Stopwatch.GetTimestamp();
        int pass = 0, fail = 0;

        byte[] active = ActivePacket();
        byte[] silent = SilentPacket();

        // 1) 单个包不能立刻启动保持（否则波形流会被拖长）。
        var hold = new OrdinaryRumbleHoldState();
        hold.Observe(active, "xinput", false, true);
        bool firstCreates = hold.TryCreate(now, false, 1, out _);
        Check(log, ref pass, ref fail, "single packet does not start hold", !firstCreates && !hold.Steady);

        // 2) 状态被重复确认后，即使完全没有写入回调，也必须自己按周期补包。
        //    这是上一版失效的根因：补包被挂在 GATT 成功回调链上。
        now += 60 * ticksPerMs;
        hold.Observe(active, "xinput", false, true);
        Check(log, ref pass, ref fail, "repeated state becomes steady", hold.Steady);

        int generated = 0;
        long lastRefresh = now;
        for (int ms = 1; ms <= 240; ms++)
        {
            now += ticksPerMs;
            // 游戏每 60 ms 重复一次完全相同的反馈：不能打断补包节奏。
            if (ms % 60 == 0)
            {
                hold.Observe(active, "xinput", false, true);
            }
            if (hold.TryCreate(now, false, (byte)(ms & 15), out _))
            {
                generated++;
                lastRefresh = now;
                // 故意不调用 Written：验证补包不依赖写入成功回调。
            }
        }
        Check(log, ref pass, ref fail, "self-driven refresh throughout 240ms (>=8)", generated >= 8);
        Check(log, ref pass, ref fail, "last refresh within 30ms", (now - lastRefresh) / ticksPerMs <= 30);
        log.AppendLine($"  refresh_packets={generated} hold_active={hold.Active} steady={hold.Steady}");

        // 3) 静音包必须立刻终止保持，不能继续补包。
        hold.Observe(silent, "xinput", false, true);
        now += 30 * ticksPerMs;
        bool createdAfterStop = hold.TryCreate(now, false, 1, out _);
        Check(log, ref pass, ref fail, "silent feedback stops refresh", !createdAfterStop && !hold.Active);

        // 4) 波形流每 20 ms 内容都变，必须一直重开、永不生成补包。
        hold.Reset();
        byte[] wave = ActivePacket();
        int waveRefresh = 0;
        for (int step = 0; step < 12; step++)
        {
            wave[2] ^= (byte)(0x08 + step);
            hold.Observe(wave, "dualsense-hd", true, true);
            now += 20 * ticksPerMs;
            if (hold.TryCreate(now, false, (byte)step, out _)) waveRefresh++;
        }
        Check(log, ref pass, ref fail, "changing waveform never held", waveRefresh == 0 && !hold.Steady);

        // 5) 节奏探针：24 ms 写入间隔应被如实统计，且每秒产出一行 [RAW] 日志。
        var probe = new RumbleWritePacingProbe();
        probe.Reset();
        long t = Stopwatch.GetTimestamp();
        string? line = null;
        for (int i = 0; i < 60; i++)
        {
            long started = t + (long)(i * 24 * ticksPerMs);
            string? sample = probe.ObserveWrite(started, started + ticksPerMs,
                i % 3 == 0 ? "source" : "refresh", 0, i, 0, true, true);
            if (sample != null) line = sample;
        }
        bool probeOk = line != null && line.StartsWith("[BLE_RUMBLE_PACE]", StringComparison.Ordinal) &&
            line.Contains("interval_ms_p50=24.0", StringComparison.Ordinal);
        Check(log, ref pass, ref fail, "pacing probe reports BLE-prefixed 24ms cadence", probeOk);
        if (line != null) log.AppendLine("  " + line);

        log.AppendLine($"summary={(fail == 0 ? "PASS" : "FAIL")} pass={pass} fail={fail}");
        return log.ToString();
    }

    private static void Check(StringBuilder log, ref int pass, ref int fail, string name, bool ok)
    {
        log.AppendLine($"  result={(ok ? "PASS" : "FAIL")} {name}");
        if (ok) pass++; else fail++;
    }

    private static byte[] ActivePacket()
    {
        byte[] packet = new byte[RumbleFrameCodec.PacketSize];
        packet[1] = 0x50;
        packet[17] = 0x50;
        for (int offset = 1; offset <= 17; offset += 16)
        {
            for (int frame = 0; frame < 3; frame++)
            {
                int at = offset + 1 + frame * 5;
                packet[at] = 0x20;
                packet[at + 1] = 0x44; // 非零振幅
                packet[at + 2] = 0x01;
                packet[at + 3] = 0xC0;
                packet[at + 4] = 0x01;
            }
        }
        return packet;
    }

    private static byte[] SilentPacket()
    {
        byte[] packet = new byte[RumbleFrameCodec.PacketSize];
        packet[1] = 0x50;
        packet[17] = 0x50;
        return packet;
    }
}
