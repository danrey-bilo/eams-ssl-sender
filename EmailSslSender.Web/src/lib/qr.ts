// QR-код по ISO/IEC 18004: байтовый режим, уровень M, версии 1–15.
// ⚠ Свой кодировщик: секрет otpauth:// не уходит в сторонний код.

const ECC_PER_BLOCK = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24];
const BLOCKS = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10];
const ECC_FORMAT_M = 0;
const MAX_VERSION = 15;

type Matrix = boolean[][];

const at = (table: number[], ver: number): number => table[ver] ?? 0;

function rawModules(ver: number): number {
  let result = (16 * ver + 128) * ver + 64;
  if (ver >= 2) {
    const align = Math.floor(ver / 7) + 2;
    result -= (25 * align - 10) * align - 55;
    if (ver >= 7) result -= 36;
  }
  return result;
}

const dataCodewords = (ver: number): number =>
  Math.floor(rawModules(ver) / 8) - at(ECC_PER_BLOCK, ver) * at(BLOCKS, ver);

function gfMultiply(x: number, y: number): number {
  let z = 0;
  for (let i = 7; i >= 0; i--) {
    z = (z << 1) ^ ((z >>> 7) * 0x11d);
    z ^= ((y >>> i) & 1) * x;
  }
  return z;
}

function rsDivisor(degree: number): number[] {
  const result = new Array<number>(degree).fill(0);
  result[degree - 1] = 1;
  let root = 1;
  for (let i = 0; i < degree; i++) {
    for (let j = 0; j < degree; j++) {
      result[j] = gfMultiply(result[j] ?? 0, root);
      if (j + 1 < degree) result[j] = (result[j] ?? 0) ^ (result[j + 1] ?? 0);
    }
    root = gfMultiply(root, 0x02);
  }
  return result;
}

function rsRemainder(data: number[], divisor: number[]): number[] {
  const result = divisor.map(() => 0);
  for (const b of data) {
    const factor = b ^ (result.shift() ?? 0);
    result.push(0);
    divisor.forEach((coef, i) => { result[i] = (result[i] ?? 0) ^ gfMultiply(coef, factor); });
  }
  return result;
}

function encodeData(bytes: number[], ver: number): number[] {
  const bits: number[] = [];
  const push = (value: number, length: number) => {
    for (let i = length - 1; i >= 0; i--) bits.push((value >>> i) & 1);
  };
  push(0b0100, 4);
  push(bytes.length, ver < 10 ? 8 : 16);
  for (const b of bytes) push(b, 8);

  const capacity = dataCodewords(ver) * 8;
  push(0, Math.min(4, capacity - bits.length));
  push(0, (8 - (bits.length % 8)) % 8);
  for (let pad = 0xec; bits.length < capacity; pad ^= 0xec ^ 0x11) push(pad, 8);

  const codewords: number[] = [];
  for (let i = 0; i < bits.length; i += 8) {
    codewords.push(bits.slice(i, i + 8).reduce((acc, bit) => (acc << 1) | bit, 0));
  }
  return codewords;
}

function interleave(data: number[], ver: number): number[] {
  const blocks = at(BLOCKS, ver);
  const eccLen = at(ECC_PER_BLOCK, ver);
  const raw = Math.floor(rawModules(ver) / 8);
  const shortBlocks = blocks - (raw % blocks);
  const shortLen = Math.floor(raw / blocks);
  const divisor = rsDivisor(eccLen);

  const all: number[][] = [];
  for (let i = 0, k = 0; i < blocks; i++) {
    const dat = data.slice(k, k + shortLen - eccLen + (i < shortBlocks ? 0 : 1));
    k += dat.length;
    const ecc = rsRemainder(dat, divisor);
    if (i < shortBlocks) dat.push(0);
    all.push(dat.concat(ecc));
  }

  const result: number[] = [];
  const length = all[0]?.length ?? 0;
  for (let i = 0; i < length; i++) {
    all.forEach((block, j) => {
      if (i !== shortLen - eccLen || j >= shortBlocks) result.push(block[i] ?? 0);
    });
  }
  return result;
}

function alignmentPositions(ver: number, size: number): number[] {
  if (ver === 1) return [];
  const count = Math.floor(ver / 7) + 2;
  const step = Math.ceil((ver * 4 + 4) / (count * 2 - 2)) * 2;
  const result = [6];
  for (let pos = size - 7; result.length < count; pos -= step) result.splice(1, 0, pos);
  return result;
}

