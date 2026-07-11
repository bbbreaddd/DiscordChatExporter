using System;
using System.IO;
using CliFx;
using DiscordChatExporter.Cli.Configuration;
using FluentAssertions;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

// Covers the reliability-feature YAML sections added to the watcher config: `notifications`
// (top-level), and the per-server / defaults `backup` + `full-scan` blocks.
public class WatchConfigReliabilitySpecs
{
    private static WatchConfig Load(string yaml)
    {
        using var dir = TempDirectory.Create();
        var path = Path.Combine(dir.Path, "watcher.yaml");
        File.WriteAllText(path, yaml);
        return WatchConfigLoader.Load(path);
    }

    private static void LoadShouldThrow(string yaml, string because)
    {
        var act = () => Load(yaml);
        act.Should().Throw<CommandException>(because);
    }

    [Fact]
    public void I_can_parse_the_notifications_backup_and_full_scan_sections()
    {
        // Act
        var config = Load(
            """
            notifications:
              enabled: true
              urls:
                - "discord://id/token"
                - "tgram://token/chatid"
              on:
                fatal-close: true
                connection-down: false
                backup-failure: true
                backup-success: true
                full-scan-complete: false
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
                backup:
                  enabled: true
                  schedule: "0 6 * * *"
                  dir: /data/backups
                  keep-daily: 10
                  keep-weekly: 3
                  compress: true
                  integrity-check: false
                full-scan:
                  enabled: true
                  schedule: "15 3 * * 1"
                  channels: [111111111111111111]
                  exclude-categories: [222222222222222222]
                  include-threads: false
                  after: "30d"
                  before: "2026-06-01"
                  max-channels: 25
                  enrich-reactors: false
                  media: false
            """
        );

        // Assert -- notifications
        config.Notifications.Enabled.Should().BeTrue();
        config.Notifications.Urls.Should().Equal("discord://id/token", "tgram://token/chatid");
        config.Notifications.OnFatalClose.Should().BeTrue();
        config.Notifications.OnConnectionDown.Should().BeFalse();
        config.Notifications.OnBackupSuccess.Should().BeTrue();
        config.Notifications.OnFullScanComplete.Should().BeFalse();

        // Assert -- backup
        var backup = config.Servers[0].Backup;
        backup.Enabled.Should().BeTrue();
        backup.Schedule.Should().Be("0 6 * * *");
        backup.Dir.Should().Be("/data/backups");
        backup.KeepDaily.Should().Be(10);
        backup.KeepWeekly.Should().Be(3);
        backup.Compress.Should().BeTrue();
        backup.IntegrityCheck.Should().BeFalse();

        // Assert -- full-scan
        var fullScan = config.Servers[0].FullScan;
        fullScan.Enabled.Should().BeTrue();
        fullScan.Schedule.Should().Be("15 3 * * 1");
        fullScan
            .Channels.Should()
            .ContainSingle()
            .Which.ToString()
            .Should()
            .Be("111111111111111111");
        fullScan.ExcludeCategories.Should().ContainSingle();
        fullScan.IncludeThreads.Should().BeFalse();
        fullScan.After.Should().Be("30d");
        fullScan.Before.Should().Be("2026-06-01");
        fullScan.MaxChannels.Should().Be(25);
        fullScan.EnrichReactors.Should().BeFalse();
        fullScan.Media.Should().BeFalse();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("~")]
    [InlineData("")] // `key:` with no value
    public void Full_scan_null_window_and_max_channels_load_as_unset(string nullSpelling)
    {
        // Regression: the shipped default template ships `after: null` / `before: null` /
        // `max-channels: null`. YamlDotNet hands a plain YAML null back as the literal text
        // "null", which previously blew up config load (TimeWindow rejected "null" as a window;
        // GetInt rejected "null" as an integer), crash-looping the watcher on its own default
        // config the first time it was built and run. A plain YAML null must load as unset.
        var config = Load(
            $"""
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
                full-scan:
                  enabled: false
                  after: {nullSpelling}
                  before: {nullSpelling}
                  max-channels: {nullSpelling}
            """
        );

        var fullScan = config.Servers[0].FullScan;
        fullScan.After.Should().BeNull();
        fullScan.Before.Should().BeNull();
        fullScan.MaxChannels.Should().BeNull();
    }

