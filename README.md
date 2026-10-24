# Mio

Windows 网络工具箱：TCP/UDP 端口转发、IPv4 静态路由与防火墙规则管理。

当前发行版本：**2.0.1**。

[下载 2.0.1](https://github.com/LeveinOvO/mio/releases/tag/v2.0.1) · [反馈问题](https://github.com/LeveinOvO/mio/issues)

## 安装与使用

下载发行附件 `Mio-Setup-2.0.1.exe`，核对同目录 SHA-256 后运行安装器。

## 功能

- IPv4 TCP/UDP 转发，目标支持 IPv4 或域名；使用 Realm 转发核心。
- 原生 C++ 宿主常驻托盘，WinUI 配置界面按需打开。
- DNS 定期检查，本机转发回环检测，监听冲突检测。
- 防火墙规则限定监听端口、协议、来源 CIDR 和网络配置文件。
- IPv4 路由增删改、临时/持久路由、静态项优先排序和筛选。
- 根据下一跳自动匹配接口；多接口歧义、直连或特殊网络可手动指定。

## 系统要求与验证范围

- Windows x64 桌面环境；安装器要求 build 19044 或更新。
- Windows PowerShell 5.1 x64、NetTCPIP 模块和对应 CIM 提供程序。
- Microsoft Visual C++ x64 运行库（Realm 依赖）。
- Windows 防火墙 BFE/MpsSvc 服务运行，相关系统策略允许本地管理。

## 数据与卸载

- 工作空间为安装目录 `data`，数据库为 `data/Mio.db`，日志为 `data/logs`。
- 卸载默认保留数据，可勾选删除整个安装目录。
- 可单独选择删除 Mio 创建且有归属记录的系统路由。

## 源码结构

```text
Mio.csproj           WinUI 主程序
Core/                模型与校验
Windows/             宿主通信及 Windows 适配
NativeHost/          C++ 宿主、SQLite、Realm 生命周期、路由模块
Installer/           NSIS 安装器
Tests/               回归测试
vendor/              固定版本的 Realm 及现有许可文件
VERSION              统一发行版本来源
```

## 构建

构建环境：Windows .NET SDK（版本见 `global.json`）、Debian/WSL、Python 3.11+、llvm-mingw、NSIS 3.11。
先设置 `LLVM_MINGW` 为解压后的工具链绝对路径，然后在 WSL 项目根目录运行：

```bash
export LLVM_MINGW=/path/to/llvm-mingw
bash scripts/build-release.sh
python3 Installer/build.py
```

`DOTNET_EXE` 可指定 Windows dotnet.exe 路径；默认使用 `/mnt/c/Program Files/dotnet/dotnet.exe`。
构建输出：`artifacts/build/V2.0.1/`；安装包：`artifacts/releases/V2.0.1/`。

默认回归测试：

```powershell
dotnet run --project Tests/Mio.Tests.csproj
```
