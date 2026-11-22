// 生成「映射页实时高亮」素材：在显示像素尺度重绘，和 Figma 定稿同款。
//   面键：亮环 + 圆内透光 + 外圈晕光
//   摇杆按下：最外圈发光
//   十字键：按上下左右分别点亮（只亮一个臂）
//   肩键：按线稿形状整块点亮（L1/R1），扳机不再显示
//   触摸板：先用本体色盖掉线稿，再叠干净的高亮，避免两层边缘不重合
//
//   node tools/gen-highlights.js            # 生成 PNG + HighlightGeometry.g.cs
//   node tools/gen-highlights.js --dry      # 只打印诊断，不写文件
const fs = require('fs');
const path = require('path');

function findPadglow() {
  let d = __dirname;
  for (let i = 0; i < 10; i++) {
    const p = path.join(d, 'figma-ui', 'padglow.js');
    if (fs.existsSync(p)) return p;
    d = path.dirname(d);
  }
  throw new Error('找不到 figma-ui/padglow.js');
}
const { readPng, writePng, px, lum } = require(findPadglow());

const ROOT = path.join(__dirname, '..');
const OUT_PNG = path.join(ROOT, 'Assets', 'Highlights');
const OUT_CS = path.join(ROOT, 'UI', 'Pages', 'HighlightGeometry.g.cs');

const IW = 448;              // PadImage 显示宽度
const K = 2;                 // 超采样（生成 2 倍显示像素）
const RING_PX = 1.0;         // 亮环半宽（显示 px）
const HALO_PX = 12;          // 晕光半径（显示 px）
const BODY = [13, 19, 27];   // 线稿本体色（触摸板遮盖用）

const BLUE = [56, 130, 255];
const RIM = [176, 214, 255];

const LAYOUTS = {
  switch: {
    file: 'switch_pro', w: 1000, h: 702,
    spots: [
      ['X', .7630, .2020, .0700], ['Y', .6850, .3010, .0700],
      ['A', .8410, .3000, .0700], ['B', .7620, .3900, .0700],
      ['－', .3770, .1920, .0460], ['＋', .6220, .1920, .0460],
      ['L3', .2220, .2990, .1150, 'ring:1.38'], ['R3', .6300, .4960, .1150, 'ring:1.38']
    ],
    dpad: [.3480, .4950, .1420],
    shoulders: [['ShR', 760, 25], ['ShL', 240, 25]]
  },
  xbox: {
    file: 'xbox', w: 1001, h: 703,
    spots: [
      ['Y', .7600, .1770, .0680], ['X', .6900, .2780, .0680],
      ['B', .8270, .2780, .0680], ['A', .7600, .3760, .0680],
      ['视图', .4300, .2700, .0460], ['菜单', .5710, .2700, .0460],
      ['L3', .2440, .2750, .1080, 'ring:1.34'], ['R3', .6320, .4910, .1080, 'ring:1.34']
    ],
    dpad: [.3710, .5010, .1440],
    // 扳机条是连成一条的，用左右窗口切开：LB 只照亮左半，RB 只照亮右半
    shoulders: [['ShR', 820, 58, { x0: 580 }], ['ShL', 820, 58, { x1: 420 }]]
  },
  ps5: {
    file: 'ps5', w: 1001, h: 678,
    spots: [
      ['△', .8170, .1650, .0680], ['□', .7490, .2650, .0680],
      ['○', .8840, .2650, .0680], ['×', .8170, .3650, .0680],
      ['PS 键', .5020, .4630, .0600], ['触摸板', .5000, .1480, .0600, 'region'],
      ['L3', .3380, .4660, .1080, 'ring:1.26'], ['R3', .6640, .4690, .1080, 'ring:1.26']
    ],
    dpad: [.1870, .2590, .1680],
    // PS5 的 L1/R1 是握把顶上那片「翼」，线稿里只画了轮廓、内部透明；
    // 触摸板两侧的小胶囊是创建/选项键，不能算肩键（之前就是点亮了那两颗）。
    shoulders: [
      ['ShL', 250, 8, null, 'wing'], ['ShR', 800, 8, null, 'wing'],
      // 触摸板两侧的小胶囊：创建（Back）/ 选项（Start），单独点亮
      ['Create', 270, 70], ['Options', 731, 70]
    ]
  }
};

