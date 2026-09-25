我看了这份实现。整体上它已经不是“普通实现”，而是明显针对性能做过优化的版本，尤其是 **Floyd-Steinberg 抖动部分的滚动寄存器优化**，思路是对的。

不过，如果目标是：

> **肉眼几乎看不出区别，但速度能明显提高**

我认为还有几个比较值得动的地方，而且其中有些优化比继续折腾 SIMD 更值得。

---

# 先给结论

我大致会这样排优先级：

| 优化                                  |     预期收益 |      画质影响 | 推荐       |
| ----------------------------------- | -------: | --------: | -------- |
| 小图禁用 Histogram 多线程                  |      ⭐⭐⭐ |         无 | **强烈推荐** |
| Histogram 的 `double m2` → `long m2` |   ⭐⭐～⭐⭐⭐ |         无 | **推荐**   |
| 优化 Histogram 的线程局部数组/合并             |      ⭐⭐⭐ |         无 | **推荐**   |
| LUT 使用更低精度/更聪明的距离计算                 |       ⭐⭐ |      极小/无 | **推荐**   |
| LUT SIMD                            |       ⭐⭐ |         无 | 可以       |
| 抖动改成整数/定点数                          |       ⭐⭐ |        极小 | 可以       |
| 降低抖动计算精度                            |       ⭐⭐ |        极小 | 可以       |
| 关闭 dithering                        | ⭐⭐⭐～⭐⭐⭐⭐ | **有可能明显** | 看场景      |
| 降低 Wu histogram 到 4 bit             |      ⭐⭐⭐ |     可能有区别 | 不建议默认    |
| 换成更简单的 Median Cut                   |      ⭐⭐⭐ |     可能有区别 | 不建议为了质量  |

其中我认为最值得先做的是：

**① Histogram 并行策略 → ② `m2` double → long → ③ LUT 优化。**

---

# 1. 目前最大的一个问题：Histogram 的并行化有点“用力过猛”

这里：

```csharp
int threadCount = Math.Min(
    Environment.ProcessorCount,
    Math.Max(1, pixelLength / 3));

int len = pixelLength / 3;
int blockSize = len / threadCount;

Parallel.For(0, threadCount, t =>
{
    long[] tVwt = ArrayPool<long>.Shared.Rent(HistogramVolume);
    long[] tVmr = ArrayPool<long>.Shared.Rent(HistogramVolume);
    long[] tVmg = ArrayPool<long>.Shared.Rent(HistogramVolume);
    long[] tVmb = ArrayPool<long>.Shared.Rent(HistogramVolume);
    double[] tM2 = ArrayPool<double>.Shared.Rent(HistogramVolume);
```

每一个 worker 都要拿：

* 5 个 histogram
* 每个约 35,937 个元素
* 其中 4 个 `long`
* 1 个 `double`

也就是每线程大约：

**35,937 × 40 ≈ 1.44 MB**

如果 16 核：

**≈23 MB**

然后每个线程还要：

```csharp
Array.Clear(...)
```

清 5 次。

最后又：

```csharp
lock (_vwt)
{
    for (int i = 0; i < HistogramVolume; i++)
    {
        _vwt[i] += tVwt[i];
        ...
    }
}
```

也就是说，实际上你的 Histogram 阶段做了：

> 像素扫描 → 每线程独立 histogram → 所有 histogram 再完整扫描合并

对于大图，这个设计是合理的，因为避免了多个线程竞争 histogram。

但是对于中小图，它可能反而比单线程更慢。

---

# 2. 最简单、几乎零风险的优化：根据像素数量决定是否 Parallel

例如：

```csharp
if (len < 100_000)
{
    BuildHistogram(pixels, len);
}
else
{
    BuildHistogramParallel(pixels, len);
}
```

阈值具体应该 benchmark。

甚至我会倾向于：

```text
< 100K pixels       单线程
100K ~ 1M          视 CPU
> 1M               多线程
```

例如你的家具图片如果通常是：

```text
800 × 600
1200 × 800
1500 × 1000
```

那很多情况下其实没有必要为了 480K～1.5M 像素启动多个任务。

### 这个优化的特点

**画质：100% 不变**

**算法：100% 不变**

**代码复杂度：非常低**

但实际性能很可能会改善。

---

# 3. 我认为一个很值得做的优化：`m2` 不应该使用 double

现在：

```csharp
private readonly double[] _m2;
```

以及：

