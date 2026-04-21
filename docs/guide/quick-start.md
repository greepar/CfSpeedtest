# 快速开始

## 启动服务端

在项目根目录运行：

```bash
dotnet run --project src/CfSpeedtest.Server
```

默认监听地址：

```text
http://0.0.0.0:5000
```

浏览器访问：

```text
http://127.0.0.1:5000
```

## 启动客户端

示例：

```bash
dotnet run --project src/CfSpeedtest.Client -- --server http://127.0.0.1:5000 --isp Telecom --name GZ-Telecom
```

常用参数：

- `--server`：服务端地址
- `--client-id`：显式指定 clientId
- `--isp`：`Telecom` / `Unicom` / `Mobile`
- `--name`：客户端名称
- `--interval`：客户端默认兜底轮询间隔（分钟）
- `--once`：只执行一轮测速
- `--auto-update`：启动时自动检查并下载更新包

## 推荐流程

如果启用了白名单模式，建议：

1. 在 WebUI 的“客户端”页生成启动参数
2. 复制生成的完整命令
3. 在客户端机器上运行
