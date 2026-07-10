using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Database;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using PowerKit;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class DatabaseBackupServiceSpecs
{
    private static string CreateSourceDatabase(string dir)
    {
        var path = Path.Combine(dir, "source.db");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString()
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t (v) VALUES ('hello');";
        command.ExecuteNonQuery();
        return path;
    }

    private static long CountRows(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM t;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task I_can_take_an_integrity_checked_backup_copy()
    {
        // Arrange
        using var dir = TempDirectory.Create();
        var source = CreateSourceDatabase(dir.Path);
        var backupDir = Path.Combine(dir.Path, "backups");
        var service = new DatabaseBackupService(
            source,
            backupDir,
            keepDaily: 7,
            keepWeekly: 4,
            compress: false,
            integrityCheck: true
        );

        // Act
        var result = await service.RunAsync();

        // Assert
        result.IntegrityOk.Should().BeTrue();
        File.Exists(result.Path).Should().BeTrue();
        result.Path.Should().EndWith(".db");
        Path.GetFileName(result.Path).Should().StartWith("source-");
        // The copy is a real, readable database with the source's content.
        CountRows(result.Path).Should().Be(1);
    }

    [Fact]
    public async Task I_can_take_a_compressed_backup()
    {
        // Arrange
        using var dir = TempDirectory.Create();
        var source = CreateSourceDatabase(dir.Path);
        var backupDir = Path.Combine(dir.Path, "backups");
        var service = new DatabaseBackupService(
            source,
            backupDir,
            keepDaily: 7,
            keepWeekly: 4,
            compress: true,
            integrityCheck: true
        );

        // Act
        var result = await service.RunAsync();

        // Assert
        result.Path.Should().EndWith(".db.gz");
        File.Exists(result.Path).Should().BeTrue();
        // The uncompressed intermediate is cleaned up.
        Directory.EnumerateFiles(backupDir, "*.db").Should().BeEmpty();
    }

    [Fact]
    public async Task Old_backups_are_pruned_by_the_retention_policy()
    {
        // Arrange
        using var dir = TempDirectory.Create();
        var source = CreateSourceDatabase(dir.Path);
        var backupDir = Path.Combine(dir.Path, "backups");
        Directory.CreateDirectory(backupDir);

        // Plant two copies from well in the past (distinct days AND weeks).
        var stale1 = Path.Combine(backupDir, "source-20200101-120000.db");
        var stale2 = Path.Combine(backupDir, "source-20200108-120000.db");
        File.WriteAllText(stale1, "stale");
        File.WriteAllText(stale2, "stale");

        var service = new DatabaseBackupService(
            source,
            backupDir,
            keepDaily: 1,
            keepWeekly: 0,
            compress: false,
            integrityCheck: false
        );

        // Act
        var result = await service.RunAsync();

        // Assert -- only today's fresh backup survives; both stale copies are pruned.
        result.Pruned.Should().Be(2);
        File.Exists(stale1).Should().BeFalse();
        File.Exists(stale2).Should().BeFalse();
        Directory
            .EnumerateFiles(backupDir, "source-*.db")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(result.Path);
    }
}
