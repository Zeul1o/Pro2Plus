using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Pro2Plus;

/// <summary>
/// Switch 2 Pro（057E:2069）USB 有线直读。
///
/// 协议依据 SDL3 的 SDL_hidapi_switch2.c，并由本机实测确认（498.7 Hz，约 2ms 一帧）：
///   * 命令/应答走 interface 1 的 vendor bulk（OUT 0x02 / IN 0x82）
///   * 输入状态报告 0x05 走 interface 0 的 HID 中断端点（IN 0x81），64 字节
///   * 震动写 interface 0 的中断 OUT 端点（0x01）
///
/// 注意：设备在 Windows 上被 libusbK/WinUSB 接管（不在 HIDClass 里），
/// 所以只能通过 libusb 访问，hid.dll 枚举不到它。
///
/// 报告 0x05 布局（0 基）：
///   [5..8]   按键
///   [11..16] 左右摇杆（12bit 打包）
///   [0x31] accel X  [0x33] accel Z（取负）  [0x35] accel Y
///   [0x37] gyro  X  [0x39] gyro  Z（取负）  [0x3B] gyro  Y
/// </summary>
internal sealed class Pro2WiredProtocol : IDisposable
{
    public const ushort VendorId = 0x057E;
    public const ushort ProductId = 0x2069;

    private const int InterfaceHid = 0;
    private const int InterfaceCmd = 1;
    private const byte EndpointHidIn = 0x81;
    private const byte EndpointHidOut = 0x01;
    private const byte EndpointCmdOut = 0x02;
    private const byte EndpointCmdIn = 0x82;

    private const int ReportLength = 64;
    // 陀螺满量程 ±2000 dps，Switch 原始值 16.384 LSB/(°/s)。
    private const double GyroRawPerDps = 16.384;
    private const double StickCenter = 2048.0;

