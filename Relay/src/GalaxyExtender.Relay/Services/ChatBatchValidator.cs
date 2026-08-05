using GalaxyExtender.Relay.Contracts;
using GalaxyExtender.Relay.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Contract validation for an incoming chat batch.
///
/// A violation rejects the whole batch with 400 rather than silently dropping or truncating the
/// offending line. The extension is specified to enforce the same limits, so anything arriving out
/// of bounds is a bug worth surfacing loudly — silently mangling guild chat would be far harder to
/// notice than a rejected batch.
/// </summary>
public static class ChatBatchValidator
{
    private const int MaxIdentifierLength = 64;

    private const string ControlCharacterError = "Must not contain control characters.";

    /// <summary>
    /// C0 controls and DEL are rejected everywhere. The extension maps them to spaces before
    /// sending, so a legal client never trips this; rejecting them here keeps forged newlines out
    /// of the relay's own log lines and out of the Phase 3 Discord messages.
    /// </summary>
    private static bool ContainsControlCharacters(string value)
    {
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7F)
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryValidate(
        ChatBatchRequest? request,
        RelayOptions options,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>();

        if (request is null)
        {
            errors["request"] = ["Body is required."];
            return false;
        }

        ValidateBatchId(request.BatchId, errors);
        ValidateClient(request.Client, errors);
        ValidateLines(request.Lines, options, errors);

        return errors.Count == 0;
    }

    private static void ValidateBatchId(string? batchId, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            errors["batchId"] = ["Required. Must be a GUID, reused unchanged when retrying a batch."];
        }
        else if (!Guid.TryParse(batchId, out _))
        {
            // Enforced as a GUID so the Phase 2 idempotency store has a bounded, well-formed key.
            errors["batchId"] = ["Must be a GUID."];
        }
    }

    private static void ValidateClient(ChatClient? client, Dictionary<string, string[]> errors)
    {
        if (client is null)
        {
            errors["client"] = ["Required."];
            return;
        }

        if (string.IsNullOrWhiteSpace(client.Id))
        {
            errors["client.id"] = ["Required."];
        }
        else if (client.Id.Length > MaxIdentifierLength)
        {
            errors["client.id"] = [$"Must be {MaxIdentifierLength} characters or fewer."];
        }
        else if (ContainsControlCharacters(client.Id))
        {
            errors["client.id"] = [ControlCharacterError];
        }

        ValidateOptionalIdentifier(client.Character, "client.character", errors);
        ValidateOptionalIdentifier(client.Galaxy, "client.galaxy", errors);
    }

    private static void ValidateOptionalIdentifier(
        string? value,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > MaxIdentifierLength)
        {
            errors[field] = [$"Must be {MaxIdentifierLength} characters or fewer."];
        }
        else if (ContainsControlCharacters(value))
        {
            errors[field] = [ControlCharacterError];
        }
    }

    private static void ValidateLines(
        IReadOnlyList<ChatLine>? lines,
        RelayOptions options,
        Dictionary<string, string[]> errors)
    {
        if (lines is null || lines.Count == 0)
        {
            errors["lines"] = ["At least one line is required."];
            return;
        }

        if (lines.Count > options.MaxLinesPerBatch)
        {
            errors["lines"] = [$"At most {options.MaxLinesPerBatch} lines per batch."];
            return;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            // System.Text.Json deserialises a `null` array element into a null ChatLine —
            // nullable annotations are not enforced at runtime. Without this guard the
            // dereference below is a 500, which the contract tells clients to retry with the
            // same batchId: a poison message. A 400 naming the element is the correct answer.
            if (line is null)
            {
                errors[$"lines[{index}]"] = ["Must be an object."];
                continue;
            }

            if (string.IsNullOrWhiteSpace(line.Text))
            {
                errors[$"lines[{index}].text"] = ["Required and must not be blank."];
            }
            else if (line.Text.Length > options.MaxLineLength)
            {
                errors[$"lines[{index}].text"] =
                    [$"Must be {options.MaxLineLength} characters or fewer."];
            }
            else if (ContainsControlCharacters(line.Text))
            {
                errors[$"lines[{index}].text"] = [ControlCharacterError];
            }

            if (line.Occurrence is null)
            {
                errors[$"lines[{index}].occurrence"] = ["Required."];
            }
            else if (line.Occurrence < 1)
            {
                errors[$"lines[{index}].occurrence"] =
                    ["Must be 1 or greater — it counts occurrences including the current one."];
            }

            if (line.ClientSeq is < 0)
            {
                errors[$"lines[{index}].clientSeq"] = ["Must not be negative."];
            }
        }
    }
}
