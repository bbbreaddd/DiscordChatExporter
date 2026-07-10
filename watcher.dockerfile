# Purpose-built image for running the guild watcher 24/7 via a YAML config.
# Mirrors DiscordChatExporter.Cli.dockerfile's build, but ships a watcher entrypoint that seeds a
# default config and runs `watch --config /config/watcher.yaml`.

# -- Build
# Pull the SDK for the host platform, build the assembly for the target platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

ARG TARGETARCH
ARG VERSION=0.0.0

WORKDIR /tmp/app

COPY favicon.ico .
COPY NuGet.config .
COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY DiscordChatExporter.Core DiscordChatExporter.Core
COPY DiscordChatExporter.Cli DiscordChatExporter.Cli

# Self-contained so the runtime image can be the slim runtime-deps. CSharpier_Bypass skips the
# format check during the image build (it's enforced on normal builds/CI, not needed here).
RUN dotnet publish DiscordChatExporter.Cli \
    -p:Version=$VERSION \
    -p:CSharpier_Bypass=true \
    --configuration Release \
    --self-contained \
    --use-current-runtime \
    --arch $TARGETARCH \
    --output DiscordChatExporter.Cli/bin/publish/

# -- Run
FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS run

LABEL org.opencontainers.image.title="discord-chat-watcher"
LABEL org.opencontainers.image.description="24/7 watcher that exports a Discord guild's live gateway events into a SQLite database, configured via YAML."
LABEL org.opencontainers.image.source="https://github.com/Tyrrrz/DiscordChatExporter"
LABEL org.opencontainers.image.licenses="MIT"

# Alpine lacks ICU (needed for locale-aware formatting), tzdata (timezones), and su-exec (drop root).
RUN apk add --no-cache icu-libs icu-data-full tzdata su-exec

# Apprise CLI, used by the watcher to send operator notifications (backup/full-scan/fatal-close) to
# any of 100+ services from a single config. Installed into an isolated venv (avoids Alpine's
# externally-managed-environment restriction, PEP 668) and symlinked onto PATH as `apprise`, which is
# the binary AppriseNotifier shells out to.
RUN apk add --no-cache python3 py3-pip \
    && python3 -m venv /opt/apprise \
    && /opt/apprise/bin/pip install --no-cache-dir apprise \
    && ln -s /opt/apprise/bin/apprise /usr/local/bin/apprise

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV LC_ALL=en_US.UTF-8
ENV LANG=en_US.UTF-8

# Run as a non-root user so files written to the mounted volumes are owned sensibly on the host.
RUN addgroup -S -g 1000 dce && adduser -S -H -G dce -u 1000 dce

COPY --from=build /tmp/app/DiscordChatExporter.Cli/bin/publish /opt/app
COPY docker/watcher.default.yaml /opt/app/watcher.default.yaml
COPY docker/watcher-entrypoint.sh /opt/app/watcher-entrypoint.sh
RUN chmod +x /opt/app/watcher-entrypoint.sh

# /config holds watcher.yaml (+ optional tokens.txt); /data holds the databases and media.
VOLUME ["/config", "/data"]

ENTRYPOINT ["/opt/app/watcher-entrypoint.sh"]
