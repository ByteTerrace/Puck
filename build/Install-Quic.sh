#!/usr/bin/env bash
set -euo pipefail
archive="${RUNNER_TEMP:-/tmp}/puck-packages-microsoft-prod.deb"
curl --fail --silent --show-error --location https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb --output "$archive"
echo "c13f01ac7c3001b51a9281d40dde666db5e037e05512840c319832f7852bfec4  $archive" | sha256sum -c -
sudo dpkg -i "$archive"
sudo apt-get update
sudo apt-get install -y --no-install-recommends libmsquic
