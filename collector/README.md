# Dota 2 MMR Reconstructor for Windows

`Dota2MmrReconstructor.exe` 通过 Steam QR，或一次性用户名/密码与 Steam Guard 验证码
登录本人账号，下载原始 Dota 2 GC Match History 与 Current Rank，并用内置 C# 模型生成
完整 MMR 曲线和独立 HTML。

## 双击运行

1. 完全退出 Dota 2；
2. 双击 EXE；
3. 在设置窗口输入目标场数、可选 Steam ID 和输出目录；
4. 使用默认二维码登录；如果手机没有扫码功能，可选择用户名/密码模式，并在程序随后弹出的
   窗口输入当前五位 Steam Guard 验证码；
5. 完成后打开输出目录：直接看 PNG、TXT、Markdown、Excel，或双击
   `mmr-reconstruction\mmr-history.html`。

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
