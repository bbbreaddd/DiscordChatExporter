using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using JsonExtensions.Reading;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Discord.Data;

// https://discord.com/developers/docs/resources/poll#poll-object
public record Poll(
    string Question,
    IReadOnlyList<PollAnswer> Answers,
    DateTimeOffset? Expiry,
    bool AllowMultiselect,
    bool IsFinalized
)
{
    public static Poll Parse(JsonElement json)
    {
        var question =
            json.GetProperty("question").GetPropertyOrNull("text")?.GetStringOrNull() ?? "";

        var voteCountsByAnswerId =
            json.GetPropertyOrNull("results")
                ?.GetPropertyOrNull("answer_counts")
                ?.EnumerateArrayOrNull()
                ?.ToDictionary(
                    j => j.GetProperty("id").GetInt32(),
                    j => j.GetProperty("count").GetInt32()
                )
            ?? new Dictionary<int, int>();

        var answers =
            json.GetPropertyOrNull("answers")
                ?.EnumerateArrayOrNull()
                ?.Select(j => PollAnswer.Parse(j, voteCountsByAnswerId))
                .ToArray()
            ?? [];

        var expiry = json.GetPropertyOrNull("expiry")?.GetDateTimeOffsetOrNull();
        var allowMultiselect =
            json.GetPropertyOrNull("allow_multiselect")?.GetBooleanOrNull() ?? false;
        var isFinalized =
            json.GetPropertyOrNull("results")?.GetPropertyOrNull("is_finalized")?.GetBooleanOrNull()
            ?? false;

        return new Poll(question, answers, expiry, allowMultiselect, isFinalized);
    }
}

public record PollAnswer(int Id, string? Text, Emoji? Emoji, int VoteCount)
{
    public static PollAnswer Parse(
        JsonElement json,
        IReadOnlyDictionary<int, int> voteCountsByAnswerId
    )
    {
        var id = json.GetProperty("answer_id").GetInt32();

        var pollMedia = json.GetPropertyOrNull("poll_media");
        var text = pollMedia?.GetPropertyOrNull("text")?.GetStringOrNull();
        var emoji = pollMedia?.GetPropertyOrNull("emoji")?.Pipe(Emoji.Parse);

        var voteCount = voteCountsByAnswerId.GetValueOrDefault(id);

        return new PollAnswer(id, text, emoji, voteCount);
    }
}
