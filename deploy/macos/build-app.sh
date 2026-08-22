#!/bin/zsh
set -euo pipefail

if [[ $# -ne 5 ]]; then
  print -u2 "用法: $0 <version> <build-number> <arm64|x64> <developer-id-application> <notary-keychain-profile>"
  exit 2
fi

release_version="$1"
build_number="$2"
release_architecture="$3"
signing_identity="$4"
notary_profile="$5"

if [[ ! "$release_version" =~ '^[0-9]+\.[0-9]+\.[0-9]+$' ]]; then
  print -u2 "版本号必须为 MAJOR.MINOR.PATCH。"
  exit 2
fi

if [[ ! "$build_number" =~ '^[1-9][0-9]*$' ]]; then
  print -u2 "build-number 必须是正整数。"
  exit 2
fi

if [[ "$release_architecture" != "arm64" && "$release_architecture" != "x64" ]]; then
  print -u2 "架构只支持 arm64 或 x64。"
  exit 2
fi

script_directory="${0:A:h}"
repository_root="${script_directory:h:h}"
release_directory="$repository_root/output/macos"
application_path="$release_directory/LiveStudio-$release_version-$release_architecture.app"
submission_path="$release_directory/LiveStudio-$release_version-$release_architecture-notary.zip"
archive_path="$release_directory/LiveStudio-$release_version-$release_architecture.zip"
runtime_identifier="osx-$release_architecture"

if [[ -e "$application_path" || -e "$submission_path" || -e "$archive_path" ]]; then
  print -u2 "目标版本的发布文件已经存在，请先核对并移入废纸篓：$release_directory"
  exit 3
fi

mkdir -p "$application_path/Contents/MacOS" "$application_path/Contents/Resources"
dotnet publish "$repository_root/src/LiveStudio.Desktop/LiveStudio.Desktop.csproj" \
  -c Release -r "$runtime_identifier" --self-contained true \
  -o "$application_path/Contents/MacOS"

sed -e "s/__VERSION__/$release_version/g" \
    -e "s/__BUILD_NUMBER__/$build_number/g" \
    "$script_directory/Info.plist" > "$application_path/Contents/Info.plist"
cp "$script_directory/LiveStudio.icns" "$application_path/Contents/Resources/LiveStudio.icns"

codesign --force --deep --options runtime --timestamp --sign "$signing_identity" "$application_path"
codesign --verify --deep --strict --verbose=2 "$application_path"

ditto -c -k --sequesterRsrc --keepParent "$application_path" "$submission_path"
xcrun notarytool submit "$submission_path" --keychain-profile "$notary_profile" --wait
xcrun stapler staple "$application_path"
spctl --assess --type execute --verbose=2 "$application_path"

ditto -c -k --sequesterRsrc --keepParent "$application_path" "$archive_path"
print "$archive_path"
