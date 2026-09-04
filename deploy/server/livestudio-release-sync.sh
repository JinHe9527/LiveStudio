#!/usr/bin/env bash
set -euo pipefail

readonly repository="JinHe9527/LiveStudio"
readonly download_root="/var/www/livestudio-downloads"
readonly public_root="https://wuyoupaiban.cn/livestudio"
readonly lock_path="/run/lock/livestudio-release-sync.lock"

exec 9>"${lock_path}"
flock --nonblock 9 || exit 0

temporary_root="$(mktemp -d)"
trap 'rm -rf -- "${temporary_root}"' EXIT

release_api_url="https://api.github.com/repos/${repository}/releases/latest"
curl --fail --silent --show-error --location \
  --retry 5 --connect-timeout 20 --max-time 120 \
  --header 'Accept: application/vnd.github+json' \
  --header 'User-Agent: LiveStudio-Release-Mirror' \
  --output "${temporary_root}/release.json" "${release_api_url}"
tag_name="$(jq --raw-output '.tag_name // empty' "${temporary_root}/release.json")"
if [[ ! "${tag_name}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "无法从 GitHub Latest Release 解析版本" >&2
  exit 1
fi

version="${tag_name#v}"
package_url_official="$(jq --raw-output \
  '.assets[] | select(.name == "LiveStudio-Setup.exe") | .browser_download_url' \
  "${temporary_root}/release.json")"
expected_digest="$(jq --raw-output \
  '.assets[] | select(.name == "LiveStudio-Setup.exe") | .digest' \
  "${temporary_root}/release.json")"
expected_hash="${expected_digest#sha256:}"
if [[ "${package_url_official}" != \
    "https://github.com/${repository}/releases/download/${tag_name}/LiveStudio-Setup.exe" ]] \
  || [[ ! "${expected_hash}" =~ ^[0-9A-Fa-f]{64}$ ]]; then
  echo "GitHub Latest Release 缺少固定名称安装器或有效 SHA-256" >&2
  exit 1
fi

release_directory="${download_root}/releases/${tag_name}"
if [[ -f "${download_root}/latest.json" ]] \
  && [[ "$(jq --raw-output '.tagName // empty' "${download_root}/latest.json")" == "${tag_name}" ]] \
  && [[ -f "${release_directory}/LiveStudio-Setup.exe" ]] \
  && [[ -f "${release_directory}/LiveStudio-Setup.exe.sha256" ]]; then
  read -r current_expected_hash _ < "${release_directory}/LiveStudio-Setup.exe.sha256"
  current_actual_hash="$(sha256sum "${release_directory}/LiveStudio-Setup.exe" | awk '{print $1}')"
  if [[ "${current_expected_hash}" =~ ^[0-9A-Fa-f]{64}$ ]] \
    && [[ "${current_expected_hash,,}" == "${expected_hash,,}" ]] \
    && [[ "${current_actual_hash}" == "${current_expected_hash,,}" ]]; then
    exit 0
  fi
fi

package_sources=(
  "https://gh-proxy.com/${package_url_official}"
  "${package_url_official}"
)
actual_hash=""
for package_source in "${package_sources[@]}"; do
  rm -f -- "${temporary_root}/LiveStudio-Setup.exe"
  if ! curl --fail --silent --show-error --location \
    --retry 3 --connect-timeout 20 --max-time 1800 --limit-rate 2M \
    --output "${temporary_root}/LiveStudio-Setup.exe" \
    "${package_source}"; then
    continue
  fi

  actual_hash="$(sha256sum "${temporary_root}/LiveStudio-Setup.exe" | awk '{print $1}')"
  if [[ "${actual_hash,,}" == "${expected_hash,,}" ]]; then
    break
  fi

  echo "镜像源返回的安装器摘要不匹配，尝试下一来源：${package_source}" >&2
  actual_hash=""
done

if [[ -z "${actual_hash}" ]]; then
  echo "所有来源的 Release 安装器下载或 SHA-256 校验均失败" >&2
  exit 1
fi

printf '%s  LiveStudio-Setup.exe\n' "${expected_hash,,}" \
  > "${temporary_root}/LiveStudio-Setup.exe.sha256"

if [[ ! -f "${release_directory}/LiveStudio-Setup.exe" ]] \
  || [[ "$(sha256sum "${release_directory}/LiveStudio-Setup.exe" | awk '{print $1}')" != "${actual_hash,,}" ]]; then
  staging_directory="${download_root}/releases/.${tag_name}-$RANDOM"
  install -d -m 0755 "${staging_directory}"
  install -m 0644 "${temporary_root}/LiveStudio-Setup.exe" \
    "${staging_directory}/LiveStudio-Setup.exe"
  install -m 0644 "${temporary_root}/LiveStudio-Setup.exe.sha256" \
    "${staging_directory}/LiveStudio-Setup.exe.sha256"
  if [[ -e "${release_directory}" ]]; then
    echo "已存在但校验失败的国内版本目录：${release_directory}" >&2
    exit 1
  fi
  mv "${staging_directory}" "${release_directory}"
fi

package_url="${public_root}/releases/${tag_name}/LiveStudio-Setup.exe"
checksum_url="${public_root}/releases/${tag_name}/LiveStudio-Setup.exe.sha256"
jq --null-input \
  --arg version "${version}" \
  --arg tagName "${tag_name}" \
  --arg name "LiveStudio ${tag_name}" \
  --arg publishedAt "$(jq --raw-output '.published_at' "${temporary_root}/release.json")" \
  --arg packageUrl "${package_url}" \
  --arg checksumUrl "${checksum_url}" \
  '{version:$version,tagName:$tagName,name:$name,publishedAt:$publishedAt,packageUrl:$packageUrl,checksumUrl:$checksumUrl}' \
  > "${temporary_root}/latest.json"

install -d -m 0755 "${download_root}"
ln -sfn "releases/${tag_name}/LiveStudio-Setup.exe" \
  "${download_root}/LiveStudio-Setup.exe.new"
mv --no-target-directory "${download_root}/LiveStudio-Setup.exe.new" \
  "${download_root}/LiveStudio-Setup.exe"
ln -sfn "releases/${tag_name}/LiveStudio-Setup.exe.sha256" \
  "${download_root}/LiveStudio-Setup.exe.sha256.new"
mv --no-target-directory "${download_root}/LiveStudio-Setup.exe.sha256.new" \
  "${download_root}/LiveStudio-Setup.exe.sha256"
install -m 0644 "${temporary_root}/latest.json" "${download_root}/latest.json.new"
mv "${download_root}/latest.json.new" "${download_root}/latest.json"

echo "国内更新镜像已同步：${tag_name} ${actual_hash,,}"