    private static readonly byte[][] InitSequence =
    [
        [0x07, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00],
        [0x0C, 0x91, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00, 0x27, 0x00, 0x00, 0x00],
        [0x11, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00],
        [0x0A, 0x91, 0x00, 0x08, 0x00, 0x14, 0x00, 0x00,
         0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
         0xFF, 0x35, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00,
         0x00, 0x00, 0x00, 0x00],
        [0x0C, 0x91, 0x00, 0x04, 0x00, 0x04, 0x00, 0x00, 0x27, 0x00, 0x00, 0x00],
        [0x01, 0x91, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x00],
        [0x01, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00],
        [0x08, 0x91, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00],
        // 选择报告格式 0x05（带陀螺/加速度）
        [0x03, 0x91, 0x00, 0x0A, 0x00, 0x04, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00],
        // 启动输出
        [0x03, 0x91, 0x00, 0x0D, 0x00, 0x08, 0x00, 0x00,
         0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
    ];

    private static readonly byte[] EnableImu =
        [0x0C, 0x91, 0x00, 0x04, 0x00, 0x04, 0x00, 0x00, 0x23, 0x00, 0x00, 0x00];

    private readonly object gate = new();
    private IntPtr context;
    private IntPtr handle = new(-1);
    private bool claimedHid;
    private bool claimedCmd;

    // ---- 运行时零位 ----
    // Switch 2 Pro 的 flash 校准（0x13080/0x130C0）沿用的是 Switch 1 的布局，
    // 实测读出来的 max 比中性点还小，直接用会把中心算偏，所以这里不读 flash。
    //
    // 关键：**零位只能由这条链路自己的原始读数算出来**。
    // 同一支手柄有线静止值是 (2045,2212,2217,2090)，蓝牙是另一套坐标系，
    // 把任何一边存下来的中心硬套到另一边，就是 160 多个单位（8%）的恒定偏置；
    // 0 死区下这等于永不停止的漂移，而 ±个位数窗口永远学不回来。
    //   1) 连接后等一段完全静止的窗口（四轴跨度 ≤ 24 且都在 2048±256 内，约 120ms）取均值当零位；
    //   2) 之后**只做蓝牙那一套微跟踪**：偏差 ≤ 12 个单位（0.6%）才按 1.2 秒时间常数慢慢贴。
    //      中心最多只能离开零位 12 个单位，所以既不会"推着推着被吃掉"，也不会"松手反弹"；
    //   3) 档案只借用「行程」，中心一律以实测为准。
    //
    // 这套参数就是 Pro2BleReportParser.AxisCalibration 的对等实现（NormQuietWindow=12、
    // NormTrackTauSeconds=1.2），连窗口宽度都一样。之前在这里自己发明过两种算法，都翻车了：
    //   * 每 20 帧把零位对齐到均值 → 慢推时中心被拖走，松手往反方向飞；
    //   * 限速 3 单位/秒 + ±96 窗口 → 小幅推 60 个单位超过 12 但没超过 96，中心照样被拖走，
    //     症状完全一样（实测 60 秒静止原始值 span 只有 2~3，根本不需要这么大窗口）。
    // 结论：窗口必须窄到"只有噪声能进去"，剩下的交给死区/手感，不要试图用跟踪兜底。
    private const int CenterLearnFrames = 60;           // ≈120ms @500Hz，接上就定好，不耽误操作
    private const int CenterLearnMaxSpread = 24;        // 窗口内跨度都不超过它才算静止
    private const int CenterLearnMaxDelta = 256;        // 零位必须落在 2048±256（与蓝牙一致）
    // 注：原先这里用 ±24 内 tau=1.2s 的 EMA 慢跟踪，实测松手要 1.6s 才回中（用户抱怨）。
    // 已改为与蓝牙一致的「稳定窗口直接赋值」，见 TrackCenter。
    // 蓝牙还有第二步 LearnIfCentered（±256 内重新定零），只抄第一步是不够的：
    // 实测中心被推偏 22 / 37.7 个单位后，超出窗口就永远回不来，输出恒偏、鼠标一直走。
    // 用户已明确选择「松手绝不漂」优先于「轻推保持时有输出」，因此把窗口放宽到 80（约 4%）：
    // 偏差 12~80 且连续 0.3 秒稳定（跨度≤24，容纳手抖）就认作新静止位置并归零；
    // 松手后偏差同样在 0.8 秒内被归零，不会长期反向漂。
    // 代价：轻推并保持超过 0.3 秒后输出会逐渐消失——这是用户接受的取舍。
    // 注意：不要再加"回到旧中心立刻贴回"的逻辑；它会和新静止位置重定零互相拉扯，
    // 实测会让一次推杆的残差拖 20 多秒才消失。
    private const int RelearnWindow = 80;
    private const int RelearnSpread = 24;   // 手搭在摇杆上的轻微抖动可达 ±10，8 太严会永远追不上
    private const int RelearnFrames = 150;   // ≈0.3s @500Hz：回中 0.3s（与蓝牙同量级），又不会太快吃掉推杆
    private const int ManualCenterAgreement = 96;       // 手动校准的中心要在这个范围内才采纳
    private const double DefaultStickRange = 1500.0;
    private const double MinStickRange = 1200.0;
    private const double MaxStickRange = 2047.0;

    // 陀螺零偏单独用「滑动窗口找最安静的一段」来学，不再跟着前 60 帧一起锁死。
    // 用户插上线就开始转是常态，固定窗口会把转动速度当成零偏，
    // 结果就是松手后一直朝一个方向推（真实复现：yaw 恒定 -42dps）。
    private const int GyroQuietFrames = 40;      // 40 帧 @500Hz ≈ 80ms
    private const double GyroQuietSpread = 120.0; // counts，静止时的抖动远小于此
    private const int GyroFallbackFrames = 2500;  // ≈5s 还没等到静止就接受当前窗口

    private readonly int[] quietGx = new int[GyroQuietFrames];
    private readonly int[] quietGy = new int[GyroQuietFrames];
    private readonly int[] quietGz = new int[GyroQuietFrames];
    private int quietIndex;
    private int quietCount;
    private int framesSinceStart;
    private bool gyroBiasReady;

    // 零位初次学习（静止窗口）
    private int learnCount;
    private double learnSumLx, learnSumLy, learnSumRx, learnSumRy;
    private int learnMinLx = int.MaxValue, learnMaxLx = int.MinValue;
    private int learnMinLy = int.MaxValue, learnMaxLy = int.MinValue;
    private int learnMinRx = int.MaxValue, learnMaxRx = int.MinValue;
    private int learnMinRy = int.MaxValue, learnMaxRy = int.MinValue;

    // 极静窗口重钉（兜底纠正零位）
    private readonly int[] repinCount = new int[4];
    private readonly long[] centerTicks = new long[4];

    // 小幅新静止位置重新定零（12~32 个单位）
    private readonly double[] relearnSum = new double[4];
    private readonly int[] relearnCount = new int[4];
    private readonly int[] relearnMin = new int[4];
    private readonly int[] relearnMax = new int[4];

    // 每轴的小数进位，见 StickAxisNormalizer.Normalize 的说明。
    private double carryLx, carryLy, carryRx, carryRy;

    private double centerLx = 2048, centerLy = 2048, centerRx = 2048, centerRy = 2048;
    private double rangeLx = DefaultStickRange, rangeLy = DefaultStickRange;
    private double rangeRx = DefaultStickRange, rangeRy = DefaultStickRange;
    private double gyroBiasX, gyroBiasY, gyroBiasZ;

    private bool centerReady;      // 已经用实测静止均值定过零位
    private bool centerSuspect;    // 初次学习没等到静止（手在摇杆上），允许大幅重钉
    private string centerNote = "learning";
    private StickCalibrationProfile? savedHint;
    private PhysicalStickAxes lastRaw = new(2048, 2048, 2048, 2048);

    public Pro2WiredProtocol()
    {
        // 上次校准过的 IMU 原始零偏直接复用，插线后不再重新等静止窗口。
        Pro2GyroBiasProfile saved = Pro2GyroBiasStore.Load();
        if (saved.RawCalibrated)
        {
            gyroBiasX = saved.RawX;
            gyroBiasY = saved.RawY;
            gyroBiasZ = saved.RawZ;
            gyroBiasReady = true;
        }

        ApplySavedStickCalibration();
    }

    /// <summary>
    /// 档案只借「行程」。中心不再从档案里套：档案可能来自蓝牙那套坐标系、
    /// 也可能是旧版本采到的假零位（四个 2048），套过来就是 8% 的恒定漂移。
    /// 中心一律由本链路的实测静止均值决定（见 CenterLearnFrames）。
    /// </summary>
    private void ApplySavedStickCalibration()
    {
        StickCalibrationProfile? saved = Pro2GyroBiasStore.LoadStickCalibration();
        if (saved is null || !saved.CenterCalibrated) return;

        StickCalibrationProfile profile = StickCalibrationProfile.Normalize(saved);
        savedHint = profile;

        // 先借档案的中心顶 120ms（真正的零位马上由实测重算），免得连接瞬间手柄被算偏。
        centerLx = profile.CenterLx;
        centerLy = profile.CenterLy;
        centerRx = profile.CenterRx;
        centerRy = profile.CenterRy;

        if (!profile.RangeCalibrated) return;

        // 行程按档案自己的中心量，所以即使两条链路零位不同，量出来的行程也是对的。
        rangeLx = DirectionalRange(profile.CenterLx, profile.MinLx, profile.MaxLx);
        rangeLy = DirectionalRange(profile.CenterLy, profile.MinLy, profile.MaxLy);
        rangeRx = DirectionalRange(profile.CenterRx, profile.MinRx, profile.MaxRx);
        rangeRy = DirectionalRange(profile.CenterRy, profile.MinRy, profile.MaxRy);
    }

    /// <summary>
    /// 初次零位：等一段四轴跨度都很小的窗口取均值 —— 这就是这条链路真实的零点。
    /// 手一直没停就每帧重来；等了 6 秒还没静止就先用当前均值顶上，并标记为可疑，
    /// 之后松手时极静窗口会用更大的纠正范围把它拉回来。
    /// </summary>
    private void LearnCenter(int lx, int ly, int rx, int ry)
    {
        learnSumLx += lx; learnSumLy += ly; learnSumRx += rx; learnSumRy += ry;
        if (lx < learnMinLx) learnMinLx = lx;
        if (lx > learnMaxLx) learnMaxLx = lx;
        if (ly < learnMinLy) learnMinLy = ly;
        if (ly > learnMaxLy) learnMaxLy = ly;
        if (rx < learnMinRx) learnMinRx = rx;
        if (rx > learnMaxRx) learnMaxRx = rx;
        if (ry < learnMinRy) learnMinRy = ry;
        if (ry > learnMaxRy) learnMaxRy = ry;
        learnCount++;

        if (learnCount < CenterLearnFrames) return;

        bool still =
            learnMaxLx - learnMinLx <= CenterLearnMaxSpread &&
            learnMaxLy - learnMinLy <= CenterLearnMaxSpread &&
            learnMaxRx - learnMinRx <= CenterLearnMaxSpread &&
            learnMaxRy - learnMinRy <= CenterLearnMaxSpread;

        // 和蓝牙 LearnIfCentered 一样：四轴都得在 2048±256 内才算「真的松手了」。
        // 少了这条，手按着摇杆连上时会把按到的位置学成零位，之后 ±12 的窗口再也回不来。
        int count = Math.Max(1, learnCount);
        bool nearCenter =
            NearFactoryCenter(learnSumLx / count) &&
            NearFactoryCenter(learnSumLy / count) &&
            NearFactoryCenter(learnSumRx / count) &&
            NearFactoryCenter(learnSumRy / count);

        if (!still || !nearCenter)
        {
            ResetLearnWindow();
            return;
        }

        centerLx = learnSumLx / count;
        centerLy = learnSumLy / count;
        centerRx = learnSumRx / count;
        centerRy = learnSumRy / count;
        centerReady = true;
        centerSuspect = false;
        centerNote = NoteCenter("learned_still");
        ResetLearnWindow();
    }

    /// <summary>把档案中心与实测中心的差值记进诊断串，排查「档案串链路」时一眼能看见。</summary>
    private string NoteCenter(string source)
    {
        if (savedHint is null) return source;
        int delta = Math.Max(
            Math.Max(Math.Abs((int)Math.Round(centerLx) - savedHint.CenterLx),
                     Math.Abs((int)Math.Round(centerLy) - savedHint.CenterLy)),
            Math.Max(Math.Abs((int)Math.Round(centerRx) - savedHint.CenterRx),
                     Math.Abs((int)Math.Round(centerRy) - savedHint.CenterRy)));
        return source + "(saved_center_delta=" + delta + ",ignored)";
    }

    private static bool NearFactoryCenter(double value) =>
        Math.Abs(value - GamepadState.AxisCenter) <= CenterLearnMaxDelta;

    private void ResetLearnWindow()
    {
        learnCount = 0;
        learnSumLx = learnSumLy = learnSumRx = learnSumRy = 0;
        learnMinLx = learnMinLy = learnMinRx = learnMinRy = int.MaxValue;
        learnMaxLx = learnMaxLy = learnMaxRx = learnMaxRy = int.MinValue;
    }

    /// <summary>
    /// <summary>
    /// 零位跟踪：**限速**。目的只有一个——松手时输出恒等于 2048，但绝不能吃掉真实输入。
    ///   * 偏差 ≤ 32（1.5%）才认为这是噪声/温漂，按 3 单位/秒贴上去；
    ///   * 偏差 > 32 说明确实在推杆，零位一格都不动（松手立刻回到原来的中心，没有反弹）；
    ///   * 连接时手在摇杆上（中心可疑）：0.2 秒稳定窗口纠正一次，之后回到限速跟踪。
    /// </summary>
    /// <summary>
    /// 零位微跟踪：与蓝牙 AxisCalibration.TrackNormCenter 逐字等价。
    ///   * 偏差 > 12 个单位 → 一格都不动（那就是人在推杆）；
    ///   * 偏差 ≤ 12 → 按 1.2 秒时间常数贴上去，吃掉噪声均值和极小残余。
    /// 中心因此永远离不开零位 12 个单位，所以既不会吞掉小幅推杆，也不会松手反向飞。
    /// </summary>
    /// <summary>
    /// 松手快速回中：与蓝牙 LearnIfCentered 同一机制 —— 稳定窗口**直接赋值**，不用慢 EMA。
    /// 用户实测有线松手要 1 秒缓缓回中，根因就是原先 tau=1.2s 的 EMA（收敛到 2 个单位要 2.7s）；
    /// 蓝牙是 20 帧（~150ms）稳定窗口直接把中心钉到均值，所以瞬间归位。
    ///   * 偏差 &gt; 80（RelearnWindow）→ 真实推杆，一格不动；
    ///   * 偏差 ≤ 80 且连续 0.3 秒稳定（跨度≤24）→ 中心 = 这段均值，松手即归位。
    /// 用户已选「松手绝不漂优先」：代价是轻推保持 0.3 秒后输出会归零（与蓝牙一致）。
    /// </summary>
    private void TrackCenter(int index, int raw, ref double center, long timestampTicks)
    {
        double offset = Math.Abs(raw - center);
        if (offset > RelearnWindow)
        {
            relearnCount[index] = 0;
            return;
        }

        // 方向变了（先推左再停右）必须整窗重来，否则均值是两段的混合值，
        // 会把中心定在中间，制造一个新的偏置。固定 48，不跟着跨度阈值放大。
        bool windowBroken = relearnCount[index] > 0 &&
            Math.Abs(raw - relearnSum[index] / relearnCount[index]) > 48;
        if (relearnCount[index] == 0 || windowBroken)
        {
            relearnSum[index] = 0;
            relearnCount[index] = 0;
            relearnMin[index] = int.MaxValue;
            relearnMax[index] = int.MinValue;
        }
        if (raw < relearnMin[index]) relearnMin[index] = raw;
        if (raw > relearnMax[index]) relearnMax[index] = raw;
        relearnSum[index] += raw;
        if (++relearnCount[index] >= RelearnFrames &&
            relearnMax[index] - relearnMin[index] <= RelearnSpread)
        {
            center = relearnSum[index] / relearnCount[index];
            centerNote = NoteCenter("relearned_still");
            relearnCount[index] = 0;
        }
    }

    private static double DirectionalRange(double center, ushort min, ushort max) =>
        Math.Clamp(
            Math.Max(Math.Abs(center - min), Math.Abs(max - center)),
            MinStickRange,
            MaxStickRange);

    /// <summary>
    /// 设置页刚采到的零位/行程立刻生效，不用重连。
    /// 中心只有在「和实测零位吻合」时才采纳（≤96）：同一支手柄两条链路的原始
    /// 坐标系并不一样，直接套别的链路的中心就是 8% 漂移。
    /// </summary>
    public void ApplyCalibration(StickCalibrationProfile? profile)
    {
        if (profile is null || !profile.CenterCalibrated) return;
        StickCalibrationProfile normalized = StickCalibrationProfile.Normalize(profile);

        bool centerAgrees =
            Math.Abs(normalized.CenterLx - centerLx) <= ManualCenterAgreement &&
            Math.Abs(normalized.CenterLy - centerLy) <= ManualCenterAgreement &&
            Math.Abs(normalized.CenterRx - centerRx) <= ManualCenterAgreement &&
            Math.Abs(normalized.CenterRy - centerRy) <= ManualCenterAgreement;

        if (centerAgrees)
        {
            savedHint = normalized;
            centerLx = normalized.CenterLx;
            centerLy = normalized.CenterLy;
            centerRx = normalized.CenterRx;
            centerRy = normalized.CenterRy;
            centerReady = true;
            centerSuspect = false;
            centerNote = "manual";
            Array.Clear(repinCount);
            Array.Clear(relearnCount);
            Array.Clear(centerTicks);
            carryLx = carryLy = carryRx = carryRy = 0;
        }

        if (!centerAgrees)
        {
            // 中心来自另一条链路的坐标系：只借行程，中心继续以实测为准。
            centerNote = NoteCenter("manual_center_rejected");
        }

        if (!normalized.RangeCalibrated) return;
        rangeLx = DirectionalRange(normalized.CenterLx, normalized.MinLx, normalized.MaxLx);
        rangeLy = DirectionalRange(normalized.CenterLy, normalized.MinLy, normalized.MaxLy);
        rangeRx = DirectionalRange(normalized.CenterRx, normalized.MinRx, normalized.MaxRx);
        rangeRy = DirectionalRange(normalized.CenterRy, normalized.MinRy, normalized.MaxRy);
    }

    public bool CalibrationReady => centerReady;

    /// <summary>最近一帧的原始四轴读数（未经归一化）。设置页校准采样要用它。</summary>
    public PhysicalStickAxes LastRaw => lastRaw;

    private int diagLx, diagLy, diagRx, diagRy;

    /// <summary>诊断用：最近一帧的原始轴值与当前中心，排查零位/漂移时直接看它。</summary>
    public string StickDiagnostic =>
        "raw=(" + diagLx + "," + diagLy + "," + diagRx + "," + diagRy + ")" +
        " center=(" + centerLx.ToString("F3") + "," + centerLy.ToString("F3") + "," +
        centerRx.ToString("F3") + "," + centerRy.ToString("F3") + ")" +
        " range=(" + rangeLx.ToString("F1") + "," + rangeLy.ToString("F1") + "," +
        rangeRx.ToString("F1") + "," + rangeRy.ToString("F1") + ")" +
        " dz=" + Pro2GyroBiasStore.ActiveStickDeadzone +
        " center_src=" + centerNote;
    public double GyroBiasDps => Math.Sqrt(gyroBiasX * gyroBiasX + gyroBiasY * gyroBiasY + gyroBiasZ * gyroBiasZ) / GyroRawPerDps;

    public string CalibrationSummary
    {
        get
        {
            if (!centerReady) return "正在测定零位（保持手柄静止）";

            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"实测零位 L=({centerLx:F0},{centerLy:F0}) R=({centerRx:F0},{centerRy:F0}) " +
                $"range=({rangeLx:F0},{rangeLy:F0},{rangeRx:F0},{rangeRy:F0}) " +
                $"deadzone={Pro2GyroBiasStore.ActiveStickDeadzone} " +
                $"gyro_bias={GyroBiasDps:F2}dps");
        }
    }

