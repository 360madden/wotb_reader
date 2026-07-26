using WotBTreader.Application.Results;

namespace WotBTreader.Host.Cli.Cli;

public sealed record CliInvocation(
    string Command,
    IReadOnlyList<string> Positionals,
    IReadOnlyDictionary<string, string?> Options)
{
    public bool Json => Options.ContainsKey("json");

    public static OperationResult<CliInvocation> Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return OperationResult.Failure<CliInvocation>(
                new ApplicationError("cli.command.required", "A command is required."));
        }

        string command = arguments[0].Trim().ToLowerInvariant();
        if (command.Length == 0 || command.StartsWith('-'))
        {
            return OperationResult.Failure<CliInvocation>(
                new ApplicationError("cli.command.invalid", "The first argument must be a command."));
        }

        Dictionary<string, string?> options = new(StringComparer.Ordinal);
        List<string> positionals = [];
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            string option = argument[2..];
            int separator = option.IndexOf('=');
            if (separator >= 0)
            {
                string key = option[..separator];
                string value = option[(separator + 1)..];
                if (!TryAddOption(options, key, value))
                {
                    return DuplicateOption(key);
                }

                continue;
            }

            string? optionValue = null;
            if (index + 1 < arguments.Count &&
                !arguments[index + 1].StartsWith("--", StringComparison.Ordinal) &&
                OptionRequiresValue(option))
            {
                optionValue = arguments[++index];
            }

            if (!TryAddOption(options, option, optionValue))
            {
                return DuplicateOption(option);
            }
        }

        return OperationResult.Success<CliInvocation>(
            new CliInvocation(command, positionals, options));
    }

    private static bool OptionRequiresValue(string option) =>
        option is "data-root" or "format" or "limit" or "offset" or "output";

    private static bool TryAddOption(
        Dictionary<string, string?> options,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || options.ContainsKey(key))
        {
            return false;
        }

        options.Add(key, value);
        return true;
    }

    private static OperationResult<CliInvocation> DuplicateOption(string key) =>
        OperationResult.Failure<CliInvocation>(
            new ApplicationError("cli.option.duplicate", $"Option '--{key}' was supplied more than once."));
}
