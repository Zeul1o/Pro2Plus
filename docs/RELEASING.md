# 发布与远端约定

本项目有两个远端，用途不同，**不要混用**。

| 远端 | 地址 | 用途 | 可见性 |
| --- | --- | --- | --- |
| `origin` | `Zeul1o/Pro2Plus-history` | 日常开发同步，所有提交都推这里 | private |
| `public` | `Zeul1o/Pro2Plus` | 只放阶段性发布，平时不推 | public |

两条分支同名（`master`），内容可以暂时不一致：`origin` 领先是正常的，`public` 只在发版时对齐。

## 日常开发

```bash
git add -A
git commit -m "..."
git push            # 等价于 git push origin master，只进私有仓库
```

平时**不要**执行 `git push public`，也不要用 `git push --all` / `--tags`（会把开发过程一次性推到公开仓库）。

## 阶段性发布

发布时按顺序执行，假设版本号 `v1.1.0`：

```bash
# 1. 确认工作区干净、测试通过
git status --short          # 应为空

# 2. 打包（中文版 + 英文版），产物在 dist/
powershell -ExecutionPolicy Bypass -File .\publish.ps1
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -English

# 3. 先把开发分支推到私有仓库（保证私有侧是最新的）
git push

# 4. 再把同一个提交推到公开仓库
git push public master

# 5. 打标签并发版（标签只推到 public，私有仓库不建标签）
git tag -a v1.1.0 -m "Pro2+ v1.1.0"
git push public v1.1.0
gh release create v1.1.0 dist/Pro2+.exe dist/Pro2+-en.exe \
  --repo Zeul1o/Pro2Plus --title "Pro2+ v1.1.0" --notes-file <发布说明.md>
```

发版说明建议单独写一个 Markdown 文件再 `--notes-file` 引入，避免命令行转义问题。

## 公开仓库的历史形态

上面的做法是**正常推送**：公开仓库会累积每次发版之间的所有提交，历史连续、可 `git log` 对比版本。

如果希望公开仓库看起来只有「每个版本一个提交」，需要在发版时改用快照式推送（`git checkout --orphan` 造无父提交再 force push）。代价是公开仓库没有跨版本 diff、每次发版都会产生一条 force-push 活动记录，所以默认不这么做。

## 已知限制

当前网络环境下 `github.com`（网页与 OAuth）被代理拦截，只有 `api.github.com` 与 SSH 可用，因此：

- `gh auth refresh`、`gh repo delete` 这类需要走 `github.com` 或额外 scope 的操作会失败
- 推送请用 SSH（两个远端都已是 `git@github.com:` 形式）
