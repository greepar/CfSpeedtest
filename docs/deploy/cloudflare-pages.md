# Cloudflare Pages 部署

这套前端文档使用 VitePress，可以直接部署到 Cloudflare Pages。

## 目录结构

文档位于：

```text
docs/
```

## 本地运行

先安装依赖：

```bash
cd docs
npm install
```

本地开发：

```bash
npm run docs:dev
```

构建：

```bash
npm run docs:build
```

预览：

```bash
npm run docs:preview
```

## Cloudflare Pages 配置

在 Cloudflare Pages 新建项目时，推荐这样配置：

- Framework preset: `None`
- Root directory: `docs`
- Build command: `npm run docs:build`
- Build output directory: `.vitepress/dist`

## Node 版本

建议使用 Node 18 或更高版本。

## 推荐仓库绑定方式

直接把当前 GitHub 仓库连接到 Cloudflare Pages，然后把文档目录设为 `docs` 即可。
