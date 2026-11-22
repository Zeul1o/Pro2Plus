using System;
using System.Globalization;
using System.Text;

namespace Pro2Plus;

/// <summary>
/// 摇杆零位/死区的离线自测（不需要手柄）：
/// 用合成的 0x05 报告喂 <see cref="Pro2WiredProtocol"/>，直接看「最终输出」。
///
/// 这套测试存在的意义：有线漂移是「中心算错 + 0 死区放大」，只看代码看不出效果，
/// 所以这里把真实场景复刻成四组断言（错误的旧档案、死区、推杆不许被学成零位、长跑均值）。
/// 跑法：PRO2_STICK_TEST=1 Pro2Plus.exe
/// </summary>
public static class StickPipelineSmoke
{
    private const int TrueLx = 2045, TrueLy = 2212, TrueRx = 2217, TrueRy = 2090;
    private const int Frames = 4000;

    public static string Run()
    {
        var report = new StringBuilder();
        report.AppendLine("PRO2 stick pipeline smoke");

        // 档案键跟随真实运行环境（有线会复用上次蓝牙地址），否则测不到坏档案这条路径。
        var settings = new AppSettings();
        Pro2GyroBiasStore.Configure(settings, allowPersist: false);
        string key = Pro2GyroBiasStore.StickCalibrationKey(Pro2GyroBiasStore.CurrentKey);
        settings.Pro2StickCalibrations[key] = WrongProfile();
        Pro2GyroBiasStore.SetStickDeadzone(0);

        TestIgnoresWrongSavedCenter(report);
        TestZeroDeadzoneRestIsExact(report);
        TestDeadzoneBlocksSmallOffsets(report);
        TestHeldStickIsNotLearnedAsCenter(report);
        TestManualCalibrationApplies(report);
        TestDefaultDeadzone(report);
        TestDeadzonePerLink(report);
        TestRumbleGainShared(report);
        TestBleZeroDeadzone(report);
        TestBleHonorsDeadzone(report);
        TestRestDriftIsFollowed(report);
        TestZeroDeadzoneHasNoBias(report);
        TestSmallPushRespondsAndNoKickBack(report);
        TestWiredImuSamplesAreRaw(report);
        TestKbmMouseIdleIsZero(report);
        TestStuckSmallOffsetRecovers(report);
        TestSlightPushNoKickBack(report);
        TestJitteryOffsetRecoversFast(report);
        TestKbmInheritsStickDeadzone(report);
        TestReturnToCenterSpeed(report);
        TestStuckCenterWithJitterRecovers(report);

        report.AppendLine("summary=" + (report.ToString().Contains("FAIL", StringComparison.Ordinal) ? "FAIL" : "PASS"));
        return report.ToString();
    }

    /// <summary>
    /// 复刻线上那份坏档案：中心 (2040,2048,2056,2031) 来自另一条链路的坐标系，
    /// 而这只手柄有线的真实零位是 (2045,2212,2217,2090)。
    /// </summary>
    private static StickCalibrationProfile WrongProfile() => new()
    {
        CenterCalibrated = true,
        CenterLx = 2040,
        CenterLy = 2048,
        CenterRx = 2056,
        CenterRy = 2031
    };

    private static void TestIgnoresWrongSavedCenter(StringBuilder report)
    {
        Pro2WiredProtocol pad = new();
        // 前 1200 帧含「定零位」过程（此间中心还是默认 2048），只量定完零位之后的效果。
        Feed(pad, 1200, span: 4, offset: 0, out _);
        GamepadState last = Feed(pad, Frames, span: 4, offset: 0, out Stats stats);
        bool ok =
            Near(stats.MeanLx, 2048, 0.4) && Near(stats.MeanLy, 2048, 0.4) &&
            Near(stats.MeanRx, 2048, 0.4) && Near(stats.MeanRy, 2048, 0.4);
        report.AppendLine(
            "case=wrong_saved_center result=" + (ok ? "PASS" : "FAIL") +
            " mean=(" + F(stats.MeanLx) + "," + F(stats.MeanLy) + "," +
            F(stats.MeanRx) + "," + F(stats.MeanRy) + ")" +
            " last=(" + last.Lx + "," + last.Ly + "," + last.Rx + "," + last.Ry + ")" +
            " " + pad.StickDiagnostic);
    }

