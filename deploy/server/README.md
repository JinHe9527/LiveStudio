# 国内 Windows 发布镜像

本目录把公开 GitHub Latest Release 的 `LiveStudio-Setup.exe` 镜像到现有腾讯云站点：

- 固定人工下载地址：`https://wuyoupaiban.cn/livestudio/LiveStudio-Setup.exe`
- 软件更新清单：`https://wuyoupaiban.cn/livestudio/latest.json`
- 不可变版本地址：`https://wuyoupaiban.cn/livestudio/releases/v<version>/LiveStudio-Setup.exe`

同步程序从 GitHub 官方 Releases API 读取标签、下载地址和 Asset SHA-256。大文件可经国内传输代理获取，但只有与 GitHub 官方摘要完全相同的文件才会进入公开目录。Windows 客户端和 Release 流水线还会独立校验固定 Authenticode Publisher 与证书指纹。

Ubuntu 部署要求 `curl`、`jq`、`flock`、Nginx 和 systemd。安装文件：

```shell
sudo install -m 0755 livestudio-release-sync.sh /usr/local/sbin/livestudio-release-sync
sudo install -m 0644 livestudio-release-sync.service /etc/systemd/system/livestudio-release-sync.service
sudo install -m 0644 livestudio-release-sync.timer /etc/systemd/system/livestudio-release-sync.timer
sudo install -m 0644 livestudio-downloads.nginx.conf /etc/nginx/snippets/livestudio-downloads.conf
```

只在 HTTPS `server` 块中加入：

```nginx
include /etc/nginx/snippets/livestudio-downloads.conf;
```

然后验证并启用：

```shell
sudo nginx -t
sudo systemctl reload nginx
sudo systemctl daemon-reload
sudo systemctl start livestudio-release-sync.service
sudo systemctl enable --now livestudio-release-sync.timer
```

同步使用临时目录、摘要校验、版本目录原子移动和固定链接原子替换；失败不会把半份安装包暴露给下载者，也不会删除历史版本。
