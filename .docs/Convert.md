# Converting existing exports

The `convert` command takes one or more JSON exports (produced earlier with `-f Json`) and
renders them in another format — `PlainText`, `Csv`, `HtmlDark`, or `HtmlLight`. It works
entirely offline and never contacts Discord, so it's a good way to obtain the same chat log in
multiple formats without exporting it from Discord more than once.

```console
./DiscordChatExporter.Cli convert -i export.json -f HtmlDark -o export.html
```

> **Note**:
> Converting to `Json` is not supported, since the input is already JSON.

## Recommended workflow

Export each channel once, in the JSON format (which retains the most information), and then use
`convert` to derive the other formats from it:

```console
./DiscordChatExporter.Cli export -t "mfa.Ifrn" -c 53555 -f Json -o export.json
./DiscordChatExporter.Cli convert -i export.json -f HtmlDark -o export.html
./DiscordChatExporter.Cli convert -i export.json -f PlainText -o export.txt
```

## Converting multiple files

If `-i|--input` points to a directory, it will be searched recursively for `*.json` files, and
each one will be converted individually.

```console
./DiscordChatExporter.Cli convert -i "C:\Discord Exports\json" -f HtmlDark -o "C:\Discord Exports\html\"
```

When converting multiple files, `-o|--output` must either be an existing directory, end with a
slash, or contain template tokens (see below) — otherwise all the inputs would collide on the
same output file.

Use `--parallel` to convert multiple files at the same time:

```console
./DiscordChatExporter.Cli convert -i "C:\Discord Exports\json" -f HtmlDark -o "C:\Discord Exports\html\" --parallel 4
```

## Output path templating

`-o|--output` supports the same template tokens as the `export` command, based on the guild and
channel metadata stored in each JSON file:

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

```console
./DiscordChatExporter.Cli convert -i "C:\Discord Exports\json" -f HtmlDark -o "C:\Discord Exports\%G\%T\%C.html"
```

## Other options

`convert` supports most of the same options as `export`, applied while rendering the new format:

- `-f|--format` - output format (`PlainText`, `Csv`, `HtmlDark`, `HtmlLight`)
- `-p|--partition` - split the output into partitions, e.g. `-p 100` or `-p 10mb`
- `--filter` - only include messages matching a [message filter](Message-filters.md)
- `--markdown` - process markdown, mentions, and other special tokens (default: `true`)
- `--media` / `--reuse-media` / `--media-dir` - download assets referenced by the export
- `--locale` - locale used to format dates and numbers
- `--utc` - normalize all timestamps to UTC+0

## Limitations

Because `convert` works entirely from the data stored in the JSON file, without contacting
Discord, a few things can't be reproduced perfectly:

- If the JSON was exported with `--markdown` enabled (the default), mentions, timestamps, and
  other special tokens are already flattened into plain text in the message content. The
  converted output will show that flattened text rather than rich mentions/links. For the best
  fidelity in HTML output, export the source JSON with `--markdown false` first, then use
  `convert` with `--markdown true`.
- Mentions of channels or roles other than the exported channel's own role/category list can't be
  resolved by name, since the JSON export doesn't include the full server's channel and role
  list. These fall back to `#deleted-channel` / `@deleted-role`, the same as DCE's behavior for
  stale references.
- Message flags (e.g. the "forwarded" badge) aren't stored in the JSON export, so they won't
  appear in the converted output.
- For threads, the JSON export only stores the name/ID of the thread's parent channel under
  `category`/`categoryId` (not that channel's own category). As a result, the "Channel:" header
  in `PlainText`/`Csv` output, and the breadcrumb in `Html`, will show one fewer level than a
  live export of the same thread.
- Attachment, embed, and avatar URLs are copied as-is from the JSON. Discord's CDN URLs include
  a short-lived signature that expires (~24h after the original export), so links may no longer
  work by the time you run `convert`. Use `--media` to download a local copy instead.