```csharp
tM2[idx] +=
    (double)pixels[baseIdx] * pixels[baseIdx] +
    (double)pixels[baseIdx + 1] * pixels[baseIdx + 1] +
    (double)pixels[baseIdx + 2] * pixels[baseIdx + 2];
```

其实这里完全可以使用 `long`。

因为：

```text
255² + 255² + 255²
= 195075
```

假设图片是 100MP：

```text
195075 × 100,000,000
≈ 1.95 × 10^13
```

远远没有超过 `long.MaxValue`。

甚至非常夸张的图像尺寸也不会轻易溢出。

所以：

```csharp
long[] _m2;
```

完全够用。

然后：

```csharp
long rgb2 =
    (long)r * r +
    (long)g * g +
    (long)b * b;

tM2[idx] += rgb2;
```

最终：

```csharp
return Vol2(ref cube, _m2)
       - (dr * dr + dg * dg + db * db) / (double)wt;
```

这里最后再转换成 `double` 即可。

---

## 这个改动为什么有价值？

因为你现在每个像素都在做：

```text
byte → double
double multiplication
double multiplication
double multiplication
double addition
double addition
```

而改成：

```text
int/long multiplication
integer addition
```

CPU 对整数运算通常更加轻松，而且：

**5 个 histogram 数组的缓存占用也减少了一部分。**

更重要的是：

### 结果反而更加精确

因为 RGB² 本身就是整数。

所以这里从：

```csharp
double
```

换成：

```csharp
long
```

不是“降低精度”。

恰恰相反：

> **在这个计算阶段使用整数是完全精确的。**

最后 variance 才需要浮点。

这个属于我非常推荐的优化。

---

# 4. 还有一个容易忽略的问题：Histogram 的 `lock` 合并

现在：

```csharp
lock (_vwt)
{
    for (int i = 0; i < HistogramVolume; i++)
    {
        ...
    }
}
```

虽然这里只有一个线程进入 lock，但问题不在 lock 本身。

问题在于：

> 每个线程都必须完整遍历 35,937 个 histogram bin。

假设：

```text
16 threads
× 35,937
≈ 575,000
```

而且每个 bin 又要做：

```text
5 次读取
5 次写入
```

实际上就是几百万次内存操作。

---

# 5. 一个更聪明的方案：线程局部 Histogram + 并行 Merge

现在是：

```text
Thread 1 ──┐
Thread 2 ──┤
Thread 3 ──┼── lock → 主 histogram
Thread 4 ──┘
```

可以变成：

```text
Thread 1 ─────┐
Thread 2 ─────┤
Thread 3 ─────┼── parallel merge ──> main histogram
Thread 4 ─────┘
```

例如：

```csharp
Parallel.For(0, HistogramVolume, i =>
{
    long wt = 0;
    long mr = 0;
    ...
    
    for (int t = 0; t < threadCount; t++)
    {
        wt += local[t].Vwt[i];
        ...
    }

    _vwt[i] = wt;
});
```

这样可以让 merge 也吃满 CPU。

不过这里有个问题：

**如果线程数不多，这个收益未必值得增加代码复杂度。**

所以我会把它排在 `m2` 优化之后。

---

# 6. 真正比较耗 CPU 的另一个地方：BuildMappingLut

这里：

```csharp
for (int i = 0; i < paletteCount; i++)
{
    float dr = fr - palette[i * 3];
    float dg = fg - palette[i * 3 + 1];
    float db = fb - palette[i * 3 + 2];

    float dist = dr * dr + dg * dg + db * db;

    if (dist < minSqDist)
```

理论上：

```text
33 × 33 × 33
= 35,937
```

个颜色空间点。

每个最多：

```text
256
```

个 palette 比较。

所以大约：

```text
35,937 × 256
≈ 9.2 million
```

次距离计算。

这个地方非常适合 SIMD。

---

# 7. 但我不建议第一时间上 Vector256

你可能会想到：

```csharp
Vector256<float>
```

然后一次计算 8 个 palette。

确实可以。

但是这里存在一个问题：

**只有 36K 个 LUT 项。**

9 million 次浮点运算听起来很多，但对于现代 CPU 来说并没有大到离谱。

而且：

```text
palette[i * 3]
palette[i * 3 + 1]
palette[i * 3 + 2]
```

这种 RGB interleaved 数据并不是特别适合 SIMD。

如果为了这个地方把 palette 改成：

```csharp
byte[] paletteR;
byte[] paletteG;
byte[] paletteB;
```

反而有可能更适合。

例如：