const ASSET = {
  switch: { Y: 'Y', X: 'X', A: 'A', B: 'B', '－': 'Minus', '＋': 'Plus', L3: 'L3', R3: 'R3' },
  xbox: { Y: 'Y', X: 'X', B: 'B', A: 'A', '视图': 'View', '菜单': 'Menu', L3: 'L3', R3: 'R3' },
  ps5: { '△': 'Tri', '□': 'Square', '○': 'Circle', '×': 'Cross', 'PS 键': 'PS', '触摸板': 'Touch', L3: 'L3', R3: 'R3' }
};

const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
const smooth = (v) => { const t = clamp01(v); return t * t * (3 - 2 * t); };

function sampler(img) {
  return (x, y) => {
    const x0 = Math.floor(x), y0 = Math.floor(y);
    const fx = x - x0, fy = y - y0;
    let r = 0, g = 0, b = 0, a = 0, w = 0;
    for (let dy = 0; dy <= 1; dy++) {
      for (let dx = 0; dx <= 1; dx++) {
        const X = Math.min(img.w - 1, Math.max(0, x0 + dx));
        const Y = Math.min(img.h - 1, Math.max(0, y0 + dy));
        const k = (dx ? fx : 1 - fx) * (dy ? fy : 1 - fy);
        const p = px(img, X, Y);
        r += p[0] * k; g += p[1] * k; b += p[2] * k; a += p[3] * k; w += k;
      }
    }
    if (w > 0) { r /= w; g /= w; b /= w; a /= w; }
    return [r, g, b, a, lum(r, g, b)];
  };
}

/** 沿 64 个方向从中心向外找最外侧笔画 */
function ringProbe(sample, cx, cy, rMax, rMin) {
  const radii = [];
  for (let i = 0; i < 64; i++) {
    const t = (i / 64) * Math.PI * 2, cs = Math.cos(t), sn = Math.sin(t);
    for (let r = rMax; r >= rMin; r -= 0.5) {
      if (sample(cx + cs * r, cy + sn * r)[4] > 45) { radii.push(r); break; }
    }
  }
  if (!radii.length) return { r: 0, found: 0 };
  radii.sort((a, b) => a - b);
  return { r: radii[radii.length >> 1], found: radii.length / 64 };
}

/** 从种子点浸染（遇笔画即停），返回 mask + 包围盒 + 盒式模糊 */
function flood(img, sx, sy, hollow) {
  const w = img.w, h = img.h, mask = new Uint8Array(w * h);
  const pass = (x, y) => {
    const p = px(img, x, y);
    // hollow：只走透明像素，用来浸染「只描边、内部空心」的肩键翼
    return hollow ? p[3] < 8 : (p[3] >= 8 && lum(p[0], p[1], p[2]) < 58);
  };
  if (!pass(sx, sy)) return null;
  const stack = [sy * w + sx];
  let x0 = sx, x1 = sx, y0 = sy, y1 = sy, count = 0;
  while (stack.length) {
    const p = stack.pop();
    if (mask[p]) continue;
    const x = p % w, y = (p - x) / w;
    if (!pass(x, y)) continue;
    mask[p] = 1; count++;
    if (x < x0) x0 = x; if (x > x1) x1 = x;
    if (y < y0) y0 = y; if (y > y1) y1 = y;
    if (x > 0) stack.push(p - 1);
    if (x < w - 1) stack.push(p + 1);
    if (y > 0) stack.push(p - w);
    if (y < h - 1) stack.push(p + w);
  }
  if (!count) return null;
  const at = (x, y) => (x < 0 || y < 0 || x >= w || y >= h ? 0 : mask[y * w + x]);
  const blur = (radius) => {
    const tmp = new Float32Array(w * h), out = new Float32Array(w * h);
    for (let y = 0; y < h; y++) {
      let s = 0;
      for (let x = -radius; x <= radius; x++) s += at(x, y);
      for (let x = 0; x < w; x++) {
        tmp[y * w + x] = s / (radius * 2 + 1);
        s += at(x + radius + 1, y) - at(x - radius, y);
      }
    }
    for (let x = 0; x < w; x++) {
      let s = 0;
      for (let y = -radius; y <= radius; y++) s += (y < 0 || y >= h ? 0 : tmp[y * w + x]);
      for (let y = 0; y < h; y++) {
        out[y * w + x] = s / (radius * 2 + 1);
        s += (y + radius + 1 < h ? tmp[(y + radius + 1) * w + x] : 0) - (y - radius >= 0 ? tmp[(y - radius) * w + x] : 0);
      }
    }
    return out;
  };
  return { at, x0, x1, y0, y1, count, blur };
}

