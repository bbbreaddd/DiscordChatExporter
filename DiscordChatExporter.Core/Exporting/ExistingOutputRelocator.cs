using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting;

// Both the channel exporter (incremental JSON exports) and the convert command compute their
// output path from the guild/category/channel's *current* name. If one of those got renamed on
// Discord since the last run, the freshly-computed path won't match whatever was written last
// time. This helper finds that previous output by channel ID and either renames it onto the
// fresh path, or -- if the fresh path is already taken by something else -- redirects the
// request to keep writing under the old name, so a rename never results in two disconnected
// output histories for the same channel.
internal static class ExistingOutputRelocator
{
    // Returns the paths of all partition files that currently exist on disk for a channel, in
    // partition order (index 0 first). Always contains at least one path, since it's only
    // called after confirming the base file exists.
    public static string[] GetExistingPartitionFilePaths(string baseFilePath)
    {
        var paths = new List<string>();

        for (var index = 0; ; index++)
        {
            var path = MessageExporter.GetPartitionFilePath(baseFilePath, index);
            if (!File.Exists(path))
                break;

            paths.Add(path);
        }

        return paths.ToArray();
    }

    // Looks for an existing base export/convert file (partition #1) for the given channel ID
    // anywhere under the search root, regardless of the cosmetic guild/category/channel name in
    // its path. The search is recursive because a rename can change not just the file name but
    // also the directory it lives in, when the output path uses template directories (e.g.
    // "%G/%T/%C/"). If a channel or category got renamed since the last run, or the
    // name-escaping rules changed between versions, more than one match may exist; the most
    // recently written one is assumed to be the most complete and is preferred.
    public static string? FindExistingBaseFilePath(
        string searchRootDirPath,
        string channelId,
        string extension
    )
    {
        if (!Directory.Exists(searchRootDirPath))
            return null;

        var suffix = $"[{channelId}].{extension}";

        return Directory
            .EnumerateFiles(searchRootDirPath, $"*{suffix}", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    // A relocation in progress is marked at the destination so an interrupted multi-file move
    // (killed between two File.Move calls) can be resumed cheaply on the next run, without
    // re-scanning the whole output tree to rediscover where it was relocating from.
    private static string GetRelocationMarkerPath(string newBaseFilePath) =>
        newBaseFilePath + ".relocating";

    // Pairs up every partition that exists on EITHER side of the rename, by walking both the
    // old and new partition sequences together and stopping once neither side has anything at
    // an index. This is what makes a resumed relocation correct: if partition 0 already moved
    // in a previous, interrupted attempt, GetExistingPartitionFilePaths(oldBaseFilePath) alone
    // would see a "gap" at index 0 and report zero partitions left to move, even though later
    // partitions might still be stranded under the old name.
    private static List<(string Old, string New)> GetPartitionPathPairs(
        string oldBaseFilePath,
        string newBaseFilePath
    )
    {
        var pairs = new List<(string Old, string New)>();
        for (var index = 0; ; index++)
        {
            var oldPath = MessageExporter.GetPartitionFilePath(oldBaseFilePath, index);
            var newPath = MessageExporter.GetPartitionFilePath(newBaseFilePath, index);
            if (!File.Exists(oldPath) && !File.Exists(newPath))
                break;

            pairs.Add((oldPath, newPath));
        }

        return pairs;
    }

    // Pairs up the scratch/temp files a crash could leave behind for a channel, so they follow
    // a rename instead of being stranded under the old name where CrashRecovery (which always
    // computes its paths from the channel's *current* name) would never find them again.
    // Covers everything CrashRecovery.RecoverAsync looks for: the streaming-append temp and its
    // own mid-write temp, a stranded partial merge, each partition's bare writer temp (including
    // one slot past the last finalized partition, for one that was still being written), and any
    // fetch-overflow partitions the streaming-append writer produced.
    private static IEnumerable<(string Old, string New)> GetScratchPathPairs(
        string oldBaseFilePath,
        string newBaseFilePath,
        int partitionCount
    )
    {
        yield return ($"{oldBaseFilePath}.new.tmp", $"{newBaseFilePath}.new.tmp");
        yield return ($"{oldBaseFilePath}.new.tmp.tmp", $"{newBaseFilePath}.new.tmp.tmp");
        yield return ($"{oldBaseFilePath}.merged.tmp", $"{newBaseFilePath}.merged.tmp");

        for (var index = 0; index <= partitionCount; index++)
        {
            var oldPartition = MessageExporter.GetPartitionFilePath(oldBaseFilePath, index);
            var newPartition = MessageExporter.GetPartitionFilePath(newBaseFilePath, index);
            yield return ($"{oldPartition}.tmp", $"{newPartition}.tmp");
        }

        var oldAppendBase = $"{oldBaseFilePath}.new.tmp";
        var newAppendBase = $"{newBaseFilePath}.new.tmp";
        for (var index = 1; ; index++)
        {
            var oldOverflow = MessageExporter.GetPartitionFilePath(oldAppendBase, index);
            var newOverflow = MessageExporter.GetPartitionFilePath(newAppendBase, index);
            if (!File.Exists(oldOverflow) && !File.Exists(newOverflow))
                break;

            yield return (oldOverflow, newOverflow);
            yield return ($"{oldOverflow}.tmp", $"{newOverflow}.tmp");
        }
    }

    // Moves an existing output (all of its partitions and crash-recovery scratch files, plus its
    // assets folder if that folder still follows the default naming convention) from its old,
    // stale-named location over to the freshly-computed path for the channel's current name.
    // Safe to call again on a relocation that was already partially completed (some files already
    // sitting at the new path, others still at the old one) -- each file is only moved if it's
    // still at the old path and nothing is already at the new one. Returns false without leaving
    // any partitions renamed if the destination is already occupied by some other (unrelated)
    // file, or if the fresh name can't be created at all (e.g. it's too long for the filesystem
    // -- Discord allows up to 100 characters each for guild/category/channel names, which can
    // combine into a file name longer than what the OS allows, especially with multi-byte
    // Unicode names). Either way, the caller falls back to writing under the old name instead.
    public static bool TryRenameToFreshOutputPath(
        string oldBaseFilePath,
        string newBaseFilePath,
        string newAssetsDirPath
    )
    {
        var partitionPairs = GetPartitionPathPairs(oldBaseFilePath, newBaseFilePath);
        if (partitionPairs.Count == 0)
        {
            // Nothing left at either path -- most likely a stale marker from a relocation that
            // actually finished except for deleting it. Clean it up so this isn't retried forever.
            TryDeleteMarker(GetRelocationMarkerPath(newBaseFilePath));
            return false;
        }

        // A real conflict is a new-path slot that's occupied by something other than this same
        // relocation continuing -- i.e. both the old and the new path exist at that index. A new
        // path existing alone (old path already gone) just means that partition was already
        // moved by a previous, interrupted attempt, which is fine to resume from.
        if (partitionPairs.Any(pair => File.Exists(pair.New) && File.Exists(pair.Old)))
            return false;

        // The fresh path may live in a directory that doesn't exist yet, e.g. when an output
        // template like "%G/%T/%C/" expands into a different directory tree after a rename.
        var newDirPath = Path.GetDirectoryName(newBaseFilePath);
        if (!string.IsNullOrWhiteSpace(newDirPath))
            Directory.CreateDirectory(newDirPath);

        var markerPath = GetRelocationMarkerPath(newBaseFilePath);
        try
        {
            File.WriteAllText(markerPath, oldBaseFilePath);
        }
        catch (IOException) { }

        var scratchPairs = GetScratchPathPairs(
            oldBaseFilePath,
            newBaseFilePath,
            partitionPairs.Count
        );

        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var (oldPath, newPath) in partitionPairs.Concat(scratchPairs))
            {
                if (!File.Exists(oldPath) || File.Exists(newPath))
                    continue;

                File.Move(oldPath, newPath);
                moved.Add((oldPath, newPath));
            }
        }
        catch (IOException)
        {
            // Undo whatever was already moved before bailing out, so we never leave the export
            // split between the old and new names. The rollback itself can fail if the
            // filesystem is in a bad state (e.g., became read-only between the forward move and
            // the undo); swallow that too so a broken rollback never propagates an exception
            // through RelocateIfNeeded into the channel exporter.
            foreach (var (from, to) in moved)
            {
                try
                {
                    File.Move(to, from);
                }
                catch (IOException) { }
            }
            TryDeleteMarker(markerPath);
            return false;
        }

        // Only follow along with the assets folder if it's still sitting at the default
        // location (output path + "_Files"); a custom --media-dir is left untouched since it
        // might not even be derived from the channel's name.
        var oldAssetsDirPath = Path.TrimEndingDirectorySeparator($"{oldBaseFilePath}_Files");
        var newAssetsDirPathTrimmed = Path.TrimEndingDirectorySeparator(newAssetsDirPath);
        if (
            string.Equals(
                newAssetsDirPathTrimmed,
                Path.TrimEndingDirectorySeparator($"{newBaseFilePath}_Files"),
                StringComparison.Ordinal
            )
            && Directory.Exists(oldAssetsDirPath)
            && !Directory.Exists(newAssetsDirPathTrimmed)
        )
        {
            // Best-effort only: the partitions (the actual message history) have already
            // been renamed successfully at this point, which is what matters. If the assets
            // folder can't follow along (e.g. same path-length issue), just leave it under
            // the old name rather than undoing the partition rename over a non-essential
            // side effect.
            try
            {
                Directory.Move(oldAssetsDirPath, newAssetsDirPathTrimmed);
            }
            catch (IOException) { }
        }

        TryDeleteMarker(markerPath);
        return true;
    }

