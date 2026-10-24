# Mio 原生宿主

C++20 / Win32 托盘宿主，管理 Realm 子进程、SQLite 配置、DNS、防火墙和命名管道。
界面关闭后可保留宿主。用户数据位于程序目录的 `data/Mio.db` 和 `data/logs`。

## 构建

使用 llvm-mingw UCRT x64 工具链（当前验收版本 20261006）：

```bash
export LLVM_MINGW=/path/to/llvm-mingw
bash NativeHost/build.sh
```

需要 Python 3、Bash。版本资源由 `scripts/version.py` 从 `VERSION` 生成。
输出 `NativeHost/build/Mio.Host.exe`。本宿主静态链接 C++ 运行库，但 Realm 仍依赖 Microsoft VC x64 运行库。

路由使用系统 Windows PowerShell 5.1 / NetTCPIP，以 stdin JSON 交换请求，不插入用户命令文本。
第三方现有许可文件保留在 vendor 中；不得将此构建说明视为兼容所有 Windows 版本的声明。
