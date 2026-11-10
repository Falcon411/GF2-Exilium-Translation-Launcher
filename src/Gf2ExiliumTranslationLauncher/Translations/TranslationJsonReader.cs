using System.Globalization;
using System.Text.Json;
using Gf2ExiliumTranslationLauncher.Diagnostics;
using Gf2ExiliumTranslationLauncher.Localization;

namespace Gf2ExiliumTranslationLauncher.Translations;

public sealed class TranslationJsonReader
{
    public async Task<IReadOnlyList<TranslationEntry>> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        AppLog.Info($"Reading translation JSON. File={AppLog.DescribePath(filePath)}");
        if (!File.Exists(filePath))
        {
            AppLog.Warn($"Translation JSON file not found. File={filePath}");
            throw new FileNotFoundException(AppText.Format("ErrorFileNotFound", filePath), filePath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var streamOptions = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };

        try
        {
            await using var file = new FileStream(filePath, streamOptions);
            using var document = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                AppLog.Warn($"Translation JSON root is not an object. File={filePath}, RootKind={document.RootElement.ValueKind}");
                throw new FormatException(AppText.Get("ErrorJsonRootObjectRequired"));
            }

            var translationEntries = new List<TranslationEntry>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (id, original) = ParseId(property.Name.AsSpan());
                translationEntries.Add(new TranslationEntry
                {
                    Id = id,
                    Original = original,
                    Translation = ReadTranslationValue(property.Value)
                });
            }

            AppLog.Info($"Translation JSON read successfully. File={filePath}, Entries={translationEntries.Count}");
            return translationEntries;
        }
        catch (JsonException exception)
        {
            AppLog.Error($"Translation JSON parser failed. File={filePath}", exception);
            throw CreateJsonFormatException(filePath, exception);
        }
        catch (FormatException exception)
        {
            AppLog.Error($"Translation JSON format failed. File={filePath}", exception);
            throw;
        }
    }

    private static string ReadTranslationValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => string.Empty,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => value.ToString()
        };

    private static (int Id, string Original) ParseId(ReadOnlySpan<char> inputWithId)
    {
        const string prefix = "#ID:";
        if (!inputWithId.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new FormatException(AppText.Format("ErrorInputUnexpectedFormat", inputWithId.ToString()));
        }

        var separatorIndex = inputWithId.IndexOf('|');
        if (separatorIndex <= prefix.Length)
        {
            throw new FormatException(AppText.Format("ErrorInputUnexpectedFormat", inputWithId.ToString()));
        }

        var idSpan = inputWithId.Slice(prefix.Length, separatorIndex - prefix.Length);
        if (!int.TryParse(idSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            throw new FormatException(AppText.Format("ErrorParseTranslationId", inputWithId.ToString()));
        }

        var original = inputWithId[(separatorIndex + 1)..].ToString();
        return (id, original);
    }

    private static FormatException CreateJsonFormatException(string filePath, JsonException exception)
    {
        var message = AppText.Format(
            "ErrorParseTranslationJson",
            filePath,
            exception.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? AppText.Get("TextUnknown"),
            exception.BytePositionInLine?.ToString(CultureInfo.InvariantCulture) ?? AppText.Get("TextUnknown"));

        var line = ReadLineSnippet(filePath, exception.LineNumber);
        if (!string.IsNullOrWhiteSpace(line))
        {
            message += $" {AppText.Format("ErrorProblemLine", line)}";

            var hint = BuildHint(line);
            if (!string.IsNullOrWhiteSpace(hint))
            {
                message += $" {hint}";
            }
        }
        else
        {
            message += $" {exception.Message}";
        }

        return new FormatException(message, exception);
    }

    private static string ReadLineSnippet(string filePath, long? zeroBasedLineNumber)
    {
        if (zeroBasedLineNumber is null or < 0)
        {
            return "";
        }

        try
        {
            long currentLine = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                if (currentLine == zeroBasedLineNumber.Value)
                {
                    var trimmed = line.Trim();
                    return trimmed.Length <= 220 ? trimmed : $"{trimmed[..220]}...";
                }

                currentLine++;
            }
        }
        catch
        {
            return "";
        }

        return "";
    }

    private static string BuildHint(string line)
    {
        if (line.StartsWith("<<<<<<<", StringComparison.Ordinal)
            || line.StartsWith("=======", StringComparison.Ordinal)
            || line.StartsWith(">>>>>>>", StringComparison.Ordinal))
        {
            return AppText.Get("HintMergeConflict");
        }

        if (line.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return AppText.Get("HintHtmlInsteadOfJson");
        }

        return "";
    }
}