    [Fact]
    public void Reliability_features_default_to_disabled_when_omitted()
    {
        // Act
        var config = Load(
            """
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
            """
        );

        // Assert
        config.Notifications.Enabled.Should().BeFalse();
        config.Servers[0].Backup.Enabled.Should().BeFalse();
        config.Servers[0].FullScan.Enabled.Should().BeFalse();
        config.Servers[0].Vacuum.Enabled.Should().BeFalse();
    }

    [Fact]
    public void I_can_parse_the_vacuum_section()
    {
        var config = Load(
            """
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
                vacuum:
                  enabled: true
                  schedule: "0 4 * * 1"
                  incremental: true
            """
        );

        var vacuum = config.Servers[0].Vacuum;
        vacuum.Enabled.Should().BeTrue();
        vacuum.Schedule.Should().Be("0 4 * * 1");
        vacuum.Incremental.Should().BeTrue();
    }

    [Fact]
    public void Vacuum_defaults_to_a_disabled_full_weekly_run()
    {
        var config = Load(
            """
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
            """
        );

        var vacuum = config.Servers[0].Vacuum;
        vacuum.Enabled.Should().BeFalse();
        vacuum.Incremental.Should().BeFalse();
        vacuum.Schedule.Should().Be("0 6 * * 0");
    }

    [Fact]
    public void Defaults_vacuum_block_is_inherited_by_a_server()
    {
        var config = Load(
            """
            defaults:
              vacuum:
                enabled: true
                incremental: true
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
            """
        );

        config.Servers[0].Vacuum.Enabled.Should().BeTrue();
        config.Servers[0].Vacuum.Incremental.Should().BeTrue();
    }

    [Fact]
    public void An_invalid_vacuum_cron_is_rejected()
    {
        LoadShouldThrow(
            """
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
                vacuum:
                  enabled: true
                  schedule: "not a cron"
            """,
            "the cron expression is invalid"
        );
    }

    [Fact]
    public void An_unknown_key_in_the_vacuum_block_is_rejected()
    {
        LoadShouldThrow(
            """
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
                vacuum:
                  enabled: true
                  frequency: daily
            """,
            "unknown keys should fail loudly"
        );
    }

    [Fact]
    public void Defaults_backup_block_is_inherited_by_a_server()
    {
        // Act
        var config = Load(
            """
            defaults:
              backup:
                enabled: true
                dir: /data/backups
                keep-daily: 14
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
            """
        );

        // Assert
        config.Servers[0].Backup.Enabled.Should().BeTrue();
        config.Servers[0].Backup.Dir.Should().Be("/data/backups");
        config.Servers[0].Backup.KeepDaily.Should().Be(14);
    }

    [Fact]
    public void A_backup_enabled_without_a_dir_is_rejected()
    {
        LoadShouldThrow(
            """
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
                backup:
                  enabled: true
            """,
            "backup.dir is required when backup is enabled"
        );
    }

    [Fact]
    public void An_invalid_backup_cron_is_rejected()
    {
        LoadShouldThrow(
            """
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
                backup:
                  enabled: true
                  dir: /data/backups
                  schedule: "not a cron"
            """,
            "the cron expression is invalid"
        );
    }

    [Fact]
    public void An_invalid_full_scan_window_is_rejected()
    {
        LoadShouldThrow(
            """
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
                full-scan:
                  enabled: true
                  after: "not a date"
            """,
            "the after window is invalid"
        );
    }

    [Fact]
    public void Notifications_enabled_without_urls_is_rejected()
    {
        LoadShouldThrow(
            """
            notifications:
              enabled: true
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
            """,
            "at least one Apprise URL is required when notifications are enabled"
        );
    }

    [Fact]
    public void An_unknown_key_in_a_reliability_block_is_rejected()
    {
        LoadShouldThrow(
            """
            servers:
              - name: Test
                id: 123456789012345678
                output: /data/test.db
                full-scan:
                  enabled: true
                  parallelism: 4
            """,
            "unknown keys should fail loudly"
        );
    }
}
