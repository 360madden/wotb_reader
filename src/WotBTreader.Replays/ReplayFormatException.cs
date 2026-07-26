namespace WotBTreader.Replays;

internal sealed class ReplayFormatException : Exception
{
    public ReplayFormatException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
