using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Core.Exporting.Database;

public sealed record BackupResult(string Path, long SizeBytes, bool IntegrityOk, int Pruned);

// Performs a scheduled online backup of the live SQLite database via a SEPARATE read connection, so it
// never blocks the watcher's writer (WAL allows concurrent readers, and the backup captures a
// consistent committed snapshot including un-checkpointed WAL frames). Writes a timestamped copy, runs
// an integrity check, optionally gzips it, and prunes old copies per a daily + weekly retention policy.
public sealed class DatabaseBackupService
{
    private readonly string _databaseFilePath;
    private readonly string _destinationDir;
    private readonly int _keepDaily;
    private readonly int _keepWeekly;
    private readonly bool _compress;
    private readonly bool _integrityCheck;

    public DatabaseBackupService(
        string databaseFilePath,
        string destinationDir,
        int keepDaily,
        int keepWeekly,
        bool compress,
        bool integrityCheck
    )
    {
        _databaseFilePath = databaseFilePath;
        _destinationDir = destinationDir;
        _keepDaily = keepDaily;
        _keepWeekly = keepWeekly;
        _compress = compress;
        _integrityCheck = integrityCheck;
    }

    private string BaseName => Path.GetFileNameWithoutExtension(_databaseFilePath);

    public async Task<BackupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_destinationDir);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var destPath = Path.Combine(_destinationDir, $"{BaseName}-{stamp}.db");

        // SqliteConnection.BackupDatabase is synchronous and can take a while for a multi-GB file, so
        // run it off the caller's thread. A separate read-only source connection means the watcher's
        // writer keeps going throughout.
        await Task.Run(
            () =>
            {
                var sourceConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _databaseFilePath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString();
                var destConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = destPath,
                }.ToString();

                using var source = new SqliteConnection(sourceConnectionString);
                using var dest = new SqliteConnection(destConnectionString);
                source.Open();
                dest.Open();
                source.BackupDatabase(dest);
            },
            cancellationToken
        );

        var integrityOk = true;
        if (_integrityCheck)
            integrityOk = await Task.Run(() => QuickCheck(destPath), cancellationToken);

        var finalPath = destPath;
        if (_compress)
        {
            var gzPath = destPath + ".gz";
            await CompressAsync(destPath, gzPath, cancellationToken);
            File.Delete(destPath);
            finalPath = gzPath;
        }

        var pruned = Prune();
        var size = new FileInfo(finalPath).Length;
        return new BackupResult(finalPath, size, integrityOk, pruned);
    }

    private static bool QuickCheck(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = command.ExecuteScalar() as string;
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CompressAsync(string source, string dest, CancellationToken ct)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(dest);
        await using var gzip = new GZipStream(output, CompressionLevel.Optimal);
        await input.CopyToAsync(gzip, ct);
    }

    // Retention: keep the newest backup for each of the last `keepDaily` days, plus the newest for each
    // of the last `keepWeekly` ISO weeks; delete everything else. Returns how many files were removed.
    private int Prune()
    {
        var entries = EnumerateBackups().ToList();
        if (entries.Count == 0)
            return 0;

        var keep = new HashSet<string>(StringComparer.Ordinal);

        foreach (
            var group in entries
                .GroupBy(e => e.Timestamp.Date)
                .OrderByDescending(g => g.Key)
                .Take(_keepDaily)
        )
            keep.Add(group.OrderByDescending(e => e.Timestamp).First().Path);

        foreach (
            var group in entries
                .GroupBy(e => IsoWeekKey(e.Timestamp))
                .OrderByDescending(g => g.Key, StringComparer.Ordinal)
                .Take(_keepWeekly)
        )
            keep.Add(group.OrderByDescending(e => e.Timestamp).First().Path);

        var pruned = 0;
        foreach (var entry in entries)
        {
            if (keep.Contains(entry.Path))
                continue;
            try
            {
                File.Delete(entry.Path);
                pruned++;
            }
            catch (IOException)
            {
                // Leave it; a later run will retry the prune.
            }
        }
        return pruned;
    }

    private IEnumerable<(string Path, DateTimeOffset Timestamp)> EnumerateBackups()
    {
        if (!Directory.Exists(_destinationDir))
            yield break;

        foreach (var path in Directory.EnumerateFiles(_destinationDir, $"{BaseName}-*.db*"))
        {
            var fileName = Path.GetFileName(path);
            // Expected form: "{BaseName}-yyyyMMdd-HHmmss.db" (optionally + ".gz").
            var stamp = fileName[(BaseName.Length + 1)..];
            var dot = stamp.IndexOf('.');
            if (dot >= 0)
                stamp = stamp[..dot];

            if (
                DateTimeOffset.TryParseExact(
                    stamp,
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var timestamp
                )
            )
                yield return (path, timestamp);
        }
    }

    private static string IsoWeekKey(DateTimeOffset ts)
    {
        var date = ts.Date;
        var week = ISOWeek.GetWeekOfYear(date);
        var year = ISOWeek.GetYear(date);
        return $"{year:D4}-{week:D2}";
    }
}