/** 采样 mask 模糊值（双线性） */
function fieldAt(field, img, x, y) {
  const x0 = Math.floor(x), y0 = Math.floor(y), fx = x - x0, fy = y - y0;
  const at = (X, Y) => (X < 0 || Y < 0 || X >= img.w || Y >= img.h ? 0 : field[Y * img.w + X]);
  return (at(x0, y0) * (1 - fx) + at(x0 + 1, y0) * fx) * (1 - fy) +
         (at(x0, y0 + 1) * (1 - fx) + at(x0 + 1, y0 + 1) * fx) * fy;
}

/** 统一渲染：box 是线稿坐标里的裁剪框，draw 返回 [r,g,b,a] 或 null */
function renderBox(img, box, artPerPx, draw) {
  const Sx = Math.max(8, Math.round(((box.x1 - box.x0) / artPerPx) * K));
  const Sy = Math.max(8, Math.round(((box.y1 - box.y0) / artPerPx) * K));
  const buf = Buffer.alloc(Sx * Sy * 4);
  const sample = sampler(img);
  for (let j = 0; j < Sy; j++) {
    for (let i = 0; i < Sx; i++) {
      const ax = box.x0 + ((i + 0.5) / Sx) * (box.x1 - box.x0);
      const ay = box.y0 + ((j + 0.5) / Sy) * (box.y1 - box.y0);
      const col = draw(ax, ay, sample);
      if (!col) continue;
      const o = (j * Sx + i) * 4;
      buf[o] = col[0]; buf[o + 1] = col[1]; buf[o + 2] = col[2];
      buf[o + 3] = Math.round(clamp01(col[3]) * 255);
    }
  }
  return { buf, w: Sx, h: Sy };
}

const dry = process.argv.includes('--dry');
const geo = {};
let written = 0;

function emit(layout, art, asset, box, artPerPx, draw, note) {
  const { buf, w, h } = renderBox(art, box, artPerPx, draw);
  geo[layout][asset] = {
    x: box.x0 / art.w, y: box.y0 / art.h,
    w: (box.x1 - box.x0) / art.w, h: (box.y1 - box.y0) / art.h
  };
  console.log(`${layout.padEnd(7)} ${asset.padEnd(9)} ${note.padEnd(24)} ${w}x${h}px`);
  if (!dry) {
    writePng(path.join(OUT_PNG, (layout === 'pro2' ? 'switch' : layout) + '_' + asset + '.png'), w, h, buf);
    written++;
  }
}