```csharp
for (int i = 0; i < paletteCount; i++)
{
    float dr = fr - paletteR[i];
    float dg = fg - paletteG[i];
    float db = fb - paletteB[i];

    float dist = dr * dr + dg * dg + db * db;
}
```

这样 SIMD 会比较舒服。

不过我认为：

> **先 benchmark，再决定是否 SIMD。**

---

# 8. 其实 LUT 可以有一个非常便宜的优化

你的 histogram 本身只有 5 bit：

```csharp
r >> 3
g >> 3
b >> 3
```

所以 LUT 输入本身就是：

```text
0 ~ 31
```

你已经做到了这一点。

但 `BuildMappingLut()` 使用的是：

```csharp
(r - 0.5f) * 8
```

即：

```text
4, 12, 20, ..., 252
```

这个没有问题。

可以进一步直接使用整数距离：

```csharp
int dr = fr - paletteR[i];
int dg = fg - paletteG[i];
int db = fb - paletteB[i];

int dist = dr * dr + dg * dg + db * db;
```

这里完全没有必要用 float。

因为 LUT 的颜色中心都是整数：

```text
4, 12, 20...
```

palette 也是：

```text
byte
```

所以：

**整个 LUT 构建过程可以纯整数化。**

这是一个很典型的：

> 肉眼完全看不出区别，算法结果也不会改变，但计算成本更低。

---

# 9. Floyd-Steinberg 这里反而已经优化得很好

这部分：

```csharp
float carryR
float next0R
float next1R
float next2R
```

实际上是这份代码里面我最认可的优化。

你已经把传统的：

```text
当前像素
 ↓
修改右边
修改左下
修改正下
修改右下
```

这种大量数组读写：

转换成了：

```text
register carry
register next0
register next1
register next2
```

也就是把 memory traffic 转移到了 CPU register。

而且你的注释明确说明：

> 加法的操作数与先后顺序与逐像素版一致，浮点结果逐位相同。

这个非常好。

**我不会轻易改这里。**

---

# 10. 这里还有一个非常有意思的优化：Floyd-Steinberg 可以用定点整数

现在：

```csharp
float er = (r - palette[palOff]) * InvSixteen;
```

实际上误差传播就是：

```text
error / 16

× 7
× 5
× 3
× 1
```

因此可以把误差扩大 16 倍保存：

```text
errorFixed = color - paletteColor
```

然后：

```text
7 / 16
5 / 16
3 / 16
1 / 16
```

最后统一：

```csharp
>> 4
```

这样理论上可以完全不使用 float。

但这里存在一个重要问题：

### 结果不会再和当前算法完全一致

因为你现在的：

```csharp
float
```

会产生：

```text
0.4375
0.3125
0.1875
0.0625
```

等浮点值。

如果改成定点整数，舍入策略不同，可能在某些像素产生不同的 palette index。

不过通常：

> **肉眼很难发现。**

所以这是一个可以 benchmark 的“第二阶段优化”。

---

# 11. 还有一个非常大的性能开关：Dithering

整个流程现在：

```text
Histogram
 ↓
Moments
 ↓
Wu Quantization
 ↓
Build LUT
 ↓
Floyd-Steinberg
```

而你的 dithering：

```csharp
for (int y = 0; y < height; y++)
{
    for (int x = 0; x < width; x++)
    {
        ...
    }
}
```

本质上是：

> **完全串行的。**

因为 Floyd-Steinberg 的误差依赖前面的像素。

所以如果图片很大：

```text
4000 × 3000
= 12M pixels
```

这里就是 1200 万次串行循环。

---

# 12. 一个非常实际的策略：只对“需要 dithering”的图片开启

你现在：

```csharp
enableDithering = true
```

默认开启。

但对于很多照片：

**Floyd-Steinberg 并不是始终肉眼明显。**

尤其：

* 家具照片
* 木纹
* 复杂纹理
* 有噪声的照片
* 光照比较复杂的照片

本身就有大量高频细节。

这种情况下 dithering 的收益可能非常有限。

反过来：

* 天空
* 墙壁
* 渐变背景
* 大面积纯色
* UI 截图

dithering 非常重要。

所以可以考虑：

```text
照片型：
    dithering off

渐变/插画/UI：
    dithering on
```

这个可能是**整个算法里面最大的性能/质量 trade-off**。

但如果你是做通用 GIF 转换器，我不会直接默认关闭。

---

# 13. 你的 Wu Quantizer 本身其实没什么必要大改

这里：

```csharp
for (int i = 1; i < MaxColors; i++)
{
    if (Split(...))
```

