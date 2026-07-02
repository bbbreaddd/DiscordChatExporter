# Using the CLI

## Step 1

After extracting the `.zip` archive, open your preferred terminal.

## Step 2

Change the current directory to DCE's folder with `cd C:\path\to\DiscordChatExporter` (`cd /path/to/DiscordChatExporter` on **MacOS** and **Linux**), then press ENTER to run the command.

**Windows** users can quickly get the folder's path by clicking the address bar while inside the folder.
![Copy path from Explorer](https://i.imgur.com/XncnhC2.gif)

**macOS** users can press Command+Option+C (⌘⌥C) while inside the folder (or selecting it) to copy its path to the clipboard.

You can also drag and drop the folder on **every platform**.
![Drag and drop folder](https://i.imgur.com/sOpZQAb.gif)

## Step 3

Now we're ready to run the commands.

Type the following command in your terminal of choice, then press ENTER to run it. This will list all available subcommands and options.

```console
./DiscordChatExporter.Cli
```

> **Note**:
> On Windows, if you're using the default Command Prompt (`cmd`), omit the leading `./` at the start of the command.

> **Docker** users, please refer to the [Docker usage instructions](Docker.md).

## CLI commands

| Command     | Description                                          |
| ----------- | ---------------------------------------------------- |
| export      | Exports a channel                                    |
| exportdm    | Exports all direct message channels                  |
| exportguild | Exports all channels within the specified server     |
| exportall   | Exports all accessible channels                      |
| convert     | Converts an existing JSON export to another format   |
| channels    | Outputs the list of channels in the given server     |
| dm          | Outputs the list of direct message channels          |
| guilds      | Outputs the list of accessible servers               |
| guide       | Explains how to obtain token, server, and channel ID |

To use the commands, you'll need a token. For the instructions on how to get a token, please refer to [this page](Token-and-IDs.md), or run `./DiscordChatExporter.Cli guide`.

To get help with a specific command, run:

```console
./DiscordChatExporter.Cli command --help
```

For example, to figure out how to use the `export` command, run:

```console
./DiscordChatExporter.Cli export --help
```

## Authentication

Most commands need an authentication token, provided with `-t|--token`, or through the
`DISCORD_TOKEN` environment variable (handy for scripts, so the token doesn't end up in your shell
history or process list).

If you have more than one token (e.g. a personal account plus a bot, or several bots), put them in
a text file, one per line (`#` starts a comment), and pass it with `--token-file` instead. The
tokens are used as fallbacks for one another: if a request fails because the active token is
invalid, lacks access to the resource, or is stuck being rate limited, DCE automatically retries
with the next token in the file.

```console
./DiscordChatExporter.Cli export --token-file tokens.txt -c 53555
```

#### Rate limits

By default, DCE respects Discord's advisory rate limits, which are deliberately stricter than what
the server actually enforces, in order to minimize the chance of your account/bot getting flagged.
If a request can't go through yet, DCE will pause (visibly, in the CLI's progress display) until
it's safe to continue. To prioritize speed instead, and only back off when Discord returns an
actual hard rate limit error (HTTP 429), disable this with `--respect-rate-limits false`. Use this
with caution, especially on a user token, since it increases the risk of getting rate limited or
flagged for abuse.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --respect-rate-limits false
```

## Export a specific channel

You can quickly export with DCE's default settings by using just `-t token` and `-c channelid`.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555
```

#### Changing the format

You can change the export format to `HtmlDark`, `HtmlLight`, `PlainText` `Json` or `Csv` with `-f format`. The default
format is `HtmlDark`.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 -f Json
```

#### Changing the output filename

You can change the filename by using `-o name.ext`. e.g. for the `HTML` format:

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 -o myserver.html
```

#### Changing the output directory

You can change the export directory by using `-o` and providing a path that ends with a slash or does not have a file
extension.
If any of the folders in the path have a space in its name, escape them with quotes (").

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 -o "C:\Discord Exports"
```

#### Changing the filename and output directory

You can change both the filename and export directory by using `-o directory\name.ext`.
Note that the filename must have an extension, otherwise it will be considered a directory name.
If any of the folders in the path have a space in its name, escape them with quotes (").

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 -o "C:\Discord Exports\myserver.html"
```

#### Generating the filename and output directory dynamically

You can use template tokens to generate the output file path based on the server and channel metadata.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 -o "C:\Discord Exports\%G\%T\%C.html"
```

Assuming you are exporting a channel named `"my-channel"` in the `"Text channels"` category from a server
called `"My server"`, you will get the following output file
path: `C:\Discord Exports\My server\Text channels\my-channel.html`

Here is the full list of supported template tokens:

- `%g` - server ID
- `%G` - server name
- `%t` - category ID
- `%T` - category name
- `%c` - channel ID
- `%C` - channel name
- `%p` - channel position
- `%P` - category position
- `%a` - the "after" date
- `%b` - the "before" date
- `%d` - the current date
- `%%` - escapes `%`

#### Partitioning

You can use partitioning to split files after a given number of messages or file size.
For example, a channel with 36 messages set to be partitioned every 10 messages will output 4 files.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 -p 10
```

A 45 MB channel set to be partitioned every 20 MB will output 3 files.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 -p 20mb
```

#### Downloading assets

If this option is set, the export will include additional files such as user avatars, attached files, images, etc.
Only files that are referenced by the export are downloaded, which means that, for example, user avatars will not be
downloaded when using the plain text (TXT) export format.
A folder containing the assets will be created along with the exported chat. They must be kept together.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --media
```

When exporting to the SQLite database format (`-f Db`) or using `watchguild --media`, Discord-hosted media is saved
next to the database by default in a sharded media directory (for example, `attachments/ab/cd/file.png`) and the local
paths are recorded in the database. This avoids putting very large numbers of files into one folder.

#### Reusing assets

Previously downloaded assets can be reused to skip redundant downloads as long as the chat is always exported to the
same folder. Using this option can speed up future exports. This option requires the `--media` option.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --media --reuse-media
```

#### Caching assets without rewriting URLs

`--cache-media` downloads assets the same way `--media` does, but keeps the original Discord CDN
URLs in the export instead of replacing them with local file paths. This is useful for warming a
media cache ahead of time (e.g. before running [`convert`](Convert.md) with `--media --reuse-media`
against the same `--media-dir`), without making the export's URLs depend on local paths. It cannot
be combined with `--media`.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 -f Json --cache-media --media-dir "C:\Discord Media"
```

#### Changing the media directory

By default, the media directory is created alongside the exported chat. You can change this by using `--media-dir` and
providing a path that ends with a slash. All of the exported media will be stored in this directory.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --media --media-dir "C:\Discord Media"
```

#### Changing the date format

You can customize how dates are formatted in the exported files by using `--locale` and inserting one of Discord's
locales. The default locale is `en-US`.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --locale "de-DE"
```

#### Date ranges

**Messages sent before a date**
Use `--before` to export messages sent before the provided date. E.g. messages sent before September 18th, 2019:

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --before 2019-09-18
```

**Messages sent after a date**
Use `--after` to export messages sent after the provided date. E.g. messages sent after September 17th, 2019 11:34 PM:

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --after "2019-09-17 23:34"
```

**Messages sent in a date range**
Use `--before` and `--after` to export messages sent during the provided date range. E.g. messages sent between
September 17th, 2019 11:34 PM and September 18th:

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --after "2019-09-17 23:34" --before "2019-09-18"
```

You can try different formats like `17-SEP-2019 11:34 PM` or even refine your ranges down to
milliseconds `17-SEP-2019 23:45:30.6170`!
Don't forget to quote (") the date if it has spaces!
More info about .NET date
formats [here](https://docs.microsoft.com/en-us/dotnet/standard/base-types/custom-date-and-time-format-strings).

#### Filtering messages

Use `--filter` to filter what messages are included in the export.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --filter "from:Tyrrrz has:image"
```

Documentation on message filter syntax can be found [here](https://github.com/Tyrrrz/DiscordChatExporter/blob/prime/.docs/Message-filters.md).

#### Incremental exports

`--incremental` appends newly posted messages to an existing JSON export instead of re-exporting
the whole channel from scratch. It only works with the JSON format (`-f Json`), and keeps a small
manifest file alongside the output to remember where each channel left off. This is the option to
reach for when re-running the same export repeatedly (e.g. on a schedule):

```console
./DiscordChatExporter.Cli exportguild -t "mfa.Ifrn" -g 21814 -f Json -o "C:\Discord Exports\" --incremental
```

If you need other formats too, export incrementally to JSON, then use [`convert`](Convert.md) to
derive `HtmlDark`/`PlainText`/etc. from it -- that way the (slower) Discord API calls only ever
fetch new messages, and the format conversion stays a fast, offline step.

#### Exporting messages in reverse order

By default, messages are fetched oldest-first. Use `--reverse` to fetch newest-first instead.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --reverse
```

#### Normalizing timestamps to UTC

By default, message timestamps are shown in their original timezone (as recorded by Discord). Use
`--utc` to normalize every timestamp in the export to UTC+0 instead.

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 --utc
```

### Export channels from a specific server

To export all channels in a specific server, use the `exportguild` command and provide the server ID through the `-g|--guild` option:

```console
./DiscordChatExporter.Cli exportguild -t "mfa.Ifrn" -g 21814
```

#### Including threads

By default, threads are not included in the export. You can change this behavior by using `--include-threads` and
specifying which threads should be included. It has possible values of `none`, `active`, or `all`, indicating which
threads should be included. To include both active and archived threads, use `--include-threads all`.

```console
./DiscordChatExporter.Cli exportguild -t "mfa.Ifrn" -g 21814 --include-threads all
```

#### Including voice channels

By default, voice channels are included in the export. You can change this behavior by using `--include-vc` and
specifying whether to include voice channels in the export. It has possible values of `true` or `false`, to exclude
voice channels, use `--include-vc false`.

```console
./DiscordChatExporter.Cli exportguild -t "mfa.Ifrn" -g 21814 --include-vc false
```

#### Exporting in parallel

By default, channels are exported one at a time. Use `--parallel` to export multiple channels
concurrently, which can significantly speed up large servers -- at the cost of making more
simultaneous requests, which increases the chance of getting rate limited.

```console
./DiscordChatExporter.Cli exportguild -t "mfa.Ifrn" -g 21814 --parallel 4
```

### Export all channels

To export all accessible channels, use the `exportall` command:

```console
./DiscordChatExporter.Cli exportall -t "mfa.Ifrn"
```

#### Excluding DMs

To exclude DMs, add the `--include-dm false` option.

```console
./DiscordChatExporter.Cli exportall -t "mfa.Ifrn" --include-dm false
```

#### Excluding server channels

To export only DMs and skip server channels, add the `--include-guilds false` option.

```console
./DiscordChatExporter.Cli exportall -t "mfa.Ifrn" --include-guilds false
```

#### Exporting from a Discord data package

If you've requested your data from Discord (User Settings → Privacy & Safety → Request all of my
data), you can point `--data-package` at the downloaded ZIP file to only export the channels
referenced in it, instead of pulling the full list of currently-accessible channels from the API.
This is the only way to export channels/servers you no longer have access to, as long as you were
still a member when the data package was generated.

```console
./DiscordChatExporter.Cli exportall -t "mfa.Ifrn" --data-package "package.zip"
```

### Convert an existing JSON export to another format

To convert a previously exported JSON file into `PlainText`, `Csv`, `HtmlDark`, or `HtmlLight`,
use the `convert` command. This works offline and doesn't require a token:

```console
./DiscordChatExporter.Cli convert -i export.json -f HtmlDark -o export.html
```

Use `--skip-unchanged` for repeat batch conversions to skip outputs that are already newer than
their source JSON exports.

See [Converting existing exports](Convert.md) for more details, including batch conversion and
output path templating.

### List channels in a server

To list the channels available in a specific server, use the `channels` command and provide the server ID through the `-g|--guild` option:

```console
./DiscordChatExporter.Cli channels -t "mfa.Ifrn" -g 21814
```

By default, voice channels are included and threads are not. Use `--include-vc false` to exclude
voice channels, and `--include-threads active` or `--include-threads all` to also list active, or
all (including archived), threads under each channel.

```console
./DiscordChatExporter.Cli channels -t "mfa.Ifrn" -g 21814 --include-vc false --include-threads all
```

### List direct message channels

To list all DM channels accessible to the current account, use the `dm` command:

```console
./DiscordChatExporter.Cli dm -t "mfa.Ifrn"
```

### List servers

To list all servers accessible by the current account, use the `guilds` command:

```console
./DiscordChatExporter.Cli guilds -t "mfa.Ifrn" > C:\path\to\output.txt
```