for (const [layout, cfg] of Object.entries(LAYOUTS)) {
  const art = readPng(path.join(ROOT, 'Assets', 'Handhelds', cfg.file + '.png'));
  const artPerPx = cfg.w / IW;
  const haloArt = HALO_PX * artPerPx;
  const ringHalf = RING_PX * artPerPx;
  geo[layout] = {};

  // ── 面键 / 小键 / 摇杆 / 触摸板 ──
  for (const [name, nx, ny, d, hint] of cfg.spots) {
    const cx = nx * cfg.w, cy = ny * cfg.h, rHot = (d * cfg.w) / 2;
    const asset = ASSET[layout][name];

    if (hint === 'region') {
      const mask = flood(art, Math.round(cx), Math.round(cy));
      if (!mask) { console.log('  ! region 失败', layout, name); continue; }
      const soft = mask.blur(3), glow = mask.blur(7);
      const box = { x0: mask.x0 - haloArt, y0: mask.y0 - haloArt, x1: mask.x1 + haloArt, y1: mask.y1 + haloArt };
      emit(layout, art, asset, box, artPerPx, (x, y, sample) => {
        const [, , , a0] = sample(x, y);
        if (a0 < 8) return null;
        const f = fieldAt(soft, art, x, y);
        const gm = fieldAt(glow, art, x, y);
        // 遮盖层比面板轮廓外扩一点，把线稿自己的那圈描边整条盖掉
        const cover = smooth((f - 0.10) / 0.42);
        const edge = clamp01(1 - Math.abs(f - 0.42) / 0.30);
        if (cover < 0.02 && gm < 0.02) return null;
        const mix = (base, top, a) => [
          Math.round(top[0] * a + base[0] * (1 - a)),
          Math.round(top[1] * a + base[1] * (1 - a)),
          Math.round(top[2] * a + base[2] * (1 - a))
        ];
        if (cover > 0.02) {
          // 不透明：先铺本体色，再叠「亮边 + 淡填充」，输出的是合成后的实色
          let col = mix(BODY, BLUE, 0.26 * cover);
          if (edge > 0.02) col = mix(col, RIM, edge * 0.95);
          return [col[0], col[1], col[2], cover];
        }
        // 面板外侧的一圈柔光
        const a = 0.42 * gm;
        return a < 0.01 ? null : [BLUE[0], BLUE[1], BLUE[2], a];
      }, 'region·遮盖+净边');
      continue;
    }

    const want = hint && hint.startsWith('ring:') ? parseFloat(hint.slice(5)) : 0;
    const probe = ringProbe(sampler(art), cx, cy, want ? rHot * want + 8 : rHot * 1.45, want ? rHot * 0.9 : rHot * 0.45);
    const ringR = probe.found >= 0.75 ? probe.r : (want ? rHot * want : rHot * 0.96);
    const box = { x0: cx - ringR - haloArt, y0: cy - ringR - haloArt, x1: cx + ringR + haloArt, y1: cy + ringR + haloArt };
    emit(layout, art, asset, box, artPerPx, (x, y, sample) => {
      const [, , , a0, l] = sample(x, y);
      if (a0 < 8) return null;
      const dd = Math.hypot(x - cx, y - cy);
      if (Math.abs(dd - ringR) <= ringHalf) return [RIM[0], RIM[1], RIM[2], 1];
      if (dd < ringR - ringHalf) {
        // 面键把字母提亮；摇杆不点亮内部那几圈，否则看起来像双环
        return (l > 50 && !want) ? [RIM[0], RIM[1], RIM[2], 0.72] : [BLUE[0], BLUE[1], BLUE[2], 0.30];
      }
      const f = clamp01(1 - (dd - ringR - ringHalf) / haloArt);
      const a = 0.55 * f * f;
      return a <= 0.004 ? null : [BLUE[0], BLUE[1], BLUE[2], a];
    }, 'ring R=' + ringR.toFixed(1));
  }

  // ── 十字键：四臂分开点亮 ──
  {
    const [nx, ny, d] = cfg.dpad;
    const cx = nx * cfg.w, cy = ny * cfg.h, r = (d * cfg.w) / 2;
    // 从每个臂的中点浸染：Switch/Xbox 的四臂连通（得到整块十字），PS5 则是四个独立按键
    const armPt = {
      Up: [cx, cy - r * 0.55], Down: [cx, cy + r * 0.55],
      Left: [cx - r * 0.55, cy], Right: [cx + r * 0.55, cy]
    };
    const masks = {};
    for (const k of Object.keys(armPt)) {
      const [mx, my] = armPt[k];
      masks[k] = flood(art, Math.round(mx), Math.round(my));
    }
    const ref = masks.Up || masks.Down || masks.Left || masks.Right;
    if (ref) {
      // 臂宽：在上臂 mask 上量一排
      const scanY = Math.round(cy - r * 0.5);
      let lo = 1e9, hi = -1;
      for (let x = Math.round(cx - r); x <= cx + r; x++) if (ref.at(x, scanY)) { lo = Math.min(lo, x); hi = Math.max(hi, x); }
      const armHalf = (hi > lo ? (hi - lo) / 2 : r * 0.28) + 2;

      // 每条臂用 45° 楔形切分中心方块：两个方向同时按下也不会互相叠加出深色方块
      const dirs = [
        ['DpadUp', 'Up', (x, y) => (cy - y) > 0 && Math.abs(x - cx) <= (cy - y)],
        ['DpadDown', 'Down', (x, y) => (y - cy) > 0 && Math.abs(x - cx) <= (y - cy)],
        ['DpadLeft', 'Left', (x, y) => (cx - x) > 0 && Math.abs(y - cy) <= (cx - x)],
        ['DpadRight', 'Right', (x, y) => (x - cx) > 0 && Math.abs(y - cy) <= (x - cx)]
      ];
      for (const [asset, key, inArm] of dirs) {
        const mask = masks[key] || ref;
        const soft = mask.blur(1), glow = mask.blur(5);
        const box = { x0: cx - r - haloArt, y0: cy - r - haloArt, x1: cx + r + haloArt, y1: cy + r + haloArt };
        emit(layout, art, asset, box, artPerPx, (x, y, sample) => {
          if (!inArm(x, y)) return null;
          const [, , , a0, l] = sample(x, y);
          if (a0 < 8) return null;
          const m = clamp01(fieldAt(soft, art, x, y) * 1.5);
          if (m < 0.02) {
            const gm = fieldAt(glow, art, x, y);
            if (gm < 0.06) return null;
            return [BLUE[0], BLUE[1], BLUE[2], 0.38 * gm];
          }
          if (l > 34) {
            const cov = clamp01((l - 24) / 80);
            return [RIM[0], RIM[1], RIM[2], Math.max(0.55, cov)];
          }
          return [BLUE[0], BLUE[1], BLUE[2], 0.30 * m];
        }, 'arm ' + key + ' half=' + armHalf.toFixed(0));
      }
    }
  }

  // ── 肩键（L1/R1）：按线稿形状整块点亮 ──
  for (const [asset, sx, sy, clip, mode] of cfg.shoulders) {
    if (mode === 'wing') {
      // 翼：浸染出空心内部 → 内部铺一层蓝 + 贴着空腔的描边提亮 + 外圈柔光
      const inner = flood(art, sx, sy, true);
      if (!inner) { console.log('  ! wing 失败', layout, asset); continue; }
      const soft = inner.blur(2), glow = inner.blur(6);
      const box = { x0: inner.x0 - haloArt, y0: inner.y0 - haloArt, x1: inner.x1 + haloArt, y1: inner.y1 + haloArt };
      emit(layout, art, asset, box, artPerPx, (x, y, sample) => {
        const [, , , a0] = sample(x, y);
        const m = clamp01(fieldAt(soft, art, x, y) * 1.6);
        const gm = fieldAt(glow, art, x, y);
        if (a0 < 8) {
          // 空腔：整块点亮（半透明蓝，压在手柄图上看着像一块亮起来的面板）
          if (m > 0.05) return [BLUE[0], BLUE[1], BLUE[2], 0.58 * Math.min(1, m * 1.4)];
          return gm > 0.04 ? [BLUE[0], BLUE[1], BLUE[2], 0.34 * gm] : null;
        }
        // 笔画：贴着空腔的那一圈用亮边
        if (m > 0.05) return [RIM[0], RIM[1], RIM[2], Math.max(0.55, 0.95 * m)];
        return gm > 0.04 ? [BLUE[0], BLUE[1], BLUE[2], 0.42 * gm] : null;
      }, 'wing ' + (inner.x1 - inner.x0) + 'x' + (inner.y1 - inner.y0));
      continue;
    }

    const mask = flood(art, sx, sy);
    if (!mask) { console.log('  ! shoulder 失败', layout, asset); continue; }
    const soft = mask.blur(1), glow = mask.blur(5);
    let box = { x0: mask.x0 - haloArt, y0: mask.y0 - haloArt, x1: mask.x1 + haloArt, y1: mask.y1 + haloArt };
    if (clip) {
      if (clip.x0 != null) box.x0 = Math.max(box.x0, clip.x0 - 12);
      if (clip.x1 != null) box.x1 = Math.min(box.x1, clip.x1 + 12);
    }
    emit(layout, art, asset, box, artPerPx, (x, y, sample) => {
      const [, , , a0, l] = sample(x, y);
      if (a0 < 8) return null;
      // 连成一条的扳机条按左右窗口切开，切口做 40px 渐隐
      let wgt = 1;
      if (clip) {
        if (clip.x0 != null) wgt = smooth((x - clip.x0) / 40);
        if (clip.x1 != null) wgt = smooth((clip.x1 - x) / 40);
      }
      if (wgt <= 0.01) return null;
      const m = clamp01(fieldAt(soft, art, x, y) * 1.35);
      const gm = fieldAt(glow, art, x, y);
      if (m < 0.02 && gm < 0.05) return null;
      let col = BLUE, alpha;
      if (m > 0.02 && l > 34) {
        col = RIM;
        alpha = Math.max(0.55, clamp01((l - 24) / 80));
      } else if (m > 0.02) {
        alpha = 0.55 * m;
      } else {
        alpha = 0.40 * gm;
      }
      return [col[0], col[1], col[2], alpha * wgt];
    }, 'shoulder' + (clip ? (clip.x0 != null ? ' R' : ' L') : ''));
  }
}