    public int ReportCount { get; private set; }

    public bool IsOpen => handle != new IntPtr(-1);

    /// <summary>查找并打开有线 Pro2，成功返回 true。失败时 detail 说明原因。</summary>
    public bool TryOpen(out string detail)
    {
        detail = "";
        if (!LibUsb.EnsureLoaded(out string libDetail))
        {
            detail = "libusb 不可用（" + libDetail + "）";
            return false;
        }

        int rc = LibUsb.libusb_init(out context);
        if (rc != LibUsb.Success)
        {
            context = IntPtr.Zero;
            detail = "libusb_init=" + LibUsb.Describe(rc);
            return false;
        }

        // 注意顺序：设备列表必须在 context 还活着的时候释放。
        // 之前这里是「try 里 Cleanup()（内部 libusb_exit）+ finally 里 free_device_list」，
        // 拔掉 USB 走蓝牙时刚好走到「没找到设备」的分支 —— 先把 context 销毁了，
        // 再去 free 属于它的 device list，就是 use-after-free，
        // 表现为 System.AccessViolationException 直接闪退（托管 catch 拦不住）。
        IntPtr list = IntPtr.Zero;
        string failure = "";
        bool opened = false;
        try
        {
            IntPtr count = LibUsb.libusb_get_device_list(context, out list);
            if (count <= 0 || list == IntPtr.Zero)
            {
                failure = "枚举 USB 设备失败";
            }
            else
            {
                IntPtr found = IntPtr.Zero;
                for (int i = 0; i < count; i++)
                {
                    IntPtr device = System.Runtime.InteropServices.Marshal.ReadIntPtr(list, i * IntPtr.Size);
                    if (LibUsb.libusb_get_device_descriptor(device, out LibUsb.DeviceDescriptor descriptor) != LibUsb.Success)
                    {
                        continue;
                    }

                    if (descriptor.idVendor == VendorId && descriptor.idProduct == ProductId)
                    {
                        found = device;
                        break;
                    }
                }

                if (found == IntPtr.Zero)
                {
                    failure = "未找到 057E:2069（USB 线没插或没识别）";
                }
                else
                {
                    rc = LibUsb.libusb_open(found, out IntPtr openedHandle);
                    if (rc != LibUsb.Success || openedHandle == IntPtr.Zero || openedHandle == new IntPtr(-1))
                    {
                        handle = new IntPtr(-1);
                        failure = "打开设备失败（" + LibUsb.Describe(rc) + "），可能被 Steam 或其它软件占用";
                    }
                    else
                    {
                        handle = openedHandle;
                        opened = true;
                    }
                }
            }
        }
        finally
        {
            if (list != IntPtr.Zero)
            {
                LibUsb.libusb_free_device_list(list, 1);
            }
        }

        if (!opened)
        {
            // 设备列表已经释放，这时候再收尾（含 libusb_exit）是安全的。
            detail = failure;
            Cleanup();
            return false;
        }

        LibUsb.libusb_set_auto_detach_kernel_driver(handle, 1);

        rc = LibUsb.libusb_claim_interface(handle, InterfaceHid);
        if (rc != LibUsb.Success)
        {
            detail = "占用 interface 0 失败（" + LibUsb.Describe(rc) + "）";
            Cleanup();
            return false;
        }

        claimedHid = true;

        rc = LibUsb.libusb_claim_interface(handle, InterfaceCmd);
        if (rc != LibUsb.Success)
        {
            detail = "占用 interface 1 失败（" + LibUsb.Describe(rc) + "）";
            Cleanup();
            return false;
        }

        claimedCmd = true;

        foreach (byte endpoint in new[] { EndpointHidIn, EndpointHidOut, EndpointCmdOut, EndpointCmdIn })
        {
            LibUsb.libusb_clear_halt(handle, endpoint);
        }

        SendInit();

        StartRumbleLoop();

        detail = "ok";
        return true;
    }

