# Dota 2 MMR Reconstructor for Windows

`Dota2MmrReconstructor.exe` 通过 Steam QR，或一次性用户名/密码与 Steam Guard 验证码
登录本人账号，下载原始 Dota 2 GC Match History 与 Current Rank，并用内置 C# 模型生成
完整 MMR 曲线和独立 HTML。

v0.5.6 对无法满足端点约束的隐藏区间继续生成报告：逐场分差留空，曲线以断线和真实锚点表示。
CSV/JSON 保留这些比赛；英雄和队友统计排除这些场次并注明覆盖缺口。GC 请求策略保持原样。

## 双击运行

1. 完全退出 Dota 2；
2. 双击 EXE；
3. 在设置窗口输入目标场数、可选 Steam ID 和输出目录；
4. 使用默认二维码登录；如果手机没有扫码功能，可选择用户名/密码模式，并在程序随后弹出的
   窗口输入当前五位 Steam Guard 验证码；
5. 完成后点击“打开队友报告”查看队友榜单，或点击“打开 MMR 曲线”查看分数变化。
   “打开输出文件夹”可查看 PNG、TXT、Markdown、Excel 等文件。

Steam ID 可以留空自动识别，也可填写 ID32 或 SteamID64 防止扫错账号。默认扫描
5,000 条 GC 历史，建议单次目标不超过 10,000。GC 请求不能在服务端只筛天梯，因此目标数
包含普通局；下载后模型才保留 `lobby_type = 7` 的天梯局。

二维码是推荐登录方式。兼容模式中的用户名、密码和验证码仅保留在本次进程内，不写入文件、
日志或命令行；密码框遮挡内容。程序在 Steam 请求二次验证时才弹出验证码窗口，输错后可输入
新验证码。SteamKit 使用 Steam 返回的 RSA 公钥加密密码后发送。由于 .NET 字符串不能可靠
原地擦除，程序只能及时释放引用，不能承诺敏感值绝不短暂残留于进程内存或崩溃转储。

## 固定目录与断点续传

建议使用 GUI 中的默认输出目录，并在以后运行时保持这个根目录不变。只选择根目录，不要进入
某个 SteamID 子文件夹，也不需要手动选择缓存文件。登录后程序会自动创建或识别：

```text
<固定输出根目录>\<自动识别的 ID32>\gc-match-history-cache.json
```

再次运行同一账号时，程序会自动加载缓存，先补最新比赛，再从最老 MatchID 继续向前扩展。
每 10 页以及正常结束或连接中断时都会保存缓存。多个账号可以共用同一个根目录，因为各账号
使用不同的 ID32 子目录。

例如缓存位于 `E:\Dota2MmrData\123456789\` 时，GUI 应选择 `E:\Dota2MmrData`，不要选择
末尾的 `123456789`。如果改选另一个根目录，旧缓存不会被发现。

## 命令行

```powershell
.\Dota2MmrReconstructor.exe `
  --account-id 123456789 `
  --history-matches 8000 `
  --output-dir .\artifacts
```

使用已有 GC 文件重新拟合，不连接 Steam：

```powershell
.\Dota2MmrReconstructor.exe `
  --reconstruct-existing .\artifacts\123456789\gc-collection.json `
  --output-dir .\artifacts\123456789\mmr-reconstruction
```

使用 `--raw-only` 可以只下载原始 JSON。原始文件与缓存永远独立保存，拟合只写入
`mmr-reconstruction` 子目录。

凭据登录仅在 GUI 提供，不支持通过命令行传入密码或 OTP，以免泄露到命令历史或进程列表。

曲线目录还会生成 `complete-mmr-curve.png` 静态大图，以及内容一致的
`hero-mmr-contribution.txt`、`.md` 和 `.xlsx`。英雄贡献按净 MMR 从高到低排列，GC
真实变化和低置信度端点拟合变化都会计入，并分列显示。Excel 版本包含冻结表头、自动筛选
和真正的数字单元格，可在 Excel/WPS 中继续排序和计算。

本包依赖 .NET 8 Desktop Runtime。请完整解压 ZIP 并保留附带的 DLL。

如果既有 `hero-mmr-contribution.xlsx` 正在被Excel等程序打开或无法覆盖，0.5.1起会保留
原文件，并将本次结果另存为带时间的 `.xlsx`。完成提示和 `reconstruction-manifest.json`
会标明新文件名，HTML生成和队友采集继续执行。

