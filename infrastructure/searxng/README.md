# AI Sales OS 可选本地 SearXNG

此目录只提供用户主动启用的本地搜索后端。AI Sales OS 不会自动安装、启动或要求 Docker；不使用它时，主程序与全部现有业务数据不受影响。

## 启动

1. 安装并启动 Docker Desktop。
2. 在 PowerShell 中运行 `./start-searxng.ps1`。
3. 在 AI Sales OS 的“设置 > 客户外部调查”中启用 SearXNG，地址保持 `http://127.0.0.1:8080`。
4. 点击“测试 SearXNG”。测试会发起一次真实搜索。

服务只映射到本机回环地址；JSON 输出已启用，安全搜索级别为 1。随机本地密钥写入被 Git 忽略的 `.env`，不会进入程序数据库或版本库。

## 停止

运行 `./stop-searxng.ps1`。它只停止并移除该可选容器，不会删除 AI Sales OS 数据、调查数据库或 `.env` 配置。
