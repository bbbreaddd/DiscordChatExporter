using System.IO;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExistingOutputRelocatorSpecs
{
    [Fact]
    public void Renaming_carries_partitions_and_crash_recovery_scratch_files_to_the_new_name()
    {
        // Arrange
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var oldBasePath = Path.Combine(dir, "OldName [555].json");
            var newBasePath = Path.Combine(dir, "NewName [555].json");

            File.WriteAllText(oldBasePath, "partition 0");
            // A streaming-append batch that was fetched but never merged before the rename.
            File.WriteAllText($"{oldBasePath}.new.tmp", "unmerged new messages");
            // A bare writer temp for a partition that was still being written.
            File.WriteAllText($"{oldBasePath}.tmp", "in-progress partition 1 writer temp");

            // Act
            var renamed = ExistingOutputRelocator.TryRenameToFreshOutputPath(
                oldBasePath,
                newBasePath,
                $"{newBasePath}_Files"
            );

            // Assert
            renamed.Should().BeTrue();
            File.Exists(newBasePath).Should().BeTrue();
            File.Exists($"{newBasePath}.new.tmp")
                .Should()
                .BeTrue("crash recovery looks for this under the *new* name");
            File.Exists($"{newBasePath}.tmp").Should().BeTrue();

            File.Exists(oldBasePath).Should().BeFalse();
            File.Exists($"{oldBasePath}.new.tmp").Should().BeFalse();
            File.Exists($"{oldBasePath}.tmp").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Renaming_resumes_correctly_when_some_partitions_already_moved_in_a_prior_interrupted_attempt()
    {
        // Arrange: simulates a kill between two File.Move calls -- partition 0 already landed at
        // the new name, but partition 2 (and a scratch file) are still stuck at the old one.
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var oldBasePath = Path.Combine(dir, "OldName [555].json");
            var newBasePath = Path.Combine(dir, "NewName [555].json");

            // Partition 0 already moved.
            File.WriteAllText(newBasePath, "partition 0 (already moved)");
            // Partition 1 (part 2) still stranded at the old name.
            var oldPart2 = Path.Combine(dir, "OldName [555] [part 2].json");
            File.WriteAllText(oldPart2, "partition 1 (still old)");
            // A scratch file also still stranded.
            File.WriteAllText($"{oldBasePath}.new.tmp", "unmerged new messages");

            // Act: a naive "does the base file already exist" check would treat this as nothing
            // to do, stranding partition 1 and the scratch file forever.
            var renamed = ExistingOutputRelocator.TryRenameToFreshOutputPath(
                oldBasePath,
                newBasePath,
                $"{newBasePath}_Files"
            );

            // Assert
            renamed.Should().BeTrue();
            var newPart2 = Path.Combine(dir, "NewName [555] [part 2].json");
            File.Exists(newPart2).Should().BeTrue("the stranded partition should be picked up");
            File.Exists($"{newBasePath}.new.tmp")
                .Should()
                .BeTrue("the stranded scratch file should be picked up");

            File.Exists(oldPart2).Should().BeFalse();
            File.Exists($"{oldBasePath}.new.tmp").Should().BeFalse();

            // Partition 0, already at the new name before this call, must be untouched.
            File.ReadAllText(newBasePath).Should().Be("partition 0 (already moved)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Renaming_refuses_when_the_new_path_is_genuinely_occupied_by_something_else()
    {
        // Arrange: both the old and the new partition 0 exist independently -- a real conflict,
        // not a resume.
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var oldBasePath = Path.Combine(dir, "OldName [555].json");
            var newBasePath = Path.Combine(dir, "NewName [555].json");

            File.WriteAllText(oldBasePath, "old content");
            File.WriteAllText(newBasePath, "unrelated existing content");

            // Act
            var renamed = ExistingOutputRelocator.TryRenameToFreshOutputPath(
                oldBasePath,
                newBasePath,
                $"{newBasePath}_Files"
            );

            // Assert: nothing should have moved
            renamed.Should().BeFalse();
            File.Exists(oldBasePath).Should().BeTrue();
            File.ReadAllText(newBasePath).Should().Be("unrelated existing content");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