    /// <summary>
    /// 手柄偶尔插线后只以 250Hz 推 0x05 报告（正常约 500Hz）。
    /// 重新发一遍初始化序列，让手柄回到高速模式；只在连接初期调用。
    /// </summary>
    public void ReapplyInit()
    {
        lock (gate)
        {
            if (!IsOpen) return;
            SendInit();
        }
    }

    private void SendInit()
    {
        byte[] buffer = new byte[64];
        foreach (byte[] command in InitSequence)
        {
            Transfer(EndpointCmdOut, command, 300);
            // 每条命令配一个应答；设备偶尔不回也不影响后续，读到超时就继续。
            Transfer(EndpointCmdIn, buffer, 120);
        }

        Transfer(EndpointCmdOut, EnableImu, 300);
        Transfer(EndpointCmdIn, buffer, 120);
    }

    private void ReadCalibration()
    {
        // Switch 2 Pro 的 flash 校准布局与 Switch 1 不同（0x13080/0x130C0 读出来的
        // max 比中性点还小），直接用会把中心算偏。校准改由 ObserveCalibration 运行时自学习。
    }

    /// <summary>每帧喂原始四轴：先定零位，之后只做小步跟踪 + 极静重钉，并按需扩展行程。</summary>
    private void ObserveCalibration(int lx, int ly, int rx, int ry, long timestampTicks)
    {
        diagLx = lx; diagLy = ly; diagRx = rx; diagRy = ry;
        lastRaw = new PhysicalStickAxes(
            (ushort)Math.Clamp(lx, 0, GamepadState.AxisMax),
            (ushort)Math.Clamp(ly, 0, GamepadState.AxisMax),
            (ushort)Math.Clamp(rx, 0, GamepadState.AxisMax),
            (ushort)Math.Clamp(ry, 0, GamepadState.AxisMax));

        // 还没定好零位、或者上一次是"手按着摇杆连上的"（centerSuspect），
        // 都重新学：学不到干净窗口就一直等，绝不把推到的位置当零位。
        if (!centerReady || centerSuspect)
        {
            LearnCenter(lx, ly, rx, ry);
            if (!centerReady) return;
        }

        TrackCenter(0, lx, ref centerLx, timestampTicks);
        TrackCenter(1, ly, ref centerLy, timestampTicks);
        TrackCenter(2, rx, ref centerRx, timestampTicks);
        TrackCenter(3, ry, ref centerRy, timestampTicks);

        // 学到零位之后继续扩大行程：推到边就记住，下次同样幅度就能输出满 1.0。
        rangeLx = Math.Clamp(Math.Max(rangeLx, Math.Abs(lx - centerLx)), MinStickRange, MaxStickRange);
        rangeLy = Math.Clamp(Math.Max(rangeLy, Math.Abs(ly - centerLy)), MinStickRange, MaxStickRange);
        rangeRx = Math.Clamp(Math.Max(rangeRx, Math.Abs(rx - centerRx)), MinStickRange, MaxStickRange);
        rangeRy = Math.Clamp(Math.Max(rangeRy, Math.Abs(ry - centerRy)), MinStickRange, MaxStickRange);
    }