if (!dry) {
  const dump = (key) => '        [' + JSON.stringify(key) + '] = new()\n        {\n' +
    Object.entries(geo[key]).map(([a, g]) =>
      `            ["${a}"] = (${g.x.toFixed(5)}, ${g.y.toFixed(5)}, ${g.w.toFixed(5)}, ${g.h.toFixed(5)}),`
    ).join('\n') + '\n        },';
  const cs = `// <auto-generated> tools/gen-highlights.js —— 高亮素材的归一化包围盒（相对线稿宽高）
// 改素材/半径后重跑生成器，不要手改。
#nullable enable

namespace Y700Switch2V60Viiper.UI.Pages;

internal static class HighlightGeometry
{
    internal static readonly System.Collections.Generic.Dictionary<string,
        System.Collections.Generic.Dictionary<string, (double X, double Y, double W, double H)>> Map = new()
    {
${dump('switch')}
${dump('xbox')}
${dump('ps5')}
    };

    /// <summary>Pro1/Pro2 与 Switch 共用同一张线稿。</summary>
    internal static System.Collections.Generic.Dictionary<string, (double X, double Y, double W, double H)>?
        For(string layout) => Map.TryGetValue(layout is "pro2" or "pro1" ? "switch" : layout, out var m) ? m : null;
}
`;
  fs.writeFileSync(OUT_CS, cs);
  console.log(`\n写出 ${written} 张 PNG -> ${path.relative(ROOT, OUT_PNG)}`);
  console.log('写出几何表 -> ' + path.relative(ROOT, OUT_CS));
}
