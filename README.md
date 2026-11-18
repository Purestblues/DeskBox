# DeskBox

轻量级、零推广、多显示器友好的 Windows 桌面格子收纳工具。

1:1 复刻桌面整理核心收纳体验，剔除云搜索、AI 助手、壁纸推销等冗余模块；基于 C# (.NET 10) + WPF + Win32 P/Invoke 构建，单进程运行，常驻内存低于 40MB。

## 核心特性

- **非破坏性收纳**：绝不将用户桌面文件/文件夹搬离原桌面目录，保证资源管理器与第三方应用工作目录 100% 不受影响；支持受控隐藏已入格的原生桌面图标，并在移出、解散、退出或卸载时无损恢复位置与可见性。
- **多显示器与高分屏友好**：基于硬件显示器 ID + 分辨率 + DPI 自动生成拓扑签名（`LayoutSnapshot`），插拔副屏或切换分辨率时自动切换与还原专属格子布局，内置防越界纠偏。
- **精致视觉与交互**：低内存壁纸下采样高斯模糊磨砂背景、自定义配色与透明度、双击标题栏折叠 / 悬停自动展开、网格 / 列表双视图、多维度排序、原生 Shell 右键菜单与拖放支持。
- **纯净无扰**：零联网、零遥测、零广告推广，所有配置仅保存在本地 `%APPDATA%\DeskBox\config.json`。
- **单文件安装与标准卸载**：自带安装/卸载向导（`DeskBox.Setup`），支持写入 Windows 标准卸载项、开机自启、桌面右键菜单集成与干净卸载。

## 快速开始

### 方式一：直接安装（推荐）

前往 [Releases](../../releases) 页面下载 `DeskBox-Setup-1.0.0-win-x64.exe` 直接运行安装。

### 方式二：从源码运行与打包

环境要求：Windows 10 / 11 + [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```powershell
# 运行单元测试
dotnet test

# 启动主程序
dotnet run --project src/DeskBox/DeskBox.csproj

# 一键测试并构建单文件安装程序（产物输出至 dist/）
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1

# 如需包含 .NET 运行时的独立版本，可附加 -SelfContained 参数
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -SelfContained
```

## 项目结构

```text
DeskBox/
├─ DeskBox.slnx                 # 解决方案文件
├─ build-installer.ps1          # 一键测试与打包安装程序脚本（输出至 dist/）
├─ src/
│  ├─ DeskBox/                  # WPF + Win32 主程序（格子窗口、拓扑监听、模糊缓存、Shell 集成等）
│  └─ DeskBox.Setup/            # 单文件安装与标准卸载向导
└─ tests/
   └─ DeskBox.Tests/            # 核心逻辑单元测试（多显示器拓扑匹配、布局防越界、配置序列化等）
```

## 开源协议

本项目基于 [MIT License](LICENSE) 开源。