const MASKS: Array<(x: number, y: number) => boolean> = [
  (x, y) => (x + y) % 2 === 0,
  (_, y) => y % 2 === 0,
  (x) => x % 3 === 0,
  (x, y) => (x + y) % 3 === 0,
  (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0,
  (x, y) => ((x * y) % 2) + ((x * y) % 3) === 0,
  (x, y) => (((x * y) % 2) + ((x * y) % 3)) % 2 === 0,
  (x, y) => (((x + y) % 2) + ((x * y) % 3)) % 2 === 0,
];

function build(ver: number, codewords: number[]): Matrix {
  const size = ver * 4 + 17;
  const modules: Matrix = Array.from({ length: size }, () => new Array<boolean>(size).fill(false));
  const fixed: Matrix = Array.from({ length: size }, () => new Array<boolean>(size).fill(false));
  const row = (m: Matrix, y: number): boolean[] => m[y] as boolean[];
  const set = (x: number, y: number, dark: boolean) => { row(modules, y)[x] = dark; row(fixed, y)[x] = true; };

  for (let i = 0; i < size; i++) { set(6, i, i % 2 === 0); set(i, 6, i % 2 === 0); }

  for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]] as const) {
    for (let dy = -4; dy <= 4; dy++) {
      for (let dx = -4; dx <= 4; dx++) {
        const x = cx + dx, y = cy + dy;
        if (x < 0 || y < 0 || x >= size || y >= size) continue;
        const dist = Math.max(Math.abs(dx), Math.abs(dy));
        set(x, y, dist !== 2 && dist !== 4);
      }
    }
  }

  const align = alignmentPositions(ver, size);
  align.forEach((ay, i) => align.forEach((ax, j) => {
    const last = align.length - 1;
    if ((i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0)) return;
    for (let dy = -2; dy <= 2; dy++) {
      for (let dx = -2; dx <= 2; dx++) set(ax + dx, ay + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }
  }));

  const drawFormat = (mask: number) => {
    const data = (ECC_FORMAT_M << 3) | mask;
    let rem = data;
    for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
    const bits = ((data << 10) | rem) ^ 0x5412;
    const bit = (i: number) => ((bits >>> i) & 1) !== 0;
    for (let i = 0; i <= 5; i++) set(8, i, bit(i));
    set(8, 7, bit(6));
    set(8, 8, bit(7));
    set(7, 8, bit(8));
    for (let i = 9; i < 15; i++) set(14 - i, 8, bit(i));
    for (let i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
    for (let i = 8; i < 15; i++) set(8, size - 15 + i, bit(i));
    set(8, size - 8, true);
  };

  drawFormat(0);

  if (ver >= 7) {
    let rem = ver;
    for (let i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >>> 11) * 0x1f25);
    const bits = (ver << 12) | rem;
    for (let i = 0; i < 18; i++) {
      const dark = ((bits >>> i) & 1) !== 0;
      const a = size - 11 + (i % 3), b = Math.floor(i / 3);
      set(a, b, dark);
      set(b, a, dark);
    }
  }

  let i = 0;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    for (let vert = 0; vert < size; vert++) {
      for (let j = 0; j < 2; j++) {
        const x = right - j;
        const upward = ((right + 1) & 2) === 0;
        const y = upward ? size - 1 - vert : vert;
        if (!row(fixed, y)[x] && i < codewords.length * 8) {
          row(modules, y)[x] = (((codewords[i >>> 3] ?? 0) >>> (7 - (i & 7))) & 1) !== 0;
          i++;
        }
      }
    }
  }

  const applyMask = (mask: number) => {
    const test = MASKS[mask] as (x: number, y: number) => boolean;
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        if (!row(fixed, y)[x] && test(x, y)) row(modules, y)[x] = !row(modules, y)[x];
      }
    }
  };

  let best = 0;
  let bestPenalty = Infinity;
  for (let mask = 0; mask < 8; mask++) {
    applyMask(mask);
    drawFormat(mask);
    const p = penalty(modules);
    if (p < bestPenalty) { best = mask; bestPenalty = p; }
    applyMask(mask);
  }
  applyMask(best);
  drawFormat(best);

  return modules;
}

// Упрощённая оценка маски: годится любая, выбирается лишь более читаемая.
function penalty(m: Matrix): number {
  const size = m.length;
  const cell = (x: number, y: number) => (m[y] as boolean[])[x] as boolean;
  let result = 0;
  let dark = 0;
  for (let a = 0; a < size; a++) {
    let runRow = 1, runCol = 1;
    for (let b = 1; b < size; b++) {
      if (cell(b, a) === cell(b - 1, a)) runRow++; else { if (runRow >= 5) result += runRow - 2; runRow = 1; }
      if (cell(a, b) === cell(a, b - 1)) runCol++; else { if (runCol >= 5) result += runCol - 2; runCol = 1; }
    }
    if (runRow >= 5) result += runRow - 2;
    if (runCol >= 5) result += runCol - 2;
  }
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      if (cell(x, y)) dark++;
      if (x < size - 1 && y < size - 1) {
        const c = cell(x, y);
        if (c === cell(x + 1, y) && c === cell(x, y + 1) && c === cell(x + 1, y + 1)) result += 3;
      }
    }
  }
  const total = size * size;
  result += Math.floor(Math.abs(dark * 20 - total * 10) / total) * 10;
  return result;
}

/** Матрица модулей QR для текста; null — текст не помещается в версию 15. */
export function qrMatrix(text: string): Matrix | null {
  const bytes = Array.from(new TextEncoder().encode(text));
  for (let ver = 1; ver <= MAX_VERSION; ver++) {
    const needed = 4 + (ver < 10 ? 8 : 16) + bytes.length * 8;
    if (needed <= dataCodewords(ver) * 8) return build(ver, interleave(encodeData(bytes, ver), ver));
  }
  return null;
}

/** Путь SVG (по модулю на клетку) и размер с полем в четыре модуля. */
export function qrPath(text: string): { size: number; path: string } | null {
  const matrix = qrMatrix(text);
  if (!matrix) return null;
  let path = '';
  matrix.forEach((cells, y) => cells.forEach((dark, x) => { if (dark) path += `M${x + 4} ${y + 4}h1v1h-1z`; }));
  return { size: matrix.length + 8, path };
}