最多 255 次 split。

而每次：

```csharp
3 directions
× 最多 31 个位置
```

大概也就：

```text
255 × 3 × 31
≈ 23,715
```

次 split candidate。

这点计算量非常小。

所以：

> **不要把优化重点放在 `Split()`。**

它看起来复杂，但实际上不是性能热点。

---

# 14. `CalculateMoments()` 也不是最值得优化的地方

它大约：

```text
33³ ≈ 36K
```

循环。

即使里面做了大量累加，也就是几十万级别操作。

相比：

```text
百万～千万像素
```

的 Histogram 和 Dithering，这里基本可以忽略。

---

# 15. 我会推荐你做一个“三档优化”

如果这是我维护的生产代码，我会这样做。

## 第一档：完全不改变算法结果

### ① `m2`：

```csharp
double[] → long[]
```

### ② LUT：

```csharp
float distance → int distance
```

### ③ Histogram：

根据图片大小：

```csharp
small image → single thread
large image → parallel
```

### ④ Histogram merge

根据 benchmark 决定是否 parallel merge。

这一档：

> **理论上画质完全不变。**

---

# 16. 第二档：极小的数值差异

把：

```csharp
float error
```

换成：

```text
fixed-point integer
```

然后：

```text
7 / 16
5 / 16
3 / 16
1 / 16
```

用整数实现。

这种情况下：

> GIF 可能有少量 pixel index 不一样，但肉眼基本无法区分。

而且有可能比 float 更快。

---

# 17. 第三档：针对照片优化

如果你的实际用途主要是：

> 家具产品照片 → GIF / 256 色

那么我反而会测试：

```text
Wu + No Dither
```

和：

```text
Wu + Floyd-Steinberg
```

的实际视觉差异。

因为这有可能是一个非常大的性能收益。

尤其是如果 GIF 本身后面还要：

```text
LZW compression
```

的话，dithering 产生的高频噪声还可能：

> **让 GIF 压缩率变差。**

这是一个容易被忽略的问题。

---

# 18. 我对你这份代码的性能判断

如果粗略按照执行时间排序，我预计大致会是：

```text
大图 + dithering

Floyd-Steinberg
████████████████████████

Histogram
██████████████

BuildMappingLut
█████

CalculateMoments
█

Wu Split
█
```

但：

**小图的时候排序可能完全不同。**

因为 Histogram 的：

```text
Parallel.For
ArrayPool
Array.Clear
线程局部 histogram
lock + merge
```

固定成本比较高。

所以你的程序如果处理：

```text
300×300
500×500
800×600
```

大量小图，我反而非常怀疑：

> **当前 HistogramParallel 并不是最优方案。**

---

# 19. 最值得做的其实不是“猜”，而是 Benchmark

这个算法非常适合做 BenchmarkDotNet。

至少测：

```text
512×512
1024×1024
1920×1080
3840×2160
```

分别：

```text
Dither ON
Dither OFF
```

然后拆成：

```text
BuildHistogram
CalculateMoments
WuSplit
BuildMappingLut
Dithering
```

这样你马上就能看到真正的热点。

---

## 如果让我直接改，我会按这个顺序

**第一步：**

```csharp
double[] _m2
```

→

```csharp
long[] _m2
```

**第二步：**

LUT：

```csharp
float
```

→

```csharp
int
```

**第三步：**

Histogram：

```csharp
小图单线程
大图多线程
```

**第四步：**

Benchmark Histogram 的：

```text
lock merge
```

vs

```text
parallel merge
```

**第五步：**

再考虑：

```text
Floyd-Steinberg fixed-point
```

**最后才考虑：**

```text
AVX2 / Vector256
```

因为目前这份代码里，**SIMD 并不是我认为最容易获得“大收益”的地方**。

尤其你已经把 Floyd-Steinberg 做成滚动寄存器了，这已经是一个很不错的优化；继续在那里硬塞 SIMD，复杂度会上升不少，但收益未必有前面几个优化大。

---

# 附录：实测复核（2026-09-25）

上文是分析推断，本节是对照 `src/Formats/Gif/Quantizer.cs` 逐条实测后的结论。
所有数字来自 `--gif-bench`（中位数），测试图 `examples/progressive.jpg` 缩放得到，24 核。

## 结论总表

