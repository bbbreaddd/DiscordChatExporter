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

    // Moves an existing output (all of its partitions, plus its assets folder if that folder
    // still follows the default naming convention) from its old, stale-named location over to
    // the freshly-computed path for the channel's current name. Returns false without leaving
    // any partitions renamed if the destination is already occupied by some other file, or if
    // the fresh name can't be created at all (e.g. it's too long for the filesystem -- Discord
    // allows up to 100 characters each for guild/category/channel names, which can combine into
    // a file name longer than what the OS allows, especially with multi-byte Unicode names).
    // Either way, the caller falls back to writing under the old name instead.
    public static bool TryRenameToFreshOutputPath(
        string oldBaseFilePath,
        string newBaseFilePath,
        string newAssetsDirPath
    )
    {
        var oldPartitionPaths = GetExistingPartitionFilePaths(oldBaseFilePath);
        var newPartitionPaths = oldPartitionPaths
            .Select((_, index) => MessageExporter.GetPartitionFilePath(newBaseFilePath, index))
            .ToArray();

        if (newPartitionPaths.Any(File.Exists))
            return false;

        // The fresh path may live in a directory that doesn't exist yet, e.g. when an output
        // template like "%G/%T/%C/" expands into a different directory tree after a rename.
        var newDirPath = Path.GetDirectoryName(newBaseFilePath);
        if (!string.IsNullOrWhiteSpace(newDirPath))
            Directory.CreateDirectory(newDirPath);

        var movedCount = 0;
        try
        {
            for (; movedCount < oldPartitionPaths.Length; movedCount++)
                File.Move(oldPartitionPaths[movedCount], newPartitionPaths[movedCount]);
        }
        catch (IOException)
        {
            // Undo whatever partitions were already moved before bailing out, so we never
            // leave the export split between the old and new names. The rollback itself can
            // fail if the filesystem is in a bad state (e.g., became read-only between the
            // forward move and the undo); swallow that too so a broken rollback never
            // propagates an exception through RelocateIfNeeded into the channel exporter.
            try
            {
                for (var i = 0; i < movedCount; i++)
                    File.Move(newPartitionPaths[i], oldPartitionPaths[i]);
            }
            catch (IOException) { }
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

        return true;
    }

    // Entry point used by both the channel exporter and the convert command: if nothing exists
    // yet at the request's freshly-computed output path, look for a stale-named file tracking
    // the same channel ID and either rename it into place, or -- if the fresh path is already
    // occupied -- redirect the request to keep writing to the old file.
    public static void RelocateIfNeeded(ExportRequest request)
    {
        if (File.Exists(request.OutputFilePath))
            return;

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