    /// <summary>
    /// 用最近 40 帧里最「安静」的窗口估计陀螺零偏。
    /// 只要抖动小于阈值就立刻锁定；一直没安静过，5 秒后也接受当前窗口，
    /// 保证陀螺不会因为学不到零偏而一直朝一个方向推。
    /// </summary>
    private void ObserveGyroBias(int gx, int gy, int gz)
    {
        if (gyroBiasReady) return;

        quietGx[quietIndex] = gx;
        quietGy[quietIndex] = gy;
        quietGz[quietIndex] = gz;
        quietIndex = (quietIndex + 1) % GyroQuietFrames;
        if (quietCount < GyroQuietFrames) quietCount++;
        framesSinceStart++;

        if (quietCount < GyroQuietFrames) return;

        bool quiet =
            Spread(quietGx) <= GyroQuietSpread &&
            Spread(quietGy) <= GyroQuietSpread &&
            Spread(quietGz) <= GyroQuietSpread;

        if (!quiet && framesSinceStart < GyroFallbackFrames) return;

        gyroBiasX = MeanOfRing(quietGx);
        gyroBiasY = MeanOfRing(quietGy);
        gyroBiasZ = MeanOfRing(quietGz);
        gyroBiasReady = true;
        Pro2GyroBiasStore.SaveRaw(gyroBiasX, gyroBiasY, gyroBiasZ, "auto_still", force: true);
    }

    private static double MeanOfRing(int[] values)
    {
        long sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        return (double)sum / values.Length;
    }

