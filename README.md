# 桌面清单 · DeskTodo

**透明底面，狂野打字**

[![build](https://github.com/wantieacid/desk-todo/actions/workflows/build.yml/badge.svg)](https://github.com/wantieacid/desk-todo/actions/workflows/build.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

一个贴在桌面上的半透明待办清单。底面是真透明——DWM 亚克力 + 圆角，桌面透过它看得见；打字是真狂野——每敲一个字都飞出来，回车落进清单时卡片砸一下、砸出一圈冲击波。双击就能用，不装任何东西。

Windows 上的待办小工具通常要么躲进托盘、要么一按就消失；这个清单一直贴在桌面上，透明底、圆角、不挡视线，抬头就能看见今天还剩什么。

## 功能

- **置顶半透明**：DWM 亚克力背景 + 圆角，始终在最前，可拖边缘改大小、拖标题区挪位置，位置和大小自动记住。
- **回车即加**：底部输入行打字，回车落进清单。中文输入法的候选框跟着光标走。
- **打勾**：点一下左边的方框即完成，文字划线、褪成灰色；长按方框会把方框从底往上灌满。
- **删除**：长按右边的 ✕，文字被扫除、整行移除。
- **拖动排序**：长按一行中间的空白或文字拖动换顺序，被拖的行会浮起，原位置有一条亮线指示落点，拖到边缘自动滚动。
- **主次分明**：排第一的字最大、带颜色，其余是小号灰字，当备注用。
- **开机自启**：写 HKCU 的 Run 项；程序被挪位置后会自动修正路径，但用户在任务管理器里禁用后不会偷偷加回来。
- **数据可带走**：清单与窗口位置存在 `%APPDATA%\桌面清单\todo.json`，纯 JSON，拷走就能接着用。
- **对截图/录屏不可见**：`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`，属于功能特性。
- 单实例运行；崩溃不弹窗，写 `%APPDATA%\桌面清单\crash.log`。

## 下载

到 [Releases](https://github.com/wantieacid/desk-todo/releases) 下载，两个版本挑一个：

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `DeskTodo-1.1.0-win-x64.exe` | 约 63 MB | 自包含单文件，不装 .NET 也能直接跑，发给别人用这个 |
| `DeskTodo-1.1.0-win-x64-lite.exe` | 约 0.2 MB | 需先装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（x64） |

两个是同一个程序，数据、位置、行为完全一样，不要同时开。下载后可以随便改名，比如改成 `桌面清单.exe`。

## 使用

- 在底部输入行打字，回车加一条；`Esc` 清空输入，拖动中按 `Esc` 取消拖动。
- 只有方框能打勾，点其他位置不会误触。
- 滚轮滚动列表。
- 右上角的 ✕ 是退出。

更细的说明见 [docs/USAGE.md](docs/USAGE.md)。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：

```powershell
git clone https://github.com/wantieacid/desk-todo.git
cd desk-todo
dotnet run
```

一键出正式包（自包含单文件 + 轻量单文件 + 调试版，输出到 `dist/`）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

项目只有一个 WinForms 工程，无第三方 NuGet 依赖，全部绘制走 GDI+。主要文件：

```
Program.cs     入口、单实例、崩溃日志、开机自启路径修正、todo.json 读写
MainForm.cs    窗口本体：亚克力背景、行绘制、动效、输入法、拖动排序
DeskTodo.csproj
app.ico
```

## 已知限制

- 只支持 Windows 10 1809+ / Windows 11，且自包含版只打了 x64；ARM64 需要自行 `dotnet publish -r win-arm64`。
- 界面按中文排版设计，其他语言的字体回退没有专门处理。
- 「对截图不可见」会让录屏教程里看不到它，需要录制时请用调试版（`TDL_DEBUGUI` 编译，不启用抓屏排除）。

## 许可

[MIT](LICENSE)
