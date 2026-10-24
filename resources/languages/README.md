**Language:** English · [Deutsch](README.de.md)

# DLSS Manager language files

This directory contains the **application interface translations**. README translations are separate.

```text
resources/languages/
├── de-de.ini     German
├── en-en.ini     English (fallback)
└── README.md     Format and contribution guide
```

Keep this directory inside resources, next to the other application resources. Language packs are UTF-8 text files; no additional libraries are required.

## Selecting a language

Open **Settings → General → Language**.

- The default setting is **System language (automatic)**.
- The manager reads the Windows display language, tries an exact match, then an available pack from the same language family. For example, `de-AT` can use `de-de.ini` and `en-US` can use `en-en.ini`.
- If the language is unavailable, the manager uses English.
- An explicit selection is saved as `Language=de-de`, `Language=en-en`, or `Language=system` under `[Settings]` in data/manager_config.ini.
- Applying a new selection restarts the manager. Finish active scans and updates and close other dialogs first.

Missing translation keys fall back to English. The manager also includes an English emergency fallback so missing external files do not leave the application without readable labels.

The filenames use lowercase language-region identifiers. `en-en` is this project's English fallback identifier; regional English settings are mapped to it automatically.

## File format

Each pack contains metadata and a string dictionary:

```ini
[meta]
schemaVersion=1
code=en-en
name=English

[strings]
language.title=Language
language.system=System language (automatic)
```

The shipped files contain the complete dictionary. `text_…` keys are stable identifiers generated from the original source labels. Keep those keys unchanged when translating.

Values use JSON-style escaping **without surrounding quotation marks**:

| Escape | Meaning |
| --- | --- |
| `\n` | New line |
| `\r` | Carriage return |
| `\t` | Tab |
| `\\` | Literal backslash |
| `\"` | Literal quotation mark |

Leading and trailing spaces in values are significant: some messages combine several translated fragments with a filename, version, count, or game name. Preserve these spaces and any surrounding punctuation. Keep technical filenames, executable extensions, INI key names, numbers used by mod settings, and protocol names unchanged.

Native Windows dialog buttons and operating-system error messages may follow the Windows language rather than the selected application language. Background-worker protocol identifiers and diagnostic logs are not language selectors; their displayed status text is translated by the manager.

## Adding a translation

1. Copy `en-en.ini`, for example to `fr-fr.ini`.
2. Set `code=fr-fr` and a native display name such as `name=Français` in `[meta]`.
3. Translate the values in `[strings]`, preserving all keys, escapes, significant spaces, and technical values.
4. Keep related status words consistent with their full status labels; status and filter labels share the same dictionary.
5. Save the file as UTF-8 and place it in this directory.
6. Restart the manager and select the new language in Settings.
7. Check the main window, settings tabs, context menus, scan review, mod settings, and update messages. Confirm that dropdown values still save the intended technical values.

Please contribute complete translations based on the current English pack. The English fallback makes partial packs usable, but a complete pack provides a more consistent interface.
