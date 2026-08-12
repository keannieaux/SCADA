namespace SCADA.Runtime.Configuration;

public sealed class ProjectConfigurationException : Exception
{
    public ProjectConfigurationException(IReadOnlyList<string>  errors)
        :base("Ошибки конфигурации проекта: \n" + string.Join("\n", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors{get;}
}
