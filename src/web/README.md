# CfSpeedtest Web

React + TypeScript + Tailwind CSS 管理面板源码。

## 开发

```bash
npm install
npm run dev
```

开发服务器默认 `http://localhost:5173`，`/api`、`/i`、`/client-updates` 会代理到后端 `http://localhost:5211`。

## 构建

```bash
npm run build
```

构建产物会输出到：

```text
../CfSpeedtest.Server/wwwroot
```

即 .NET 服务端项目的静态资源目录。
