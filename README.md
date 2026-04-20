# Cloudflare 国内测速汇集程序

一个基于 C# / .NET 的 Cloudflare IP 测速系统，分为：

- 服务端：分发测速任务、汇总结果、提供 WebUI 管理页面
- 客户端：按运营商类型执行测速，并将最优结果回传服务端

客户端支持 NativeAOT，适合直接发布为单文件可执行程序。

## 功能概览

- 按运营商拆分 IP 池：`Telecom` / `Unicom` / `Mobile`
- IP 池支持多来源：
  - 手动添加
  - HTTP API 拉取
  - DoH CNAME 拉取（通过 `https://doh.pub/dns-query` 获取 A 记录）
- 支持在 WebUI 中：
  - 管理测速参数
  - 管理各运营商 DNS 域名
  - 配置多个拉取源
  - 预览拉取结果
  - 查看客户端列表
  - 查看测速历史
  - 编辑当前池内容
- 客户端支持：
  - TCP 延迟 / 丢包测试
  - HTTPS 下载测速
  - 返回 Top N 结果
  - 按服务端下发的轮询间隔继续工作

## 项目结构

```text
CfSpeedtest/
├── README.md
├── build.bat
├── build.sh
└── src/
    ├── CfSpeedtest.Shared/
    ├── CfSpeedtest.Server/
    └── CfSpeedtest.Client/
```

## 运行环境

- .NET SDK 10
- Windows / Linux 均可

如果只运行已发布的 NativeAOT 客户端，则客户端机器不需要安装 .NET 运行时。

## 快速开始

### 1. 启动服务端

在项目根目录执行：

```bash
dotnet run --project src/CfSpeedtest.Server
```

默认监听：

```text
http://0.0.0.0:5000
```

浏览器打开：

```text
http://127.0.0.1:5000
```

### 2. 启动客户端

在项目根目录执行示例：

```bash
dotnet run --project src/CfSpeedtest.Client -- --server http://127.0.0.1:5000 --isp Telecom --name GZ-Telecom
```

参数说明：

- `--server`：服务端地址
- `--isp`：运营商，可选 `Telecom` / `Unicom` / `Mobile`
- `--name`：客户端名称
- `--interval`：默认轮询间隔分钟数，仅作为兜底值
- `--once`：只执行一轮测速

示例：

```bash
dotnet run --project src/CfSpeedtest.Client -- --server http://127.0.0.1:5000 --isp Mobile --name BJ-Mobile --once
```

## WebUI 使用说明

WebUI 顶部有 5 个主页面：

- `概览`
- `测速记录`
- `客户端`
- `IP 池`
- `配置`

### 概览

显示：

- 客户端总数
- 在线客户端数量
- IP 池总量
- 最近测速次数
- 最近测速结果

### 测速记录

显示客户端上报的测速历史，包括：

- 运营商
- 时间
- 最优 IP 列表
- 下载速度
- 延迟
- 丢包率
- 综合评分

### 客户端

显示已注册客户端信息：

- 客户端 ID
- 名称
- 运营商
- 在线状态
- 注册时间
- 最后活跃时间

### IP 池

这是最常用的页面。

上方可以切换当前运营商池：

- 电信池
- 联通池
- 移动池

#### 自动拉取源

支持给当前运营商添加多个拉取源。

每一行包含：

- 左侧类型选择：
  - `HTTP API`
  - `DoH CNAME`
- 右侧输入框：
  - API 地址 或 域名
- `▶`：预览这个拉取源会拿到哪些 IP
- `×`：删除这一行拉取源

点击 `+ 添加源` 可继续新增多条。

点击 `保存拉取配置` 保存当前运营商的拉取源配置。

点击 `立即拉取当前池` 会马上执行当前运营商的拉取。

点击 `拉取所有池` 会一次性拉取全部运营商池。

#### 手动添加 IP

可在文本框内输入多个 IP：

- 每行一个
- 或逗号分隔
- 或空格分隔

点击 `手动添加 IP` 后，IP 会加入当前运营商池。

#### 当前池编辑

当前池列表支持两种维护方式：

1. 单条删除

- 每一行右侧有 `X`
- 点击即可删除这一条

2. 文本编辑模式

- 点击 `文本编辑模式`
- 会把当前池中的全部 IP 放进文本框
- 可批量修改
- 点击 `保存文本修改` 后整体覆盖当前池

说明：

- 当前池是“当前结果快照”
- 如果某个 IP 来自自动拉取源，手动删掉后，下次重新拉取时它仍然可能再次出现