    private static void TestZeroDeadzoneRestIsExact(StringBuilder report)
    {
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 5, offset: 0, out _);
        // 长跑均值必须就是 2048：均值偏一点，键鼠模式就是每秒固定像素的漂移。
        Feed(pad, Frames, span: 5, offset: 0, out Stats stats);
        bool ok =
            Near(stats.MeanLx, 2048, 0.08) && Near(stats.MeanLy, 2048, 0.08) &&
            Near(stats.MeanRx, 2048, 0.08) && Near(stats.MeanRy, 2048, 0.08);
        report.AppendLine(
            "case=zero_deadzone_rest result=" + (ok ? "PASS" : "FAIL") +
            " mean=(" + F(stats.MeanLx) + "," + F(stats.MeanLy) + "," +
            F(stats.MeanRx) + "," + F(stats.MeanRy) + ")" +
            " span=(" + stats.SpanLx + "," + stats.SpanLy + "," + stats.SpanRx + "," + stats.SpanRy + ")");
    }

    private static void TestDeadzoneBlocksSmallOffsets(StringBuilder report)
    {
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 4, offset: 0, out _);
        Pro2GyroBiasStore.SetStickDeadzone(64);

        // 死区内：不论噪声怎么抖，输出必须一字不差是 2048。
        GamepadState inside = Feed(pad, 600, span: 8, offset: 40, out Stats insideStats);
        // 死区外：必须立刻有响应，不能被死区吃掉。
        GamepadState outside = Feed(pad, 200, span: 2, offset: 260, out _);
        Pro2GyroBiasStore.SetStickDeadzone(0);

        bool ok =
            insideStats.SpanLx == 0 && insideStats.SpanLy == 0 &&
            insideStats.SpanRx == 0 && insideStats.SpanRy == 0 &&
            inside.Lx == 2048 && inside.Ly == 2048 && inside.Rx == 2048 && inside.Ry == 2048 &&
            outside.Lx > 2200;
        report.AppendLine(
            "case=deadzone_64 result=" + (ok ? "PASS" : "FAIL") +
            " inside_span=(" + insideStats.SpanLx + "," + insideStats.SpanLy + "," +
            insideStats.SpanRx + "," + insideStats.SpanRy + ")" +
            " outside=(" + outside.Lx + "," + outside.Ly + "," + outside.Rx + "," + outside.Ry + ")");
    }

    private static void TestHeldStickIsNotLearnedAsCenter(StringBuilder report)
    {
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 4, offset: 0, out _);
        // 一直推着 400 个单位：零位绝不能被这段"看似静止"的读数带走。
        GamepadState held = Feed(pad, 1500, span: 3, offset: 400, out Stats heldStats);
        GamepadState back = Feed(pad, 1500, span: 4, offset: 0, out Stats backStats);
        bool ok =
            held.Lx > 2300 && backStats.MeanLx < 2049.5 && backStats.MeanLx > 2046.5;
        report.AppendLine(
            "case=held_stick result=" + (ok ? "PASS" : "FAIL") +
            " held_lx=" + held.Lx +
            " released_mean_lx=" + F(backStats.MeanLx) +
            " held_mean_lx=" + F(heldStats.MeanLx));
    }

    private static void TestManualCalibrationApplies(StringBuilder report)
    {
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 4, offset: 0, out _);

        // 另一条链路的中心（差 164）：不能采纳，否则就是 8% 漂移。
        pad.ApplyCalibration(new StickCalibrationProfile
        {
            CenterCalibrated = true,
            CenterLy = 2048,
            CenterLx = 2048,
            CenterRx = 2048,
            CenterRy = 2048
        });
        bool rejectNoted = pad.StickDiagnostic.Contains("manual_center_rejected", StringComparison.Ordinal);
        GamepadState afterReject = Feed(pad, 300, span: 4, offset: 0, out Stats rejectStats);
        bool rejected = Near(rejectStats.MeanLy, 2048, 0.5) && rejectNoted;

        // 同一支手柄在本链路采到的零位（差 3）：立刻生效，并且输出仍然是 2048
        // （归零跟踪会继续把零位钉在实测值上，所以「校准」只会让输出更准，不会把输出推偏）。
        pad.ApplyCalibration(new StickCalibrationProfile
        {
            CenterCalibrated = true,
            CenterLx = TrueLx + 3,
            CenterLy = TrueLy + 3,
            CenterRx = TrueRx + 3,
            CenterRy = TrueRy + 3
        });
        string acceptedNote = pad.StickDiagnostic;
        bool accepted =
            acceptedNote.Contains("center_src=manual", StringComparison.Ordinal) &&
            !acceptedNote.Contains("rejected", StringComparison.Ordinal);
        Feed(pad, 600, span: 4, offset: 0, out _);   // 1.2 秒：限速 3 单位/秒足够吃掉 3 个单位的偏差
        GamepadState afterAccept = Feed(pad, 300, span: 4, offset: 0, out Stats acceptStats);
        bool stable = Near(acceptStats.MeanLy, 2048, 1.5);

        bool ok = rejected && accepted && stable;
        report.AppendLine(
            "case=manual_calibration result=" + (ok ? "PASS" : "FAIL") +
            " after_reject_out=" + afterReject.Ly +
            " accepted=" + accepted +
            " after_accept_mean_ly=" + F(acceptStats.MeanLy) +
            " " + acceptedNote);
    }

    /// <summary>按链路的默认死区：有线 2%（41），蓝牙 0。两份独立。</summary>
    private static void TestDefaultDeadzone(StringBuilder report)
    {
        var settings = new AppSettings();
        int wired = settings.EffectiveWiredStickDeadzone;
        int ble = settings.EffectiveBleStickDeadzone;
        bool ok = wired == AppSettings.DefaultWiredStickDeadzone && wired == 41 &&
                  ble == AppSettings.DefaultBleStickDeadzone && ble == 0;
        report.AppendLine(
            "case=default_deadzone result=" + (ok ? "PASS" : "FAIL") +
            " wired=" + wired + " ble=" + ble);
    }

    /// <summary>
    /// 震动强度双模式统一：同一个增益对有线和蓝牙 report 的振幅缩放结果必须一致。
    /// 旧版蓝牙硬编码 1.2、有线 1.0，这里验证缩放共用同一个 Scale。
    /// </summary>
    private static void TestRumbleGainShared(StringBuilder report)
    {
        const double gain = 0.6;
        byte[] original = new byte[64];
        BuildRumble(original, 400);
        byte[] wired = (byte[])original.Clone();   // 有线：原地缩放
        byte[] ble = new byte[33];                  // 蓝牙：编码时缩放
        Pro2NativeRumbleEncoder.ScaleReportAmplitudes(wired, gain);
        Pro2NativeRumbleEncoder.TryEncode(original, ble, gain, out _, out _);

        // 两个 0x50 块、各 3 子帧的振幅都应缩到 400*0.6=240
        bool ok = true;
        for (int side = 1; side <= 17; side += 16)
        for (int frame = 0; frame < 3; frame++)
        {
            int off = side + 1 + frame * 5;
            ulong packed = 0;
            for (int i = 0; i < 5; i++) packed |= (ulong)wired[off + i] << (i * 8);
            int amp1 = (int)((packed >> 10) & 0x3ff);
            int amp2 = (int)((packed >> 30) & 0x3ff);
            if (amp1 != 240 || amp2 != 240) ok = false;
        }

        report.AppendLine(
            "case=rumble_gain_shared result=" + (ok ? "PASS" : "FAIL") +
            " wired_amp=" + ReadAmp(wired, 1) + " ble_amp=" + ReadAmp(ble, 1));
    }

    private static int ReadAmp(byte[] p, int side)
    {
        ulong packed = 0;
        for (int i = 0; i < 5; i++) packed |= (ulong)p[side + 1 + i] << (i * 8);
        return (int)((packed >> 10) & 0x3ff);
    }

    private static void BuildRumble(byte[] report, int amp)
    {
        Array.Clear(report);
        report[0] = 2;
        ulong packed = 0x87UL | ((ulong)amp << 10) | (0x87UL << 20) | ((ulong)amp << 30);
        for (int side = 1; side <= 17; side += 16)
        {
            report[side] = 0x50;
            for (int frame = 0; frame < 3; frame++)
            {
                int off = side + 1 + frame * 5;
                if (off + 5 > report.Length) break;
                for (int i = 0; i < 5; i++) report[off + i] = (byte)(packed >> (i * 8));
            }
        }
    }

    /// <summary>两条链路各自保存死区，互不干扰；切链路时各读各的。</summary>
    private static void TestDeadzonePerLink(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(41, wired: true);
        Pro2GyroBiasStore.SetStickDeadzone(0, wired: false);
        int wired = Pro2GyroBiasStore.WiredStickDeadzone;
        int ble = Pro2GyroBiasStore.BleStickDeadzone;

        // 切到蓝牙 key 时 ActiveStickDeadzone 必须跟着变
        Pro2GyroBiasStore.SetDeviceKey("BLE:112233445566", out _);
        int activeBle = Pro2GyroBiasStore.ActiveStickDeadzone;
        Pro2GyroBiasStore.SetDeviceKey(Pro2GyroBiasStore.WiredKey, out _);
        int activeWired = Pro2GyroBiasStore.ActiveStickDeadzone;

        bool ok = wired == 41 && ble == 0 && activeBle == 0 && activeWired == 41;
        report.AppendLine(
            "case=deadzone_per_link result=" + (ok ? "PASS" : "FAIL") +
            " wired=" + wired + " ble=" + ble +
            " active_ble=" + activeBle + " active_wired=" + activeWired);
    }

    /// <summary>
    /// 蓝牙「零死区」的实证：死区为 0 时，30 个单位（约 1.5%）的偏移必须原样出现在输出里。
    /// 如果蓝牙其实偷偷带死区，这一项会输出恒 2048 而 FAIL。
    /// </summary>
    private static void TestBleZeroDeadzone(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0);
        Pro2BleReportParser parser = new();
        // 蓝牙解析器每满 20 帧就会在「四轴仍在 2048±256 内」时重新定一次零位，
        // 所以量死区必须在 20 帧的边界上取，且只看之后的一小段（不然偏移会被它自己吃成零位）。
        FeedBle(parser, 200, 4, 0, out _);
        FeedBle(parser, 200, 4, 0, out Stats rest);
        FeedBle(parser, 10, 0, 30, out Stats offset);
        bool ok = Near(rest.MeanLy, 2048, 1.5) && offset.MeanLy > 2060;
        report.AppendLine(
            "case=ble_zero_deadzone result=" + (ok ? "PASS" : "FAIL") +
            " rest_mean_ly=" + F(rest.MeanLy) +
            " offset30_mean_ly=" + F(offset.MeanLy) +
            " " + parser.StickTelemetry);
    }

    /// <summary>同一个死区值对蓝牙同样生效：死区内输出恒 2048，死区外照常响应。</summary>
    private static void TestBleHonorsDeadzone(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(41);   // 2%
        Pro2BleReportParser parser = new();
        FeedBle(parser, 400, 4, 0, out _);
        GamepadState inside = FeedBle(parser, 300, 4, 30, out Stats insideStats);
        FeedBle(parser, 200, 2, 200, out Stats outsideStats);
        Pro2GyroBiasStore.SetStickDeadzone(0);

        bool ok =
            insideStats.SpanLy == 0 &&
            Near(insideStats.MeanLy, 2048, 0.001) &&
            outsideStats.MeanLy > 2100;
        report.AppendLine(
            "case=ble_deadzone_default result=" + (ok ? "PASS" : "FAIL") +
            " inside_span=" + insideStats.SpanLy +
            " inside_mean=" + F(insideStats.MeanLy) +
            " outside_mean_ly=" + F(outsideStats.MeanLy) +
            " last_out=" + inside.Ly);
    }

    /// <summary>
    /// 摇杆「静止值自己缓慢回中漂」的复刻：静止读数每 20 帧漂 1 个单位，2 秒漂 +100，
    /// 期间两端噪声 ±4。两条链路都必须把输出压在 2048 附近——
    /// 这正是"放着不动，鼠标/视角还在慢慢走"的那个场景。
    /// </summary>
    private static void TestRestDriftIsFollowed(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0);

        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 4, offset: 0, out _);
        FeedRamp(pad, 4000, span: 4, out Stats wired);

        Pro2BleReportParser parser = new();
        FeedBle(parser, 200, 4, 0, out _);
        FeedBle(parser, 200, 4, 0, out _);
        FeedBleRamp(parser, 4000, 4, out Stats ble);

        bool ok = Near(wired.MeanLy, 2048, 8) && Near(ble.MeanLy, 2048, 8);
        report.AppendLine(
            "case=rest_drift_followed result=" + (ok ? "PASS" : "FAIL") +
            " wired_mean_ly=" + F(wired.MeanLy) +
            " wired_span=" + wired.SpanLy +
            " ble_mean_ly=" + F(ble.MeanLy));
    }

    /// <summary>
    /// 零死区下最阴的一种漂：中心是带小数的均值（2212.5），静止读数减出来恒为 0.5 个单位，
    /// 小数进位就让输出在 2048/2049 之间跳，平均 2048.5 —— 键鼠模式就是每秒恒定向右走。
    /// 归零跟踪把中心钉成 2212.5 也救不了，必须靠「1 个单位取整门槛」把它压成 2048。
    /// </summary>
    private static void TestZeroDeadzoneHasNoBias(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0);
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 4, offset: 0, out _);

        byte[] raw = new byte[64];
        var state = GamepadState.Neutral();
        var stats = new Stats();
        ulong seq = 0;
        long turnStart = System.Diagnostics.Stopwatch.GetTimestamp();
        long turnStep = System.Diagnostics.Stopwatch.Frequency / 500;   // 2ms/帧
        for (int i = 0; i < 600; i++)
        {
            // 严格交替 +0 / +1：均值正好 2212.5，残差 0.5 个单位。
            int half = i % 2;
            Pack(raw, TrueLx + half, TrueLy + half, TrueRx + half, TrueRy + half);
            state = pad.Parse(raw, raw.Length, turnStart + (i + 1) * turnStep, ++seq);
            stats.Add(state);
        }

        bool ok =
            Near(stats.MeanLx, 2048, 0.02) && Near(stats.MeanLy, 2048, 0.02) &&
            stats.SpanLx == 0 && stats.SpanLy == 0;
        report.AppendLine(
            "case=zero_deadzone_no_bias result=" + (ok ? "PASS" : "FAIL") +
            " mean=(" + F(stats.MeanLx) + "," + F(stats.MeanLy) + ")" +
            " span=(" + stats.SpanLx + "," + stats.SpanLy + ")" +
            " last=(" + state.Lx + "," + state.Ly + ")" +
            " " + pad.StickDiagnostic);
    }

    /// <summary>
    /// 13:45 日志：解析层 dz=21，但键鼠层 dz=0，残差原样变成鼠标速度。
    /// 现在键鼠层必须继承设置页死区，1% 以内的偏差不出鼠标位移。
    /// </summary>
    private static void TestKbmInheritsStickDeadzone(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(21);   // 1%
        KeyMouseMapper mapper = new();            // gyro.json 默认 dz=0
        long now = 1000;
        int total = 0;
        for (int i = 0; i < 1000; i++)
        {
            GamepadState state = GamepadState.Neutral();
            state.Lx = state.Ly = state.Rx = state.Ry = GamepadState.AxisCenter;
            // 0.8% 的残差：解析层死区外、但在设置页 1% 死区内
            mapper.Update(state, 0, 0, 0.008, 0, now);
            (int dx, int dy) = mapper.TakeMouseDelta(now, out _);
            total += dx + dy;
            now += 2;
        }
        Pro2GyroBiasStore.SetStickDeadzone(0, wired: true);

        bool ok = total == 0;
        report.AppendLine(
            "case=kbm_inherits_dz result=" + (ok ? "PASS" : "FAIL") +
            " total=" + total + " " + mapper.MouseTelemetry);
    }

    /// <summary>
    /// 复刻 13:39 日志：raw 在基准±5 之间持续抖动（跨度 10），中心落后 12 个单位。
    /// 旧判定要求跨度≤8，永远不满足 → 中心落后 18 秒 → 鼠标一直有 43 计数/秒输出。
    /// 现在跨度≤24 且 0.3 秒重定零，必须在 1 秒内追上。
    /// </summary>
    private static void TestJitteryOffsetRecoversFast(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0);
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 2, offset: 0, out _);

        // 4000 帧（8 秒）持续在 +12 附近抖动 ±5：跨度约 10，超过旧的 8。
        byte[] raw = new byte[64];
        var random = new Random(20261120);
        var state = GamepadState.Neutral();
        ulong seq = 0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long step = System.Diagnostics.Stopwatch.Frequency / 500;
        for (int i = 0; i < 4000; i++)
        {
            int jitter = random.Next(-5, 6);
            Pack(raw, TrueLx + 12 + jitter, TrueLy + 12 + jitter,
                 TrueRx + 12 - jitter, TrueRy + 12 - jitter);
            state = pad.Parse(raw, raw.Length, start + (i + 1) * step, ++seq);
        }
        string lateNote = pad.StickDiagnostic;
        bool ok = Math.Abs(state.Lx - 2048) <= 3 && Math.Abs(state.Ry - 2048) <= 3;
        report.AppendLine(
            "case=jittery_offset_recovers result=" + (ok ? "PASS" : "FAIL") +
            " out=(" + state.Lx + "," + state.Ly + "," + state.Rx + "," + state.Ry + ")" +
            " " + lateNote);
    }

    /// <summary>
    /// 轻推 20 个单位并停住 4 秒（会被"小幅重定零"当成新静止位置），然后松手。
    /// 松手瞬间必须立刻回到原位，不能留下反向偏置继续漂。
    /// </summary>
    private static void TestSlightPushNoKickBack(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0);
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 2, offset: 0, out _);
        Feed(pad, 2000, span: 2, offset: 20, out _);   // 轻推并保持
        Feed(pad, 1000, span: 2, offset: 0, out _);    // 松手后微跟踪需要几秒收敛
        GamepadState last = Feed(pad, 400, span: 2, offset: 0, out Stats released);

        // 用户取舍：松手绝不漂优先。要求最终收敛回 2048，不要求瞬间。
        bool ok = Near(released.MeanLx, 2048, 3.0) && Near(released.MeanRy, 2048, 3.0) &&
                  Math.Abs(last.Lx - 2048) <= 3 && Math.Abs(last.Ry - 2048) <= 3;
        report.AppendLine(
            "case=slight_push_no_kick result=" + (ok ? "PASS" : "FAIL") +
            " release_mean=(" + F(released.MeanLx) + "," + F(released.MeanRy) + ")" +
            " last=(" + last.Lx + "," + last.Ry + ") " + pad.StickDiagnostic);
    }

    /// <summary>
    /// 复刻 13:19 / 13:31 日志里的现场：零位被一次短扰动拖偏后，
    /// 因为超过 ±12 微跟踪窗口而永远修不回来，输出恒 2071、鼠标一直走。
    /// 现在小幅新静止位置（12~32）在 0.8 秒稳定窗口内会重新定零。
    /// </summary>
    private static void TestStuckSmallOffsetRecovers(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0);
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 2, offset: 0, out _);      // 连接时定零
        Feed(pad, 100, span: 2, offset: -38, out _);     // 短暂碰一下（复刻日志 -15）
        Feed(pad, 400, span: 2, offset: 38, out _);      // 停在新位置 +38（日志差 37.7）→ 重定零
        GamepadState last = Feed(pad, 1500, span: 2, offset: 38, out Stats settled);

        bool ok =
            Near(settled.MeanLx, 2048, 1.0) && Near(settled.MeanRy, 2048, 1.0) &&
            Near(settled.MeanRx, 2048, 1.0) && last.Ry != 2071;
        report.AppendLine(
            "case=stuck_offset_recovers result=" + (ok ? "PASS" : "FAIL") +
            " mean=(" + F(settled.MeanLx) + "," + F(settled.MeanRy) + ")" +
            " last_ry=" + last.Ry + " " + pad.StickDiagnostic);
    }

    /// <summary>
    /// 键鼠模式：陀螺关、右摇杆正中时，鼠标位移必须精确为 0。
    /// 如果有任何一格位移，就说明偏移产生在映射层而不是摇杆解析层。
    /// </summary>
    private static void TestKbmMouseIdleIsZero(StringBuilder report)
    {
        KeyMouseMapper mapper = new();
        long now = 1000;
        int totalX = 0, totalY = 0;
        for (int i = 0; i < 3000; i++)   // 6 秒 @2ms
        {
            GamepadState state = GamepadState.Neutral();
            state.Lx = state.Ly = state.Rx = state.Ry = GamepadState.AxisCenter;
            mapper.Update(state, 0, 0, 0, 0, now);
            (int dx, int dy) = mapper.TakeMouseDelta(now, out _);
            totalX += dx; totalY += dy;
            now += 2;
        }

        bool ok = totalX == 0 && totalY == 0;
        report.AppendLine(
            "case=kbm_idle_zero result=" + (ok ? "PASS" : "FAIL") +
            " total=(" + totalX + "," + totalY + ") " + mapper.MouseTelemetry);
    }

    /// <summary>
    /// GyroStick（陀螺→右摇杆）吃的是 SwitchRawImuSamples，必须与蓝牙一样是**未扣零偏**的原始值。
    /// 有线以前把扣过 gyroBias 的样本喂进去，等于零偏扣两遍，残差会被 anti_deadzone
    /// 放大成恒定右摇杆偏转（用户看到的"一直往右转"）。这里直接验原始字节有没有被动过。
    /// </summary>
    private static void TestWiredImuSamplesAreRaw(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0);
        Pro2WiredProtocol pad = new();
        byte[] raw = new byte[64];
        Pack(raw, TrueLx, TrueLy, TrueRx, TrueRy);

        // 报告里的轴序是 gyro X,Z,Y，且 Z 取负；这里塞三个明显非零的值。
        WriteI16(raw, 0x37, 1234);   // gyroX
        WriteI16(raw, 0x39, -234);   // gyroZ
        WriteI16(raw, 0x3B, 567);    // gyroY

        GamepadState state = pad.Parse(
            raw, raw.Length, System.Diagnostics.Stopwatch.GetTimestamp(), 1);
        SwitchImuRawSample[] imu = state.SwitchRawImuSamples;
        bool ok = imu.Length == 1 &&
                  imu[0].GyroX == 1234 && imu[0].GyroY == 567 && imu[0].GyroZ == 234;
        report.AppendLine(
            "case=wired_imu_raw result=" + (ok ? "PASS" : "FAIL") +
            " sample=" + (imu.Length == 0 ? "none" :
                "(" + imu[0].GyroX + "," + imu[0].GyroY + "," + imu[0].GyroZ + ")") +
            " parsed_gyro=" + state.GyroX + "," + state.GyroY + "," + state.GyroZ);
    }

    private static void WriteI16(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value & 0xFF);
        target[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// 用户实测的两个症状一起验：
    ///   ① 小范围推（60 个单位，约 3%）必须**持续有输出**，不能被零位跟踪吃掉；
    ///   ② 松手立刻回 2048，不能因为"中心被推走了"而往反方向飞。
    /// </summary>
    private static void TestSmallPushRespondsAndNoKickBack(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0, wired: true);
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 4, offset: 0, out _);

        // 用户选择「松手绝不漂」优先：轻推保持 0.8 秒后会归零，所以只要求
        // 刚推动时有输出（0.5 秒内），以及松手后绝不反向漂。
        Feed(pad, 250, span: 2, offset: 60, out Stats push1);
        Feed(pad, 2250, span: 2, offset: 60, out Stats push2);
        // 松手 0.6 秒后必须回到 2048 附近，不能长期反向跑。
        Feed(pad, 300, span: 2, offset: 0, out _);   // 0.6s：留足重定零时间
        GamepadState last = Feed(pad, 300, span: 2, offset: 0, out Stats steady);

        bool pushed = push1.MeanLx > 2080;
        bool noKick = Near(steady.MeanLx, 2048, 2.0);
        bool settled = Near(steady.MeanLx, 2048, 0.5) && steady.SpanLx <= 6;

        bool ok = pushed && noKick && settled;
        report.AppendLine(
            "case=small_push result=" + (ok ? "PASS" : "FAIL") +
            " push_start=" + F(push1.MeanLx) +
            " push_after_rezero=" + F(push2.MeanLx) +
            " settled=" + F(steady.MeanLx) +
            " last=" + last.Lx);
    }

    /// <summary>有线：offset 从 0 线性漂到上限（默认 2.5 单位/秒）。</summary>
    private static void FeedRamp(Pro2WiredProtocol pad, int frames, int span, out Stats stats)
    {
        var random = new Random(20261121);
        byte[] report = new byte[64];
        var state = GamepadState.Neutral();
        stats = new Stats();
        ulong seq = 0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long stepTicks = System.Diagnostics.Stopwatch.Frequency / 500;

        for (int i = 0; i < frames; i++)
        {
            int offset = Math.Min(20, i / 200);   // 2.5 单位/秒（真实温漂量级）
            int jitterX = random.Next(-span, span + 1);
            int jitterY = random.Next(-span, span + 1);
            Pack(report,
                TrueLx + offset + jitterX,
                TrueLy + offset + jitterY,
                TrueRx + offset - jitterX,
                TrueRy + offset - jitterY);
            state = pad.Parse(report, report.Length, start + (i + 1) * stepTicks, ++seq);
            stats.Add(state);
        }
    }

    /// <summary>蓝牙：同一段漂移曲线。</summary>
    private static void FeedBleRamp(Pro2BleReportParser parser, int frames, int span, out Stats stats)
    {
        var random = new Random(20261122);
        byte[] payload = new byte[64];
        var state = GamepadState.Neutral();
        stats = new Stats();

        for (int i = 0; i < frames; i++)
        {
            int offset = Math.Min(20, i / 200);   // 2.5 单位/秒（真实温漂量级）
            int jitterX = random.Next(-span, span + 1);
            int jitterY = random.Next(-span, span + 1);
            Array.Clear(payload);
            PackBle(payload,
                TrueLx + offset + jitterX,
                TrueLy + offset + jitterY,
                TrueRx + offset - jitterX,
                TrueRy + offset - jitterY);
            if (parser.TryParseFd2Payload(payload, out GamepadState parsed, out _)) state = parsed;
            stats.Add(state);
        }
    }

    /// <summary>喂一帧标准 FD2 载荷（布局与 Pro2BleReportParser.ParseFd2Payload 对齐）。</summary>
    private static GamepadState FeedBle(
        Pro2BleReportParser parser, int frames, int span, int offset, out Stats stats)
    {
        var random = new Random(20261120 + offset + frames);
        byte[] payload = new byte[64];
        var state = GamepadState.Neutral();
        stats = new Stats();

        for (int i = 0; i < frames; i++)
        {
            int jitterX = random.Next(-span, span + 1);
            int jitterY = random.Next(-span, span + 1);
            Array.Clear(payload);
            PackBle(
                payload,
                TrueLx + offset + jitterX,
                TrueLy + offset + jitterY,
                TrueRx + offset - jitterX,
                TrueRy + offset - jitterY);
            if (parser.TryParseFd2Payload(payload, out GamepadState parsed, out _))
            {
                state = parsed;
            }
            stats.Add(state);
        }

        return state;
    }

    private static void PackBle(byte[] payload, int lx, int ly, int rx, int ry)
    {
        lx = Math.Clamp(lx, 0, 4095);
        ly = Math.Clamp(ly, 0, 4095);
        rx = Math.Clamp(rx, 0, 4095);
        ry = Math.Clamp(ry, 0, 4095);
        payload[10] = (byte)(lx & 0xFF);
        payload[11] = (byte)(((lx >> 8) & 0x0F) | ((ly & 0x0F) << 4));
        payload[12] = (byte)((ly >> 4) & 0xFF);
        payload[13] = (byte)(rx & 0xFF);
        payload[14] = (byte)(((rx >> 8) & 0x0F) | ((ry & 0x0F) << 4));
        payload[15] = (byte)((ry >> 4) & 0xFF);
    }

    /// <summary>
    /// 复刻 14:04 日志现场：raw Rx 稳定在 2206，但 center 卡在 2222.52（差 16.5），
    /// 且 raw 带 ±8 的手抖（跨度 16）。旧判定要求跨度≤24 才重定零，理论上该满足，
    /// 但 center 连续 24 秒不变 → 输出恒偏 -16（截图里的 -0.0078）。
    /// 这里验证：这种"卡住的偏置 + 手抖"必须在几秒内被纠正。
    /// </summary>
    private static void TestStuckCenterWithJitterRecovers(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0);
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 2, offset: 0, out _);     // 定零

        // 模拟：推到 +40 保持 0.5s 让 center 被钉过去，然后回到 +16 带手抖
        Feed(pad, 250, span: 2, offset: 40, out _);
        byte[] raw = new byte[64];
        var random = new Random(31415);
        ulong seq = 0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long step = System.Diagnostics.Stopwatch.Frequency / 500;
        for (int i = 0; i < 3000; i++)                   // 6 秒
        {
            int jitter = random.Next(-8, 9);             // 手抖 ±8，跨度 16
            Pack(raw, TrueLx + 16 + jitter, TrueLy + 16 + jitter,
                 TrueRx + 16 - jitter, TrueRy + 16 - jitter);
            pad.Parse(raw, raw.Length, start + (i + 1) * step, ++seq);
        }
        string note = pad.StickDiagnostic;
        // 输出必须接近 2048：中心已跟到 +16 的新位置
        bool ok = note.Contains("relearned", StringComparison.Ordinal);
        report.AppendLine(
            "case=stuck_jitter_recovers result=" + (ok ? "PASS" : "FAIL") +
            " " + note);
    }

    /// <summary>
    /// 用户实测："蓝牙松手瞬间回中，有线松手有时 1 秒缓缓回中"。
    /// 根因：有线 TrackCenter 用 tau=1.2s 的 EMA，松手后中心要 1~2 秒才爬回；
    /// 蓝牙是 20 帧（~150ms）稳定窗口直接赋值，所以瞬间归位。
    /// 这里量化：推 20 保持 1 秒让中心跟过去，松手后多少帧回到 2048±2。
    /// 目标与蓝牙对齐：<= 200 帧（0.4s）。
    /// </summary>
    private static void TestReturnToCenterSpeed(StringBuilder report)
    {
        Pro2GyroBiasStore.SetStickDeadzone(0);
        Pro2WiredProtocol pad = new();
        Feed(pad, 1200, span: 2, offset: 0, out _);    // 连接定零
        Feed(pad, 500, span: 2, offset: 20, out _);     // 推 20 保持 1 秒，中心被跟过去

        byte[] raw = new byte[64];
        ulong seq = 0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long step = System.Diagnostics.Stopwatch.Frequency / 500;   // 2ms/帧
        int recover = -1;
        for (int i = 0; i < 1000; i++)                               // 最多观测 2 秒
        {
            Pack(raw, TrueLx, TrueLy, TrueRx, TrueRy);               // 松手：raw 回原位
            GamepadState st = pad.Parse(raw, raw.Length, start + (i + 1) * step, ++seq);
            if (recover < 0 && Math.Abs(st.Lx - 2048) <= 2) recover = i + 1;
        }

        bool ok = recover > 0 && recover <= 200;
        report.AppendLine(
            "case=return_speed result=" + (ok ? "PASS" : "FAIL") +
            " recover_ms=" + (recover * 2) + " " + pad.StickDiagnostic);
    }

    private static GamepadState Feed(
        Pro2WiredProtocol pad, int frames, int span, int offset, out Stats stats)
    {
        var random = new Random(20261119 + offset);
        byte[] report = new byte[64];
        var state = GamepadState.Neutral();
        stats = new Stats();
        ulong seq = 0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long step = System.Diagnostics.Stopwatch.Frequency / 500;   // 2ms/帧，按真实 500Hz 走

        for (int i = 0; i < frames; i++)
        {
            int jitterX = random.Next(-span, span + 1);
            int jitterY = random.Next(-span, span + 1);
            Pack(report,
                TrueLx + offset + jitterX,
                TrueLy + offset + jitterY,
                TrueRx + offset - jitterX,
                TrueRy + offset - jitterY);
            state = pad.Parse(report, report.Length, start + (i + 1) * step, ++seq);
            stats.Add(state);
        }

        return state;
    }

    private static void Pack(byte[] report, int lx, int ly, int rx, int ry)
    {
        lx = Math.Clamp(lx, 0, 4095);
        ly = Math.Clamp(ly, 0, 4095);
        rx = Math.Clamp(rx, 0, 4095);
        ry = Math.Clamp(ry, 0, 4095);
        report[11] = (byte)(lx & 0xFF);
        report[12] = (byte)(((lx >> 8) & 0x0F) | ((ly & 0x0F) << 4));
        report[13] = (byte)((ly >> 4) & 0xFF);
        report[14] = (byte)(rx & 0xFF);
        report[15] = (byte)(((rx >> 8) & 0x0F) | ((ry & 0x0F) << 4));
        report[16] = (byte)((ry >> 4) & 0xFF);
        // 陀螺/加速度清零：本测试只看摇杆，IMU 别掺进来。
        for (int i = 0x31; i < 0x3D; i++) report[i] = 0;
    }

    private static bool Near(double value, double target, double tolerance) =>
        Math.Abs(value - target) <= tolerance;

    private static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private sealed class Stats
    {
        private long count;
        private double sumLx, sumLy, sumRx, sumRy;
        private int minLx = 4095, maxLx, minLy = 4095, maxLy, minRx = 4095, maxRx, minRy = 4095, maxRy;

        public double MeanLx => count == 0 ? 0 : sumLx / count;
        public double MeanLy => count == 0 ? 0 : sumLy / count;
        public double MeanRx => count == 0 ? 0 : sumRx / count;
        public double MeanRy => count == 0 ? 0 : sumRy / count;
        public int SpanLx => maxLx - minLx;
        public int SpanLy => maxLy - minLy;
        public int SpanRx => maxRx - minRx;
        public int SpanRy => maxRy - minRy;

        public void Add(GamepadState state)
        {
            count++;
            sumLx += state.Lx;
            sumLy += state.Ly;
            sumRx += state.Rx;
            sumRy += state.Ry;
            if (state.Lx < minLx) minLx = state.Lx;
            if (state.Lx > maxLx) maxLx = state.Lx;
            if (state.Ly < minLy) minLy = state.Ly;
            if (state.Ly > maxLy) maxLy = state.Ly;
            if (state.Rx < minRx) minRx = state.Rx;
            if (state.Rx > maxRx) maxRx = state.Rx;
            if (state.Ry < minRy) minRy = state.Ry;
            if (state.Ry > maxRy) maxRy = state.Ry;
        }
    }
}
