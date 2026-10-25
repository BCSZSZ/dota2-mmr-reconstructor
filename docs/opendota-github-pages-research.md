# OpenDota 全量练习赛采集与 GitHub Pages 方案依据

> 调查日期：2026-10-25（Asia/Tokyo）  
> 范围：只核对 OpenDota 与 GitHub 的官方文档、官方 API、官方仓库源码。本文记录可验证事实、约束和由这些事实推出的设计结论，不是实现代码。

## 结论摘要

1. `GET /players/{account_id}/matches` 是正确的玩家历史入口。要取练习赛，必须显式传 `lobby_type=1&significant=0`；只传 `lobby_type=1` 仍会受到默认 `significant=1` 的过滤。
2. 不传 `date`、`limit` 时，当前服务端实现会从在线存储和玩家归档合并后返回该玩家在 OpenDota 中**可访问、已关联**的全部历史；这不等于 Valve 意义上的绝对完整历史，也不保证每场练习赛都有完整比赛详情。
3. 用比赛详情中的非匿名 `account_id` 扩张种子、按 `match_id` 去重并迭代至不再发现新玩家，在工程上可行；但私密历史、匿名席位、OpenDota 未收录/未能生成详情的练习赛会形成不可消除的缺口。因此产品应称为“OpenDota 可见范围内的闭包”，不能宣称 100% 完整。
4. “匿名免费优先，免费日额度耗尽后才使用付费 key”可实现。当前 OpenDota 响应能区分分钟 429 与日额度 429；只有明确的日额度耗尽信号才应切换付费 key，分钟限流应等待后重试。
5. GitHub Pages 只能托管静态文件，不能安全地在浏览器里持有付费 key。正确边界是：GitHub Actions 服务端采集并产出静态 JSON，Pages 只读取 JSON、在浏览器本地做时间筛选和两张表的聚合。
6. GitHub Actions cache 和普通 workflow artifact 都不是长期事实库。建议把标准化数据快照提交到专用数据分支（或仓库内明确的 generated-data 目录），同一工作流随后构建并部署 Pages；artifact 仅用于部署交接和诊断。

## 1. OpenDota 数据面

### 1.1 玩家历史入口与过滤参数

OpenDota OpenAPI 将 `GET /players/{account_id}/matches` 描述为 “Matches played (full history, and supports column selection)”。接口支持 `limit`、`offset`、`lobby_type`、`date`、`significant`、`project` 等参数：

