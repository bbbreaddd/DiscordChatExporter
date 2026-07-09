#!/usr/bin/env sh
set -e

CONFIG=/config/watcher.yaml

# First pass runs as root: prepare the mounted volumes, seed a default config if the user hasn't
# provided one yet, fix ownership, then drop to the unprivileged dce user and re-exec.
if [ "$(id -u)" = '0' ]; then
  mkdir -p /config /data

  if [ ! -f "$CONFIG" ]; then
    cp /opt/app/watcher.default.yaml "$CONFIG"
    echo "======================================================================"
    echo " Created a default config at config/watcher.yaml."
    echo " EDIT it (set your token, server id, and output), then the watcher"
    echo " will start. Until then it will keep retrying and log a config error."
    echo "======================================================================"
  fi

  chown -R dce:dce /config /data
  exec su-exec dce "$0" "$@"
fi

# Second pass runs as dce: launch the watcher. Extra args (e.g. --server <name>) are passed through.
exec /opt/app/DiscordChatExporter.Cli watch --config "$CONFIG" "$@"