    private static void TryDeleteMarker(string markerPath)
    {
        try
        {
            File.Delete(markerPath);
        }
        catch (IOException) { }
    }

    // Entry point used by both the channel exporter and the convert command: if nothing exists
    // yet at the request's freshly-computed output path, look for a stale-named file tracking
    // the same channel ID and either rename it into place, or -- if the fresh path is already
    // occupied -- redirect the request to keep writing to the old file.
    public static void RelocateIfNeeded(ExportRequest request)
    {
        if (File.Exists(request.OutputFilePath))
        {
            // The common case (nothing to relocate) returns right above at negligible cost. The
            // marker check below covers the rare case where a previous relocation got killed
            // partway through -- cheap (one extra File.Exists), so safe to do on every run.
            var markerPath = GetRelocationMarkerPath(request.OutputFilePath);
            if (File.Exists(markerPath))
            {
                string? oldBaseFilePath;
                try
                {
                    oldBaseFilePath = File.ReadAllText(markerPath).Trim();
                }
                catch (IOException)
                {
                    oldBaseFilePath = null;
                }

                if (!string.IsNullOrWhiteSpace(oldBaseFilePath))
                {
                    TryRenameToFreshOutputPath(
                        oldBaseFilePath,
                        request.OutputFilePath,
                        request.AssetsDirPath
                    );
                }
            }

            return;
        }

        var existingPath = FindExistingBaseFilePath(
            request.OutputSearchRootDirPath,
            request.Channel.Id.ToString(),
            request.Format.GetFileExtension()
        );

        if (existingPath is null)
            return;

        var renamed = TryRenameToFreshOutputPath(
            existingPath,
            request.OutputFilePath,
            request.AssetsDirPath
        );

        if (!renamed)
            request.RedirectOutputFilePath(existingPath);
    }
}