### 配置

可配置以下内容：

- 测速 URL 模板
- HTTPS SNI Host
- 测速端口
- 每批分发 IP 数量
- 下载测速时长
- TCP 测试时长
- 返回 Top N
- 客户端轮询间隔（分钟）
- 所有拉取源的后台定时刷新间隔（分钟）
- 各运营商 DNS 域名

## 拉取源格式说明

### 1. HTTP API

接口返回内容支持：

- 每行一个 IP
- 逗号分隔
- 分号分隔
- 空格分隔

例如：

```text
1.1.1.1
1.0.0.1
104.16.0.1
```

也支持带备注：

```text
162.159.36.18#CF优选-电信
173.245.58.196#节点2
```

程序会自动：

- 截掉 `#` 后面的备注
- 只保留合法 IPv4 地址

### 2. DoH CNAME

输入一个域名，例如：

```text
example.com
```

系统会请求：

```text
https://doh.pub/dns-query?name=example.com&type=A
```

并从返回的 JSON 中提取 A 记录 IPv4 地址加入池中。

## 客户端测速逻辑

客户端每次从服务端获取一个任务，任务包含：

- 当前运营商应该测速的 IP 列表
- 测速 URL 模板
- Host
- 端口
- 下载测速时长
- TCP 测试时长
- Top N
- 下次轮询间隔

客户端会对每个 IP 做两类测试：

### 1. TCP 测试

- 连发 TCP 连接请求
- 统计：
  - 平均延迟
  - 最低延迟
  - 丢包率

### 2. HTTPS 下载测速

客户端会：

- 应用层继续访问真实 `Host`
- TLS/SNI 使用真实域名
- 底层 TCP 强制连接到指定 IP

这样可以正确测试 Cloudflare 场景下的指定 IP 性能。

### 评分规则

综合评分由以下权重组成：

- 速度：60%
- 延迟：25%
- 丢包：15%

最后返回 Top N 个结果给服务端。

## 轮询间隔说明

客户端休眠间隔由服务端下发。

服务端配置中的：

```text
客户端轮询间隔 (分钟)
```

会在每次任务中下发给客户端。

客户端命令行的 `--interval` 仅作为：

- 启动时默认值
- 服务端未返回有效值时的兜底值

## DNS 更新

服务端当前已经预留 DNS 更新入口：

文件：

```text
src/CfSpeedtest.Server/Services/DnsUpdateService.cs
```

你可以在这里接入：

- Cloudflare DNS API
- DNSPod API
- 阿里云 DNS API
- 其他自定义 DNS 平台

目前流程是：

1. 客户端上报 Top N 结果
2. 服务端保存历史
3. 调用 `DnsUpdateService.UpdateDnsAsync()`

## NativeAOT 发布

### Windows x64

```bash
dotnet publish src/CfSpeedtest.Client/CfSpeedtest.Client.csproj -c Release -r win-x64 --self-contained
```

### Linux x64

```bash
dotnet publish src/CfSpeedtest.Client/CfSpeedtest.Client.csproj -c Release -r linux-x64 --self-contained
```

发布后可以直接运行单文件可执行程序。

## 一键构建

### Windows

```bash
build.bat
```

### Linux

```bash
./build.sh
```

## 常见问题

### 1. 预览拉取成功，但池里没有 IP

常见原因：

- 没有点击 `保存拉取配置`
- 只做了预览，没有执行 `立即拉取当前池`

### 2. 手动删掉的 IP 又回来了

说明这个 IP 仍存在于自动拉取源中。

解决方法：

- 修改或删除拉取源
- 或者以后增加黑名单机制

### 3. 客户端显示下载测速失败

先检查：

- `TestUrl` 是否可被目标站点正常访问
- `TestHost` 是否正确
- URL 对应资源是否支持持续下载
- 目标 IP 是否真的能为该 Host 提供服务

### 4. 为什么 URL 模板里的 `{ip}` 看起来没有直接变成测速 IP

这是设计使然。

客户端下载测速时：

- 应用层 URL / Host / SNI 使用真实域名
- 底层 TCP 强制连接到指定 IP

这是为了正确模拟 HTTPS 场景下“指定 IP 访问域名”的行为。

## 开发说明

构建：

```bash
dotnet build CfSpeedtest.slnx -c Release
```

当前已验证：

- 服务端可构建
- 客户端可构建
- NativeAOT 客户端可发布