| # | 文档主张 | 是否属实 | 处置 |
| - | ------ | ---- | --- |
| §1/§2 | 小图应禁用 Histogram 多线程 | **属实，且收益被低估** | ✅ 已实施 |
| §3 | `m2` double → long | 属实（整数精确），但**收益≈0** | ❌ 回退 |
| §8 | LUT 整数距离 | 属实（结果逐位一致），但**收益≈0** | ❌ 回退 |
| §5 | 并行 merge 替代 lock merge | 属实是开销，但大图直方图仅占编码 4.5% | ❌ 不做 |
| §10 | 抖动改定点整数 | 属实可行，但会改变产物字节 | ❌ 不做 |
| §13/§14 | Split / Moments 不是热点 | **属实** | 不动 |
| §18 | 大图热点排序 抖动 ≫ 直方图 > LUT > 矩/Split | **属实** | — |
| §9 | 抖动滚动寄存器已优化，别动 | **属实** | 不动 |

## 1. 已实施：直方图单/多线程门控（收益 40%）

同二进制 A/B（环境变量 `SIC_Q_HIST_FORCE_PAR`，避免跨进程漂移），每组 5 次取中位数：

| 尺寸 | 抖动开 | 抖动关 |
| ---- | ---: | ---: |
| 512×512 | **−40.1%** | **−50.4%** |
| 800×600 | **−26.7%** | **−43.7%** |
| 1024×1024 | **−10.6%** | −16.8% |
| 1280×720 | −8.9% | **−24.8%** |
| 1440×900 | −3.3% | **−19.0%** |
| 1600×1200 及以上 | ≈0（走并行路径） | ≈0 |

8 个尺寸 × 抖动开/关 = 16 组产物与优化前**逐字节一致**。

### 文档在这里的两处偏差

- **文档建议的固定阈值（100K 像素）不对。** 并行的固定开销随线程数增长，交叉点随核数移动：
  实测 4 核约 0.5 MP、8 核约 1.05 MP、24 核约 1.6 MP。
  若按 100K 硬编码，24 核机器上会白丢 0.1–1.6 MP 区间的 10%–40% 收益。
  实现改为 `60_000 × ProcessorCount + 300_000`，并在 4/8/16/24 核（用 `DOTNET_PROCESSOR_COUNT` 模拟）
  下验证过与「两者取优」的差距在噪声内（±2%）。
- **优先级排错了。** 文档把 `m2` 排第一、直方图策略排第三；实测恰好相反——
  直方图门控贡献了几乎全部收益，`m2` 与 LUT 的贡献在测量噪声内。

## 2. 回退：`m2` double → long（实测≈0，且文档有一处理由是错的）

改法本身正确：RGB² 是整数，long 完全精确，产物逐字节一致（已验证 16 组）。
但**收益测不出来**——在两条路径都走并行直方图的大图上（差异只剩 `m2`+LUT），
中位数差异落在 ±2% 噪声带内。

文档称「5 个 histogram 数组的缓存占用也减少了一部分」是**错的**：
`double` 与 `long` 都是 8 字节，缓存占用一点没变。真正省下的只有每像素 3 次
`int → double` 转换，而这个循环是散射写（scatter）带宽受限，不是运算受限。

按项目（当前 7%）准入规则不保留，已回退；原因写入源码注释避免重复尝试。

## 3. 回退：LUT 整数距离（实测≈0）

改动正确且结果逐位一致（LUT 中心是 `8r-4` 即 4,12,…,252，palette 是 byte，
差值与平方和 ≤ 190,512，int 装得下）。但同样测不出收益：
9.2M 次比较的瓶颈是 `dist < minSqDist` 的分支预测，不是浮点乘法。

## 4. 不做：并行 merge（§5）与定点抖动（§10）

- **并行 merge**：大图上直方图总计只占 quantize 约 6%、编码约 4.5%，
  即便整段做成零耗时也到不了 7% 阈值，数学上封死。
- **定点抖动**：会改变舍入策略 ⇒ 产物字节不再一致，违反本项目的验收口径；
  且已有实验（见 `docs/PerfReport.md`）表明抖动热点是「LUT + 调色板两次依赖加载」
  的串行链延迟，换成整数运算降不了这条链。

## 5. 方法论提醒：跨二进制对比有约 2% 系统性偏差

本次踩到并已定位：**把「行为与 HEAD 完全相同」的二进制（阈值设 0）与 HEAD 二进制对比，
仍然测出 −2.05%**。即跨二进制比较本身带约 ±2% 偏差，不能用来判定 2% 量级的收益。
2% 以内的差异一律改用**同一份二进制内的 `static readonly` 环境变量开关**做 A/B。
