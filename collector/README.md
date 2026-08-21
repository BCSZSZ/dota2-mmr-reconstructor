# Dota 2 MMR Reconstructor for Windows

`Dota2MmrReconstructor.exe` 通过 Steam QR 登录本人账号，下载原始 Dota 2 GC Match
History 与 Current Rank，并用内置 C# 模型生成完整 MMR 曲线和独立 HTML。

## 双击运行

1. 完全退出 Dota 2；
2. 双击 EXE；
3. 在设置窗口输入目标场数、可选 Steam ID 和输出目录；
4. 扫描弹出的 Steam 二维码；
5. 完成后打开输出目录，双击 `mmr-reconstruction\mmr-history.html`。

Steam ID 可以留空自动识别，也可填写 ID32 或 SteamID64 防止扫错账号。默认扫描
5,000 条 GC 历史，建议单次目标不超过 10,000。GC 请求不能在服务端只筛天梯，因此目标数
包含普通局；下载后模型才保留 `lobby_type = 7` 的天梯局。

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

本包依赖 .NET 8 Desktop Runtime。请完整解压 ZIP 并保留附带的 DLL。