## 队友报告（0.5.4）

设置窗口默认勾选“生成队友报告”。保持原来的输出根目录即可：程序先生成MMR曲线，
再用同一次GC登录补充图表范围内的天梯详情，获取队友账号ID、英雄与KDA。首次补取较慢；
每场单独缓存，后续只补缺失详情。队友采集阶段按 Ctrl+C 可保存已有结果并结束，下次续跑。

“队友报告包含普通匹配”默认关闭。开启后，在原天梯范围之外，加入本次GC历史中常规普通匹配，
合并计算同队局数、胜率、KDA及综合表现，并补取相应详情。排除快速模式、技能征召、活动、自定义和人机。
MMR曲线始终只含天梯；普通匹配没有MMR分差，不计入真实或估算样本。上分／下分榜的共同天梯局数也需达到所选门槛。
开启采集后生成的HTML也有“包含普通匹配”开关，可离线切换仅天梯／天梯＋普通匹配；切换后
名单、最低局数滑块、概览和八张图一起更新。首次需要重新运行并开启采集开关，已有缓存会复用。

全量历史同阵营出现至少2场的账号进入候选名单，再按页面的时间范围、普通匹配开关和最低局数筛选。
按ID合并改名记录，同名不同ID分别统计。
输出在 `mmr-reconstruction` 下：

- `teammate-report.html`：天梯分数图、八张Top 10柱状图与概览；图上框选时间，再拖动“最低同队局数”筛选。
- `teammate-report.json`：统计结果、逐场曲线和队友样本、有效场数及数据缺失说明。
- `teammate-requests.json`：比赛范围、待补GC详情和STRATZ IMP比赛ID。

八项指标是同队胜率最低/最高、平均综合表现最低/最高、场均死亡最多、场均击杀+助攻最多、
一起净上分/净下分最多。每张图显示前10名，末位并列保留；不足10人时显示全部符合条件者。
图上拖动框选或调整选区两端，也可输入开始／结束日期（UTC，包含两端日期），点击“全部时间”重置。
滑块默认11局，可下调到2局。各项数值均在所选区间内重新计算，各榜相应数据的有效局数需达到
所选最低局数；缺失值不当作0。改变时间或比赛类型不会自动降低最低局数，无人达标时显示空榜。
桌面左侧四个好榜、右侧四个坏榜；手机先显示好榜，再显示坏榜。框选和筛选完全离线，不请求额外数据。
MMR使用本人共同比赛的逐场加减分，包含原模型估算、分列实际与拟合，排除校准跳变。
多人同局在每位队友下分别记录，概览按比赛去重。

平均综合表现使用STRATZ的IMP比赛评分，算法保持不变。STRATZ只请求包含至少2局候选队友的比赛，
复用已下载的评分；降低候选门槛后，新涉及的比赛可能需要补取评分。程序会尝试通过本次Steam会话完成
STRATZ OpenID登录；该网站流程可能要求额外操作。自动获取失败时，GUI会提示打开
[STRATZ API页面](https://stratz.com/api)，使用Steam登录，找到“我的 Tokens / My Tokens”
（有些页面显示“My Token”），复制Default Token中的整串字符。回到程序按Ctrl+V粘贴，
点击“继续获取评分”；也可点击“暂不获取评分”，先查看其余六项。
令牌使用Windows DPAPI按本机用户加密存储；Steam密码和会话令牌仍只保留在进程内。
限流、服务异常或缺评分时保留已有缓存和原MMR报告，缺失情况在报告中显示。

命令行添加 `--teammates` 启用此功能；使用 `--reconstruct-existing ... --teammates`
可完全离线重建，详情与IMP缓存从原 `gc-collection.json` 所在账号目录读取。
添加 `--include-normal-matches` 会启用队友报告并纳入常规普通匹配，离线时也只使用缓存。
基础缓存目录是 `gc-match-details`，评分缓存目录是 `stratz-imp`。无需OpenDota令牌。

关闭程序后，报告仍保存在 `<输出根目录>\<账号ID>\mmr-reconstruction\`：
双击 `teammate-report.html` 查看队友榜单，双击 `mmr-history.html` 查看MMR曲线。
