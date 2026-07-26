using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotBTreader.Host.Cli.Cli;

public static class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async ValueTask WriteAsync(
        CliExecution execution,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(output);

        if (json)
        {
            string serialized = JsonSerializer.Serialize(execution.Envelope, JsonOptions);
            await output.WriteLineAsync(serialized.AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(execution.HumanMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (string warning in execution.Envelope.Warnings)
        {
            await output.WriteLineAsync($"warning: {warning}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        foreach (CliError error in execution.Envelope.Errors)
        {
            await output.WriteLineAsync($"error [{error.Code}]: {error.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
