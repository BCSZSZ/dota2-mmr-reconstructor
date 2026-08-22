# Dota 2 MMR Reconstructor

从本人 Steam/Dota 2 Game Coordinator 下载天梯历史，并重建 Rank Confidence 低于
30% 时被 GC 隐藏的逐局 MMR 曲线。

Windows 普通用户只需要运行 `Dota2MmrReconstructor.exe`。它会依次完成：

1. 在 GUI 中选择目标场数、可选 Steam ID 和输出目录；
2. 通过 Steam 二维码，或一次性用户名/密码与 Steam Guard 验证码登录；
   随后下载 Match History 与 Current Rank；
3. 保留不可变的原始 GC JSON 和断点缓存；
4. 使用内置 C# 模型生成 CSV、JSON、TXT、Markdown、XLSX、SVG、PNG 和独立 HTML；
5. 用户直接双击 `mmr-history.html`，即可缩放、拖动和筛选曲线。

不需要安装 Python、uv、Node.js，也不需要安装浏览器插件。Python 版本继续保留为模型的
参考实现和研究工具。

> [!IMPORTANT]
> Valve 没有公开完整的服务器 MMR/Rank Confidence 更新公式。低 Confidence 区间中的逐局
> 变化是模型估计，不是 Valve 的真实逐局结算；可见 GC 行和每个区间的真实端点不会被模型
> 覆盖。

## Windows 快速开始

### 1. 下载

