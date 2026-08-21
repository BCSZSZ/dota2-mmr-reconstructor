# Windows 上安装并使用 uv（可选参考实现）

正式发布的 `Dota2MmrReconstructor.exe` 已经内置 C# 模型，普通用户不需要 Python 或
uv。本手顺只面向希望运行 Python 参考实现、复核模型或参与开发的人。

以下命令在 Windows 10/11 的 PowerShell 中执行。uv 本身不依赖预先安装的 Python，且可
自动下载项目需要的 Python。安装方式和 Python 管理行为以
[Astral 官方安装文档](https://docs.astral.sh/uv/getting-started/installation/)和
[官方 Python 管理指南](https://docs.astral.sh/uv/guides/install-python/)为准。

## 1. 打开 PowerShell

在开始菜单中打开“终端”或“Windows PowerShell”。不需要以管理员身份运行。

## 2. 安装 uv

推荐使用 Windows 自带的 WinGet：

```powershell
winget install --id=astral-sh.uv -e
```

如果系统没有 WinGet，可以使用官方 PowerShell 安装器：

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
```

安装结束后关闭终端并重新打开，然后验证：

```powershell
uv --version
```

如果仍提示找不到命令，先注销并重新登录 Windows，再运行 `Get-Command uv`。仍未找到时，
重新执行 WinGet 或官方安装器；不要在来源不明的网站下载同名程序。

## 3. 取得项目源码

有 Git 时：

```powershell
git clone https://github.com/BCSZSZ/dota2-mmr-reconstructor.git
Set-Location .\dota2-mmr-reconstructor
```

没有 Git 时，在 GitHub 项目页面选择 `Code` → `Download ZIP`，完整解压，然后在资源管理器
地址栏输入 `powershell` 并回车。确认当前目录包含 `pyproject.toml` 和 `uv.lock`。

## 4. 安装项目所需 Python 和依赖

项目固定使用 Python 3.12：

```powershell
uv python install 3.12
uv sync --frozen
```

`uv sync` 会在项目目录创建隔离的 `.venv`，不会向系统 Python 混装依赖。正常使用
`uv run` 时不需要手动激活虚拟环境。

## 5. 运行参考模型

如果 C# EXE 已经生成原始文件：

```powershell
uv run dota2-mmr-reconstruct 123456789 --no-collect
```

默认从 `artifacts\123456789\gc-collection.json` 读取，并把模型结果写入同一账号目录下的
`mmr-reconstruction`。

也可以让 Python 工作流调用已经构建好的 C# 下载器：

```powershell
uv run dota2-mmr-reconstruct 123456789 --history-matches 8000
```

## 6. 运行验证

```powershell
uv run pytest
uv run ruff check .
```

## 常见问题

### `uv` 不是可识别的命令

关闭所有终端窗口后重新打开，必要时注销并重新登录 Windows。仍失败时重新运行 WinGet 或
官方安装器，并用 `Get-Command uv` 检查命令是否进入 PATH。

### 没有安装 Python

这是允许的。`uv python install 3.12` 会安装由 uv 管理的 Python；官方文档也说明 uv 在缺少
合适解释器时可以按需自动下载。

### PowerShell 阻止脚本执行

使用 WinGet 安装通常不会遇到这个问题。不要为了本项目永久放宽整机执行策略；官方安装器
命令中的 `-ExecutionPolicy ByPass` 只作用于那一次 PowerShell 进程。

### 想清空环境重新安装

删除项目内的 `.venv` 后重新执行即可：

```powershell
Remove-Item -LiteralPath .\.venv -Recurse -Force
uv sync --frozen
```

只删除确认位于项目目录中的 `.venv`，不要对不确定的路径执行递归删除。