    private static double Spread(int[] values)
    {
        int min = int.MaxValue;
        int max = int.MinValue;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] < min) min = values[i];
            if (values[i] > max) max = values[i];
        }

        return max - min;
    }

    private static double Mean(int[] values)
    {
        long sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        return (double)sum / values.Length;
    }

    private int Transfer(byte endpoint, byte[] data, int timeoutMs)
    {
        if (handle == new IntPtr(-1)) return -1;

        bool isIn = (endpoint & LibUsb.EndpointIn) != 0;
        bool isBulk = endpoint == EndpointCmdIn || endpoint == EndpointCmdOut;
        byte[] buffer = isIn ? new byte[Math.Max(64, data.Length)] : data;

        int transferred;
        int rc;
        if (isBulk)
        {
            rc = LibUsb.libusb_bulk_transfer(handle, endpoint, buffer, buffer.Length, out transferred, (uint)timeoutMs);
        }
        else
        {
            rc = LibUsb.libusb_interrupt_transfer(handle, endpoint, buffer, buffer.Length, out transferred, (uint)timeoutMs);
        }

        if (rc != LibUsb.Success && rc != LibUsb.ErrorTimeout) return -1;
        if (isIn && transferred > 0 && !ReferenceEquals(buffer, data))
        {
            Array.Copy(buffer, data, Math.Min(transferred, data.Length));
        }

        return rc == LibUsb.Success ? transferred : 0;
    }

    /// <summary>读一帧 0x05 报告。返回长度；超时返回 0；设备错误返回 -1。</summary>
    public int ReadReport(byte[] buffer, int timeoutMs)
    {
        if (handle == new IntPtr(-1)) return -1;

        int rc = LibUsb.libusb_interrupt_transfer(
            handle, EndpointHidIn, buffer, buffer.Length, out int transferred, (uint)timeoutMs);

        if (rc == LibUsb.Success)
        {
            ReportCount++;
            return transferred;
        }

        if (rc == LibUsb.ErrorTimeout) return 0;

        return -1;
    }

    public bool WriteRumbleOutput(byte[] packet)
    {
        if (handle == new IntPtr(-1)) return false;
        int rc = LibUsb.libusb_interrupt_transfer(handle, EndpointHidOut, packet, packet.Length, out _, 50);
        return rc == LibUsb.Success;
    }

    // ---------------- 有线震动 ----------------
    // 输出报告和蓝牙特性用的是同一份 64 字节 raw02：报告 ID 在 [0]，
    // 两个马达块从 [1]（左）和 [17]（右）开始，每块 = 1 字节 0x50|seq 头 + 3 帧 x 5 字节。
    // 走 intf0 的中断 OUT 0x01（HID 输出报告的正常路径）；若某台机器上这个
    // 端点不认，就回退到 intf1 的 bulk OUT 0x02（SDL 的 switch2 驱动走这条）。
    //
    // 关键：HD 马达是「持续刷新」型，SDL 的 RUMBLE_INTERVAL = 12ms 就是这个意思。
    // 主机（游戏）只在震动强度变化时给一次反馈，照抄「来一包发一包」的话，
    // 马达收到一帧、占空比只有 12ms 里的 1/3，听感就是「按一下有个微弱的咔哒，然后没了」。
    // 所以这里缓存最新一帧，由一个独立线程按 12ms 持续重发；主机停止更新强度
    // 超过 RumbleHoldMs 就补一帧零振幅收尾。震动线程独立于输入线程，
    // 免得写 OUT 端点卡住 500Hz 的读取（实测会从 499Hz 掉到 477Hz）。
    private const int RumbleIntervalMs = 12;
    private const int RumbleHoldMs = 400;

    private readonly object rumbleGate = new();
    private readonly AutoResetEvent rumbleSignal = new(false);
    private byte[]? rumbleLatest;
    private bool rumbleLatestActive;
    private long rumbleLatestTicks;
    private bool rumbleSilent = true;
    private Thread? rumbleThread;
    private volatile bool rumbleRunning;
    private int rumbleSeq;
    private long rumbleLastSentTicks;
    private int rumbleRoute;          // 0=未探测 1=intf0 中断 0x01 2=intf1 bulk 0x02 3=两条都不通
    private int rumbleFailures;
    private long rumbleSentCount;
    private long rumbleFailTotal;
    private long rumbleCooldownUntilTicks;
    private int rumbleLastError;

    public long RumbleSentCount => Interlocked.Read(ref rumbleSentCount);

    public long RumbleFailCount => Interlocked.Read(ref rumbleFailTotal);

    public string RumbleErrorLabel => rumbleLastError == 0
        ? "none"
        : (System.Runtime.InteropServices.Marshal.PtrToStringAnsi(
               LibUsb.libusb_error_name(rumbleLastError)) ?? rumbleLastError.ToString());

    public string RumbleRouteLabel => rumbleRoute switch
    {
        1 => "intf0-int-0x01",
        2 => "intf1-bulk-0x02",
        3 => "unsupported",
        _ => "pending"
    };

    /// <summary>震动强度增益，由设置页推送；与蓝牙链路用同一个值。</summary>
    private double rumbleGain = 1.0;

    public void SetRumbleGain(double gain) =>
        rumbleGain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 3) : 1.0;

    /// <summary>投递一帧 64 字节 raw02 震动报告；线程按 12ms 持续重发，直到收到新帧或超时。</summary>
    public bool QueueRumble(ReadOnlySpan<byte> report)
    {
        if (report.Length < ReportLength || report[0] != 0x02) return false;
        byte[] copy = report.Slice(0, ReportLength).ToArray();
        // 与蓝牙共用同一套振幅缩放，保证两种连接震动强度完全一致。
        Pro2NativeRumbleEncoder.ScaleReportAmplitudes(copy, rumbleGain);
        bool active = Pro2NativeRumbleEncoder.HasEffect(copy);
        lock (rumbleGate)
        {
            rumbleLatest = copy;
            rumbleLatestActive = active;
            rumbleLatestTicks = Stopwatch.GetTimestamp();
            rumbleSilent = false;
        }

        try { rumbleSignal.Set(); } catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>开震动线程。放在 TryOpen 成功之后调用。</summary>
    private void StartRumbleLoop()
    {
        if (rumbleRunning) return;
        rumbleRunning = true;
        var thread = new Thread(RumbleLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "pro2-rumble"
        };
        rumbleThread = thread;
        thread.Start();
    }

    private void StopRumbleLoop()
    {
        rumbleRunning = false;
        try { rumbleSignal.Set(); } catch { }
        Thread? thread = rumbleThread;
        rumbleThread = null;
        if (thread != null)
        {
            try { thread.Join(400); } catch { }
        }
    }

    private void RumbleLoop()
    {
        while (rumbleRunning)
        {
            try { rumbleSignal.WaitOne(RumbleIntervalMs); } catch { }
            if (!rumbleRunning) return;
            try { PumpRumble(); } catch { }
        }
    }

    /// <summary>到 12ms 就把缓存的那一帧重发一次；主机静默太久就补一帧零振幅。</summary>
    private void PumpRumble()
    {
        if (rumbleRoute == 3 || handle == new IntPtr(-1)) return;
        if (Interlocked.Read(ref rumbleCooldownUntilTicks) > Stopwatch.GetTimestamp()) return;

        byte[] packet;
        lock (rumbleGate)
        {
            if (rumbleSilent || rumbleLatest == null) return;

            long now = Stopwatch.GetTimestamp();
            if (rumbleLastSentTicks != 0 &&
                Stopwatch.GetElapsedTime(rumbleLastSentTicks, now).TotalMilliseconds < RumbleIntervalMs)
            {
                return;
            }

            if (rumbleLatestActive &&
                Stopwatch.GetElapsedTime(rumbleLatestTicks, now).TotalMilliseconds > RumbleHoldMs)
            {
                // 主机已经不再刷新强度：这一包写零振幅，然后静默，别让马达一直转。
                rumbleLatestActive = false;
                rumbleSilent = true;
                packet = BuildNeutralPacket();
            }
            else
            {
                packet = rumbleLatest;
                if (!rumbleLatestActive) rumbleSilent = true;   // 中性帧只发一次
            }
        }

        if (!SendRumblePacket(packet)) return;
        rumbleLastSentTicks = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref rumbleSentCount);
    }

    /// <summary>停止时把两个马达写成零振幅，避免松开后马达还在转。</summary>
    public void SendNeutralRumble()
    {
        lock (rumbleGate)
        {
            rumbleSilent = true;
            rumbleLatestActive = false;
        }

        if (handle == new IntPtr(-1) || rumbleRoute == 3) return;
        SendRumblePacket(BuildNeutralPacket());
    }

    private static byte[] BuildNeutralPacket()
    {
        byte[] packet = new byte[ReportLength];
        packet[0] = 0x02;
        WriteNeutralFrame(packet);
        return packet;
    }

    private static void WriteNeutralFrame(Span<byte> packet)
    {
        // 同样是「1 字节头 + 3 个满子帧」。中性帧也必须三帧齐全，
        // 只写一帧会在设备侧留下两个全 0 子帧，等于把通道拖进 STALL。
        for (int side = 1; side <= 17; side += 16)
        {
            packet[side] = 0x50;
            for (int frame = 0; frame < 3; frame++)
            {
                int at = side + 1 + frame * 5;
                packet[at] = 0x87;
                packet[at + 1] = 0x01;
                packet[at + 2] = 0x20;
                packet[at + 3] = 0x11;
                packet[at + 4] = 0x00;
            }
        }
    }

    private bool SendRumblePacket(byte[] source)
    {
        if (handle == new IntPtr(-1)) return false;

        byte[] packet = (byte[])source.Clone();
        // [1]/[17] 的低 4 位是包序号，马达靠它区分「新包」。序号不变时设备会把
        // 重复包当同一条忽略，表现为「马达只响一下」。
        if ((packet[1] & 0xf0) == 0x50 && (packet[17] & 0xf0) == 0x50)
        {
            byte seq = (byte)(rumbleSeq & 0x0f);
            packet[1] = (byte)(0x50 | seq);
            packet[17] = (byte)(0x50 | seq);
            rumbleSeq = (rumbleSeq + 1) & 0x0f;
        }

        if (rumbleRoute == 0)
        {
            int probe = LibUsb.libusb_interrupt_transfer(
                handle, EndpointHidOut, packet, packet.Length, out _, 30);
            if (probe == LibUsb.Success) { rumbleRoute = 1; rumbleFailures = 0; return true; }

            probe = LibUsb.libusb_bulk_transfer(
                handle, EndpointCmdOut, packet, packet.Length, out _, 30);
            if (probe == LibUsb.Success) { rumbleRoute = 2; rumbleFailures = 0; return true; }

            // 两条路暂时都不通（多半是设备刚上电握手、或被 Steam 抢着打开）。
            // 不要永久放弃：记错误码、冷却 1 秒后重探，否则一次偶发失败就把震动废掉。
            rumbleLastError = probe;
            Interlocked.Increment(ref rumbleFailTotal);
            EnterRumbleCooldown();
            return false;
        }

        int rc = TransferRumble(packet);
        if (rc == LibUsb.Success) { rumbleFailures = 0; return true; }

        // 端点一旦 STALL，之后每次写入都会直接返回 ERROR_PIPE，不清 halt 永远写不进去。
        // 这就是「震一下之后就再也没了」的直接原因：第 42 包踩到 STALL，后面 20 次
        // 全部秒失败，通道被标记成 unsupported 就再也没恢复过。
        if (rc == LibUsb.ErrorPipe || rc == LibUsb.ErrorIo)
        {
            LibUsb.libusb_clear_halt(
                handle, rumbleRoute == 1 ? EndpointHidOut : EndpointCmdOut);
            rc = TransferRumble(packet);
            if (rc == LibUsb.Success) { rumbleFailures = 0; return true; }
        }

        rumbleLastError = rc;
        Interlocked.Increment(ref rumbleFailTotal);
        if (++rumbleFailures >= 5) EnterRumbleCooldown();
        return false;
    }

    private int TransferRumble(byte[] packet) => rumbleRoute == 1
        ? LibUsb.libusb_interrupt_transfer(handle, EndpointHidOut, packet, packet.Length, out _, 20)
        : LibUsb.libusb_bulk_transfer(handle, EndpointCmdOut, packet, packet.Length, out _, 20);

    /// <summary>连续失败后歇 1 秒再试，避免每 12ms 撞一次端点拖慢别的线程。</summary>
    private void EnterRumbleCooldown()
    {
        rumbleFailures = 0;
        Interlocked.Exchange(ref rumbleCooldownUntilTicks,
            Stopwatch.GetTimestamp() + Stopwatch.Frequency);
    }

    // ---------------- 陀螺遥测（诊断用）----------------
    // 只用来判断「陀螺到底有没有数据」，不参与任何滤波。
    private double probeDpsX, probeDpsY, probeDpsZ;
    private double probePeakDps;
    private double probeGx, probeGy, probeGz;
    private string probeImuHex = "";

    /// <summary>取一次遥测快照并清零窗口峰值。格式固定，便于日志对照。</summary>
    public string TakeGyroProbe()
    {
        double peak = probePeakDps;
        probePeakDps = 0;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"gyro_now=({probeDpsX:F1},{probeDpsY:F1},{probeDpsZ:F1})dps peak5s={peak:F1} " +
            $"accel_g=({probeGx:F2},{probeGy:F2},{probeGz:F2}) imu_raw={probeImuHex}");
    }

    /// <summary>把 0x05 报告解析成项目内部的 GamepadState（与 BLE 的 FD2 同口径）。</summary>
    public GamepadState Parse(byte[] report, int length, long timestampTicks, ulong sequence)
    {
        GamepadState state = GamepadState.Neutral();
        if (length < 0x3D)
        {
            return state;
        }

        ApplyButtons(state, report);

        int rawLx = report[11] | ((report[12] & 0x0F) << 8);
        int rawLy = (report[12] >> 4) | (report[13] << 4);
        int rawRx = report[14] | ((report[15] & 0x0F) << 8);
        int rawRy = (report[15] >> 4) | (report[16] << 4);

        short accelX = BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(0x31, 2));
        short accelZ = BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(0x33, 2));
        short accelY = BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(0x35, 2));
        short gyroX = BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(0x37, 2));
        short gyroZ = BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(0x39, 2));
        short gyroY = BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(0x3B, 2));

        ObserveGyroBias(gyroX, gyroY, gyroZ);

        // 遥测：把「原始字节有没有数据」和「换算成 dps/g 是多少」都留一份。
        // 陀螺不动时 peak5s 会接近 0，那是硬件/IMU 流的问题；peak5s 很大但游戏没反应，
        // 才是滤波或输出段的问题。
        double dpsX = (gyroX - gyroBiasX) / GyroRawPerDps;
        double dpsY = (gyroY - gyroBiasY) / GyroRawPerDps;
        double dpsZ = -(gyroZ - gyroBiasZ) / GyroRawPerDps;
        probeDpsX = dpsX;
        probeDpsY = dpsY;
        probeDpsZ = dpsZ;
        probeGx = accelX / 4096.0;
        probeGy = accelY / 4096.0;
        probeGz = -accelZ / 4096.0;
        double dpsMag = Math.Sqrt(dpsX * dpsX + dpsY * dpsY + dpsZ * dpsZ);
        if (dpsMag > probePeakDps) probePeakDps = dpsMag;
        probeImuHex = Convert.ToHexString(report.AsSpan(0x31, 12));

        ObserveCalibration(rawLx, rawLy, rawRx, rawRy, timestampTicks);

        // 轴方向依据 SDL3 SDL_hidapi_switch2.c 的 MapJoystickAxis：
        //   X 轴 invert=false —— 原始值增大 = 向右推；
        //   Y 轴 invert=true  —— 但那是翻给 SDL「Y 正 = 下」的口径用的，
        //   换成原始值本身的方向就是「增大 = 向上推」，正好等于本项目
        //   「Ly/Ry 增大 = 上推」的 XInput 口径，所以这里一个都不用翻。
        //   （之前这里对 Ly/Ry 多取了一次负，导致有线模式下左摇杆上下、
        //     右摇杆上下、手柄模式摇杆 Y 全部反相，与蓝牙路径也不一致。）
        // 与蓝牙共用同一套归一化公式。死区默认 0（用户要求）：靠实测零位 + 小数进位
        // 保证静止均值就是 2048；设置页把死区调大时两条链路一起生效。
        int deadzone = Pro2GyroBiasStore.ActiveStickDeadzone;
        state.Lx = StickAxisNormalizer.Normalize(rawLx, centerLx, rangeLx, deadzone, ref carryLx);
        state.Ly = StickAxisNormalizer.Normalize(rawLy, centerLy, rangeLy, deadzone, ref carryLy);
        state.Rx = StickAxisNormalizer.Normalize(rawRx, centerRx, rangeRx, deadzone, ref carryRx);
        state.Ry = StickAxisNormalizer.Normalize(rawRy, centerRy, rangeRy, deadzone, ref carryRy);

        // 报告里的 IMU 轴序是 accel X,Z,Y / gyro X,Z,Y，且两处 Z 取负；
        // 这里还原成 SwitchIMU 的标准 12 字节块（X,Y,Z 各 int16，小端），
        // 好让陀螺通路的 (X, Z, -Y) 重排与 BLE 走同一条路径。
        byte[] standard = new byte[12];
        BinaryPrimitives.WriteInt16LittleEndian(standard.AsSpan(0, 2), accelX);
        BinaryPrimitives.WriteInt16LittleEndian(standard.AsSpan(2, 2), accelY);
        BinaryPrimitives.WriteInt16LittleEndian(standard.AsSpan(4, 2), Negate(accelZ));
        BinaryPrimitives.WriteInt16LittleEndian(standard.AsSpan(6, 2), SubtractBias(gyroX, gyroBiasX));
        BinaryPrimitives.WriteInt16LittleEndian(standard.AsSpan(8, 2), SubtractBias(gyroY, gyroBiasY));
        BinaryPrimitives.WriteInt16LittleEndian(standard.AsSpan(10, 2), Negate(SubtractBias(gyroZ, gyroBiasZ)));

        state.AccelX = BinaryPrimitives.ReadInt16LittleEndian(standard.AsSpan(0, 2));
        state.AccelY = BinaryPrimitives.ReadInt16LittleEndian(standard.AsSpan(2, 2));
        state.AccelZ = BinaryPrimitives.ReadInt16LittleEndian(standard.AsSpan(4, 2));
        state.GyroX = BinaryPrimitives.ReadInt16LittleEndian(standard.AsSpan(6, 2));
        state.GyroY = BinaryPrimitives.ReadInt16LittleEndian(standard.AsSpan(8, 2));
        state.GyroZ = BinaryPrimitives.ReadInt16LittleEndian(standard.AsSpan(10, 2));
        state.AccelValid = true;
        state.GyroValid = true;
        // 上面已经把 raw 零偏扣掉了，别让上层再扣一遍。
        state.GyroBiasApplied = true;

        // GyroStick（陀螺→右摇杆）要用**未扣零偏**的原始样本，和蓝牙完全一致：
        // 蓝牙 AttachRawImuSamples 喂的是 payload 原始数据，零偏由 GyroStick 自己学。
        // 有线以前把 standard（已扣 gyroBiasX/Y/Z）喂进去，等于同一份零偏扣两遍，
        // 残差越过 GyroStick 的 deadzone 后会被 anti_deadzone 放大成恒定右摇杆偏转
        // —— 这正是「有线一直往右转、蓝牙完全正常」的来源。
        byte[] imuRaw = new byte[12];
        BinaryPrimitives.WriteInt16LittleEndian(imuRaw.AsSpan(0, 2), accelX);
        BinaryPrimitives.WriteInt16LittleEndian(imuRaw.AsSpan(2, 2), accelY);
        BinaryPrimitives.WriteInt16LittleEndian(imuRaw.AsSpan(4, 2), Negate(accelZ));
        BinaryPrimitives.WriteInt16LittleEndian(imuRaw.AsSpan(6, 2), gyroX);
        BinaryPrimitives.WriteInt16LittleEndian(imuRaw.AsSpan(8, 2), gyroY);
        BinaryPrimitives.WriteInt16LittleEndian(imuRaw.AsSpan(10, 2), Negate(gyroZ));

        SwitchImuRawBlock block = ProfessionalImuConverter.DecodeSwitchImuSamples(imuRaw, 0, 1);
        state.SwitchRawImuSamples = block.Samples;
        state.SwitchRawImuOffset = block.Offset;
        state.SwitchRawImuBytesHex = block.RawBytesHex;

        state.MotionTimestampUs = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(0x2B, 4));
        state.SourceTimestampTicks = timestampTicks;
        state.RawNotificationSequence = sequence;
        state.Updates = (uint)sequence;
        return state;
    }

    private static short Negate(short value) => value == short.MinValue ? short.MaxValue : (short)-value;

    private static short SubtractBias(short raw, double bias)
    {
        double value = raw - bias;
        if (value > short.MaxValue) value = short.MaxValue;
        if (value < short.MinValue) value = short.MinValue;
        return (short)Math.Round(value);
    }

    private static void ApplyButtons(GamepadState state, byte[] report)
    {
        byte right = report[5];
        byte left = report[6];
        byte shared = report[7];
        byte paddles = report[8];

        // Switch 物理位置 → 项目内部 Xbox 位次命名（与 FD2 解析完全一致）。
        // 位定义依据 SDL3 SDL_hidapi_switch2.c 的 HandleSwitchProState：
        //   0x01 = WEST（物理左，任天堂丝印 Y），0x02 = NORTH（物理上，丝印 X）
        //   0x04 = SOUTH（物理下，丝印 B），  0x08 = EAST（物理右，丝印 A）
        // 位次名 = 物理位置，所以输出的虚拟 Xbox 手柄天然就是 Xbox 布局：
        //   物理下 → South(A)、物理右 → East(B)、物理左 → West(X)、物理上 → North(Y)。
        Set(state, GamepadButtons.West, (right & 0x01) != 0);   // 物理左 → West
        Set(state, GamepadButtons.North, (right & 0x02) != 0);  // 物理上 → North
        Set(state, GamepadButtons.South, (right & 0x04) != 0);  // 物理下 → South
        Set(state, GamepadButtons.East, (right & 0x08) != 0);   // 物理右 → East
        Set(state, GamepadButtons.R1, (right & 0x40) != 0);
        Set(state, GamepadButtons.R2, (right & 0x80) != 0);

        Set(state, GamepadButtons.Back, (left & 0x01) != 0);      // -
        Set(state, GamepadButtons.Start, (left & 0x02) != 0);     // +
        Set(state, GamepadButtons.RightStick, (left & 0x04) != 0);
        Set(state, GamepadButtons.LeftStick, (left & 0x08) != 0);
        Set(state, GamepadButtons.Home, (left & 0x10) != 0);
        Set(state, GamepadButtons.Capture, (left & 0x20) != 0);
        Set(state, GamepadButtons.Aux, (left & 0x40) != 0);       // C 键

        Set(state, GamepadButtons.DPadDown, (shared & 0x01) != 0);
        Set(state, GamepadButtons.DPadUp, (shared & 0x02) != 0);
        Set(state, GamepadButtons.DPadLeft, (shared & 0x08) != 0);
        Set(state, GamepadButtons.DPadRight, (shared & 0x04) != 0);
        Set(state, GamepadButtons.L1, (shared & 0x40) != 0);
        Set(state, GamepadButtons.L2, (shared & 0x80) != 0);

        Set(state, GamepadButtons.PaddleRight, (paddles & 0x01) != 0);
        Set(state, GamepadButtons.PaddleLeft, (paddles & 0x02) != 0);

        state.L2 = state.IsPressed(GamepadButtons.L2) ? GamepadState.TriggerMax : (ushort)0;
        state.R2 = state.IsPressed(GamepadButtons.R2) ? GamepadState.TriggerMax : (ushort)0;
        state.RawButtons = ((uint)paddles << 24) | ((uint)shared << 16) | ((uint)left << 8) | right;
    }

    private static void Set(GamepadState state, GamepadButtons button, bool pressed)
    {
        if (pressed) state.Buttons |= button;
    }

    public void Dispose()
    {
        StopRumbleLoop();
        Cleanup();
        try { rumbleSignal.Dispose(); } catch { }
    }

    private void Cleanup()
    {
        lock (gate)
        {
            // 句柄只有「确实 open 成功」才关。-1 是未打开，0 是无效值；
            // 拿 0 去 libusb_close 同样会是 AccessViolation。
            if (handle != new IntPtr(-1) && handle != IntPtr.Zero)
            {
                if (claimedCmd) LibUsb.libusb_release_interface(handle, InterfaceCmd);
                if (claimedHid) LibUsb.libusb_release_interface(handle, InterfaceHid);
                LibUsb.libusb_close(handle);
            }

            handle = new IntPtr(-1);

            claimedCmd = false;
            claimedHid = false;

            if (context != IntPtr.Zero)
            {
                LibUsb.libusb_exit(context);
                context = IntPtr.Zero;
            }
        }
    }
}