从 [Releases](https://github.com/BCSZSZ/dota2-mmr-reconstructor/releases) 下载
`Dota2MmrReconstructor-win-x64.zip`，完整解压后运行 `Dota2MmrReconstructor.exe`。

要求：Windows 10/11 和
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。发布包采用小体积
framework-dependent 方式，因此必须保留 ZIP 中的 DLL，不能只复制 EXE。

### 2. GUI 设置

双击 EXE 后会显示设置窗口：

- `Steam ID`：可以留空，登录后自动识别；也接受 ID32 或 SteamID64，填写后可防止登录错账号；
- `Steam 登录方式`：默认使用二维码；无法扫码的账号可选择“用户名 + 密码 + Steam Guard
  验证码”；
- `Steam 用户名/密码`：只在凭据登录模式启用。用户名是登录账户名，不是个人资料昵称；
  密码框会遮挡内容；
- `目标 GC 历史行数`：默认 5,000，建议不超过 10,000；GC 请求本身不能只筛天梯，
  因此它包含普通局，下载后模型才保留 `lobby_type = 7` 的天梯局；`0` 表示只请求当前 Rank；
- `输出根目录`：每个账号自动写入独立的 `<ID32>` 子目录；
- `生成拟合结果与可交互 HTML`：默认启用。

点击“开始”前必须完全退出 Dota 2。二维码会在独立窗口中显示和自动刷新。凭据登录模式会
在 Steam 真正需要二次验证时另行弹出验证码窗口，避免预先填写的 30 秒 OTP 过期；验证码
错误时会要求输入新的验证码。Match History 每页 20 场、约每秒一页，缓存会补齐最新比赛
并从最老断点继续，不会每次重抓全部历史。

二维码仍是推荐方式。用户名、密码、OTP、Steam refresh token 和邮箱 Guard 数据都不会写入
配置、缓存、输出或日志，也没有对应的命令行参数。密码由 SteamKit 使用 Steam 返回的 RSA
公钥加密后发送；认证成功或失败后，程序会立即清空自己持有的用户名和密码引用。详见
[隐私与安全](#隐私与安全)。

### 3. 断点续传与目录选择

建议直接使用 GUI 提供的默认输出目录，并在以后每次运行时保持这个根目录不变。用户只选择
根目录，不需要选择缓存文件，也不要进入某个 SteamID 子目录。登录后程序会自动识别 ID32，
并创建或复用：

```text
<固定输出根目录>/<ID32>/
├── gc-match-history-cache.json
└── gc-collection.json
```

例如既存缓存位于：

```text
E:\Dota2MmrData\123456789\gc-match-history-cache.json
```

GUI 中应选择 `E:\Dota2MmrData`，而不是选择 `E:\Dota2MmrData\123456789`。同一个根目录
可以保存多个账号；程序会按登录账号自动进入各自的 ID32 子目录，不会混用缓存。

续传完全自动进行：

- 每 10 页（约 200 行）及正常结束/连接中断时保存缓存；
- 再次登录同一账号并选择同一根目录时，自动载入既存缓存；
- 先补齐缓存之后的新比赛，再从缓存中最老的 MatchID 继续向前扩展；
- 目标从 8,000 改为 10,000 时，只补新增比赛和更早的约 2,000 行；
- 目标不变时只检查并补充最近新增比赛，不会重新抓取全部历史；
- 缓存账号与登录账号不一致时会终止，不会覆盖原文件。

真正用于断点续传的是 `gc-match-history-cache.json`；`gc-collection.json` 是每次完成后的
原始数据快照。若改选了另一个根目录，程序看不到旧缓存，就会当作一次新的下载。

### 4. 输出

一次成功运行会生成：

```text
<输出根目录>/<ID32>/
├── gc-collection.json                 # 原始 GC 快照，模型不会覆盖
├── gc-match-history-cache.json        # 可断点续传的原始历史缓存
├── reconstruction-manifest.json
└── mmr-reconstruction/
    ├── model-summary.json
    ├── match-estimates.csv
    ├── complete-mmr-curve.csv
    ├── complete-mmr-curve.svg
    ├── complete-mmr-curve.png           # 适合直接查看和转发的静态大图
    ├── hero-mmr-contribution.txt        # 英雄 MMR 净贡献，从正到负
    ├── hero-mmr-contribution.md         # 同口径 Markdown 报告
    ├── hero-mmr-contribution.xlsx       # 可筛选、排序和计算的 Excel 工作簿
    ├── hidden-segments.csv
    ├── mmr-dataset.json
    └── mmr-history.html                # 直接双击打开
```

`mmr-history.html` 已内嵌这一账号的数据，不需要手动导入文件，也不会把数据上传到服务器。
曲线支持滚轮缩放时间轴、`Ctrl + 滚轮`缩放 MMR、拖动平移、双击复位、悬停详情、筛选、
虚拟滚动表格和筛选结果导出。

三个 `hero-mmr-contribution` 文件汇总曲线区间内使用过的全部英雄，按 MMR 净贡献从高到低
排列。TXT 保持 v0.4.0 的原始格式；Markdown 适合网页或聊天转发；XLSX 包含“英雄贡献”和
“说明”两个工作表，表格使用真正的数字单元格，并支持冻结表头、筛选、排序和计算。GC 可见
比赛采用真实 Rank Change；低于 30% Confidence 的隐藏比赛采用端点约束后的拟合 Rank
Change，并分别列出真实与拟合的场数和贡献。校准或轨道切换产生的锚点跳变不属于任何一场
比赛，因此不归因给英雄。`complete-mmr-curve.png` 是 2300×1250 的静态概览图；需要缩放、
拖动或逐场检查时仍使用 HTML。

## 命令行用法

GUI 不是必须的。自动化或高级用户可以运行：

```powershell
.\Dota2MmrReconstructor.exe `
  --account-id 123456789 `
  --history-matches 8000 `
  --output-dir .\artifacts
```

`--account-id` 接受 ID32 或 SteamID64。只下载原始数据、不生成曲线：

```powershell
.\Dota2MmrReconstructor.exe `
  --account-id 123456789 `
  --history-matches 8000 `
  --output .\artifacts\123456789\gc-collection.json `
  --history-cache .\artifacts\123456789\gc-match-history-cache.json `
  --raw-only
```

不请求 Steam，直接用已有原始文件重新拟合：

```powershell
.\Dota2MmrReconstructor.exe `
  --reconstruct-existing .\artifacts\123456789\gc-collection.json `
  --output-dir .\artifacts\123456789\mmr-reconstruction `
  --account-id 123456789
```

用户名/密码/Steam Guard 验证码登录故意只在 GUI 提供。程序不接受 `--password`、`--otp`
之类的参数，避免密码进入命令历史、进程列表或自动化日志。

## 当前模型

C# 生产模型标识为 `endpoint-constrained-glicko-dd-v2-csharp`，与仓库中的 Python v2
参考实现使用相同的生产曲线逻辑。

- 2020-03-02 到 2023-04-19：只绘制单一 MMR 轨道中 GC 返回的真实分数；
- 2023-04-20 之后：`previous_rank/rank_change` 缺失的天梯局被视为待拟合区间；
- 当前 Rank 的 `rank_value` 作为最后一个可用硬端点；
- 根据原始 `rank_data1/rank_data3` 复现已研究客户端版本的闲置 uncertainty 投影与
  Confidence 显示映射；
- 单局先验随 uncertainty 增大而饱和，并使用可见历史拟合 Double Down 混合概率；
- 每个隐藏区间通过有界端点约束分配整数 delta，严格命中下一真实 MMR。

区间端点严格命中只说明累计变化满足观测，不代表区间内每一局都被准确恢复。详见：

- [`docs/model.md`](docs/model.md)
- [`docs/gc-data.md`](docs/gc-data.md)

## 可选：Python/uv 参考实现

普通用户不需要这一步。需要复核模型、研究或开发时，参照
[`docs/windows-uv-guide.md`](docs/windows-uv-guide.md)。最短流程是：

```powershell
winget install --id=astral-sh.uv -e
uv python install 3.12
uv sync --frozen
uv run dota2-mmr-reconstruct 123456789 --no-collect
```

## 源码构建与验证

```powershell
.\scripts\build_collector.ps1
uv sync --frozen
uv run pytest
uv run ruff check .
dotnet build .\collector\Dota2MmrCollector.csproj -c Release
.\scripts\test_credential_login_security.ps1
.\scripts\test_csharp_reconstruction.ps1
```

## 隐私与安全

- `.env`、`artifacts/`、`outputs/`、缓存和构建目录默认不会进入 Git；
- 二维码登录不接触账号密码；凭据登录仅为无法扫码的兼容选项；
- 用户名、密码、OTP、邮箱 Guard 数据和 Steam refresh token 都不写入磁盘或日志；
- 密码框遮挡输入，凭据没有命令行参数；SteamKit 使用 Steam 提供的 RSA 公钥加密密码后发送；
- 登录完成、失败或程序正常退出时，会清空程序持有的凭据引用；但 .NET 字符串不可原地可靠
  擦除，因此无法承诺它们绝不会短暂残留在托管内存、交换文件或系统崩溃转储中；在不信任的
  电脑上仍应使用二维码；
- 凭据登录使用非持久会话，只在本次运行内保留登录所需 token；
- GC 私有 MMR 数据只能由对应账号本人授权取得；
- 原始 JSON 与模型输出分目录保存，重新拟合不会修改原始数据；
- 发布前仍应自行检查数据目录，避免提交玩家历史。

## References 与许可证

GC 采集流程参考了 ShowMMR；完整说明、固定 revision 和其他协议/论文来源见
[`REFERENCES.md`](REFERENCES.md)。依赖声明见
[`collector/THIRD_PARTY_NOTICES.md`](collector/THIRD_PARTY_NOTICES.md)。

本项目自有代码使用 [MIT License](LICENSE)。SteamKit2 等第三方组件遵循各自许可证。
