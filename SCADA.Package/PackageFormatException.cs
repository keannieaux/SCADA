namespace SCADA.Package;

public sealed class PackageFormatException : Exception
{
    public PackageFormatException(string message)
        : base(message)
    {
        Errors = [message];
    }

    public PackageFormatException(IReadOnlyList<string> errors)
        : base("Ошибки пакета:\n" + string.Join("\n", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
