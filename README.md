# Dota 2 MMR Reconstructor

从本人 Steam/Dota 2 Game Coordinator 下载原始天梯历史，并重建 Rank Confidence 低于
30% 时被 GC 隐藏的逐局 MMR 曲线。

项目由三个相互独立的部分组成：

1. `Dota2MmrCollector.exe`：只负责二维码登录、原始 Match History 和 Current Rank 下载；
2. Python 模型：识别低 Confidence 缺口，以前后真实 MMR 为端点进行约束拟合；
3. `tools/mmr-table-viewer.html`：导入模型结果，交互查看曲线和逐局表格。

> [!IMPORTANT]
> Valve 没有公开完整的服务器 MMR/Rank Confidence 更新公式。低 Confidence 区间中的逐局
> 变化是模型估计，不是 Valve 的真实逐局结算；可见 GC 行和每个区间的真实端点不会被模型
> 覆盖。

## Windows 快速开始

要求：Windows 10/11、[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
以及 Python 3.12+。Python 环境推荐使用 [uv](https://docs.astral.sh/uv/)。

### 1. 构建或下载 Collector

Release 中的 `Dota2MmrCollector-win-x64.zip` 是小体积、依赖 .NET 8 的版本。源码构建：

```powershell
.\scripts\build_collector.ps1
```

生成：

```text
dist/Dota2MmrCollector-win-x64/
dist/Dota2MmrCollector-win-x64.zip
```

Collector 不包含 Python 模型或曲线查看器。双击 EXE 默认下载 5,000 行，并把原始 JSON 与
断点缓存写在 EXE 所在目录。正式流程建议显式传入账号与输出路径：

```powershell
.\dist\Dota2MmrCollector-win-x64\Dota2MmrCollector.exe `
  --account-id 123456789 `
  --history-matches 8000 `
  --output artifacts\123456789\gc-collection.json `
  --history-cache artifacts\123456789\gc-match-history-cache.json
```

运行前必须完全退出 Dota 2；一个账号只能保持一个 Dota GC 会话。二维码会在独立窗口中
刷新，登录令牌只保留在内存中。`--account-id` 使用 ID32，用于防止扫错账号。

Match History 每页 20 场、约每秒请求一页。缓存会同时补齐最新比赛和最老断点，所以重复
运行不会把已有 8,000 场全部重抓。请避免一次请求超过约 10,000 场，以降低触发 GC
限流的风险。

### 2. 运行低 Confidence 模型

```powershell
uv sync
uv run dota2-mmr-reconstruct 123456789 --no-collect
```

也可以让统一命令先调用已构建的 Collector，再运行模型：

```powershell
uv run dota2-mmr-reconstruct 123456789 --history-matches 8000
```

主要输出：

```text
artifacts/<account_id>/gc-collection.json
artifacts/<account_id>/gc-match-history-cache.json
artifacts/<account_id>/mmr-reconstruction/model-summary.json
artifacts/<account_id>/mmr-reconstruction/match-estimates.csv
artifacts/<account_id>/mmr-reconstruction/complete-mmr-curve.csv
artifacts/<account_id>/mmr-reconstruction/complete-mmr-curve.svg
artifacts/<account_id>/mmr-reconstruction/mmr-dataset.json
artifacts/<account_id>/reconstruction-manifest.json
```

原始 `gc-collection.json` 和 cache 只由 Collector 写入；模型输出始终进入单独的
`mmr-reconstruction/` 目录。

### 3. 查看交互曲线

```powershell
Start-Process .\tools\mmr-table-viewer.html
```

拖入 `mmr-dataset.json`、`match-estimates.csv` 或 `complete-mmr-curve.csv`：

- 蓝色实线是 GC 真实分数，橙色虚线是模型拟合；
- 滚轮缩放时间轴，`Ctrl + 滚轮`缩放 MMR，按住拖动平移；
- 双击恢复全图，支持悬停 Match 详情和图表全屏；
- 筛选条件同时作用于图和虚拟滚动表格；
- 可导出筛选 CSV，或生成内嵌数据的独立 HTML。

全部处理都在本地浏览器进行，不上传数据。

## 当前模型

生产模型标识为 `endpoint-constrained-glicko-dd-v2`。

- 2020-03-02 到 2023-04-19：只绘制单一 MMR 轨道中 GC 返回的真实分数；
- 2023-04-20 之后：`previous_rank/rank_change` 缺失的天梯局被视为待拟合区间；
- 当前 Rank 的 `rank_value` 作为最后一个可用硬端点；
- Python 根据原始 `rank_data1/rank_data3` 复现已研究客户端版本的闲置 uncertainty
  投影与显示 Confidence；Collector 本身不解释这些字段；
- 单局先验随 uncertainty 增大而放大，并使用可见历史拟合 Double Down 混合概率；
- 每个隐藏区间通过有界端点约束分配整数 delta，严格命中下一真实 MMR。

区间端点严格命中只说明累计变化满足观测，不代表区间内每一局都被准确恢复。详细假设、
限制和验证计划见：

- `docs/model.md`
- `docs/gc-data.md`

## 开发

```powershell
uv sync
uv run pytest
uv run ruff check .
dotnet build .\collector\Dota2MmrCollector.csproj -c Release
```

CI 会同时验证 Python 模型和 Windows Collector；推送 `v*` 标签会生成 Collector Release
压缩包。

## 隐私与安全

- `.env`、`artifacts/`、`outputs/`、缓存和构建目录默认不会进入 Git；
- Collector 不保存密码或 Steam refresh token；
- GC 私有 MMR 数据只能由对应账号本人扫码取得；
- 发布前仍应自行检查数据目录，避免提交玩家历史。

## References 与许可证

GC 采集流程参考了 ShowMMR；完整说明、固定 revision 和其他协议/论文来源见
[`REFERENCES.md`](REFERENCES.md)。Collector 的依赖声明见
[`collector/THIRD_PARTY_NOTICES.md`](collector/THIRD_PARTY_NOTICES.md)。

本项目自有代码使用 [MIT License](LICENSE)。SteamKit2 等第三方组件遵循各自许可证。