- [官方 API 文档：玩家比赛历史](https://docs.opendota.com/#tag/players/operation/get_players_by_account_id_matches)
- [官方 OpenAPI 源码：玩家历史路由与响应](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/spec.ts#L398-L430)
- [官方 OpenAPI 源码：`limit`、`offset`、`lobby_type`、`date`](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/requests/playerParams.ts#L32-L103)
- [官方 OpenAPI 源码：`significant` 默认值](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/requests/playerParams.ts#L167-L175)

练习房的常量是 `lobby_type=1`，其官方名称为 `lobby_type_practice`：[dotaconstants 官方常量](https://github.com/odota/dotaconstants/blob/5c0242df9057848fa2036ca3fafe9f2bd4508163/build/lobby_type.json#L6-L10)。

服务端会在未传 `significant=0` 时强制加入 `significant=1`；只有值严格为字符串 `"0"` 时才移除该过滤：[OpenDota 查询中间件](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/api.ts#L17-L24)。因此历史枚举请求的必要形态是：

`/players/{account_id}/matches?lobby_type=1&significant=0`

`date=N` 表示“从请求时刻往前 N×86400 秒”，实现使用严格大于 `now - N days`，不是 JST 自然日边界：[日期过滤实现](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/util/filter.ts#L55-L57)。因此：

- 首次回填若目标是“OpenDota 能给出的最大历史”，不要传 `date`。
- 每日增量可以传一个重叠回看窗口，但最终的任意日期范围应以 `start_time` 在本地按 JST 边界过滤，不能把 `date=N` 当作任意起止日期接口。

### 1.2 “全历史”、`limit`/`offset` 与分页边界

当前实现会读取玩家在线缓存和归档，合并、过滤，默认按 `match_id` 降序排列，随后才应用 `offset` 和 `limit`；没有 `limit` 时返回过滤后的全部结果：[玩家历史构建实现](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/util/buildPlayer.ts#L26-L94)。底层 Cassandra 读取本身启用了自动分页：[底层读取实现](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/util/buildPlayer.ts#L213-L247)。

由此可得：

- 对预计规模很小的内部对黑，省调用次数的首选是一次不带 `limit` 的历史请求。
- 若响应体过大才使用 `limit` + `offset`。官方接口没有返回总数或下一页 cursor；客户端只能在空页/短页时结束，并始终按 `match_id` 去重。
- offset 分页期间若新比赛进入列表，页边界可能移动，出现重复或遗漏；每日任务应允许重叠回扫并去重，不能把 offset 当作稳定快照游标。

“full history”只描述 OpenDota 当前可返回的已存数据。OpenDota 的当前 full-history 抓取器从 Valve 历史接口取 1–5 页、每页 100 场，代码明确写到最多 500 场；私密历史会得到 status 15：[full-history 抓取实现](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/fullhistory.ts#L19-L73)。所以不能把这个接口解释成“Valve 全生涯、无遗漏”的合同保证。

### 1.3 比赛详情与玩家发现

`GET /matches/{match_id}` 返回比赛详情：[官方 API 文档](https://docs.opendota.com/#tag/matches/operation/get_matches_by_match_id)、[官方路由源码](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/spec.ts#L217-L245)。响应包含：

- `lobby_type`、`radiant_win`、`start_time`；
- `players[]`；每个席位包含 `account_id` 与 `player_slot`。

对应官方 schema 见 [MatchResponse 的比赛字段](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/responses/MatchResponse.ts#L89-L180) 和 [玩家数组](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/responses/MatchResponse.ts#L232-L264)。`player_slot` 的 0–127 为 Radiant、128–255 为 Dire：[公共字段定义](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/responses/properties/commonProperties.ts#L16-L20)。这些字段足以计算每个人胜负以及任意两人同队时的共同胜率。

设计推论：玩家历史响应用于发现 `match_id`；完整详情用于发现这场比赛内所有可识别玩家。每个非空、正数 `account_id` 都可加入待扫描队列，双方玩家都应加入，而不只是当前玩家的同队者。

### 1.4 公开性、匿名席位与练习赛详情缺口

以下限制会影响闭包完整性：

1. **关闭公开比赛数据的玩家。** OpenDota 记录 `fh_unavailable`，并对非本人、非职业选手的玩家历史直接返回空数组：[隐私判断](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/api.ts#L42-L67)、[空结果实现](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/util/buildPlayer.ts#L30-L37)。API key 只是额度/限速凭据，不等于该玩家的 OpenDota 登录会话，不能解锁私密历史。
2. **匿名席位。** OpenDota 在标准化时会删除匿名账户 ID：[匿名 ID 转换](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/util/compute.ts#L467-L475)。没有 `account_id` 的席位无法成为新种子，也无法进入按人的矩阵。
3. **OpenDota 收录覆盖。** 官方 FAQ 说明公开比赛数据依赖 Dota 客户端的 “Expose Public Match Data”，高级数据还依赖 replay：[OpenDota 官方 FAQ](https://blog.opendota.com/2014/08/01/faq/)。
4. **练习赛详情尤其不能保证。** OpenDota 源码把 Valve 的 “Practice matches are not available via GetMatchDetails” 视为不可重试错误：[Steam API 错误处理](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/util/http.ts#L135-L154)；当前 API fetcher 还注明 Steam `GetMatchDetails` 已损坏而停用直接获取：[ApiFetcher](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/fetcher/ApiFetcher.ts#L20-L35)。OpenDota 自身可能已经通过其他采集/解析路径保存了详情，但公开 API 没有承诺任意玩家历史里的练习赛 ID 一定能在 `/matches/{id}` 得到完整 `players[]`。

`POST /request/{match_id}` 可以提交解析请求，且按限速计 10 次；但当前路由会先要求 OpenDota 已有该场的 API blob，否则返回 `400 match ID not found`，所以它不是任意缺失练习赛的可靠补洞手段：[解析请求官方 schema 与实现](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/spec.ts#L1568-L1656)。

因此最终数据必须记录覆盖诊断，至少包括：历史为空的玩家数、匿名席位数、详情 404/不完整的比赛 ID、首次/最后可见时间、最近成功刷新时间。页面可显示“OpenDota 可见范围”，不能显示“完整无遗漏”。

## 2. 免费额度、付费 key 与 429

### 2.1 当前公开数值与保证边界

截至调查日，OpenDota 官方生产 `GET /api/metadata` 返回：

| 项目 | 当前生产元数据 |
|---|---:|
| 无 key 日调用额度 | 3,000 |
| 无 key 分钟限速 | 60 |
| 有 key 分钟限速 | 3,000 |

直接来源：[OpenDota 生产 metadata](https://api.opendota.com/api/metadata)。官方网页从该 metadata 读取三项限制并展示，付费总调用上限显示为 Unlimited：[官方 API 页面组件](https://github.com/odota/web/blob/91b1d421e17f4740fc5028c303c3da0d1dd7c946/src/components/Api/Api.tsx#L238-L248)、[免费/付费对照表](https://github.com/odota/web/blob/91b1d421e17f4740fc5028c303c3da0d1dd7c946/src/components/Api/Api.tsx#L403-L445)。

付费价格在当前官方页面实现中是 `$0.01 / 100 calls`，需要绑定支付方式；404、429、500 不计费：[官方页面组件](https://github.com/odota/web/blob/91b1d421e17f4740fc5028c303c3da0d1dd7c946/src/components/Api/Api.tsx#L403-L461)、[官方英文文案](https://github.com/odota/web/blob/91b1d421e17f4740fc5028c303c3da0d1dd7c946/src/lang/en-US.json#L28-L49)。服务端计费实现也排除 404、429、500：[计费源码](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/web.ts#L73-L117)。

这些数值应视为“当前生产发布值”，不是不可变 SLA：core 仓库默认配置目前是免费 3,000/日、60/分钟、key 300/分钟，但部署时 `process.env` 会覆盖默认值；生产 metadata 的 key 限速实际上是 3,000/分钟。[默认配置](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/config.ts#L59-L63)、[环境变量覆盖](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/config.ts#L107-L111)。因此实现应在运行时读取 metadata/响应头，不能只硬编码仓库默认值。

官方 OpenAPI 允许无 key 使用；key 可提高限额，并可通过 query 参数或 `Authorization: Bearer ...` 发送：[OpenAPI 安全方案](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/api/spec.ts#L180-L203)。为避免 key 出现在 URL、代理日志和错误信息中，应选 Bearer header。

### 2.2 当前 429 行为

当前中间件以“API key”作为有 key 请求的计数身份，以“请求 IP”作为无 key请求的计数身份；响应设置 `X-Rate-Limit-Remaining-Minute`，无 key 时另设 `X-Rate-Limit-Remaining-Day`：[限流实现](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/web.ts#L413-L498)。

| 场景 | HTTP | 当前 JSON 错误 | 正确动作 |
|---|---:|---|---|
| 分钟额度超过 | 429 | `minute rate limit exceeded` | 等待下一个分钟窗口并加抖动后重试；不能据此判断免费日额度已用完 |
| 无 key 日额度超过 | 429 | `daily api limit exceeded` | 若配置了付费 key，才以 key 重试同一请求；否则保存断点并结束 |

对应源码：[429 分支](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/web.ts#L488-L507)。分钟 key 在下一分钟边界过期，免费日 key 在 UTC 当日结束时过期：[时间窗口实现](https://github.com/odota/core/blob/86e09304d7775540fd5e22e9a94d930034c4da09/svc/util/time.ts#L1-L25)。当前源码没有为这两类 429 设置 `Retry-After`。

无 key 的额度是 OpenDota 所见来源 IP 的额度，不是某个 GitHub 仓库的专属额度。GitHub-hosted runner 的出口地址与运行实例并非由 OpenDota 保证独占，所以不能预先假定每次运行都拥有完整 3,000 次余额；可靠依据只能是本次响应头和 429 正文。

### 2.3 “免费优先、付费兜底”的可靠判定

建议状态机：

1. 每个普通 GET 第一次都不带 key。
2. 2xx 时记录两个 remaining header；header 缺失不能自行推断余额。
3. 遇到分钟 429，匿名等待到下一分钟边界后重试，并使用低于 60/min 的保守节流和随机抖动。
4. 只有收到 `daily api limit exceeded`，或无 key 响应明确给出日剩余量小于等于 0，才将**尚未完成的队列**切到付费 key。
5. 429 正文未知、缺少可辨识信号时，先退避并再次匿名尝试；仍无法分类则失败并保留断点。这样才符合“额度不足才付费”，而不是把服务故障或分钟突发误判为付费条件。
6. key 模式仍要服从每分钟限制；5xx、网络错误、404/数据缺失都不能通过换 key 解决。
7. 增加每次运行的付费调用硬上限和计数清单。付费 key 的总量在页面上标为 Unlimited，因此如果闭包意外扩大，没有本地上限就不存在账单止损边界。

当前实现的错误字符串和响应头虽可作为可靠运行信号，但 OpenAPI 没把它们声明成稳定响应 schema。应把无法分类的 429 当作显式失败，而不是默认付费；同时在采集日志中记录匿名/付费调用数及切换原因。

## 3. 采集闭包的设计推论

### 3.1 首次回填

建议把 12 个初始账号和后续发现账号统一放入持久化玩家注册表，并维护 `pending/scanned/partial` 状态：

1. 取一个尚未扫描玩家的全部可见练习赛历史：`lobby_type=1&significant=0`，首次不传 `date`。
2. 将历史中的 `match_id` 写入集合，天然去重；只对尚无详情的 ID 请求 `/matches/{id}`。
3. 再次验证详情的 `lobby_type===1`，防止脏数据进入统计。
4. 从详情 `players[]` 中加入所有非匿名 `account_id`；新玩家进入队尾。
5. 队列为空时达到本次固定点。私密/空历史标记为 `partial`，但不能无限重试。

即使预期只是小圈子，也应设置 `maxPlayers`、`maxMatches`、`maxApiCalls`、`maxPaidCalls` 四个保险阀。任何一个触发都要保存断点和“未完成”状态，而不是发布一个看起来完整的结果。

### 3.2 每日更新

已知玩家规模很小时，每天重新查询所有已知玩家的练习赛历史最简单、也最能弥补 OpenDota 后补数据。为降低响应体，可在完成首次回填后对已扫描玩家使用带重叠的 `date` 窗口；对当天新发现的玩家仍做无 `date` 的完整回填。所有新 `match_id` 获取详情，新玩家继续扩张队列，直到再次达到固定点。

推荐持久化三层数据，而不是只保存两张汇总表：

- `players`：账号 ID、展示名、首次/最近发现、扫描状态；
- `matches`：`match_id`、`start_time`、`radiant_win`、双方可识别参与者、详情状态；
- `collection_metadata`：刷新时间、覆盖区间、匿名/私密/缺失诊断、匿名与付费调用计数。

网页按任意时间范围重算：

- 玩家表：胜、负、总场数、胜率；
- 对称二维矩阵：两人同队场数与同队胜率，格子格式 `3/66.7%`，无共同场留空，对角线用 `—`。

时间范围建议统一定义为 JST 日期的半开区间 `[起始日 00:00, 结束日次日 00:00)`，这样双滑块、手工日期输入与“过去 3 天 / 7 天 / 1 个月 / 全部”不会产生边界差异。筛选完全在浏览器本地完成，不再调用 OpenDota。

## 4. GitHub Actions 约束与调度

### 4.1 首次部署、每日调度和手动运行

- 首次部署用 `workflow_dispatch`，它可从 Actions 页面、CLI 或 REST API 手动启动；工作流文件必须存在于默认分支，网页手动运行者需要仓库 write 权限：[GitHub 官方手动运行文档](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow)。
- 每日 JST 10:00 等价于 UTC 01:00。可写 UTC cron `0 1 * * *`；当前 GitHub 也支持 IANA timezone，因此可表达为 10:00 + `Asia/Tokyo`：[workflow schedule 语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#onschedule)。
- schedule 只运行默认分支上最新提交中的工作流。高负载时可能延迟，整点尤其拥挤，严重时队列任务可能被丢弃；公开仓库连续 60 天无活动还会自动禁用 schedule：[schedule 事件规则](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule)。所以“每天 10:00”是计划触发时间，不是准点 SLA。若业务允许，10:07 之类非整点能降低延迟概率；若必须 10:00，则保留整点并接受该约束。

### 4.2 并发

手动运行可能与定时运行重叠，而两者都会写同一份数据。必须使用同一个 workflow-level concurrency group。GitHub 默认的 concurrency 最多保留一个 running 和一个 pending；更新的 pending 会替换旧 pending。当前文档支持 `queue: max` 以排队多个等待任务，最多 100 个，并按开始等待时间 FIFO（实际启动先后仍不保证）：[GitHub concurrency 文档](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency)。

设计选择：采集/提交/部署共用一个 concurrency group，禁用运行中取消；若使用当前支持的排队能力则选 `queue: max`。这样不会在半写入状态中止正在进行的闭包。

### 4.3 最小权限

GitHub 明确建议通过 `permissions` 缩小 `GITHUB_TOKEN` 权限；一旦列出某些权限，未列出的权限会变成 `none`：[workflow permissions](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#permissions)。建议按 job 分离：

| job | 最小相关权限 |
|---|---|
| 采集但不提交 | `contents: read` |
| 将数据快照提交回仓库 | `contents: write` |
| Pages 部署 | `contents: read`, `pages: write`, `id-token: write` |

Pages 官方部署要求 `pages: write`、`id-token: write`、`environment: github-pages`，部署 job 通过 `needs` 等待 build job：[Pages 自定义 Actions 工作流](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages)。

### 4.4 cache、artifact 与提交数据

| 机制 | 官方定位/限制 | 是否适合做唯一事实库 |
|---|---|---|
| Actions cache | 用于依赖和可重新生成的中间结果；未访问的 cache 默认 7 天删除，容量压力会淘汰旧 cache | 否 |
| 普通 workflow artifact | 用于保存运行产物或 job 间传递；默认保留 90 天，可按仓库策略调整 | 否 |
| Git 提交的数据快照 | 有版本历史，可供下一次运行确定性读取 | 是，推荐 |
| Pages deployment artifact | 官方 Pages build→deploy 的交接物；单个压缩包小于 10GB且不能含软/硬链接 | 只用于部署，不作为采集数据库 |

来源：[GitHub cache 定位与安全](https://docs.github.com/en/actions/concepts/workflows-and-actions/dependency-caching)、[cache 淘汰规则](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching#usage-limits-and-eviction-policy)、[artifact 保留规则](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-github-actions-settings-for-a-repository#configuring-the-retention-period-for-github-actions-artifacts-and-logs-in-your-repository)、[Pages artifact 要求](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages#configuring-the-upload-pages-artifact-action)。

推荐将规范化 JSON 提交到专用 `data` 分支，避免每天污染源码分支历史；也可以放在默认分支的 generated-data 目录，换取更简单的流程。无论选哪种，工作流都应在**同一次运行**中从刚生成的数据构建并部署 Pages。原因是使用 `GITHUB_TOKEN` 推送的普通事件不会再触发新的 workflow run：[GitHub 防递归规则](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow#triggering-a-workflow-from-a-workflow)。分支发布方式也明确说明，Actions 用 `GITHUB_TOKEN` 推送的提交不会触发 Pages build：[创建 Pages 站点](https://docs.github.com/en/pages/getting-started-with-github-pages/creating-a-github-pages-site#creating-your-site)。

## 5. GitHub Pages 边界

GitHub Pages 是 HTML/CSS/JavaScript 的静态托管服务：[GitHub Pages 定义](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages)。它不支持 PHP、Ruby、Python 等服务端语言：[创建 Pages 站点](https://docs.github.com/en/pages/getting-started-with-github-pages/creating-a-github-pages-site#static-site-generators)。因此：

- 浏览器只下载静态 JSON 并计算筛选结果；OpenDota 抓取只能放在 Actions。
- 付费 key 绝不能打进 JavaScript bundle、HTML 或 JSON，也不能由浏览器直接调用带 key 的 OpenDota URL。
- 项目站点通常位于 `/<repository>/` 子路径，前端资源和 `fetch` URL 必须使用正确的 base path，不能假设根路径 `/`。
- 官方限制包括已发布站点不超过 1GB、部署超过 10 分钟会超时、月带宽软限制 100GB：[GitHub Pages limits](https://docs.github.com/en/pages/getting-started-with-github-pages/github-pages-limits)。本项目的小规模 JSON 远低于这些限制，但应只发布归一化后的必要字段，而非完整巨型 OpenDota 响应。
- Pages 默认在公网可访问；即使源仓库是私有仓库，普通计划下站点也可能公开。私有 Pages 访问控制需要 GitHub Enterprise Cloud：[Pages 可见性](https://docs.github.com/en/enterprise-cloud@latest/pages/getting-started-with-github-pages/changing-the-visibility-of-your-github-pages-site)。账号 ID、显示名、比赛关系矩阵应在部署前确认可以公开。

部署使用官方推荐链路：`configure-pages` → `upload-pages-artifact` → `deploy-pages`；具体当前主版本和权限应以部署时的官方文档为准：[Pages 自定义工作流](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages)。

## 6. API key 与 `.env` 安全

### 6.1 本地

- 本地只创建未跟踪的 `.env`，变量名建议 `OPENDOTA_API_KEY`。
- 仓库提交 `.env.example`，只含空值和说明；`.gitignore` 必须覆盖真实 `.env` 及可能的本地变体。
- collector 只从进程环境读取 key，并优先用 `Authorization: Bearer`。不要把 key 写入 URL、日志、异常、快照、测试 fixture 或 Pages 数据。
- 不要使用会被前端构建器自动暴露的客户端环境变量前缀；key 只注入采集进程，Pages build job 不需要它。

### 6.2 GitHub Actions

- 在仓库或受保护 environment 中创建 Actions secret `OPENDOTA_API_KEY`，只注入采集 step 的环境。
- GitHub secret 只有在 workflow 明确引用时才能被 job 读取；日志会自动遮罩已登记 secret，但 GitHub 也明确说明遮罩并非对所有变形值绝对保证，因此代码仍不得打印它：[GitHub Secrets 概念](https://docs.github.com/en/actions/concepts/security/secrets)。
- 除 `GITHUB_TOKEN` 外，secrets 不会传给来自 fork 的工作流；secret 未设置时表达式返回空字符串：[在 Actions 中使用 secrets](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets)。采集工作流不应由不受信任的 `pull_request` 触发；schedule 与具有 write 权限者的 `workflow_dispatch` 足够。
- key 缺失时必须明确进入“仅免费模式”，不是把空字符串附加到请求；免费额度耗尽后保存断点并使运行以可诊断状态结束。

## 7. 推荐的最终架构

```text
12 个初始账号 + 持久化发现账号
              │
              ▼
GitHub Actions collector（匿名 GET 优先；仅日额度耗尽时用 secret key）
              │
              ├── 玩家练习赛历史：lobby_type=1, significant=0
              ├── match_id 去重、详情补全、非匿名玩家扩张
              └── 覆盖诊断、断点、调用/付费计数
              │
              ▼
规范化数据快照（Git 持久化）
              │
              ▼
静态站点 build → Pages artifact → GitHub Pages
              │
              ▼
浏览器本地时间筛选 → 玩家胜负表 + “共同场数/胜率”二维矩阵
```

这个拆分把付费凭据和网络采集留在可信执行环境，把任意时间区间的交互留在静态前端；同时允许首次长回填断点续跑、每日幂等刷新和数据来源审计。

## 8. 实施前必须明确的产品决定

1. 公开性：是否接受账号 ID、显示名和组队关系在 Pages 公网可见。
2. 付费止损：`maxPaidCallsPerRun` 的明确数值；未设定前不应启用自动付费兜底。
3. 覆盖文案：接受“OpenDota 可见范围内的数据”，并在页面显示缺失详情/匿名/私密数量。
4. 数据持久化位置：专用 `data` 分支（历史更干净）或默认分支 generated-data 目录（实现更简单）。
5. 调度精度：严格 10:00 JST，还是允许改为 10:07 以降低 GitHub 整点拥堵概率。

